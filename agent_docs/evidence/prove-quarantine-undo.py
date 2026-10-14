"""Exercise quarantine and undo only in disposable custom-directory fixtures."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument('--app', required=True, type=Path)
parser.add_argument('--gh', action='append', type=Path)
parser.add_argument('--output', required=True, type=Path)
args = parser.parse_args()
app = args.app.resolve()
results = []
for gh in args.gh or [None]:
    with tempfile.TemporaryDirectory(prefix='skillview-quarantine-proof-') as temporary:
        root = Path(temporary).resolve()
        home = root / 'home'
        home.mkdir()
        env = {key: value for key, value in os.environ.items() if not key.startswith('GIT_')}
        env.update(HOME=str(home), USERPROFILE=str(home), GH_CONFIG_DIR=str(root / 'gh-config'),
                   SKILLVIEW_STATE_HOME=str(root / 'state'), XDG_CONFIG_HOME=str(root / 'config'),
                   XDG_STATE_HOME=str(root / 'xdg-state'), APPDATA=str(root / 'appdata'),
                   LOCALAPPDATA=str(root / 'localappdata'), CLAUDE_CONFIG_DIR=str(home / '.claude'),
                   PI_CODING_AGENT_DIR=str(home / '.pi' / 'agent'), GH_TELEMETRY='false',
                   GH_NO_UPDATE_NOTIFIER='1', GH_NO_EXTENSION_UPDATE_NOTIFIER='1')
        if gh:
            env.update(GH_EXTENSION='1', GH_PATH=str(gh.resolve()))
        else:
            env.pop('GH_EXTENSION', None)
            env.pop('GH_PATH', None)
        skills = root / 'custom-skills'
        payload = skills / 'demo'
        payload.mkdir(parents=True)
        payload.joinpath('SKILL.md').write_text('---\nname: demo\ndescription: Fixture\n---\nBody\n')
        payload.joinpath('data').write_bytes(bytes([0, 255, 3]))
        payload.joinpath('empty').mkdir()
        if os.name != 'nt':
            payload.joinpath('data').chmod(0o500)
            payload.joinpath('link').symlink_to('data')
        original_inode = payload.stat().st_ino
        original_mode = payload.joinpath('data').stat().st_mode
        original = payload.joinpath('SKILL.md').read_bytes()

        def run(command, *arguments, ok=True):
            process = subprocess.run([str(app), '--scan-root', str(skills), command, *map(str, arguments), '--json'],
                                     env=env, cwd=root, capture_output=True, text=True, timeout=120)
            assert (process.returncode == 0) == ok, (process.returncode, process.stdout, process.stderr)
            return json.loads(process.stdout)

        preview = run('quarantine', '--path', payload)['operations'][0]
        assert preview['status'] == 'preview' and not preview['mutationObserved']
        assert not root.joinpath('state').exists(), 'Preview created state'
        run('quarantine', '--path', payload, '--yes', '--standalone', '--expect', '0' * 64, ok=False)
        assert payload.exists() and not root.joinpath('state').exists()
        applied = run('quarantine', '--path', payload, '--yes', '--standalone', '--expect', preview['reviewFingerprint'])['operations'][0]
        assert applied['succeeded'] and applied['status'] == 'quarantined', applied
        retained = Path(applied['retainedPath'])
        operation_id = applied['id']
        assert not payload.exists() and retained.stat().st_ino == original_inode
        assert retained.joinpath('SKILL.md').read_bytes() == original
        assert retained.joinpath('data').read_bytes() == bytes([0, 255, 3])
        assert retained.joinpath('data').stat().st_mode == original_mode
        assert retained.joinpath('empty').is_dir()
        if os.name != 'nt':
            assert os.readlink(retained / 'link') == 'data'
            assert retained.parent.stat().st_mode & 0o777 == 0o700
        assert run('quarantine-history')['operations'][0]['status'] == 'quarantined'
        undo_preview = run('undo', '--id', operation_id)['operations'][0]
        assert undo_preview['succeeded'] and not undo_preview['mutationObserved'] and not payload.exists()
        restored = run('undo', '--id', operation_id, '--yes')['operations'][0]
        assert restored['succeeded'] and restored['mutationObserved'] and restored['status'] == 'restored', restored
        assert payload.stat().st_ino == original_inode and not retained.exists()
        assert payload.joinpath('SKILL.md').read_bytes() == original
        assert not run('undo', '--id', operation_id, '--yes')['operations'][0]['mutationObserved']
        assert run('quarantine-history')['operations'][0]['status'] == 'restored'
        # A second operation exercises ordinary refusal cases without touching user installs.
        preview2 = run('quarantine', '--path', payload)['operations'][0]
        second = run('quarantine', '--path', payload, '--yes', '--standalone', '--expect', preview2['reviewFingerprint'])['operations'][0]
        retained2 = Path(second['retainedPath'])
        payload.mkdir()
        payload.joinpath('replacement').write_text('keep')
        run('undo', '--id', second['id'], '--yes', ok=False)
        assert payload.joinpath('replacement').read_text() == 'keep' and retained2.exists()
        assert not retained2.parent.joinpath('undo-prepared.json').exists()
        assert not list(root.rglob('*.svbackup')), 'Quarantine unexpectedly extracted/copied an archive'
        results.append(dict(nativeHost=str(app), nativeSha256=hashlib.sha256(app.read_bytes()).hexdigest(),
                            gh=str(gh) if gh else 'runner default', previewReadOnly=True,
                            staleReviewRefusedBeforeState=True, originalDirectoryIdentityRetained=True,
                            payloadBytesModesLinksAndEmptyDirectoriesPreserved=True, undoPreviewReadOnly=True,
                            undoRestoredOriginalDirectory=True, repeatedUndoVerifiedNoOp=True,
                            occupiedRestorePathPreserved=True, completeJournalHistory=True,
                            securityIsolationClaimed=False, archiveExtractionUsed=False))
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(results, indent=2))
print(json.dumps(results, indent=2))
