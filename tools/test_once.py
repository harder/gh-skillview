#!/usr/bin/env python3
"""One-off local verification; no scheduler, AI calls, or repository edits."""
import argparse
import datetime
import json
import os
from pathlib import Path
import platform
import re
import shutil
import signal
import subprocess
import sys
import sysconfig
import tempfile
import time


ROOT = Path(__file__).resolve().parents[1]
PROJECTS = {
    'unit': 'tests/SkillView.Tests/SkillView.Tests.csproj',
    'integration': 'tests/SkillView.IntegrationTests/SkillView.IntegrationTests.csproj',
    'app': 'src/SkillView.App/SkillView.App.csproj',
    'extension': 'src/SkillView.GhExtension/SkillView.GhExtension.csproj',
}
OFFLINE = ('static-package-check', 'native-object-evidence', 'gitfile-protection',
           'inventory-filter', 'managed-discovery', 'quarantine-undo')
LIVE = ('revision-update', 'recorded-replacement')
LINK_PROOFS = {'static-package-check', 'gitfile-protection', 'inventory-filter',
               'quarantine-undo', 'recorded-replacement'}
RELEASE_RIDS = {'win-x64', 'win-arm64', 'linux-x64', 'osx-arm64'}


def make_output(requested):
    base = Path('/var/tmp') if sys.platform.startswith('linux') else Path(tempfile.gettempdir())
    candidate = requested.expanduser().resolve() if requested else base.resolve()
    if candidate == ROOT or ROOT in candidate.parents:
        raise ValueError('Results must be outside the checkout, including through directory aliases')
    # Product containment protects every .git marker, including invalid gitfiles,
    # empty directories and dangling links that git rev-parse would ignore.
    for ancestor in (candidate, *candidate.parents):
        try:
            (ancestor / '.git').lstat()
        except FileNotFoundError:
            continue
        raise ValueError(f'Results/fixtures would be beneath a .git marker at {ancestor}; choose another --output')
    if requested:
        candidate.mkdir(parents=True, exist_ok=False)
        return candidate
    return Path(tempfile.mkdtemp(prefix='skillview-one-off-', dir=candidate)).resolve()


def isolated_environment(original, output):
    env = {key: value for key, value in original.items()
           if not key.upper().startswith(('GIT_', 'GH_', 'GITHUB_', 'SKILLVIEW_', 'CODEX_', 'CLAUDE_', 'PI_'))}
    # Preserve the ordinary package cache, not the user's skill/config homes.
    env['NUGET_PACKAGES'] = original.get('NUGET_PACKAGES', str(Path.home() / '.nuget' / 'packages'))
    for key, leaf in {
        'HOME': 'home', 'USERPROFILE': 'home', 'DOTNET_CLI_HOME': 'dotnet-home',
        'APPDATA': 'appdata', 'LOCALAPPDATA': 'localappdata',
        'XDG_CONFIG_HOME': 'config', 'XDG_STATE_HOME': 'xdg-state',
        'XDG_DATA_HOME': 'data', 'XDG_CACHE_HOME': 'cache',
        'GH_CONFIG_DIR': 'gh-config', 'SKILLVIEW_STATE_HOME': 'state',
        'CODEX_HOME': 'home/.codex', 'CLAUDE_CONFIG_DIR': 'home/.claude',
        'PI_CODING_AGENT_DIR': 'home/.pi/agent',
        'TMPDIR': 'tmp', 'TMP': 'tmp', 'TEMP': 'tmp',
    }.items():
        path = output / leaf
        path.mkdir(parents=True, exist_ok=True)
        env[key] = str(path)
    env.update(GIT_CONFIG_COUNT='0', DOTNET_CLI_TELEMETRY_OPTOUT='1',
               DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1', DOTNET_NOLOGO='1',
               DOTNET_CLI_UI_LANGUAGE='en-US', SKILLVIEW_EXPOSURE_LINK_PROOF='1',
               GH_NO_UPDATE_NOTIFIER='1', GH_NO_EXTENSION_UPDATE_NOTIFIER='1', GH_TELEMETRY='false')
    return env


