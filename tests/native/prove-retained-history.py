#!/usr/bin/env python3
"""Offline read-only History PTY proof; creates and retains fresh private fixtures only."""
import argparse, codecs, fcntl, hashlib, json, os, pathlib, pty, re, select, signal, stat, struct, subprocess, tempfile, termios, time, unicodedata

parser = argparse.ArgumentParser()
parser.add_argument('--app', required=True)
parser.add_argument('--proof', required=True)
parser.add_argument('--output', required=True)
args = parser.parse_args()
app = str(pathlib.Path(args.app).resolve())
proof = str(pathlib.Path(args.proof).resolve())
output = pathlib.Path(args.output).resolve()
output.mkdir(parents=True, exist_ok=True)
os.umask(0o077)
WIDTH, HEIGHT = 80, 24
screen = []; x = y = 0; pending = ''; transcript = bytearray()
decoder = codecs.getincrementaldecoder('utf-8')('replace')
master = None; proc = None

def send(value):
    os.write(master, value if isinstance(value, bytes) else value.encode())

def render(text):
    global x, y, pending
    pending += text
    while pending:
        if pending[0] == "\x1b":
            if len(pending) < 2:
                return
            if pending[1] == "[":
                m = re.match(r"\x1b\[([0-?]*)([ -/]*)([@-~])", pending)
                if not m:
                    return
                data, inter, cmd = m.groups()
                pending = pending[m.end() :]
                nums = [
                    int(v) if v.isdigit() else 0
                    for v in data.lstrip("?=>").split(";")
                ]
                n = nums[0] or 1
                if cmd in "Hf":
                    y = min((HEIGHT - 1), max(0, n - 1))
                    x = min((WIDTH - 1), max(0, (nums[1] if len(nums) > 1 else 1) - 1))
                elif cmd == "A":
                    y = max(0, y - n)
                elif cmd == "B":
                    y = min((HEIGHT - 1), y + n)
                elif cmd == "C":
                    x = min((WIDTH - 1), x + n)
                elif cmd == "D":
                    x = max(0, x - n)
                elif cmd == "G":
                    x = min((WIDTH - 1), n - 1)
                elif cmd == "J" and nums[0] in [2, 3]:
                    for row in screen:
                        row[:] = [" "] * WIDTH
                elif cmd == "K":
                    if nums[0] == 2:
                        screen[y][:] = [" "] * WIDTH
                    else:
                        screen[y][x:] = [" "] * (WIDTH - x)
                elif cmd == "n" and nums[0] == 6:
                    send("\x1b[1;1R")
                elif cmd == "t" and nums[0] == 18:
                    send(f"\x1b[8;{HEIGHT};{WIDTH}t")
                elif cmd == "c":
                    send("\x1b[?1;2c")
                elif cmd == "u" and data == "?":
                    send("\x1b[?0u")
                continue
            if pending[1] == "]":
                end = pending.find("\x07")
                end2 = pending.find("\x1b\\")
                if end < 0 and end2 < 0:
                    return
                if end >= 0 and (end2 < 0 or end < end2):
                    pending = pending[end + 1 :]
                else:
                    pending = pending[end2 + 2 :]
                continue
            pending = pending[2:]
            continue
        ch, pending = pending[0], pending[1:]
        if ch == "\r":
            x = 0
        elif ch == "\n":
            y = min((HEIGHT - 1), y + 1)
        elif ch == "\b":
            x = max(0, x - 1)
        elif ord(ch) >= 32:
            if unicodedata.combining(ch):
                continue
            screen[y][min(x, (WIDTH - 1))] = ch
            x += 2 if unicodedata.east_asian_width(ch) in "WF" else 1
            if x >= WIDTH:
                x = 0
                y = min((HEIGHT - 1), y + 1)

def read(seconds=0.1):
    if select.select([master], [], [], seconds)[0]:
        try:
            data = os.read(master, 65536)
        except OSError:
            return
        transcript.extend(data)
        render(decoder.decode(data))

def text():
    return "\n".join("".join(row) for row in screen)

def wait(predicate, timeout=60):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        read()
        if predicate():
            return
        if proc.poll() is not None:
            # Exit can race the first predicate check above. poll caches
            # the return code, so recheck before treating it as unexpected.
            assert predicate(), "TUI exited: " + str(proc.returncode) + "\n" + text()
            return
    raise AssertionError("TUI condition timed out\n" + text())




def snapshot(paths):
    result = {}
    for root in paths:
        if not root.exists():
            result[str(root)] = None
            continue
        for directory, directories, files in os.walk(root, followlinks=False):
            for path in [pathlib.Path(directory), *[pathlib.Path(directory) / name for name in files]]:
                value = path.lstat()
                result[str(path)] = dict(mode=value.st_mode, dev=value.st_dev, ino=value.st_ino,
                    mtime=value.st_mtime_ns, ctime=value.st_ctime_ns,
                    sha256=hashlib.sha256(path.read_bytes()).hexdigest() if stat.S_ISREG(value.st_mode) else None)
    return result


def frame(name):
    (output / (name + '.txt')).write_text(text() + '\n')
    (output / (name + '.transcript')).write_bytes(transcript)


def settle(seconds=.3):
    end = time.monotonic() + seconds
    while time.monotonic() < end:
        read(.05)


