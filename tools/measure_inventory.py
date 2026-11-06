#!/usr/bin/env python3
"""Opt-in bounded offline measurements of a built SkillView host (not acceptance)."""
import argparse
import codecs
import datetime
import hashlib
import json
import math
import os
from pathlib import Path
import platform
import re
import shutil
import signal
import subprocess
import sys
import threading
import time
import unicodedata

from inventory_fixture import fixture_environment, flags, generate, snapshot, write_json
from test_once import ROOT, execute, make_output, stop_tree


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def summary(values):
    ordered = sorted(values)
    return dict(samples=len(values), milliseconds=values,
                p95Ms=ordered[math.ceil(.95 * len(values)) - 1] if values else None)


def log_evidence(text):
    children = re.findall(r'\[subprocess\] exec: (.*)', text)
    return dict(phases=re.findall(r'\[inventory.timing\] (.*)', text),
                inventoryCaptures=text.count('[inventory.timing] local discovery and default catalog:'),
                loggedChildCount=len(children),
                ghListChildCount=sum(' skill list ' in child for child in children),
                loggedChildren=children)


def verify_story(root, manifest, document):
    """Check retained CLI evidence, not just the number of generated files."""
    rows = document['skills']
    owners = document['ownerRecords']
    evidence = {}

    def check(name, condition, detail=None):
        scenario = manifest['scenarios'][name]
        if scenario['status'] == 'unsupported':
            evidence[name] = scenario
        elif not condition:
            raise RuntimeError('Messy story evidence missing: ' + name)
        else:
            evidence[name] = dict(status='observed', detail=detail)

    named = lambda name: [row for row in rows if row['name'] == name]
    shared = named('shared')
    check('shared-payload', len(shared) == 1 and {'User', 'Project'}.issubset({a['scope'] for a in shared[0]['agents']}))
    check('independent-duplicates', len(named('duplicate')) == 2 and
          len({row['resolvedPath'] for row in named('duplicate')}) == 2)
    check('project-override', len(named('override')) == 2 and {r['scope'] for r in named('override')} == {'User', 'Project'})
    mismatch = named('revision-mismatch')
    check('mismatched-revisions', len(mismatch) == 1 and mismatch[0]['repositoryRef'] == 'a' * 40 and
          any(o['repositoryRef'] == 'b' * 40 for o in mismatch[0]['ownerRecords']) and bool(mismatch[0]['diagnostics']))
    pin = named('lock-only-pin')
    check('lock-only-pin', len(pin) == 1 and any(o['pinnedRef'] == 'v1.2.3' for o in pin[0]['ownerRecords']),
          'Pin retained as owner evidence; do not infer authored tracking or update authority')
    check('unmatched-owner-record', not named('absent-owner') and any(o['selector'] == 'absent-owner' for o in owners))
    check('malformed-metadata', any(row['validity'] == 'InvalidMetadata' and row['diagnostics'] for row in named('malformed')))
    check('broken-link', any(row['validity'] == 'BrokenSymlink' for row in named('broken-link')))
    check('empty-leftover', any(row['validity'] == 'MissingSkillMd' for row in named('empty-leftover')))
    check('managed-read-only', any(o['readOnly'] and o['provider'] == 'codex-builtin' for row in named('managed') for o in row['ownerRecords']))
    check('missing-resource', len(named('missing-resource')) == 1 and not
          (root / 'home/.agents/skills/missing-resource/references/missing.md').exists(),
          'Fixture reference only; inventory does not validate Markdown resource links')
    denied = str(root / 'inaccessible')
    check('inaccessible-location', document['partial'] and not named('unobservable') and
          any(denied in path for path in document['inaccessiblePaths']))
    return evidence


def offline_gh(root, real_gh):
    """Allow local gh surfaces only. Authentication is a declared offline stub."""
    directory = root / 'bin'
    directory.mkdir()
    wrapper = directory / 'gh'
    wrapper.write_text('#!' + sys.executable + '\n' +
        'import os, sys\n'
        'a = sys.argv[1:]\n'
        'if a == ["auth", "status"]:\n'
        '    print("Offline fixture: no authentication configured", file=sys.stderr)\n'
        '    raise SystemExit(1)\n'
        'if a == ["--version"] or a == ["skill", "--help"] or a[:2] == ["skill", "list"]:\n'
        '    os.execv(' + repr(str(real_gh)) + ', [' + repr(str(real_gh)) + '] + a)\n'
        'print("Offline fixture refused gh argv: " + repr(a), file=sys.stderr)\n'
        'raise SystemExit(97)\n', encoding='utf-8')
    wrapper.chmod(0o700)
    git = shutil.which('git')
    if not git:
        raise RuntimeError('git is required for local project-root probes')
    (directory / 'git').symlink_to(Path(git).resolve())
    return wrapper


