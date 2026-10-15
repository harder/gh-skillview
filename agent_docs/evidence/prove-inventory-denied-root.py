"""Real POSIX permission-denial inventory and cleanup proof in a private fixture."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument('--app', required=True, type=Path)
parser.add_argument('--gh', type=Path)
parser.add_argument('--output', required=True, type=Path)
args = parser.parse_args()
assert os.name == 'posix', 'This proof exercises POSIX permissions.'
assert os.geteuid() != 0, 'Root would bypass the permission denial.'
app = args.app.resolve()
gh = (args.gh or Path(shutil.which('gh') or 'gh')).resolve()

with tempfile.TemporaryDirectory(prefix='skillview-denied-inventory-') as temporary:
    root = Path(temporary).resolve()
    home = root / 'home'
    home.mkdir()
    readable, denied = root / 'readable', root / 'denied'
    for parent, name in [(readable, 'visible'), (denied, 'inaccessible')]:
        payload = parent / name
        payload.mkdir(parents=True)
        payload.joinpath('SKILL.md').write_bytes(
            f'---\nname: {name}\ndescription: Private permission fixture\n---\nBody\n'.encode())

    def manifest():
        entries = []
        for parent in [readable, denied]:
            # Enumerate afresh to include additions as well as existing entries.
            for path in [parent, *sorted(parent.rglob('*'))]:
                info = path.lstat()
                entries.append((str(path.relative_to(root)), info.st_dev, info.st_ino,
                                stat.S_IMODE(info.st_mode),
                                hashlib.sha256(path.read_bytes()).hexdigest() if path.is_file() else None))
        return entries

    before = manifest()
    mode = stat.S_IMODE(denied.stat().st_mode)
    env = {key: value for key, value in os.environ.items() if not key.startswith(('GIT_', 'SKILLVIEW_'))}
    env.update(HOME=str(home), USERPROFILE=str(home), GH_EXTENSION='1', GH_PATH=str(gh),
               GH_CONFIG_DIR=str(root / 'gh-config'), SKILLVIEW_STATE_HOME=str(root / 'state'),
               XDG_CONFIG_HOME=str(root / 'config'), XDG_STATE_HOME=str(root / 'xdg-state'),
               XDG_CACHE_HOME=str(root / 'cache'), XDG_DATA_HOME=str(root / 'data'),
               CLAUDE_CONFIG_DIR=str(home / '.claude'), PI_CODING_AGENT_DIR=str(home / '.pi' / 'agent'),
               GH_NO_UPDATE_NOTIFIER='1', GH_NO_EXTENSION_UPDATE_NOTIFIER='1', GH_TELEMETRY='false',
               GIT_CONFIG_COUNT='1', GIT_CONFIG_KEY_0='protocol.allow', GIT_CONFIG_VALUE_0='never',
               HTTP_PROXY='http://127.0.0.1:9', HTTPS_PROXY='http://127.0.0.1:9',
               ALL_PROXY='http://127.0.0.1:9', NO_PROXY='')
    captures = {}
    try:
        denied.chmod(0)
        try:
            list(denied.iterdir())
        except PermissionError:
            pass
        else:
            raise AssertionError('OS did not enforce directory denial')
        for command in ['list', 'cleanup']:
            process = subprocess.run([str(app), '--scan-root', str(readable), '--scan-root', str(denied),
                                      command, '--allow-hidden-dirs', '--json'],
                                     env=env, cwd=root, capture_output=True, text=True, timeout=90)
            assert process.returncode == 0, (command, process.returncode, process.stdout, process.stderr)
            document = json.loads(process.stdout)
            assert document['partial'] and str(denied) in document['inaccessiblePaths'], document
            if command == 'list':
                assert [row['name'] for row in document['skills']] == ['visible'], document
                assert any(location['path'] == str(denied) and location['state'] == 'inaccessible'
                           for location in document['discoveryLocations']), document
            else:
                assert all(row['kind'] not in ['Untracked', 'SourceOrphaned', 'Duplicate']
                           for row in document['entries']), document
                assert all(row['reviewOnly'] for row in document['entries']), document
            captures[command] = document
    finally:
        denied.chmod(mode)
    assert manifest() == before, 'Inventory or cleanup altered fixture objects/bytes/modes'
    result = dict(nativeHost=str(app), nativeSha256=hashlib.sha256(app.read_bytes()).hexdigest(),
                  gh=subprocess.check_output([str(gh), '--version'], text=True, env=env, cwd=root).splitlines()[0],
                  actualPosixPermissionDenialObserved=True, readableControlRetained=True,
                  partialAndInaccessibleDiagnostics=True, absenceCleanupSuppressed=True,
                  installedObjectsBytesAndModesUnchanged=True, captures=captures)
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps([result], indent=2) + '\n')
print('POSIX denied-root inventory/cleanup proof passed')