def stop_tree(process, log):
    if os.name == 'nt':
        subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'],
                       stdout=log, stderr=subprocess.STDOUT, timeout=15, check=False)
    else:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
    process.wait(timeout=10)


def symlink_limitation(output):
    """Probe fixture setup rights, independently of SkillView's behavior."""
    try:
        with tempfile.TemporaryDirectory(prefix='symlink-probe-', dir=output) as temporary:
            root = Path(temporary)
            (root / 'file').write_text('fixture', encoding='utf-8')
            (root / 'directory').mkdir()
            (root / 'file-link').symlink_to(root / 'file')
            (root / 'directory-link').symlink_to(root / 'directory', target_is_directory=True)
        return None
    except OSError as error:
        return 'Fixture symlink creation unavailable: ' + str(error)


def execute(argv, *, cwd, env, log, timeout):
    started = time.monotonic()
    result = dict(argv=[str(value) for value in argv], log=str(log), status='failed', exitCode=None)
    with log.open('w', encoding='utf-8') as stream:
        try:
            options = {'creationflags': subprocess.CREATE_NEW_PROCESS_GROUP} if os.name == 'nt' else {'start_new_session': True}
            process = subprocess.Popen(result['argv'], cwd=cwd, env=env, stdin=subprocess.DEVNULL,
                                       stdout=stream, stderr=subprocess.STDOUT, **options)
            try:
                result['exitCode'] = process.wait(timeout=timeout)
                result['status'] = 'passed' if result['exitCode'] == 0 else 'failed'
            except subprocess.TimeoutExpired:
                result['status'] = 'timeout'
                stop_tree(process, stream)
            except KeyboardInterrupt:
                result['status'] = 'interrupted'
                stop_tree(process, stream)
            except BaseException:
                stop_tree(process, stream)
                raise
        except (OSError, subprocess.TimeoutExpired) as error:
            result['error'] = str(error)
            stream.write('\n' + str(error) + '\n')
    result['seconds'] = round(time.monotonic() - started, 2)
    return result