class Screen:
    """Small VT cell observer for Terminal.Gui output, not a general emulator.

    Unknown cursor/edit operations fail measurement rather than accepting stale
    transcript matches. SGR and terminal modes do not affect text predicates.
    """
    def __init__(self, rows=48, columns=160):
        self.rows, self.columns = rows, columns
        self.cells = [[' '] * columns for _ in range(rows)]
        self.y = self.x = 0
        self.pending = ''
        self.saved = (0, 0)
        self.decoder = codecs.getincrementaldecoder('utf-8')('replace')

    @property
    def text(self):
        return '\n'.join(''.join(row) for row in self.cells)

    def feed(self, data):
        self.pending += self.decoder.decode(data)
        responses = bytearray()
        while self.pending:
            if self.pending.startswith('\x1b['):
                match = re.match(r'\x1b\[([0-?]*)([ -/]*)([@-~])', self.pending)
                if not match:
                    break
                self.pending = self.pending[match.end():]
                arg, intermediate, op = match.groups()
                if op == 'n' and arg == '6':
                    responses.extend(f'\x1b[{self.y+1};{self.x+1}R'.encode())
                elif op == 'c':
                    responses.extend(b'\x1b[?1;2c' if not arg.startswith('>') else b'\x1b[>0;0;0c')
                elif op == 't' and arg == '18':
                    responses.extend(f'\x1b[8;{self.rows};{self.columns}t'.encode())
                elif op == 'u' and arg == '?':
                    responses.extend(b'\x1b[?0u')
                elif op in 'mhlqt' or intermediate:
                    pass
                else:
                    self.csi(arg, op)
            elif self.pending.startswith(('\x1b]', '\x1bP')):
                end = re.search(r'\x07|\x1b\\', self.pending)
                if not end:
                    break
                self.pending = self.pending[end.end():]
            elif self.pending[0] == '\x1b':
                if len(self.pending) < 2:
                    break
                op = self.pending[1]
                if op in '()':
                    if len(self.pending) < 3:
                        break
                    self.pending = self.pending[3:]
                else:
                    self.pending = self.pending[2:]
                    if op == '7':
                        self.saved = self.y, self.x
                    elif op == '8':
                        self.y, self.x = self.saved
                    elif op not in '=>':
                        raise ValueError('Unobserved VT escape: ' + repr(op))
            else:
                c, self.pending = self.pending[0], self.pending[1:]
                if c == '\r':
                    self.x = 0
                elif c == '\n':
                    self.y = min(self.rows - 1, self.y + 1)
                elif c == '\b':
                    self.x = max(0, self.x - 1)
                elif c == '\t':
                    self.x = min(self.columns - 1, (self.x // 8 + 1) * 8)
                elif c >= ' ' and not unicodedata.combining(c):
                    self.cells[self.y][self.x] = c
                    width = 2 if unicodedata.east_asian_width(c) in ('W', 'F') else 1
                    self.x = min(self.columns - 1, self.x + width)
        return bytes(responses)

    def csi(self, arg, op):
        if arg.startswith(('?', '>')):
            if op in 'sru':
                return
            raise ValueError(f'Unsupported private CSI {arg}{op}')
        values = [int(x) if x else 0 for x in arg.split(';')]
        n = values[0] or 1
        if op in 'Hf':
            self.y = min(self.rows - 1, max(0, n - 1))
            self.x = min(self.columns - 1, max(0, (values[1] if len(values) > 1 else 1) - 1))
        elif op == 'A':
            self.y = max(0, self.y - n)
        elif op == 'B':
            self.y = min(self.rows - 1, self.y + n)
        elif op == 'C':
            self.x = min(self.columns - 1, self.x + n)
        elif op == 'D':
            self.x = max(0, self.x - n)
        elif op == 'G':
            self.x = min(self.columns - 1, n - 1)
        elif op == 'd':
            self.y = min(self.rows - 1, n - 1)
        elif op == 'J':
            index = self.y * self.columns + self.x
            for y in range(self.rows):
                for x in range(self.columns):
                    i = y * self.columns + x
                    if values[0] in (2, 3) or (values[0] == 0 and i >= index) or (values[0] == 1 and i <= index):
                        self.cells[y][x] = ' '
        elif op in 'KX':
            start = 0 if op == 'K' and values[0] in (1, 2) else self.x
            end = self.x + 1 if op == 'K' and values[0] == 1 else min(self.columns, self.x + n) if op == 'X' else self.columns
            self.cells[self.y][start:end] = [' '] * (end - start)
        elif op == 's':
            self.saved = self.y, self.x
        elif op == 'u':
            self.y, self.x = self.saved
        else:
            raise ValueError(f'Unsupported CSI {arg}{op}')


class Terminal:
    def __init__(self, argv, cwd, env, output):
        import fcntl
        import pty
        import struct
        import termios
        master, slave = pty.openpty()
        self.master = master
        self.output = output
        self.screen = Screen()
        self.deadline = time.monotonic() + 120
        self.bytes_read = 0
        self.raw = output.with_suffix('.ansi').open('wb')
        try:
            fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', 48, 160, 0, 0))
            # Disable echo before launch, so query replies never become evidence.
            attrs = termios.tcgetattr(slave)
            attrs[3] &= ~termios.ECHO
            termios.tcsetattr(slave, termios.TCSANOW, attrs)
            self.started = time.perf_counter()
            self.process = subprocess.Popen(argv, cwd=cwd, env=env, stdin=slave,
                                            stdout=slave, stderr=slave, start_new_session=True)
        except BaseException:
            os.close(master)
            self.raw.close()
            raise
        finally:
            os.close(slave)

    def send(self, data):
        os.write(self.master, data)

    def click_tab(self, label):
        # Header labels are stable visible targets, independent of text focus.
        row = self.screen.text.splitlines()[1]
        x, y = row.index(label) + 2, 2
        self.click(x, y)

    def click(self, x, y):
        self.send(f'\x1b[<0;{x};{y}M\x1b[<0;{x};{y}m'.encode())

    def wait(self, predicate, timeout=30):
        import select
        deadline = min(time.monotonic() + timeout, self.deadline)
        while time.monotonic() < deadline:
            if self.process.poll() is not None:
                raise RuntimeError(f'PTY exited with {self.process.returncode}')
            readable, _, _ = select.select([self.master], [], [], min(.05, max(0, deadline - time.monotonic())))
            if readable:
                data = os.read(self.master, 65536)
                if not data:
                    raise RuntimeError('PTY reached EOF')
                self.bytes_read += len(data)
                if self.bytes_read > 32 * 1024 * 1024:
                    raise RuntimeError('PTY transcript exceeded the 32 MiB observation bound')
                self.raw.write(data)
                self.raw.flush()
                response = self.screen.feed(data)
                if response:
                    self.send(response)
                if predicate(self.screen.text):
                    return time.perf_counter()
        raise TimeoutError('Expected rendered cells were not observed before PTY deadline')

    def close(self, graceful=True):
        try:
            self.exit_code = None
            if graceful:
                self.send(b'\x11')
                self.process.wait(timeout=5)
                self.exit_code = self.process.returncode
        except (OSError, subprocess.TimeoutExpired):
            self.exit_code = None
        finally:
            # Kill the owned group even if its leader has already exited.
            os.close(self.master)
            try:
                stop_tree(self.process, self.raw)
            finally:
                self.output.with_suffix('.screen.txt').write_text(self.screen.text, encoding='utf-8')
                self.raw.close()


def log_text(root):
    return '\n'.join(p.read_text(encoding='utf-8') for p in sorted(root.glob('cache/SkillView/logs/*.log')))


def selected_detail_matches(text, root, index, copy):
    # Alternating group/copy rows can share the same title. Require the final
    # selection-specific content, not any change in a partially received frame.
    detail = '\n'.join(line[83:] for line in text.splitlines()[5:40])
    name = f'item-{index:04d}'
    if copy:
        path = str(root / f'roots/root-{index % 50:03d}' / name)
        return name in detail and '1 physical copy' in detail and path in re.sub(r'[\s│█░▲▼]', '', detail)
    return name in detail and f'Unknown source [root-{index % 50:03d}]' in detail and 'Select an exact copy;' in detail


def pty_measure(app, root, manifest, env, output, transitions=30, local=False):
    metrics = ('navigation', 'filtering') if local else ('tabTransitions',)
    result = dict(status='unmeasured', reason='PTY observation did not complete', shellSamples=1,
                  exactShellReadiness=dict(status='unmeasured', reason='No exact input-ready milestone exists; F1 response provides an upper bound'))
    for metric in metrics:
        result[metric] = dict(status='unmeasured', reason='PTY observation did not complete')
    terminal = None
    try:
        terminal = Terminal([str(app), '--debug', *flags(root, manifest)], root / manifest['cwd'], env, output)
        painted = terminal.wait(lambda s: 'Search:' in s and 'Skill' in s)
        result['firstShellPaintMs'] = round((painted - terminal.started) * 1000, 3)
        # F1 works from a text field and is read-only. Its displayed overlay
        # establishes a causal input response, without guessing input readiness.
        terminal.send(b'\x1bOP')
        interactive = terminal.wait(lambda s: 'SkillView keys' in s and 'Primary' in s)
        result['inputResponseUpperBoundMs'] = round((interactive - terminal.started) * 1000, 3)
        terminal.send(b'\r')
        terminal.wait(lambda s: 'SkillView keys' not in s)
        terminal.click_tab('Installed')
        installed = lambda s: f"{manifest['copies']} observed copies" in '\n'.join(s.splitlines()[-5:]) and 'Coverage' in s
        if not installed(terminal.screen.text):
            terminal.wait(installed)
        if local:
            # Unique rows ensure changes in selected detail can be observed.
            terminal.click(10, 7)  # focus the first table row, independent of startup focus
            before = log_evidence(log_text(root))
            timings = []
            for i in range(1, 31):
                started = time.perf_counter()
                terminal.send(b'\x1b[B')
                terminal.wait(lambda s, index=i // 2, copy=i % 2 == 1: selected_detail_matches(s, root, index, copy))
                timings.append(round((time.perf_counter() - started) * 1000, 3))
            result['navigation'] = dict(summary(timings), status='observed')
            terminal.click(12, 3)  # Installed's visible Search field
            timings = []
            for i in range(30):
                query = f'item-{900 + i:04d}'
                started = time.perf_counter()
                terminal.send(b'\x01' + query.encode())
                terminal.wait(lambda s, q=query: q in s.splitlines()[6][:83] and
                              '1 of 1000 observed copies' in '\n'.join(s.splitlines()[-5:]))
                timings.append(round((time.perf_counter() - started) * 1000, 3))
            result['filtering'] = dict(summary(timings), status='observed')
            after = log_evidence(log_text(root))
            result['localActivity'] = dict(capturesBefore=before['inventoryCaptures'], capturesAfter=after['inventoryCaptures'],
                                          loggedChildrenBefore=before['loggedChildCount'], loggedChildrenAfter=after['loggedChildCount'])
        else:
            issues = lambda s: 'Updates not checked' in s and bool(re.search(r'\d+ Problems.*\d+ Review', s)) and 'loading inventory' not in s.lower()
            terminal.click_tab('Issues')
            terminal.wait(issues)
            terminal.click_tab('Installed')
            terminal.wait(installed)
            before = log_evidence(log_text(root))['inventoryCaptures']
            values = []
            for i in range(transitions):
                started = time.perf_counter()
                terminal.click_tab('Issues' if i % 2 == 0 else 'Installed')
                terminal.wait(issues if i % 2 == 0 else installed)
                values.append(round((time.perf_counter() - started) * 1000, 3))
            after = log_evidence(log_text(root))['inventoryCaptures']
            result['tabTransitions'] = dict(summary(values), capturesBefore=before, capturesAfter=after,
                                           status='measured' if before == after == 1 else 'unmeasured',
                                           reason=None if before == after == 1 else 'Cache reuse was not established')
        result['status'] = 'observed'
        result.pop('reason', None)
    except (OSError, RuntimeError, TimeoutError, ValueError, IndexError) as error:
        result['reason'] = str(error)
        for metric in metrics:
            if result[metric]['status'] == 'unmeasured':
                result[metric]['reason'] = str(error)
    finally:
        if terminal:
            terminal.close(graceful=result['status'] == 'observed')
            result['cleanExit'] = terminal.exit_code == 0
        result['logs'] = log_evidence(log_text(root))
        output.with_suffix('.log').write_text(log_text(root), encoding='utf-8')
    return result


def timed_process(argv, *, cwd, env, stdout, stderr, timeout):
    """Direct host launch to blocking waitpid observation, with a bounded owner.

    POSIX Popen.wait(timeout) polls at up to 50 ms intervals. A blocking waiter
    records exit on its own thread; Event.wait supplies the deadline without
    quantizing successful samples to that polling schedule.
    """
    result = dict(argv=[str(value) for value in argv], status='failed', exitCode=None,
                  startedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat())
    clock = time.get_clock_info('perf_counter')
    result['clock'] = dict(implementation=clock.implementation, resolutionSeconds=clock.resolution,
                           monotonic=clock.monotonic, adjustable=clock.adjustable)
    finished = threading.Event()
    process = waiter = None
    natural_exit = False
    with stdout.open('wb') as output_stream, stderr.open('wb') as error_stream:
        try:
            result['startedNs'] = time.perf_counter_ns()
            process = subprocess.Popen(result['argv'], cwd=cwd, env=env, stdin=subprocess.DEVNULL,
                                       stdout=output_stream, stderr=error_stream, start_new_session=True)
            result.update(pid=process.pid, popenReturnedNs=time.perf_counter_ns())

            def observe_exit():
                try:
                    result['exitCode'] = process.wait()  # blocking waitpid; no timed polling
                except BaseException as error:
                    result['waitError'] = repr(error)
                finally:
                    result['exitObservedNs'] = time.perf_counter_ns()
                    finished.set()

            waiter = threading.Thread(target=observe_exit, name='inventory-process-exit', daemon=True)
            waiter.start()
            remaining = max(0, timeout - (time.perf_counter_ns() - result['startedNs']) / 1_000_000_000)
            if finished.wait(remaining):
                natural_exit = 'waitError' not in result
                result['status'] = 'passed' if result['exitCode'] == 0 and natural_exit else 'failed'
            else:
                result['status'] = 'timeout'
        except KeyboardInterrupt:
            result['status'] = 'interrupted'
        except OSError as error:
            result['error'] = str(error)
        finally:
            result['parentResumedNs'] = time.perf_counter_ns()
            if process is not None:
                try:
                    stop_tree(process, error_stream)
                finally:
                    if waiter is not None and waiter.ident is not None:
                        waiter.join(timeout=10)
                        if waiter.is_alive():
                            raise RuntimeError('Owned process exit waiter did not drain after termination')
    result['naturalExitObserved'] = natural_exit
    if natural_exit:
        result['elapsedNs'] = result['exitObservedNs'] - result['startedNs']
        result['processWallMs'] = round(result['elapsedNs'] / 1_000_000, 3)
    return result


def run_cli(app, root, manifest, env, output, timeout):
    stdout, stderr = output.with_suffix('.json'), output.with_suffix('.stderr')
    step = timed_process([app, '--debug', *flags(root, manifest), 'list', '--json'],
                         cwd=root / manifest['cwd'], env=env, stdout=stdout, stderr=stderr, timeout=timeout)
    timing = output.with_suffix('.timing.json')
    write_json(timing, step)
    step['artifacts'] = {name: dict(path=str(path), sha256=digest(path), bytes=path.stat().st_size)
                         for name, path in (('stdout', stdout), ('stderr', stderr), ('timing', timing))}
    step['evidence'] = log_evidence(stderr.read_text(encoding='utf-8') if stderr.exists() else '')
    if step['status'] != 'passed':
        raise RuntimeError(f"CLI measurement {output.name}: {step['status']}; see diagnostics")
    document = json.loads(stdout.read_text(encoding='utf-8'))
    step['inventory'] = dict(rows=len(document['skills']), partial=document['partial'],
                             providers=document['providers'], inaccessiblePaths=document['inaccessiblePaths'])
    return step, document


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', type=Path, required=True, help='Already built current executable; never dotnet run')
    parser.add_argument('--gh', type=Path, default=Path(shutil.which('gh') or 'gh'))
    parser.add_argument('--expect-head', required=True)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--samples', type=int, default=5)
    parser.add_argument('--timeout', type=int, default=60)
    parser.add_argument('--cli-only', action='store_true', help='Remeasure CLI only; retain earlier PTY receipts as separate historical observations')
    args = parser.parse_args(argv)
    def interrupted(signum, frame):
        raise KeyboardInterrupt
    signal.signal(signal.SIGTERM, interrupted)
    if not 2 <= args.samples <= 10 or not 5 <= args.timeout <= 120:
        parser.error('Use 2..10 samples and 5..120 seconds per CLI sample')
    app, gh = args.app.resolve(strict=True), args.gh.resolve(strict=True)
    if sys.platform not in ('linux', 'darwin'):
        parser.error('Measurement observer/gh adapter supports Linux and macOS only; use inventory_fixture.py for portable topology generation')
    git_env = {k: v for k, v in os.environ.items() if not k.upper().startswith('GIT_')}
    head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, env=git_env, text=True, timeout=10).strip()
    if head != args.expect_head:
        parser.error('HEAD does not match --expect-head')
    output = make_output(args.output)
    output.chmod(0o700)
    report = dict(schema=2, head=head, startedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                  platform=platform.platform(), machine=platform.machine(), python=sys.version,
                  cpuCount=os.cpu_count(), app=str(app), appSha256=digest(app), gh=str(gh), ghSha256=digest(gh),
                  sourceStatus=subprocess.check_output(['git', 'status', '--porcelain'], cwd=ROOT, env=git_env, text=True, timeout=10),
                  limitations=[
                      'Opt-in observation only, no release certification or target enforcement.',
                      'Actual local gh list/help/version; auth status is an explicit unauthenticated offline stub.',
                      'Private homes/CWD/config, no tokens inherited, local Git config only, dead proxies; no remote commands.',
                      'First-process means first CLI invocation on freshly generated files, not OS-cache cold.',
                      'Warm samples are repeated fresh processes over the same files; not in-process cache hits.',
                      'CLI processWallMs spans immediately before direct Popen to blocking waitpid return on an exit-observer thread; includes launch and host serialization, excludes parent notification/cleanup.',
                      'Nanosecond counter fields expose endpoints/resolution, not nanosecond accuracy; scheduler and waiter wake-up latency remain included.',
                      'Messy and large CLI cohorts have one observation each; no statistical distribution is inferred.',
                      'PTY timings end at required rendered cells in a small VT observer, not physical display presentation.',
                      'F1 response is an upper bound on input readiness; exact readiness and OS-cold inventory are unmeasured.',
                      'Logged children count product ProcessRunner launches, not uninstrumented grandchildren or OS traces.',
                      'No inference about filesystem I/O in draw handlers, resource growth, cancellation or mutation acceptance.'])
    report['toolFiles'] = {p.name: digest(p) for p in (Path(__file__), ROOT / 'tools/inventory_fixture.py', ROOT / 'tools/test_once.py')}
    report['requiredSdk'] = json.loads((ROOT / 'global.json').read_text())['sdk']['version']
    report['parameters'] = dict(cliSamples=args.samples, cliTimeoutSeconds=args.timeout, ptyRequested=not args.cli_only,
                                ptyDeadlineSeconds=120, ptyConditionTimeoutSeconds=30,
                                ptyTranscriptLimitBytes=32 * 1024 * 1024, columns=160, rows=48)
    report['binaryKind'] = 'managed apphost' if app.with_suffix('.dll').exists() else 'native or single-file (not inferred further)'
    report['binaryFiles'] = {p.name: digest(p) for p in sorted(app.parent.iterdir()) if p.is_file()}
    try:
        adapter = offline_gh(output, gh)
        report['offlineAdapterSha256'] = digest(adapter)
        meta = output / 'metadata'
        meta.mkdir(mode=0o700)
        meta_env = fixture_environment(meta, adapter)
        report['ghVersion'] = subprocess.check_output([str(adapter), '--version'], env=meta_env, cwd=meta, text=True, timeout=10).strip()
        runtime_config = app.with_suffix('.runtimeconfig.json')
        report['runtimeConfig'] = json.loads(runtime_config.read_text()) if runtime_config.exists() else None
        report['measurements'] = {}
        for name, kind, copies, roots in [('messy', 'messy', 200, 50), ('typical', 'performance', 200, 50), ('large', 'performance', 1000, 50)]:
            root = output / name
            root.mkdir(mode=0o700)
            manifest, _ = generate(root, kind, copies, roots)
            env = fixture_environment(root, adapter)
            env['PATH'] = str(adapter.parent)
            write_json(root / 'environment.json', env)
            before = snapshot(root, manifest, identities=True)
            write_json(root / 'manifest.json', snapshot(root, manifest))
            results = dict(fixture=manifest, environment=env, fixtureSha256=digest(root / 'fixture.json'),
                           environmentSha256=digest(root / 'environment.json'), manifestSha256=digest(root / 'manifest.json'),
                           sampleScope='one first-process observation; no distribution estimated' if name != 'typical'
                           else 'one first-process and repeated warm fresh-process observations')
            before_file = output / f'{name}-inputs-before.json'
            write_json(before_file, before)
            results['inputIdentitiesBefore'] = dict(path=str(before_file), sha256=digest(before_file))
            report['measurements'][name] = results
            for i in range(args.samples if name == 'typical' else 1):
                step, document = run_cli(app, root, manifest, env, output / f'{name}-{i:02d}', args.timeout)
                results.setdefault('cli', []).append(step)
                step['cohort'] = 'first-process' if i == 0 else 'warm-process'
                if kind == 'performance' and (step['inventory']['rows'] != copies or step['inventory']['partial']
                                             or not all(p['succeeded'] for p in document['providers'])):
                    raise RuntimeError(f'{name}: generated workload count/coverage did not match; no performance claim')
                if name == 'messy':
                    results['storyEvidence'] = verify_story(root, manifest, document)
            if name != 'messy':
                if args.cli_only:
                    results['pty'] = dict(status='not-run', reason='--cli-only requested; no new PTY measurement')
                elif sys.platform in ('linux', 'darwin'):
                    # A new private environment/log sink separates PTY capture counts from CLI samples.
                    pty_root = output / (name + '-pty')
                    pty_root.mkdir(mode=0o700)
                    pm, _ = generate(pty_root, kind, copies, roots)
                    pe = fixture_environment(pty_root, adapter)
                    pe['PATH'] = str(adapter.parent)
                    write_json(pty_root / 'environment.json', pe)
                    pty_before = snapshot(pty_root, pm, identities=True)
                    results['pty'] = pty_measure(app, pty_root, pm, pe, output / (name + '-pty'), local=name == 'large')
                    results['pty'].update(environment=pe, fixture=pm, root=str(pty_root))
                    results['pty']['inputsUnchanged'] = snapshot(pty_root, pm, identities=True) == pty_before
                    if not results['pty']['inputsUnchanged']:
                        raise RuntimeError(f'{name}: PTY fixture inputs changed')
                else:
                    results['pty'] = dict(status='unmeasured', reason='POSIX PTY observer requires Linux or macOS')
            after = snapshot(root, manifest, identities=True)
            after_file = output / f'{name}-inputs-after.json'
            write_json(after_file, after)
            results['inputIdentitiesAfter'] = dict(path=str(after_file), sha256=digest(after_file))
            results['inputsUnchanged'] = after == before
            if not results['inputsUnchanged']:
                raise RuntimeError(f'{name}: input topology/bytes/modes changed')
        report['status'] = 'completed'
    except (OSError, RuntimeError, ValueError, KeyError, TypeError, subprocess.TimeoutExpired, KeyboardInterrupt) as error:
        report.update(status='incomplete', error=str(error) or 'Interrupted')
    finally:
        report['appHashUnchanged'] = digest(app) == report['appSha256']
        # Record all managed runtime payloads: apphost hash alone does not identify .NET code.
        report['binaryFilesUnchanged'] = report['binaryFiles'] == {
            p.name: digest(p) for p in sorted(app.parent.iterdir()) if p.is_file()}
        write_json(output / 'report.json', report)
        print(output / 'report.json', flush=True)
    return 0 if report['status'] == 'completed' and report['appHashUnchanged'] and report['binaryFilesUnchanged'] else 1


if __name__ == '__main__':
    sys.exit(main())
