"""Real gh shared update, raw-image recovery and process-kill/restart proof.

The proof-only host pauses at production checkpoints. The product CLI performs
all planning, inspection, repair, recovery and undo in a disposable HOME/CWD.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import tempfile
import threading
import time

parser = argparse.ArgumentParser()
parser.add_argument('--app', required=True, type=Path)
parser.add_argument('--proof-host', required=True, type=Path)
parser.add_argument('--gh', required=True, action='append', type=Path)
parser.add_argument('--output', required=True, type=Path)
args = parser.parse_args()
app, host = args.app.resolve(), args.proof_host.resolve()
auth = os.environ.get('GH_TOKEN') or os.environ.get('GITHUB_TOKEN')
if not auth:
    auth = subprocess.run([str(args.gh[-1]), 'auth', 'token', '--hostname', 'github.com'],
                          capture_output=True, text=True, check=True).stdout.strip()
old_commit = '33789149b9def6b66ddf247438a79fac94e22074'


def manifest(directory):
    entries = []
    for path in [directory, *sorted(directory.rglob('*'))]:
        information = path.lstat()
        kind = 'link' if path.is_symlink() else 'directory' if path.is_dir() else 'file'
        value = os.readlink(path).encode() if kind == 'link' else path.read_bytes() if kind == 'file' else b''
        entries.append((str(path.relative_to(directory)), kind, stat.S_IMODE(information.st_mode),
                        hashlib.sha256(value).hexdigest(), information.st_dev, information.st_ino))
    return entries


def prove_guard():
    with tempfile.TemporaryDirectory(prefix='skillview-proof-guard-') as temporary:
        base = Path(temporary).resolve()
        root, outside = base / 'workspace', base / 'outside'
        root.mkdir()
        outside.mkdir()
        outside.joinpath('sentinel').write_text('Unchanged outside authority\n')
        root.joinpath('disposable-proof-marker').write_bytes(b'SkillView disposable process proof\n')
        for name in ('home', 'state'):
            root.joinpath(name).mkdir()
            root.joinpath(name + '-link').symlink_to(outside, target_is_directory=True)
        before = manifest(outside)
        for linked in (None, 'home', 'state'):
            env = {key: value for key, value in os.environ.items() if not key.startswith('GIT_')}
            home = root / ('home-link' if linked == 'home' else 'home')
            state = root / ('state-link' if linked == 'state' else 'state')
            env.update(HOME=str(home), USERPROFILE=str(home), SKILLVIEW_STATE_HOME=str(state),
                       DOTNET_CLI_HOME=str(root / 'dotnet-home'))
            result = subprocess.run([str(host), str(root), str(args.gh[-1].resolve()), '0' * 32,
                                     '0' * 64, 'apply', 'registry-truncated'], env=env, cwd=root,
                                    capture_output=True, text=True, timeout=20)
            assert result.returncode == 2, (result.returncode, result.stdout, result.stderr)
            if linked is None:
                assert 'maintenance-proof:' in result.stderr and 'marked disposable' not in result.stderr, result.stderr
            else:
                assert 'marked disposable' in result.stderr, result.stderr
            assert manifest(outside) == before


prove_guard()
results = []
for gh_path in args.gh:
    gh = gh_path.resolve()
    with tempfile.TemporaryDirectory(prefix='skillview-shared-update-proof-') as temporary:
        root = Path(temporary).resolve()
        home = root / 'home'
        home.mkdir()
        root.joinpath('disposable-proof-marker').write_bytes(b'SkillView disposable process proof\n')
        env = {key: value for key, value in os.environ.items() if not key.startswith('GIT_')}
        env.update(HOME=str(home), USERPROFILE=str(home), GH_TOKEN=auth, GH_EXTENSION='1', GH_PATH=str(gh),
                   GH_CONFIG_DIR=str(root / 'gh-config'), SKILLVIEW_STATE_HOME=str(root / 'state'),
                   XDG_CONFIG_HOME=str(root / 'config'), XDG_STATE_HOME=str(root / 'xdg-state'),
                   XDG_DATA_HOME=str(root / 'data'), XDG_CACHE_HOME=str(root / 'cache'),
                   APPDATA=str(root / 'appdata'), LOCALAPPDATA=str(root / 'localappdata'),
                   CLAUDE_CONFIG_DIR=str(home / '.claude'), PI_CODING_AGENT_DIR=str(home / '.pi' / 'agent'),
                   GH_TELEMETRY='false', GH_NO_UPDATE_NOTIFIER='1', GH_NO_EXTENSION_UPDATE_NOTIFIER='1')
        custom = root / 'custom'
        for placement in (['--dir', str(custom)], ['--agent', 'universal', '--scope', 'user']):
            installed = subprocess.run([str(gh), 'skill', 'install', *placement, '--pin=' + old_commit,
                                        '--', 'cli/cli', 'skills/gh-skill'], env=env, cwd=root,
                                       capture_output=True, text=True, timeout=120)
            assert installed.returncode == 0, installed.stderr
        payload = home / '.agents' / 'skills' / 'gh-skill'
        exposure = home / '.claude' / 'skills' / 'gh-skill'
        exposure.parent.mkdir(parents=True)
        exposure.symlink_to(payload, target_is_directory=True)
        registry_path = home / '.agents' / '.skill-lock.json'
        original = json.loads(registry_path.read_bytes())
        original['extension'] = {'keep': True}
        original['skills']['gh-skill']['selectedExtension'] = {'keep': True}
        original['skills']['unrelated'] = {'source': 'other/repo', 'sourceType': 'github', 'extension': [1, 2]}
        registry_path.write_bytes((json.dumps(original, indent=2) + '\n').encode('utf-8'))
        payload.joinpath('SKILL.md').write_bytes(payload.joinpath('SKILL.md').read_bytes() + b'\nUser modifications\n')
        payload.joinpath('user-extra').write_bytes(b'\x00\xffKeep extra')
        original_payload, unrelated_payload = manifest(payload), manifest(custom / 'gh-skill')
        original_exposure = exposure.lstat().st_ino

        def run(command, *arguments, ok=True):
            process = subprocess.run([str(app), '--scan-root', str(custom), command, *map(str, arguments),
                                      '--allow-hidden-dirs', '--json'], env=env, cwd=root,
                                     capture_output=True, text=True, timeout=180)
            assert (process.returncode == 0) == ok, (process.returncode, process.stdout, process.stderr)
            return json.loads(process.stdout)

        def plan():
            preparation = run('update', '--path', payload, '--unpin', '--force')
            assert not preparation['mutationAttempted']
            assert preparation['outcomes'][0]['status'] == 'review-required'
            result = preparation['savedPlan']
            assert preparation['applyArguments'] == ['update-apply', '--id', result['id'], '--expect', result['planFingerprint'], '--yes', '--disposition', 'retain-original', '--allow-hidden-dirs']
            assert manifest(payload) == original_payload
            assert manifest(custom / 'gh-skill') == unrelated_payload
            assert result['revision']['owner']['selector'] == 'gh-skill', result
            assert result['revision']['source']['tree'] != result['subject']['metadata']['githubTreeSha']
            assert len(result['subject']['agents']) >= 2
            assert 'user-extra' in result['revision']['localChanges']
            result['_approvedCandidate'] = manifest(Path(result['candidatePath']))
            return result

        def inspect(saved):
            return run('update-inspect', '--id', saved['id'])['operation']

        def transition(command, saved, reviewed):
            return run(command, '--id', saved['id'], '--yes', '--expect', reviewed['reviewFingerprint'])['operation']

        def kill_at(saved, action, checkpoint, expected):
            process = subprocess.Popen([str(host), str(root), str(gh), saved['id'], expected, action, checkpoint],
                                       env=env, cwd=root, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                       stderr=subprocess.STDOUT, text=True)
            ready = threading.Event()
            exited = threading.Event()
            output = []

            def consume():
                try:
                    for line in process.stdout:
                        if len(output) < 128:
                            output.append(line[:4096])
                        if line.strip() == 'READY ' + checkpoint:
                            ready.set()
                finally:
                    exited.set()

            reader = threading.Thread(target=consume, daemon=True)
            reader.start()
            try:
                deadline = time.monotonic() + 60
                while not ready.wait(0.05) and not exited.is_set() and time.monotonic() < deadline:
                    pass
                assert ready.is_set(), ('Proof host missed ' + action + '/' + checkpoint
                                        + '; exit=' + str(process.poll()) + ': ' + ''.join(output))
                assert process.poll() is None, 'Proof host exited before process kill'
                process.kill()
                assert process.wait(timeout=15) != 0
                reader.join(timeout=5)
                assert not reader.is_alive()
            finally:
                if process.poll() is None:
                    process.kill()
                    process.wait(timeout=15)
                process.stdin.close()
                reader.join(timeout=5)
                process.stdout.close()

        def assert_original():
            assert manifest(payload) == original_payload
            assert manifest(custom / 'gh-skill') == unrelated_payload
            assert exposure.is_symlink() and exposure.lstat().st_ino == original_exposure
            assert exposure.resolve() == payload

        saved = plan()
        # Change one unrelated field after planning, without touching the
        # selected entry; initial apply must preserve this fresh manager work.
        current = json.loads(registry_path.read_bytes())
        current['skills']['unrelated']['extension'] = [1, 2, 3]
        registry_path.write_bytes((json.dumps(current, indent=2) + '\n').encode('utf-8'))
        preimage = registry_path.read_bytes()
        applied = run('update-apply', '--id', saved['id'], '--yes', '--disposition', 'retain-original',
                      '--expect', saved['planFingerprint'])['operation']
        assert applied['succeeded'] and applied['owner']['state'] == 'desired', applied
        assert manifest(payload) == saved['_approvedCandidate']
        updated = json.loads(registry_path.read_bytes())
        selected = updated['skills']['gh-skill']
        assert 'pinnedRef' not in selected and selected['selectedExtension'] == {'keep': True}
        assert selected['skillFolderHash'] == saved['revision']['source']['tree']
        assert selected['installedAt'] == original['skills']['gh-skill']['installedAt']
        assert updated['skills']['unrelated']['extension'] == [1, 2, 3]
        assert manifest(custom / 'gh-skill') == unrelated_payload
        before_extension = b'"extension": [\n        1,\n        2,\n        3\n'
        after_extension = b'"extension": [\n        1,\n        2,\n        3,\n        4\n'
        live_bytes = registry_path.read_bytes()
        assert live_bytes.count(before_extension) == preimage.count(before_extension) == 1
        registry_path.write_bytes(live_bytes.replace(before_extension, after_extension))
        expected_undo = preimage.replace(before_extension, after_extension)
        assert json.loads(registry_path.read_bytes())['skills']['unrelated']['extension'] == [1, 2, 3, 4]
        assert transition('update-undo', saved, inspect(saved))['succeeded']
        assert registry_path.read_bytes() == expected_undo
        assert_original()

        rows = []
        for action, checkpoint, tear, undo in [
                ('apply', 'retain-moved', False, False),
                ('apply', 'registry-truncated', True, False),
                ('apply', 'compensation-truncated', True, False),
                ('undo', 'registry-truncated', True, True),
                ('repair', 'registry-truncated', True, False),
                ('repair', 'owner-repair-intent', True, False)]:
            saved = plan()
            before = registry_path.read_bytes()
            if undo:
                assert run('update-apply', '--id', saved['id'], '--yes', '--disposition', 'retain-original',
                           '--expect', saved['planFingerprint'])['operation']['succeeded']
            if action == 'repair':
                kill_at(saved, 'apply', 'registry-truncated', saved['planFingerprint'])
            expected = saved['planFingerprint'] if action == 'apply' else inspect(saved)['reviewFingerprint']
            kill_at(saved, action, checkpoint, expected)
            reviewed = inspect(saved)
            if tear:
                assert registry_path.read_bytes() == b''
                assert reviewed['owner']['state'] == 'torn' and reviewed['owner']['repair']
                unchanged_slot = manifest(payload)
                repaired = transition('update-repair-owner', saved, reviewed)
                assert repaired['succeeded'] and repaired['status'] == 'owner-repaired', repaired
                assert manifest(payload) == unchanged_slot
                reviewed = inspect(saved)
            recovered = transition('update-undo' if undo else 'update-recover', saved, reviewed)
            assert recovered['succeeded'], recovered
            if not undo:
                assert manifest(payload) == saved['_approvedCandidate']
                assert transition('update-undo', saved, inspect(saved))['succeeded']
            assert registry_path.read_bytes() == before
            assert_original()
            rows.append(dict(action=action, checkpoint=checkpoint, actualProcessKilled=True,
                             tornImageExplicitlyRepaired=tear, registryBytesRestored=True,
                             originalNativePayloadAndExposureRestored=True, unrelatedSameNameUnchanged=True))
        results.append(dict(host=str(app), proofHost=str(host),
                            gh=subprocess.check_output([str(gh), '--version'], text=True).splitlines()[0],
                            canonicalDisposableHomeAndStateGuardsVerified=True,
                            freshUnrelatedRegistryChangesPreserved=True, selectedExtensionsAndInstalledAtPreserved=True,
                            explicitUnpinAndExactImmutableCandidate=True, fixtures=rows))
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(results, indent=2))
print(json.dumps(results, indent=2))