def write_report(output, report):
    (output / 'report.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    rows = ['# One-off SkillView test run', '', f"Commit: `{report.get('commit', 'unavailable')}`",
            f"Platform: {report['platform']}", f"Result: **{report.get('result', 'incomplete')}**", '',
            'These are command outcomes. Read test logs for test counts and skips.', '']
    for step in report['steps']:
        rows.append(f"- {step['name']}: **{step['status']}** — {Path(step['log']).name if step.get('log') else step.get('reason', '')}")
    if report.get('blocked'):
        rows.extend(['', 'Stopped: ' + report['blocked']])
    rows.extend(['', 'Real terminal walkthrough: not run by this script.',
                 'Use docs/one-off-testing-agent.md to finish the agent-led report.'])
    (output / 'REPORT.md').write_text('\n'.join(rows) + '\n', encoding='utf-8')


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--expect-commit', required=True, help='Full 40-character commit shared by all three machines')
    parser.add_argument('--output', type=Path, help='New results directory outside the checkout (default: private temp directory)')
    parser.add_argument('--dotnet', default='dotnet', help='Exact SDK host path if it is not on PATH')
    parser.add_argument('--live', action='store_true', help='Also run actual-gh remote update/restore/undo proofs in private fixtures')
    parser.add_argument('--aot', action='store_true', help='Publish both native hosts sequentially; requires an installed native toolchain')
    parser.add_argument('--diagnostic-tests', action='store_true', help='Log long-running tests and MTP diagnostics for a bounded failure investigation')
    args = parser.parse_args(argv)
    if not re.fullmatch('[0-9a-fA-F]{40}', args.expect_commit):
        parser.error('--expect-commit requires a full commit SHA')
    try:
        output = make_output(args.output)
    except (ValueError, OSError) as error:
        parser.error(str(error))
    env = isolated_environment(os.environ, output)
    report = dict(platform=f'{platform.platform()} / Python build {sysconfig.get_platform()}',
                  startedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                  expectedCommit=args.expect_commit.lower(), aot=args.aot, live=args.live,
                  diagnosticTests=args.diagnostic_tests, steps=[])
    print(f'Results: {output}', flush=True)

    def run(name, command, timeout=1200, child_env=None):
        print(f'{name}: starting', flush=True)
        step = execute(command, cwd=ROOT, env=child_env or env,
                       log=output / (name + '.log'), timeout=timeout)
        step['name'] = name
        report['steps'].append(step)
        write_report(output, report)
        print(f"{name}: {step['status']} ({step['seconds']}s)", flush=True)
        if step['status'] == 'timeout':
            raise RuntimeError('Stopped after a timeout; review process cleanup before another run.')
        if step['status'] == 'interrupted':
            raise RuntimeError('Interrupted; remaining steps were not completed.')
        return step['status'] == 'passed'

    def not_run(name, reason):
        report['steps'].append(dict(name=name, status='not-run', reason=reason))
        print(f'{name}: not-run ({reason})', flush=True)

    try:
        if not run('git-head', ['git', 'rev-parse', 'HEAD'], 30):
            raise RuntimeError('Cannot identify checkout')
        report['commit'] = (output / 'git-head.log').read_text().strip()
        if report['commit'] != args.expect_commit.lower():
            raise RuntimeError('Checkout does not match --expect-commit')
        if not run('git-status', ['git', 'status', '--porcelain'], 30) or (output / 'git-status.log').read_text().strip():
            raise RuntimeError('Use a clean dedicated checkout; source files must match the recorded commit')
        required = json.loads((ROOT / 'global.json').read_text())['sdk']['version']
        if not run('sdk-version', [args.dotnet, '--version'], 60) or (output / 'sdk-version.log').read_text().strip() != required:
            raise RuntimeError(f'Install/use exact SDK {required}; keep global.json and lock files unchanged')
        run('sdk-info', [args.dotnet, '--info'], 60)
        gh = shutil.which('gh')
        if not gh or not run('gh-version', [gh, '--version'], 30) or not run('gh-skill-help', [gh, 'skill', '--help'], 30):
            raise RuntimeError('gh with the required skill surface is unavailable')
        version = re.search(r'gh version (\d+)\.(\d+)\.(\d+)', (output / 'gh-version.log').read_text())
        if version is None or tuple(map(int, version.groups())) < (2, 97, 0):
            raise RuntimeError('gh 2.97.0 or newer is required')
        run('changes-since-release', ['git', 'log', '--oneline', 'v1.0.1..HEAD'], 30)
        ready = {}
        for name in ('unit', 'integration'):
            project = PROJECTS[name]
            ready[name] = run(name + '-restore', [args.dotnet, 'restore', project, '--locked-mode'])
            if ready[name]:
                ready[name] = run(name + '-build', [args.dotnet, 'build', project, '-c', 'Release', '--no-restore'])
            if ready[name]:
                diagnostic = ['--long-running', '60', '--xunit-diagnostics', 'on', '--progress', 'on',
                              '--diagnostic', '--diagnostic-output-directory', str(output / (name + '-diagnostics'))] if args.diagnostic_tests else []
                run(name + '-tests', [args.dotnet, 'test', '--project', project, '-c', 'Release', '--no-build', '--no-restore', *diagnostic])
            else:
                not_run(name + '-tests', 'Restore/build failed')
        rid_match = re.search(r'^\s*RID:\s*(\S+)', (output / 'sdk-info.log').read_text(), re.MULTILINE)
        rid = rid_match.group(1) if rid_match else None
        if args.aot and rid not in RELEASE_RIDS:
            raise RuntimeError(f'No committed AOT release RID for {rid}; run managed mode and report AOT coverage missing')
        hosts = {}
        suffix = '.exe' if os.name == 'nt' else ''
        for name in ('app', 'extension'):
            project = PROJECTS[name]
            flags = ['-r', rid] if args.aot else ['-p:SelfContained=false', '-p:UseDefaultPublishRuntimeIdentifier=false']
            destination = output / name if args.aot else ROOT / Path(project).parent / 'bin/Release/net10.0'
            binary = destination / (('skillview' if name == 'app' else 'gh-skillview') + suffix)
            if args.aot:
                # Match CI: publish restores with RuntimeIdentifier while keeping
                # the four-RID RuntimeIdentifiers set in the committed locks.
                # `dotnet restore -r` overrides that set and causes NU1004.
                restored = True
                command = [args.dotnet, 'publish', project, '-c', 'Release', *flags,
                           '-p:PublishAot=true', '-p:StripSymbols=true', '-p:RestoreLockedMode=true',
                           '-o', str(destination)]
                if name == 'app':
                    command.extend(['-warnaserror:IL2026', '-warnaserror:IL3050', '-warnaserror:IL3053'])
            else:
                restored = run(name + '-restore', [args.dotnet, 'restore', project, '--locked-mode', *flags])
                command = [args.dotnet, 'build', project, '-c', 'Release', '--no-restore', *flags]
            if restored and run(name + '-build', command) and run(name + '-version', [str(binary), '--version'], 60):
                hosts[name] = binary
            else:
                not_run(name + '-proofs', 'Product restore/build failed')
        report['hosts'] = {name: str(binary) for name, binary in hosts.items()}
        link_reason = symlink_limitation(output) if os.name == 'nt' else None
        for name, binary in hosts.items():
            for proof in OFFLINE:
                if link_reason and proof in LINK_PROOFS:
                    not_run(name + '-' + proof, link_reason)
                    continue
                command = [sys.executable, str(ROOT / 'tests/native' / ('prove-' + proof + '.py')),
                           '--app', str(binary), '--output', str(output / f'{name}-{proof}.json')]
                if proof != 'static-package-check':
                    command.extend(['--gh', gh])
                run(name + '-' + proof, command)
        if args.live:
            token = os.environ.get('GH_TOKEN') or os.environ.get('GITHUB_TOKEN')
            if not token:
                # Credentials remain in memory and child environments, never command logs.
                auth = subprocess.run([gh, 'auth', 'token', '--hostname', 'github.com'], cwd=ROOT,
                                      capture_output=True, text=True, timeout=30, check=False)
                token = auth.stdout.strip() if auth.returncode == 0 else None
            if token:
                live_env = dict(env, GH_TOKEN=token)
                for name, binary in hosts.items():
                    for proof in LIVE:
                        if link_reason and proof in LINK_PROOFS:
                            not_run(name + '-' + proof, link_reason)
                            continue
                        run(name + '-' + proof, [sys.executable, str(ROOT / 'tests/native' / ('prove-' + proof + '.py')),
                            '--app', str(binary), '--gh', gh, '--output', str(output / f'{name}-{proof}.json')], child_env=live_env)
            else:
                not_run('live-proofs', 'No existing gh authentication; offline checks still ran')
        else:
            not_run('live-proofs', 'Not requested; rerun with --live for remote install/update/undo coverage')
        if not run('source-status-after', ['git', 'status', '--porcelain'], 30) or (output / 'source-status-after.log').read_text().strip():
            raise RuntimeError('Checkout changed during testing; inspect before interpreting results')
    except (RuntimeError, OSError, subprocess.TimeoutExpired, KeyboardInterrupt) as error:
        report['blocked'] = str(error) or 'Interrupted'
    finally:
        failed = any(step['status'] in ('failed', 'timeout') for step in report['steps'])
        partial = any(step['status'] == 'not-run' for step in report['steps'])
        report['result'] = 'failed' if failed else 'blocked' if report.get('blocked') else 'partial' if partial else 'passed'
        write_report(output, report)
        print(f"Result: {report['result']}; report: {output / 'REPORT.md'}", flush=True)
    return 1 if failed else 2 if report['result'] != 'passed' else 0


if __name__ == '__main__':
    sys.exit(main())
