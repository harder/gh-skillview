"""Saved newer-revision CLI proof. All payload, home and state writes are disposable."""
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
old_commit = '33789149b9def6b66ddf247438a79fac94e22074'
results = []


from source_review_proof import acknowledge_frozen_source

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
    with tempfile.TemporaryDirectory(prefix='skillview-update-proof-') as temporary:
        root = Path(temporary).resolve()
        home = root / 'home'
        home.mkdir()
        env = {key: value for key, value in os.environ.items() if key not in ('CODEX_HOME', 'CLAUDE_CONFIG_DIR', 'CLAUDE_CODE_PLUGIN_CACHE_DIR', 'CLAUDE_CODE_PLUGIN_DIRS', 'CLAUDE_CODE_PLUGIN_SEED_DIR') if not key.startswith('GIT_')}
        env.update(HOME=str(home), USERPROFILE=str(home), GH_TOKEN=auth, GH_EXTENSION='1', GH_PATH=str(gh),
                   GH_CONFIG_DIR=str(root / 'gh-config'), SKILLVIEW_STATE_HOME=str(root / 'state'),
                   XDG_CONFIG_HOME=str(root / 'config'), XDG_STATE_HOME=str(root / 'xdg-state'),
                   XDG_DATA_HOME=str(root / 'data'), XDG_CACHE_HOME=str(root / 'cache'),
                   APPDATA=str(root / 'appdata'), LOCALAPPDATA=str(root / 'localappdata'),
                   CLAUDE_CONFIG_DIR=str(home / '.claude'), PI_CODING_AGENT_DIR=str(home / '.pi' / 'agent'),
                   GH_TELEMETRY='false', GH_NO_UPDATE_NOTIFIER='1', GH_NO_EXTENSION_UPDATE_NOTIFIER='1')
        first, second = root / 'first', root / 'second'
        for directory in (first, second):
            process = subprocess.run([str(gh), 'skill', 'install', '--dir', str(directory), '--pin=' + old_commit,
                                      '--', 'cli/cli', 'skills/gh-skill'], env=env, cwd=root, capture_output=True, text=True, timeout=120)
            assert process.returncode == 0, process.stderr
        registry_path = home / '.agents' / '.skill-lock.json'
        registry = registry_path.read_bytes()
        unrelated = second / 'gh-skill'
        unrelated_before = manifest(unrelated)
        namespace = first / 'publisher' / 'collection'
        namespace.mkdir(parents=True)
        namespace.joinpath('README.md').write_text('Namespace documentation\n')
        namespaced_payload = namespace / 'gh-skill'
        shutil.copytree(unrelated, namespaced_payload)
        rows = []

        def run(command, *arguments, ok=True):
            process = subprocess.run([str(app), '--scan-root', str(first), '--scan-root', str(second), command,
                                      *map(str, arguments), '--allow-hidden-dirs', '--json'], env=env, cwd=root, capture_output=True, text=True, timeout=180)
            assert (process.returncode == 0) == ok, (process.returncode, process.stdout, process.stderr)
            return json.loads(process.stdout)

        for payload in (first / 'gh-skill', namespaced_payload):
            payload.joinpath('SKILL.md').write_bytes(payload.joinpath('SKILL.md').read_bytes() + b'\nUser modifications\n')
            payload.joinpath('user-extra').write_bytes(b'\x00\xffKeep extra')
            payload.joinpath('empty-user-directory').mkdir()
            original = manifest(payload)
            identity = payload.stat().st_ino
            refused = run('update-plan', '--path', payload, ok=False)
            assert not refused['succeeded'] and 'pin' in refused['error'].lower(), refused
            assert manifest(payload) == original and registry_path.read_bytes() == registry
            preparation = run('update', '--path', payload, '--unpin', '--force')
            assert not preparation['mutationAttempted']
            assert preparation['outcomes'][0]['status'] == 'review-required'
            plan = preparation['savedPlan']
            assert plan['status'] == 'planned' and not plan['mutationObserved'], plan
            revision = plan['revision']
            assert len(revision['source']['commit']) == 40 and len(revision['source']['tree']) == 40
            assert revision['source']['subpath'] == 'skills/gh-skill'
            assert revision['desiredMetadata']['pinnedRef'] is None and not revision['desiredMetadata']['pinned']
            assert revision['desiredMetadata']['metadata']['github-tree-sha'] == revision['source']['tree']
            assert revision['desiredMetadata']['metadata']['github-ref'] == revision['source']['ref']
            assert 'github-pinned' not in revision['desiredMetadata']['metadata']
            assert 'SKILL.md' in revision['localChanges'] and 'user-extra' in revision['localChanges']
            assert revision['source']['tree'] != plan['subject']['metadata']['githubTreeSha'], 'Fixture requires an actual available update'
            assert manifest(payload) == original and registry_path.read_bytes() == registry
            acknowledge_frozen_source(run, plan)
            approved = manifest(Path(plan['candidatePath']))
            stage_identity = Path(plan['candidatePath']).stat().st_ino
            run('update-apply', '--id', plan['id'], '--yes', '--disposition', 'retain-original', '--expect', '0' * 64, ok=False)
            assert manifest(payload) == original
            applied = run('update-apply', '--id', plan['id'], '--yes', '--disposition', 'retain-original', '--expect', plan['planFingerprint'])['operation']
            assert applied['succeeded'] and applied['status'] == 'installed', applied
            assert manifest(payload) == approved and payload.stat().st_ino == stage_identity
            assert manifest(Path(applied['retainedPath'])) == original
            assert registry_path.read_bytes() == registry and manifest(unrelated) == unrelated_before
            inspection = run('update-inspect', '--id', plan['id'])['operation']
            undone = run('update-undo', '--id', plan['id'], '--yes', '--expect', inspection['reviewFingerprint'])['operation']
            assert undone['succeeded'] and undone['status'] == 'restored', undone
            assert manifest(payload) == original and payload.stat().st_ino == identity
            restart_plan = run('update-plan', '--path', payload, '--unpin')['operation']
            acknowledge_frozen_source(run, restart_plan)
            process = subprocess.Popen([str(app), '--scan-root', str(first), '--scan-root', str(second), 'update-apply',
                                        '--id', restart_plan['id'], '--yes', '--disposition', 'retain-original',
                                        '--expect', restart_plan['planFingerprint'], '--allow-hidden-dirs', '--json'],
                                       env=env, cwd=root, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            try:
                deadline = time.monotonic() + 60
                killed = False
                while time.monotonic() < deadline and process.poll() is None:
                    if Path(restart_plan['retainedPath']).exists():
                        process.kill()
                        killed = True
                        break
                    time.sleep(0.002)
                process.communicate(timeout=60)
                assert killed and process.returncode != 0, 'Process did not stop in the retained-original interval'
            finally:
                if process.poll() is None:
                    process.kill()
                    process.communicate(timeout=60)
            inspection = run('update-inspect', '--id', restart_plan['id'])['operation']
            recovered = run('update-recover', '--id', restart_plan['id'], '--yes', '--expect', inspection['reviewFingerprint'])['operation']
            assert recovered['succeeded'] and recovered['status'] == 'installed', recovered
            inspection = run('update-inspect', '--id', restart_plan['id'])['operation']
            undone = run('update-undo', '--id', restart_plan['id'], '--yes', '--expect', inspection['reviewFingerprint'])['operation']
            assert undone['succeeded'] and manifest(payload) == original and payload.stat().st_ino == identity
            assert registry_path.read_bytes() == registry and manifest(unrelated) == unrelated_before
            rows.append(dict(namespaced=payload == namespaced_payload, oldCommit=old_commit, approvedRevision=revision['source'],
                             explicitUnpinVerified=True, localChangesReviewedAndOriginalRestored=True,
                             approvedNativeStagePromoted=True, originalNativeDirectoryRestored=True,
                             registryBytesUnchanged=True, unrelatedSameNameUnchanged=True, processKillRestartRecoveryAndUndo=True))
        results.append(dict(host=str(app), gh=subprocess.check_output([str(gh), '--version'], text=True).splitlines()[0], fixtures=rows))
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(results, indent=2))
print(json.dumps(results, indent=2))
