"""Real gh shared update, raw-image recovery and process-kill/restart proof.

The proof-only host pauses at production checkpoints. The product CLI performs
all planning, inspection, repair, recovery and undo in a disposable HOME/CWD.
Opt-in --merged keeps reviewed local bytes while C remains the source baseline
and desired shared-owner revision throughout the same restart/recovery matrix.
"""
import argparse
import base64
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
parser.add_argument('--merged', action='store_true', help='Review and preserve local bytes in a schema3 shared-owner merge, then run the same recovery matrix.')
args = parser.parse_args()
app, host = args.app.resolve(), args.proof_host.resolve()
apply_disposition = 'merge-reviewed' if args.merged else 'retain-original'
apply_action = 'apply-merged' if args.merged else 'apply'
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


def prepare_windows_fixture_owner(path):
    """Change only disposable payload objects before their baseline is captured."""
    if os.name != 'nt':
        return False
    import ctypes
    from ctypes import wintypes
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    security = ctypes.WinDLL('advapi32', use_last_error=True)
    kernel.GetCurrentProcess.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    security.OpenProcessToken.argtypes = [wintypes.HANDLE, wintypes.DWORD, ctypes.POINTER(wintypes.HANDLE)]
    security.GetTokenInformation.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
    security.SetSecurityInfo.argtypes = [wintypes.HANDLE, ctypes.c_int, wintypes.DWORD, *([ctypes.c_void_p] * 4)]
    security.SetSecurityInfo.restype = wintypes.DWORD
    kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
                                  wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    kernel.CreateFileW.restype = wintypes.HANDLE
    token = wintypes.HANDLE()
    assert security.OpenProcessToken(kernel.GetCurrentProcess(), 8, ctypes.byref(token)), ctypes.get_last_error()
    try:
        length = wintypes.DWORD()
        security.GetTokenInformation(token, 1, None, 0, ctypes.byref(length))
        assert 0 < length.value <= 65536
        information = ctypes.create_string_buffer(length.value)
        assert security.GetTokenInformation(token, 1, information, length.value, ctypes.byref(length)), ctypes.get_last_error()
        sid = ctypes.cast(information, ctypes.POINTER(ctypes.c_void_p)).contents.value
        targets = [path, *sorted(path.rglob('*'))] if path.is_dir() else [path]
        for target in targets:
            assert not target.lstat().st_file_attributes & 0x400, 'Unexpected reparse point in disposable ownership fixture'
            handle = kernel.CreateFileW(str(target), 0x80000, 7, None, 3, 0x02200000, None)
            assert handle != ctypes.c_void_p(-1).value, ctypes.get_last_error()
            try:
                error = security.SetSecurityInfo(handle, 1, 1, sid, None, None, None)
                assert error == 0, ('Set disposable fixture owner', error)
            finally:
                kernel.CloseHandle(handle)
    finally:
        kernel.CloseHandle(token)
    return True


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
            env = {key: value for key, value in os.environ.items() if key not in ('CODEX_HOME', 'CLAUDE_CONFIG_DIR', 'CLAUDE_CODE_PLUGIN_CACHE_DIR', 'CLAUDE_CODE_PLUGIN_DIRS', 'CLAUDE_CODE_PLUGIN_SEED_DIR') if not key.startswith('GIT_')}
            home = root / ('home-link' if linked == 'home' else 'home')
            state = root / ('state-link' if linked == 'state' else 'state')
            env.update(HOME=str(home), USERPROFILE=str(home), SKILLVIEW_STATE_HOME=str(state),
                       DOTNET_CLI_HOME=str(root / 'dotnet-home'))
            result = subprocess.run([str(host), str(root), str(args.gh[-1].resolve()), '0' * 32,
                                     '0' * 64, apply_action, 'registry-truncated'], env=env, cwd=root,
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
        env = {key: value for key, value in os.environ.items() if key not in ('CODEX_HOME', 'CLAUDE_CONFIG_DIR', 'CLAUDE_CODE_PLUGIN_CACHE_DIR', 'CLAUDE_CODE_PLUGIN_DIRS', 'CLAUDE_CODE_PLUGIN_SEED_DIR') if not key.startswith('GIT_')}
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
        windows_owner_prepared = prepare_windows_fixture_owner(payload) if args.merged else False
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
            if args.merged:
                source = result
                source_path = Path(source['candidatePath'])
                source_bytes = manifest(source_path)
                source_plan = json.loads(Path(source['planPath']).read_bytes())
                assert source_plan['schemaVersion'] == 2
                source_payload = source_plan['candidate']['payload']
                desired = source['revision']['desiredMetadata']
                assert desired['githubTreeSha'] == source['revision']['source']['tree']
                assert desired['githubRef'] == source['revision']['source']['ref']
                assert desired['githubPath'] == source['revision']['source']['subpath']
                assert not desired['pinned'] and desired.get('pinnedRef') is None
                desired_owner = base64.b64decode(source['revision']['owner']['desiredEntry'], validate=True)
                assert json.loads(desired_owner)['skillFolderHash'] == source['revision']['source']['tree']
                assert b'\nUser modifications\n' not in source_path.joinpath('SKILL.md').read_bytes()
                assert not source_path.joinpath('user-extra').exists()
                before_registry = registry_path.read_bytes()
                review = run('update-merge-review', '--id', source['id'], '--expect', source['planFingerprint'])
                assert any(row['path'] == 'user-extra' and row['selection'] == 'installed' for row in review['review']['paths']), review
                chosen = run('update-merge-choose', '--id', source['id'], '--expect', review['reviewFingerprint'], '--choose-installed', 'SKILL.md')
                assert chosen['canMaterialize'], chosen
                assert not chosen['review']['topologyConflicts'] and all(row['selection'] for row in chosen['review']['paths']), chosen
                repeated = run('update-merge-review', '--id', source['id'], '--expect', source['planFingerprint'])
                assert repeated['reviewFingerprint'] == chosen['reviewFingerprint']
                result = run('update-merge-plan', '--id', source['id'], '--expect', chosen['reviewFingerprint'])
                merged_plan = json.loads(Path(result['planPath']).read_bytes())
                assert merged_plan['schemaVersion'] == 3 and result['localDisposition']['kind'] == 'merge-reviewed'
                assert result['localDisposition']['sourcePlanFingerprint'] == source['planFingerprint']
                assert result['localDisposition']['sourceCandidate'] == source_plan['candidate']
                assert merged_plan['candidate']['payload'] != source_payload
                assert result['revision'] == source['revision'], 'Merged bytes must not replace C source/owner tracking'
                assert manifest(payload) == original_payload and registry_path.read_bytes() == before_registry
                assert manifest(source_path) == source_bytes
                assert Path(result['candidatePath']) != source_path
                result['_sourceCandidatePath'] = source_path
                result['_sourceCandidate'] = source_bytes
                result['_sourcePayload'] = source_payload
                result['_desiredOwnerEntry'] = desired_owner
                assert b'\nUser modifications\n' in Path(result['candidatePath']).joinpath('SKILL.md').read_bytes()
                assert Path(result['candidatePath']).joinpath('user-extra').read_bytes() == b'\x00\xffKeep extra'
            result['_approvedCandidate'] = manifest(Path(result['candidatePath']))
            return result

        def inspect(saved):
            return run('update-inspect', '--id', saved['id'])['operation']

        def transition(command, saved, reviewed):
            return run(command, '--id', saved['id'], '--yes', '--expect', reviewed['reviewFingerprint'])['operation']

        def kill_at(saved, action, checkpoint, expected):
            if args.merged and action == 'apply':
                action = apply_action
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

        def assert_merged_source(saved):
            if args.merged:
                assert manifest(saved['_sourceCandidatePath']) == saved['_sourceCandidate']

        def assert_merged_publication(saved):
            if args.merged:
                assert b'\nUser modifications\n' in payload.joinpath('SKILL.md').read_bytes()
                assert payload.joinpath('user-extra').read_bytes() == b'\x00\xffKeep extra'
                assert exposure.is_symlink() and exposure.lstat().st_ino == original_exposure
                assert exposure.resolve() == payload
                assert registry_path.read_bytes().count(saved['_desiredOwnerEntry']) == 1
                assert json.loads(registry_path.read_bytes())['skills']['gh-skill']['skillFolderHash'] == saved['revision']['source']['tree']
                assert_merged_source(saved)

        saved = plan()
        # Change one unrelated field after planning, without touching the
        # selected entry; initial apply must preserve this fresh manager work.
        current = json.loads(registry_path.read_bytes())
        current['skills']['unrelated']['extension'] = [1, 2, 3]
        registry_path.write_bytes((json.dumps(current, indent=2) + '\n').encode('utf-8'))
        preimage = registry_path.read_bytes()
        applied = run('update-apply', '--id', saved['id'], '--yes', '--disposition', apply_disposition,
                      '--expect', saved['planFingerprint'])['operation']
        assert applied['succeeded'] and applied['owner']['state'] == 'desired', applied
        assert manifest(payload) == saved['_approvedCandidate']
        assert_merged_publication(saved)
        updated = json.loads(registry_path.read_bytes())
        selected = updated['skills']['gh-skill']
        assert 'pinnedRef' not in selected and selected['selectedExtension'] == {'keep': True}
        assert selected['skillFolderHash'] == saved['revision']['source']['tree']
        assert selected['installedAt'] == original['skills']['gh-skill']['installedAt']
        assert updated['skills']['unrelated']['extension'] == [1, 2, 3]
        assert manifest(custom / 'gh-skill') == unrelated_payload
        if args.merged:
            live_registry = registry_path.read_bytes()
            next_plan = run('update-plan', '--path', payload)['operation']
            assert next_plan['revision']['source']['tree'] == saved['revision']['source']['tree']
            assert next_plan['revision']['recordedBase']['payload'] == saved['_sourcePayload']
            assert 'SKILL.md' in next_plan['revision']['localChanges'] and 'user-extra' in next_plan['revision']['localChanges']
            assert registry_path.read_bytes() == live_registry and manifest(payload) == saved['_approvedCandidate']
            assert_merged_source(saved)
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
        assert_merged_source(saved)

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
                assert run('update-apply', '--id', saved['id'], '--yes', '--disposition', apply_disposition,
                           '--expect', saved['planFingerprint'])['operation']['succeeded']
                if args.merged:
                    assert manifest(payload) == saved['_approvedCandidate']
                    assert_merged_publication(saved)
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
                assert_merged_publication(saved)
                assert transition('update-undo', saved, inspect(saved))['succeeded']
            assert registry_path.read_bytes() == before
            assert_original()
            assert_merged_source(saved)
            rows.append(dict(action=action, checkpoint=checkpoint, actualProcessKilled=True,
                             tornImageExplicitlyRepaired=tear, registryBytesRestored=True,
                             originalNativePayloadAndExposureRestored=True, unrelatedSameNameUnchanged=True,
                             **(dict(schema3MergedRecoveryVerified=True, localBytesPreserved=True, sourceCandidateUnchanged=True) if args.merged else {})))
        results.append(dict(host=str(app), proofHost=str(host),
                            gh=subprocess.check_output([str(gh), '--version'], text=True).splitlines()[0],
                            canonicalDisposableHomeAndStateGuardsVerified=True,
                            freshUnrelatedRegistryChangesPreserved=True, selectedExtensionsAndInstalledAtPreserved=True,
                            explicitUnpinAndExactImmutableCandidate=True, fixtures=rows,
                            **(dict(proofKind='shared-local-merge', schema3ReviewedPlanVerified=True,
                                    immutableSourceAndExactOwnerTransitionRetained=True, localInstructionsAndExtraPreserved=True,
                                    nextUpdateUsesRealSourceBaseline=True,
                                    windowsSupportedFixtureOwnerPrepared=windows_owner_prepared) if args.merged else {})))
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(results, indent=2))
print(json.dumps(results, indent=2))