def run_size(width, height):
    global WIDTH, HEIGHT, screen, x, y, pending, transcript, decoder, master, proc
    WIDTH, HEIGHT = width, height
    root = pathlib.Path(tempfile.mkdtemp(prefix='skillview-history-pty-', dir=os.environ.get('TMPDIR', '/var/tmp')))
    root.chmod(0o700)
    (root / 'disposable-proof-marker').write_text('SkillView disposable process proof\n')
    for name in ('tmp', 'bin'):
        (root / name).mkdir()
    env = {key: value for key, value in os.environ.items()
           if not key.startswith(('GIT_', 'GH_', 'GITHUB_', 'SKILLVIEW_', 'CLAUDE_', 'CODEX_'))}
    env.update(HOME=str(root / 'home'), USERPROFILE=str(root / 'home'), SKILLVIEW_STATE_HOME=str(root / 'state'),
        PATH=str(root / 'bin'), XDG_STATE_HOME=str(root / 'xdg-state'), XDG_CONFIG_HOME=str(root / 'xdg-config'),
        XDG_DATA_HOME=str(root / 'xdg-data'), XDG_CACHE_HOME=str(root / 'xdg-cache'), APPDATA=str(root / 'appdata'),
        LOCALAPPDATA=str(root / 'localappdata'), CODEX_HOME=str(root / 'home/.codex'), CLAUDE_CONFIG_DIR=str(root / 'home/.claude'),
        CLAUDE_CODE_PLUGIN_CACHE_DIR=str(root / 'plugins'), CLAUDE_CODE_PLUGIN_DIRS='', CLAUDE_CODE_PLUGIN_SEED_DIR='',
        PI_CODING_AGENT_DIR=str(root / 'home/.pi'), GH_CONFIG_DIR=str(root / 'gh-config'),
        GIT_CONFIG_COUNT='0', GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL=str(root / 'gitconfig'),
        TERM='xterm-256color', COLORTERM='truecolor', COLUMNS=str(width), LINES=str(height),
        TMPDIR=str(root / 'tmp'), SKILLVIEW_LOG='debug', DOTNET_ROOT='/usr/lib/dotnet',
        GH_NO_UPDATE_NOTIFIER='1', GH_NO_EXTENSION_UPDATE_NOTIFIER='1', GH_TELEMETRY='false')
    fixture = subprocess.run([proof, '--retained-history-fixture', str(root)], cwd=root, env=env,
        text=True, capture_output=True, timeout=60)
    (output / f'{width}-fixture.log').write_text(fixture.stdout + fixture.stderr)
    assert fixture.returncode == 0, fixture.stderr
    assert all('native-history-inspect: ' + status in fixture.stdout for status in ('ready', 'installed', 'original-retained')), fixture.stdout
    protected = [root / 'custom', root / 'state', root / 'home/.agents/.skill-lock.json']
    before = snapshot(protected)
    listing = subprocess.run([app, 'replacement-history', '--json'], cwd=root, env=env,
        text=True, capture_output=True, timeout=30)
    assert listing.returncode == 0, listing.stderr
    listed = json.loads(listing.stdout)
    assert len(listed['entries']) == 3 and listed['readOnly']
    assert any(row['mayNeedRecovery'] for row in listed['entries'])
    (output / f'{width}-listing.json').write_text(json.dumps(listed, indent=2))
    assert before == snapshot(protected), 'CLI listing changed retained/live objects'
    for restart in range(2):
        screen = [[' '] * width for _ in range(height)]; x = y = 0; pending = ''; transcript = bytearray()
        decoder = codecs.getincrementaldecoder('utf-8')('replace')
        master, slave = pty.openpty()
        fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack('HHHH', height, width, 0, 0))
        proc = subprocess.Popen([app, '--scan-root', str(root / 'custom')], cwd=root, env=env,
            stdin=slave, stdout=slave, stderr=slave, start_new_session=True)
        os.close(slave)
        try:
            wait(lambda: 'recovery hints' in text(), 30)
            frame(f'{width}x{height}-{restart}-startup')
            send(b'\x1bOS')  # Visible F4 route also works from text inputs.
            wait(lambda: 'retained operations' in text() and 'Inspect' in text(), 20)
            wait(lambda: '3 retained operations' in text(), 20)
            wait(lambda: 'ID:' in text() and 'Inspect to verify' in text(), 10)
            settle(.3)
            frame(f'{width}x{height}-{restart}-history')
            # Alt+I is the visible Inspect button mnemonic, with no mutation action.
            send(b'\x1bi')
            wait(lambda: 'Explicit inspection' in text() or 'could not verify' in text(), 20)
            settle(.3)
            frame(f'{width}x{height}-{restart}-inspect')
            send(b'\x1br')
            wait(lambda: '3 retained operations' in text() and 'ID:' in text() and 'Explicit inspection' not in text() and 'could not verify' not in text(), 20)
            settle(.3)
            frame(f'{width}x{height}-{restart}-refresh')
            send(b'\x1b'); settle(.8)
            wait(lambda: 'recovery hints' in text() and 'retained operations' not in text(), 10)
            send(b'\x11')
            proc.wait(timeout=10)
            assert proc.returncode == 0
        finally:
            if proc.poll() is None:
                os.killpg(proc.pid, signal.SIGKILL); proc.wait(timeout=5)
            os.close(master)
        assert before == snapshot(protected), 'PTY browsing/Inspect changed retained/live objects'
    (output / f'{width}-native-before.json').write_text(json.dumps(before, indent=2))
    (output / f'{width}-native-after.json').write_text(json.dumps(snapshot(protected), indent=2))
    return str(root)

roots = [run_size(80, 24), run_size(120, 36)]
(output / 'result.json').write_text(json.dumps(dict(passed=True, fixtures=roots,
    checks=['offline CLI listing', 'startup interrupted hint twice', 'keyboard History/Inspect/Refresh/Back',
            '80x24 and 120x36', 'all retained/live bytes, modes, native IDs and generations unchanged']), indent=2))
print(json.dumps(dict(passed=True, fixtures=roots)))
