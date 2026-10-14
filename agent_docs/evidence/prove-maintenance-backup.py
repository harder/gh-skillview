"""Prove native backup evidence in disposable roots; never execute a skill."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import stat
import subprocess
import tempfile
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument('--app', required=True, type=Path)
parser.add_argument('--gh', action='append', type=Path)
parser.add_argument('--output', required=True, type=Path)
args = parser.parse_args()
app = args.app.resolve()
shared = Path.home() / '.agents' / '.skill-lock.json'


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest() if path.is_file() else None


shared_before = digest(shared)
results = []
for gh in args.gh or [None]:
    with tempfile.TemporaryDirectory(prefix='skillview-backup-proof-') as temporary:
        root = Path(temporary).resolve()
        home = root / 'home'
        home.mkdir()
        env = {key: value for key, value in os.environ.items() if not key.startswith('GIT_')}
        env.update(HOME=str(home), USERPROFILE=str(home), SKILLVIEW_STATE_HOME=str(root / 'state'),
                   GH_CONFIG_DIR=str(root / 'gh-config'), XDG_STATE_HOME=str(root / 'xdg-state'),
                   XDG_CONFIG_HOME=str(root / 'config'), APPDATA=str(root / 'appdata'),
                   LOCALAPPDATA=str(root / 'localappdata'), CLAUDE_CONFIG_DIR=str(home / '.claude'),
                   PI_CODING_AGENT_DIR=str(home / '.pi' / 'agent'), GH_TELEMETRY='false',
                   GH_NO_UPDATE_NOTIFIER='1', GH_NO_EXTENSION_UPDATE_NOTIFIER='1')
        if gh:
            env.update(GH_EXTENSION='1', GH_PATH=str(gh.resolve()))
        else:
            env.pop('GH_PATH', None)
            env.pop('GH_EXTENSION', None)
        skills = root / 'skills'
        first, second = skills / 'first', skills / 'second'
        for payload in [first, second]:
            payload.mkdir(parents=True)
            payload.joinpath('SKILL.md').write_text('---\nname: duplicate\ndescription: Fixture\n---\nBody\n')
        first.joinpath('resource.bin').write_bytes(bytes([0, 1, 255, 42]))
        first.joinpath('empty').mkdir()
        outside = root / 'outside'
        outside.mkdir()
        outside.joinpath('secret').write_text('untouched')
        first.joinpath('link').symlink_to(outside, target_is_directory=True)
        if os.name != 'nt':
            first.joinpath('resource.bin').chmod(0o500)

        def snapshot():
            rows = []
            for payload in [first, second]:
                for path in sorted(payload.rglob('*')):
                    info = path.lstat()
                    value = os.readlink(path) if path.is_symlink() else digest(path) if path.is_file() else 'directory'
                    rows.append((str(path.relative_to(skills)), stat.S_IMODE(info.st_mode), value))
            return rows

        before = snapshot()

        def run(command, expected=0):
            process = subprocess.run([str(app), '--scan-root', str(skills)] + command, env=env, cwd=root,
                                     capture_output=True, text=True, timeout=120)
            assert process.returncode == expected, (process.returncode, process.stdout, process.stderr)
            return json.loads(process.stdout) if process.stdout.strip() else None

        inventory = run(['list', '--json'])
        selected = next(row for row in inventory['skills'] if Path(row['resolvedPath']) == first)
        captured = run(['backup', '--install-id', selected['installationId'], '--json'])
        assert captured['succeeded'] and captured['status'] == 'captured', captured
        archive_path = Path(captured['archivePath'])
        assert archive_path.is_relative_to(root / 'state')
        assert digest(archive_path) == captured['archiveHash']
        with zipfile.ZipFile(archive_path) as archive:
            manifest = json.loads(archive.read('manifest.json'))
            assert manifest['fingerprint'] == captured['fingerprint']
            rows = manifest['entries']
            assert any(row['path'] == 'empty' and row['kind'] == 'directory' for row in rows)
            assert any(row['path'] == 'link' and row['kind'] == 'link' for row in rows)
            assert not any('secret' in row['path'] for row in rows)
            for row in rows:
                if row['blob'] is not None:
                    data = archive.read(row['blob'])
                    assert hashlib.sha256(data).hexdigest().upper() == row['hash']
                    assert len(data) == row['blobBytes']
                    if row['kind'] == 'file':
                        assert len(data) == row['bytes']
                    else:
                        assert row['kind'] == 'link' and len(data) > 0
        state = archive_path.parent
        journal = json.loads(state.joinpath(captured['id'] + '.captured.json').read_text())
        assert journal['archiveHash'] == captured['archiveHash']
        assert state.joinpath(captured['id'] + '.creating.json').is_file()
        if os.name != 'nt':
            assert stat.S_IMODE(state.stat().st_mode) == 0o700
            assert all(stat.S_IMODE(path.stat().st_mode) == 0o600 for path in state.iterdir())
        assert snapshot() == before
        assert outside.joinpath('secret').read_text() == 'untouched'
        # State nested in either observed payload must refuse, even when the
        # selected payload is another copy with the same name.
        env['SKILLVIEW_STATE_HOME'] = str(second / 'backup-state')
        refused = run(['backup', '--path', str(first), '--json'], expected=10)
        assert not refused['succeeded'], refused
        assert snapshot() == before
        assert not second.joinpath('backup-state').exists()
        env['SKILLVIEW_STATE_HOME'] = str(root / 'state')
        first.joinpath('SKILL.md').write_text('---\nname: demo\ndescription: Fixture\nmetadata: {github-ref: main}\n---\nRaw body\n')
        raw_before = snapshot()
        raw = run(['backup', '--path', str(first), '--json'])
        assert raw['succeeded'], raw
        with zipfile.ZipFile(raw['archivePath']) as archive:
            manifest = json.loads(archive.read('manifest.json'))
            row = next(row for row in manifest['entries'] if row['path'] == 'SKILL.md')
            assert archive.read(row['blob']) == first.joinpath('SKILL.md').read_bytes()
        assert snapshot() == raw_before
        missing_agent_root = home / '.agents' / 'skills'
        assert not missing_agent_root.exists()
        env['SKILLVIEW_STATE_HOME'] = str(missing_agent_root)
        refused = run(['backup', '--path', str(first), '--json'], expected=10)
        assert not refused['succeeded'] and not missing_agent_root.exists(), refused
        assert snapshot() == raw_before
        results.append(dict(nativeHost=str(app), nativeSha256=digest(app), gh=str(gh) if gh else 'runner default',
                            exactInstallationIdSelected=True, archiveAndJournalHashesVerified=True,
                            completeFileBytesAndEmptyDirectoriesVerified=True, nativeLinkRemainedLeaf=True,
                            unixPrivateModesVerified=os.name != 'nt', selectedAndUnselectedPayloadsUnchanged=True,
                            otherInstalledPayloadCannotBeState=True, missingConfiguredAgentRootCannotBeState=True,
                            rawFlowMetadataArchivedByteExact=True, sharedRegistryUnchanged=True))
assert digest(shared) == shared_before
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(results, indent=2))
print(json.dumps(results, indent=2))
