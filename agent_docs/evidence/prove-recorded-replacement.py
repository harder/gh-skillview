"""Real-gh/native-host restore and undo proof; all writes use disposable homes."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import tempfile
import time

parser = argparse.ArgumentParser()
parser.add_argument('--app', required=True, type=Path)
parser.add_argument('--gh', action='append', type=Path)
parser.add_argument('--output', required=True, type=Path)
args = parser.parse_args()
app = args.app.resolve()
gh_paths = args.gh or [Path(shutil.which('gh') or 'gh')]
auth = os.environ.get('GH_TOKEN') or os.environ.get('GITHUB_TOKEN')
if not auth:
    auth = subprocess.run([str(gh_paths[-1]), 'auth', 'token'], capture_output=True, text=True, check=True).stdout.strip()
revision = '33789149b9def6b66ddf247438a79fac94e22074'
results = []


def manifest(directory):
    entries = [('/', 'directory', stat.S_IMODE(directory.stat().st_mode) if os.name != 'nt' else None, '')]
    for path in sorted(directory.rglob('*')):
        information = path.lstat()
        kind = 'link' if path.is_symlink() else 'directory' if path.is_dir() else 'file'
        value = os.readlink(path).encode() if kind == 'link' else path.read_bytes() if kind == 'file' else b''
        entries.append((str(path.relative_to(directory)), kind, stat.S_IMODE(information.st_mode) if os.name != 'nt' else None, hashlib.sha256(value).hexdigest()))
    return entries


for gh_path in gh_paths:
    gh = gh_path.resolve()
    with tempfile.TemporaryDirectory(prefix='skillview-replacement-proof-') as temporary:
        root = Path(temporary).resolve()
        home = root / 'home'
        home.mkdir()
        builtin = home / '.codex' / 'skills' / '.system' / 'builtin-fixture'
        builtin.mkdir(parents=True)
        builtin.joinpath('SKILL.md').write_text('---\nname: builtin-fixture\ndescription: Hidden built-in fixture\n---\nInstructions\n')
        env = {key: value for key, value in os.environ.items() if not key.startswith('GIT_')}
        env.update(HOME=str(home), USERPROFILE=str(home), GH_TOKEN=auth, GH_EXTENSION='1', GH_PATH=str(gh),
                   GH_CONFIG_DIR=str(root / 'gh-config'), SKILLVIEW_STATE_HOME=str(root / 'state'),
                   XDG_CONFIG_HOME=str(root / 'config'), XDG_STATE_HOME=str(root / 'xdg-state'),
                   XDG_DATA_HOME=str(root / 'data'), XDG_CACHE_HOME=str(root / 'cache'),
                   APPDATA=str(root / 'appdata'), LOCALAPPDATA=str(root / 'localappdata'),
                   CLAUDE_CONFIG_DIR=str(home / '.claude'), PI_CODING_AGENT_DIR=str(home / '.pi' / 'agent'),
                   GH_TELEMETRY='false', GH_NO_UPDATE_NOTIFIER='1', GH_NO_EXTENSION_UPDATE_NOTIFIER='1')
        first, second = root / 'first', root / 'second'
        for directory in (first, second):
            process = subprocess.run([str(gh), 'skill', 'install', '--dir', str(directory), '--pin=' + revision,
                                      '--', 'cli/cli', 'skills/gh-skill'], env=env, cwd=root, capture_output=True, text=True, timeout=120)
            assert process.returncode == 0, process.stderr
        shared = subprocess.run([str(gh), 'skill', 'install', '--agent', 'universal', '--scope', 'user', '--pin=' + revision,
                                 '--', 'cli/cli', 'skills/gh-skill'], env=env, cwd=root, capture_output=True, text=True, timeout=120)
        assert shared.returncode == 0, shared.stderr
        shared_payload = home / '.agents' / 'skills' / 'gh-skill'
        agent_root = home / '.claude' / 'skills'
        agent_root.mkdir(parents=True)
        exposure = agent_root / 'gh-skill'
        exposure.symlink_to(shared_payload, target_is_directory=True)
        lock = home / '.agents' / '.skill-lock.json'
        registry = lock.read_bytes()
        unrelated = second / 'gh-skill'
        unrelated_before = manifest(unrelated)
        rows = []

        def run(command, *arguments, ok=True):
            process = subprocess.run([str(app), '--scan-root', str(first), '--scan-root', str(second), command,
                                      *map(str, arguments), '--allow-hidden-dirs', '--json'], env=env, cwd=root, capture_output=True, text=True, timeout=180)
            assert (process.returncode == 0) == ok, (process.returncode, process.stdout, process.stderr)
            return json.loads(process.stdout)

        for payload, shared_target in ((first / 'gh-skill', False), (shared_payload, True)):
            payload.joinpath('SKILL.md').write_bytes(payload.joinpath('SKILL.md').read_bytes() + b'\nUser modifications\n')
            payload.joinpath('user-extra').write_bytes(b'\x00\xffKeep extra')
            payload.joinpath('.skillview-ignore').write_text('User exclusion\n')
            payload.joinpath('empty-user-directory').mkdir()
            if os.name != 'nt':
                payload.chmod(0o700)
                payload.joinpath('user-extra').chmod(0o500)
                payload.joinpath('user-link').symlink_to('user-extra')
            original = manifest(payload)
            identity = payload.stat().st_ino
            plan = run('restore-plan', '--path', payload)['operation']
            assert plan['status'] == 'planned' and not plan['mutationObserved'], plan
            assert '.skillview-ignore' in plan['changedPaths'] and 'user-extra' in plan['changedPaths'], plan
            assert manifest(payload) == original and lock.read_bytes() == registry
            candidate = Path(plan['candidatePath'])
            approved = manifest(candidate)
            stage_identity = candidate.stat().st_ino
            run('restore-apply', '--id', plan['id'], '--yes', '--disposition', 'retain-original', '--expect', '0' * 64, ok=False)
            assert manifest(payload) == original
            applied = run('restore-apply', '--id', plan['id'], '--yes', '--disposition', 'retain-original', '--expect', plan['planFingerprint'])['operation']
            assert applied['succeeded'] and applied['status'] == 'installed', applied
            assert manifest(payload) == approved and payload.stat().st_ino == stage_identity
            retained = Path(applied['retainedPath'])
            assert manifest(retained) == original and retained.stat().st_ino == identity
            assert not payload.joinpath('user-extra').exists() and lock.read_bytes() == registry
            if shared_target:
                assert exposure.is_symlink() and manifest(exposure) == approved
                assert len(applied['subject']['agents']) >= 2, applied
            inspection = run('restore-inspect', '--id', plan['id'])['operation']
            restored = run('restore-undo', '--id', plan['id'], '--yes', '--expect', inspection['reviewFingerprint'])['operation']
            assert restored['succeeded'] and restored['status'] == 'restored', restored
            assert manifest(payload) == original and payload.stat().st_ino == identity
            assert manifest(Path(restored['displacedPath'])) == approved
            assert manifest(unrelated) == unrelated_before and lock.read_bytes() == registry
            if shared_target:
                assert exposure.is_symlink() and manifest(exposure) == original
            restart_plan = run('restore-plan', '--path', payload)['operation']
            command = [str(app), '--scan-root', str(first), '--scan-root', str(second), 'restore-apply',
                       '--id', restart_plan['id'], '--yes', '--disposition', 'retain-original',
                       '--expect', restart_plan['planFingerprint'], '--allow-hidden-dirs', '--json']
            process = subprocess.Popen(command, env=env, cwd=root, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            deadline = time.monotonic() + 60
            killed = False
            while time.monotonic() < deadline and process.poll() is None:
                if Path(restart_plan['retainedPath']).exists():
                    process.kill()
                    killed = True
                    break
                time.sleep(0.002)
            process.communicate(timeout=60)
            assert killed and process.returncode != 0, 'Native process did not stop in the retained-original interval'
            restarted = run('restore-inspect', '--id', restart_plan['id'])['operation']
            assert restarted['status'] in ('original-retained', 'installed'), restarted
            recovered = run('restore-recover', '--id', restart_plan['id'], '--yes', '--expect', restarted['reviewFingerprint'])['operation']
            assert recovered['succeeded'] and recovered['status'] == 'installed', recovered
            inspection = run('restore-inspect', '--id', restart_plan['id'])['operation']
            undone = run('restore-undo', '--id', restart_plan['id'], '--yes', '--expect', inspection['reviewFingerprint'])['operation']
            assert undone['succeeded'] and manifest(payload) == original and payload.stat().st_ino == identity
            assert lock.read_bytes() == registry and manifest(unrelated) == unrelated_before
            rows.append(dict(shared=shared_target, localEditsExtrasExclusionAndEmptyDirectoryRestored=True,
                             stagedNativeDirectoryPromoted=True, originalNativeDirectoryRestored=True,
                             registryBytesUnchanged=True, unrelatedSameNameUnchanged=True,
                             nativeProcessKillRestartAndUndoVerified=True, hiddenBuiltinCoverageObserved=True, pinnedRef=restored['subject']['metadata']['pinnedRef']))
        results.append(dict(nativeHost=str(app), gh=subprocess.check_output([str(gh), '--version'], text=True).splitlines()[0], fixtures=rows))
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(results, indent=2))
print(json.dumps(results, indent=2))
