"""Real-gh/native-host update outcomes; every filesystem write is disposable."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import tempfile

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


def snapshot(directory):
    entries = []
    for path in [directory, *sorted(directory.rglob('*'))]:
        info = path.lstat()
        kind = 'link' if path.is_symlink() else 'directory' if path.is_dir() else 'file'
        data = os.readlink(path).encode() if kind == 'link' else path.read_bytes() if kind == 'file' else b''
        entries.append(dict(path='/' if path == directory else str(path.relative_to(directory)), kind=kind,
                            device=info.st_dev, inode=info.st_ino,
                            mode=stat.S_IMODE(info.st_mode) if os.name != 'nt' else None,
                            sha256=hashlib.sha256(data).hexdigest()))
    return entries


for gh_path in gh_paths:
    gh = gh_path.resolve()
    with tempfile.TemporaryDirectory(prefix='skillview-outcome-proof-') as temporary:
        root = Path(temporary).resolve()
        home = root / 'home'
        home.mkdir()
        env = {key: value for key, value in os.environ.items() if key not in ('CODEX_HOME', 'CLAUDE_CONFIG_DIR', 'CLAUDE_CODE_PLUGIN_CACHE_DIR', 'CLAUDE_CODE_PLUGIN_DIRS', 'CLAUDE_CODE_PLUGIN_SEED_DIR') if not key.startswith(('GIT_', 'SKILLVIEW_'))}
        env.update(HOME=str(home), USERPROFILE=str(home), GH_TOKEN=auth, GH_EXTENSION='1', GH_PATH=str(gh),
                   GH_CONFIG_DIR=str(root / 'gh-config'), SKILLVIEW_STATE_HOME=str(root / 'state'),
                   XDG_CONFIG_HOME=str(root / 'config'), XDG_STATE_HOME=str(root / 'xdg-state'),
                   XDG_DATA_HOME=str(root / 'data'), XDG_CACHE_HOME=str(root / 'cache'),
                   APPDATA=str(root / 'appdata'), LOCALAPPDATA=str(root / 'localappdata'),
                   CLAUDE_CONFIG_DIR=str(home / '.claude'), PI_CODING_AGENT_DIR=str(home / '.pi' / 'agent'),
                   GIT_CONFIG_COUNT='0', GH_TELEMETRY='false', GH_NO_UPDATE_NOTIFIER='1', GH_NO_EXTENSION_UPDATE_NOTIFIER='1')
        custom = root / 'custom'

        def execute(command):
            process = subprocess.run(list(map(str, command)), env=env, cwd=root, capture_output=True, text=True, timeout=180)
            assert process.returncode == 0, (process.returncode, process.stdout, process.stderr)
            return process.stdout

        def run(command, *arguments, ok=True):
            process = subprocess.run([str(app), '--scan-root', str(custom), command, *map(str, arguments),
                                      '--allow-hidden-dirs', '--json'], env=env, cwd=root, capture_output=True, text=True, timeout=180)
            assert (process.returncode == 0) == ok, (process.returncode, process.stdout, process.stderr)
            return json.loads(process.stdout)

        execute([gh, 'skill', 'install', '--agent', 'universal', '--scope', 'user', '--pin=' + old_commit,
                 '--', 'cli/cli', 'skills/gh-skill'])
        execute([gh, 'skill', 'install', '--dir', custom, '--pin=' + old_commit, '--', 'cli/cli', 'skills/gh-skill'])
        payload = home / '.agents' / 'skills' / 'gh-skill'
        second = custom / 'gh-skill'
        registry = home / '.agents' / '.skill-lock.json'
        initial, initial_registry = snapshot(payload), registry.read_bytes()
        pinned = run('update', '--path', payload)
        assert pinned['outcomes'][0]['status'] == 'pinned' and not pinned['mutationAttempted']
        assert snapshot(payload) == initial and registry.read_bytes() == initial_registry
        local = custom / 'local-only'
        local.mkdir()
        local.joinpath('SKILL.md').write_text('---\nname: local-only\ndescription: Untracked fixture\n---\nLocal instructions\n')
        untracked_before = snapshot(local)
        untracked = run('update', '--path', local)
        assert untracked['outcomes'][0]['status'] == 'untracked' and not untracked['mutationAttempted']
        assert snapshot(local) == untracked_before
        prepared = run('update', '--path', payload, '--unpin')
        plan = prepared['savedPlan']
        assert plan and prepared['outcomes'][0]['status'] == 'review-required' and not prepared['mutationAttempted']
        applied = run('update-apply', '--id', plan['id'], '--yes', '--disposition', 'retain-original', '--expect', plan['planFingerprint'])['operation']
        assert applied['status'] == 'installed' and applied['mutationObserved'] and applied['changedPaths']
        assert snapshot(Path(applied['retainedPath'])) == initial
        fresh_root = custom / 'fresh-current'
        execute([gh, 'skill', 'install', '--dir', fresh_root, '--', 'cli/cli', 'skills/gh-skill'])
        fresh = fresh_root / 'gh-skill'
        fresh_before = snapshot(fresh)
        current, current_registry = snapshot(payload), registry.read_bytes()
        # Two separate fresh checks must consume no saved-plan slots and retain
        # native objects as well as bytes, metadata, owner timestamp and modes.
        saved_before = sorted((root / 'state').rglob('plan.json'))
        store = Path(plan['planPath']).parent.parent
        store_entries = sorted(path.name for path in store.iterdir())
        for _ in range(2):
            noop = run('update', '--path', payload, '--force')
            assert noop['savedPlan'] is None and noop['outcomes'][0]['status'] == 'unchanged'
            assert noop['outcomes'][0]['changedFileCount'] == 0 and not noop['mutationAttempted']
            assert noop['outcomes'][0]['impact']['payloadRetained'], noop
            assert Path(noop['outcomes'][0]['impact']['retainedPayloadPath']).samefile(payload), noop
            assert snapshot(payload) == current and registry.read_bytes() == current_registry
            assert sorted((root / 'state').rglob('plan.json')) == saved_before
            assert sorted(path.name for path in store.iterdir()) == store_entries
        direct = run('update-plan', '--path', payload)
        assert direct['operation'] is None and direct['unchanged']['cleanupWarning'] is None
        assert direct['impact']['payloadRetained'], direct
        assert Path(direct['impact']['retainedPayloadPath']).samefile(payload), direct
        assert 'no-op' in direct['impact']['retentionEvidence'], direct
        assert snapshot(payload) == current and registry.read_bytes() == current_registry
        assert sorted(path.name for path in store.iterdir()) == store_entries
        fresh_check = run('update', '--path', fresh)
        assert fresh_check['savedPlan'] is None and fresh_check['outcomes'][0]['status'] == 'unchanged', fresh_check
        assert not fresh_check['mutationAttempted'] and snapshot(fresh) == fresh_before
        assert registry.read_bytes() == current_registry
        assert sorted(path.name for path in store.iterdir()) == store_entries
        # Restore local edits through the ordinary saved-update path even though
        # the immutable upstream tree is exactly the revision already installed.
        payload.joinpath('SKILL.md').write_bytes(payload.joinpath('SKILL.md').read_bytes() + b'\nUser edit\n')
        payload.joinpath('user-extra').write_bytes(b'Keep local extra\x00\xff')
        edited = snapshot(payload)
        repair = run('update', '--path', payload, '--force')
        repair_plan = repair['savedPlan']
        assert repair_plan and repair_plan['revision']['source']['tree'] == plan['revision']['source']['tree']
        assert {'SKILL.md', 'user-extra'}.issubset(repair_plan['changedPaths'])
        assert snapshot(payload) == edited
        repaired = run('update-apply', '--id', repair_plan['id'], '--yes', '--disposition', 'retain-original',
                       '--expect', repair_plan['planFingerprint'])['operation']
        assert repaired['mutationObserved'] and {'SKILL.md', 'user-extra'}.issubset(repaired['changedPaths'])
        assert snapshot(Path(repaired['retainedPath'])) == edited
        assert registry.read_bytes() == current_registry
        verified_first = snapshot(payload)
        # A second real gh installation now points at an absent source. Its
        # failed request cannot erase the first verified change or its evidence.
        markdown = second / 'SKILL.md'
        text = re.sub(r'(?m)^([ \t]+github-repo:).*$', r'\1 https://github.com/harder/skillview-missing-proof-' + root.name, markdown.read_text())
        markdown.write_text(text)
        second_before = snapshot(second)
        failed = run('update', '--path', second, '--unpin', ok=False)
        assert failed['outcomes'][0]['status'] == 'refused' and not failed['mutationAttempted']
        assert '404' in failed['stdout'] and '404' in failed['stderr'], failed
        assert snapshot(payload) == verified_first and snapshot(second) == second_before
        assert registry.read_bytes() == current_registry and snapshot(local) == untracked_before
        assert Path(repaired['planPath']).is_file() and Path(applied['planPath']).is_file()
        results.append(dict(nativeHost=str(app), nativeSha256=hashlib.sha256(app.read_bytes()).hexdigest(),
                            gh=execute([gh, '--version']).splitlines()[0], fixtures=[
            dict(kind='pinned', exactPayloadAndRegistryPreserved=True),
            dict(kind='untracked', exactPayloadPreserved=True),
            dict(kind='updated', changedPaths=applied['changedPaths'], retainedOriginalVerified=True),
            dict(kind='no-op', repeatedChecks=3, rawGhAlreadyCurrentVerified=True,
                 nativeObjectsAndRegistryBytesPreserved=True, noPlanQuotaConsumed=True, directPlanRetainsOriginalPayload=True),
            dict(kind='force-repair-same-tree', tree=repair_plan['revision']['source']['tree'], changedPaths=repaired['changedPaths'],
                 editedOriginalRetained=True, ownerBytesPreserved=True),
            dict(kind='later-remote-failure', stdout=failed['stdout'], stderr=failed['stderr'],
                 earlierMutationAndPlansPreserved=True, failedTargetObjectsPreserved=True)]))
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(results, indent=2) + '\n')
print(json.dumps(results, indent=2))
