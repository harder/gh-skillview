"""Actual-gh local merge CLI proof. All writes use disposable home/state/payloads."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import stat
import subprocess
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument('--app', required=True, type=Path)
parser.add_argument('--gh', action='append', required=True, type=Path)
parser.add_argument('--output', required=True, type=Path)
args = parser.parse_args()
app = args.app.resolve()
auth = os.environ.get('GH_TOKEN') or os.environ.get('GITHUB_TOKEN')
if not auth:
    auth = subprocess.run([str(args.gh[-1]), 'auth', 'token'], capture_output=True, text=True, check=True).stdout.strip()
old_commit = '33789149b9def6b66ddf247438a79fac94e22074'
results = []


def manifest(directory):
    rows = []
    for path in [directory, *sorted(directory.rglob('*'))]:
        native = path.lstat()
        kind = 'link' if path.is_symlink() else 'directory' if path.is_dir() else 'file'
        content = os.readlink(path).encode() if kind == 'link' else path.read_bytes() if kind == 'file' else b''
        attrs = sorted((name, hashlib.sha256(os.getxattr(path, name, follow_symlinks=False)).hexdigest())
                       for name in os.listxattr(path, follow_symlinks=False)) if hasattr(os, 'listxattr') else []
        rows.append((str(path.relative_to(directory)), kind, stat.S_IMODE(native.st_mode) if os.name != 'nt' else None,
                     hashlib.sha256(content).hexdigest(), attrs))
    return rows


for gh_path in args.gh:
    gh = gh_path.resolve()
    with tempfile.TemporaryDirectory(prefix='skillview-local-merge-proof-') as temporary:
        root = Path(temporary).resolve()
        home = root / 'home'
        home.mkdir()
        env = {key: value for key, value in os.environ.items()
               if key not in ('CODEX_HOME', 'CLAUDE_CONFIG_DIR', 'CLAUDE_CODE_PLUGIN_CACHE_DIR', 'CLAUDE_CODE_PLUGIN_DIRS', 'CLAUDE_CODE_PLUGIN_SEED_DIR')
               and not key.startswith('GIT_')}
        env.update(HOME=str(home), USERPROFILE=str(home), GH_TOKEN=auth, GH_EXTENSION='1', GH_PATH=str(gh),
                   GH_CONFIG_DIR=str(root / 'gh-config'), SKILLVIEW_STATE_HOME=str(root / 'state'),
                   XDG_CONFIG_HOME=str(root / 'config'), XDG_STATE_HOME=str(root / 'xdg-state'),
                   XDG_DATA_HOME=str(root / 'data'), XDG_CACHE_HOME=str(root / 'cache'),
                   APPDATA=str(root / 'appdata'), LOCALAPPDATA=str(root / 'localappdata'),
                   CLAUDE_CONFIG_DIR=str(home / '.claude'), PI_CODING_AGENT_DIR=str(home / '.pi' / 'agent'),
                   GH_TELEMETRY='false', GH_NO_UPDATE_NOTIFIER='1', GH_NO_EXTENSION_UPDATE_NOTIFIER='1')
        skills = root / 'skills'
        installed = subprocess.run([str(gh), 'skill', 'install', '--dir', str(skills), '--pin=' + old_commit,
                                    '--', 'cli/cli', 'skills/gh-skill'], env=env, cwd=root, capture_output=True, text=True, timeout=120)
        assert installed.returncode == 0, installed.stderr
        payload = skills / 'gh-skill'
        payload.joinpath('SKILL.md').write_bytes(payload.joinpath('SKILL.md').read_bytes() + b'\nReviewed local instructions\n')
        payload.joinpath('keep-local-extra').write_bytes(b'\x00\xffKeep exact local bytes')
        payload.joinpath('keep-empty').mkdir()
        if os.name != 'nt':
            payload.chmod(0o700)
        before = manifest(payload)
        original_id = payload.stat().st_ino
        registry = home / '.agents' / '.skill-lock.json'
        original_registry = registry.read_bytes()
        commands = []

        def run(command, *arguments, ok=True):
            process = subprocess.run([str(app), '--scan-root', str(skills), command, *map(str, arguments),
                                      '--allow-hidden-dirs', '--json'], env=env, cwd=root, capture_output=True, text=True, timeout=180)
            assert (process.returncode == 0) == ok, (command, process.returncode, process.stdout, process.stderr)
            result = json.loads(process.stdout)
            commands.append(dict(command=command, exitCode=process.returncode))
            return result

        source_plan = run('update-plan', '--path', payload, '--unpin')['operation']
        source_payload_fingerprint = json.loads(Path(source_plan['planPath']).read_text())['candidate']['payload']
        preserved = run('update-preserve', '--id', source_plan['id'], '--expect', source_plan['planFingerprint'])
        assert preserved['status'] == 'preserved' and not preserved['mutationObserved'], preserved
        assert manifest(payload) == before and registry.read_bytes() == original_registry
        source_path = Path(source_plan['candidatePath'])
        source_bytes = manifest(source_path)
        source_id = source_path.stat().st_ino
        review = run('update-merge-review', '--id', source_plan['id'], '--expect', source_plan['planFingerprint'])
        assert any(row['path'] == 'keep-local-extra' and row['selection'] == 'installed' for row in review['review']['paths']), review
        refused = run('update-apply', '--id', source_plan['id'], '--yes', '--disposition', 'retain-original',
                      '--expect', source_plan['planFingerprint'], ok=False)
        assert commands[-1]['exitCode'] == 1 and not refused['succeeded'] and not refused['canceled'], (commands[-1], refused.get('error'))
        assert 'earlier replacement approval is closed' in refused['error'].lower(), refused
        assert manifest(payload) == before and registry.read_bytes() == original_registry
        chosen = run('update-merge-choose', '--id', source_plan['id'], '--expect', review['reviewFingerprint'], '--choose-installed', 'SKILL.md')
        assert chosen['canMaterialize'], chosen
        repeat = run('update-merge-review', '--id', source_plan['id'], '--expect', source_plan['planFingerprint'])
        assert repeat['reviewFingerprint'] == chosen['reviewFingerprint']
        merged = run('update-merge-plan', '--id', source_plan['id'], '--expect', chosen['reviewFingerprint'])
        assert merged['localDisposition']['sourcePlanFingerprint'] == source_plan['planFingerprint']
        assert merged['localDisposition']['sourceCandidate']['payload'] == source_payload_fingerprint
        assert source_plan['revision']['source']['tree'] != source_plan['subject']['metadata']['githubTreeSha']
        assert manifest(payload) == before and registry.read_bytes() == original_registry
        merged_bytes = manifest(Path(merged['candidatePath']))
        merged_id = Path(merged['candidatePath']).stat().st_ino
        refused = run('update-apply', '--id', merged['id'], '--yes', '--disposition', 'retain-original', '--expect', merged['planFingerprint'], ok=False)
        assert commands[-1]['exitCode'] == 1 and not refused['succeeded'] and not refused['canceled'], (commands[-1], refused.get('error'))
        assert 'exact saved disposition: merge-reviewed' in refused['error'].lower(), refused
        assert manifest(payload) == before and registry.read_bytes() == original_registry
        applied = run('update-apply', '--id', merged['id'], '--yes', '--disposition', 'merge-reviewed', '--expect', merged['planFingerprint'])['operation']
        assert applied['succeeded'] and applied['status'] == 'installed', applied
        assert manifest(payload) == merged_bytes and payload.stat().st_ino == merged_id
        assert b'Reviewed local instructions' in payload.joinpath('SKILL.md').read_bytes()
        assert payload.joinpath('keep-local-extra').read_bytes() == b'\x00\xffKeep exact local bytes'
        assert payload.joinpath('keep-empty').is_dir()
        assert source_path.stat().st_ino == source_id and manifest(source_path) == source_bytes
        assert registry.read_bytes() == original_registry
        next_plan = run('update-plan', '--path', payload)['operation']
        assert next_plan['revision']['source']['tree'] == source_plan['revision']['source']['tree']
        assert next_plan['revision']['recordedBase']['payload'] == source_payload_fingerprint
        assert 'SKILL.md' in next_plan['revision']['localChanges'] and 'keep-local-extra' in next_plan['revision']['localChanges']
        inspection = run('update-inspect', '--id', merged['id'])['operation']
        undone = run('update-undo', '--id', merged['id'], '--yes', '--expect', inspection['reviewFingerprint'])['operation']
        assert undone['succeeded'] and undone['status'] == 'restored', undone
        assert manifest(payload) == before and payload.stat().st_ino == original_id
        assert manifest(Path(undone['displacedPath'])) == merged_bytes
        assert registry.read_bytes() == original_registry and manifest(source_path) == source_bytes
        results.append(dict(host=str(app), gh=subprocess.check_output([str(gh), '--version'], text=True).splitlines()[0],
                            sourceRevision=source_plan['revision']['source'], commands=commands, preserveVerified=True,
                            immutableReviewRestartVerified=True, priorApprovalRefused=True, wrongMergedDispositionRefused=True, mergedNativeStagePromoted=True,
                            localBytesAndEmptyDirectoryPreserved=True, sourceBaselineUnchanged=True,
                            nextUpdateUsesRealSourceBaseline=True,
                            exactOriginalNativeObjectRestored=True, registryBytesUnchanged=True))
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(results, indent=2))
print(json.dumps(dict(hosts=len(results), commands=sum(len(item['commands']) for item in results), output=str(args.output))))
