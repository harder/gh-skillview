"""Disposable actual-gh/native source-switch contracts; never run payload code.

Proxy settings block HTTP through a held unlistening loopback endpoint; they
are not an OS network sandbox. Fixture helpers are shared with reimport proof.
"""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import shutil
import socket
import stat
import subprocess
import tempfile
import threading
import time
from source_review_proof import acknowledge_frozen_source

_helpers_path = Path(__file__).with_name('prove-local-reimport.py')
_proof_output = None


def load_helpers():
    global digest, manifest, objects, root_security, executable, prepare_windows_fixture_owner, verify_source_receipt
    spec = importlib.util.spec_from_file_location('skillview_reimport_fixture_helpers', _helpers_path)
    helpers = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(helpers)
    digest, manifest, objects = helpers.digest, helpers.manifest, helpers.objects
    root_security, executable = helpers.root_security, helpers.executable
    prepare_windows_fixture_owner = helpers.prepare_windows_fixture_owner
    verify_source_receipt = helpers.verify_source_receipt


def native_directory_id(path):
    """Read full native IDs on a generated ordinary fixture directory only."""
    assert path.is_dir() and not path.is_symlink()
    if os.name != 'nt':
        info = path.stat()
        return dict(volume=info.st_dev, fileIdLow=info.st_ino, fileIdHigh=0)
    import ctypes
    from ctypes import wintypes
    class FileIdInfo(ctypes.Structure):
        _fields_ = [('volume', ctypes.c_ulonglong), ('fileId', ctypes.c_ubyte * 16)]
    assert ctypes.sizeof(FileIdInfo) == 24
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
                                  wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    kernel.CreateFileW.restype = wintypes.HANDLE
    kernel.GetFileInformationByHandleEx.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
    kernel.GetFileInformationByHandleEx.restype = wintypes.BOOL
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel.CloseHandle.restype = wintypes.BOOL
    handle = kernel.CreateFileW(str(path), 0, 7, None, 3, 0x02200000, None)
    assert handle != ctypes.c_void_p(-1).value, ctypes.get_last_error()
    try:
        value = FileIdInfo()
        assert kernel.GetFileInformationByHandleEx(handle, 18, ctypes.byref(value), ctypes.sizeof(value)), ctypes.get_last_error()
        raw = bytes(value.fileId)
        return dict(volume=value.volume, fileIdLow=int.from_bytes(raw[:8], 'little'),
                    fileIdHigh=int.from_bytes(raw[8:], 'little'))
    finally:
        assert kernel.CloseHandle(handle), ctypes.get_last_error()


def bind_fixture_path(raw, previous, desired):
    """Exact known fixture scalar only; this is not a general YAML parser."""
    lines = raw.splitlines(keepends=True)
    changed = 0
    for index, line in enumerate(lines):
        stripped = line.lstrip(b' ')
        if not stripped.startswith(b'local-path: '):
            continue
        prefix = line[:len(line) - len(stripped)] + b'local-path: '
        newline = b'\r\n' if line.endswith(b'\r\n') else b'\n'
        value = line[len(prefix):-len(newline)]
        scalar, separator, comment = value.partition(b' #')
        assert scalar in (str(previous).encode(), json.dumps(str(previous)).encode()), scalar
        lines[index] = prefix + json.dumps(str(desired)).encode() + separator + comment + newline
        changed += 1
    assert changed == 1, 'Fixture must have exactly one known local-path scalar'
    return b''.join(lines)


def run_case(app, proof_host, gh, case, metadata_refusal):
    with tempfile.TemporaryDirectory(prefix='skillview-source-switch-proof-') as temporary, socket.socket() as blocked:
        root = Path(temporary).resolve()
        blocked.bind(('127.0.0.1', 0))
        proxy = 'http://127.0.0.1:' + str(blocked.getsockname()[1])
        home, state = root / 'home', root / 'state'
        home.mkdir(); state.mkdir()
        root.joinpath('disposable-proof-marker').write_bytes(b'SkillView disposable process proof\n')
        env = {key: value for key, value in os.environ.items()
               if not key.startswith(('GIT_', 'GH_', 'GITHUB_')) and key not in {
                   'CODEX_HOME', 'CLAUDE_CONFIG_DIR', 'CLAUDE_CODE_PLUGIN_CACHE_DIR',
                   'CLAUDE_CODE_PLUGIN_DIRS', 'CLAUDE_CODE_PLUGIN_SEED_DIR'}}
        env.update(HOME=str(home), USERPROFILE=str(home), GH_EXTENSION='1', GH_PATH=str(gh),
                   GH_CONFIG_DIR=str(root / 'gh-config'), SKILLVIEW_STATE_HOME=str(state),
                   XDG_CONFIG_HOME=str(root / 'config'), XDG_STATE_HOME=str(root / 'xdg-state'),
                   XDG_DATA_HOME=str(root / 'data'), XDG_CACHE_HOME=str(root / 'cache'),
                   APPDATA=str(root / 'appdata'), LOCALAPPDATA=str(root / 'localappdata'),
                   CODEX_HOME=str(home / '.codex'), CLAUDE_CONFIG_DIR=str(home / '.claude'),
                   PI_CODING_AGENT_DIR=str(home / '.pi' / 'agent'), GIT_CONFIG_NOSYSTEM='1',
                   GIT_CONFIG_GLOBAL=os.devnull, DOTNET_EnableDiagnostics='0', GH_TELEMETRY='false',
                   GH_NO_UPDATE_NOTIFIER='1', GH_NO_EXTENSION_UPDATE_NOTIFIER='1', NO_PROXY='', no_proxy='')
        for name in ('HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'http_proxy', 'https_proxy', 'all_proxy'):
            env[name] = proxy
        old_source = root / 'old-source' / 'local-fixture'
        old_source.mkdir(parents=True)
        old_source.joinpath('SKILL.md').write_text('---\nname: local-fixture\ndescription: Local fixture\n---\nOriginal source\n')
        custom = root / 'custom'
        installed = subprocess.run([str(gh), 'skill', 'install', '--from-local', '--dir', str(custom),
                                    '--', str(old_source), 'local-fixture'], env=env, cwd=root,
                                   stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=90)
        assert installed.returncode == 0, (installed.returncode, installed.stdout, installed.stderr)
        payload = custom / 'local-fixture'
        payload.joinpath('installed-only').write_bytes(b'Installed-only bytes survive undo\x00\xff')
        payload.joinpath('.skillview-ignore').write_bytes(b'Fixture exclusion survives undo\n')
        if os.name != 'nt':
            payload.chmod(0o700)
        prepare_windows_fixture_owner(payload)
        if os.name == 'nt':
            Path(str(payload) + ':skillview-root-proof').write_bytes(b'Installed root stream retained exactly\x00\xff')
        source = root / 'selected-source' / 'local-fixture'
        source.mkdir(parents=True)
        authored_skill = ('---\r\n# Authored comment survives source switching\r\n'
                          'name: local-fixture\r\ndescription: Local fixture\r\nmetadata:\r\n'
                          '  local-path: ' + json.dumps(str(old_source)) + ' # Keep prior source comment\r\n'
                          '  custom-field: "authored"\r\n---\r\nNew selected source\r\n').encode()
        source.joinpath('SKILL.md').write_bytes(authored_skill)
        source.joinpath('empty-directory').mkdir()
        source.joinpath('scripts').mkdir()
        marker = root / 'PAYLOAD_SCRIPT_RAN'
        source.joinpath('scripts/never-run.py').write_text('from pathlib import Path\nPath(' + repr(str(marker)) + ').write_text("unexpected")\n')
        if os.name != 'nt':
            source.joinpath('scripts/never-run.py').chmod(0o751)
        # Equal authored payload still changes tracking and needs review. Use the
        # actual gh-authored I bytes, including its real old local-path scalar.
        if case == 'equal-authored':
            shutil.rmtree(source)
            shutil.copytree(payload, source)
            authored_skill = source.joinpath('SKILL.md').read_bytes()
        expected_skill = bind_fixture_path(authored_skill, old_source, source)
        prepare_windows_fixture_owner(source)
        before, before_objects = manifest(payload), objects(payload)
        source_before, source_objects = manifest(source), objects(source)
        old_before, old_objects = manifest(old_source), objects(old_source)
        original_root_security = root_security(payload)
        registries = [home / '.agents/.skill-lock.json', home / '.copilot/.skill-lock.json']
        assert not any(path.exists() for path in registries)
        if case == 'old-source-missing':
            shutil.rmtree(old_source)
        placement_ancestor = old_source.parent if case == 'old-source-missing' else old_source
        expected_ancestor_id = native_directory_id(placement_ancestor)
        commands, kills = [], []

        def source_unchanged():
            assert manifest(source) == source_before and objects(source) == source_objects
            if old_source.exists():
                assert manifest(old_source) == old_before and objects(old_source) == old_objects

        def run(command, *arguments, expected=0):
            process = subprocess.run([str(app), '--scan-root', str(custom), command, *map(str, arguments),
                                      '--allow-hidden-dirs', '--json'], env=env, cwd=root,
                                     stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=180)
            assert process.returncode == expected, (command, process.returncode, process.stdout, process.stderr)
            result = json.loads(process.stdout)
            commands.append(dict(command=command, exitCode=process.returncode))
            assert not marker.exists() and not any(path.exists() for path in registries)
            return result

        def kill_at(saved, action, fingerprint, boundary):
            process = subprocess.Popen([str(proof_host), str(root), str(gh), saved['id'], fingerprint, action, boundary],
                                       env=env, cwd=root, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                       stderr=subprocess.STDOUT, text=True)
            ready, exited, output = threading.Event(), threading.Event(), []
            def consume():
                try:
                    for line in process.stdout:
                        if len(output) < 128:
                            output.append(line[:4096])
                        if line.strip() == 'READY ' + boundary:
                            ready.set()
                finally:
                    exited.set()
            reader = threading.Thread(target=consume, daemon=True)
            reader.start()
            try:
                deadline = time.monotonic() + 90
                while not ready.wait(.05) and not exited.is_set() and time.monotonic() < deadline:
                    pass
                assert ready.is_set() and process.poll() is None, (boundary, ''.join(output))
                process.kill()
                assert process.wait(timeout=15) != 0
            finally:
                if process.poll() is None:
                    process.kill(); process.wait(timeout=15)
                reader.join(timeout=5); process.stdout.close()
                assert not reader.is_alive(), 'Output reader did not drain'
            kills.append(dict(action=action, boundary=boundary, readyObserved=True, killRequested=True,
                              exitCode=process.returncode, outputReaderDrained=not reader.is_alive()))

        plan_command = ('plan', '--purpose', 'switch-local-source') if case == 'normal' else ('source-switch-plan',)
        planned = run(*plan_command, '--path', payload, '--new-source', source,
                      expected=1 if metadata_refusal else 0)
        if metadata_refusal:
            assert planned.get('succeeded') is False and metadata_refusal in planned.get('error', ''), planned
            assert manifest(payload) == before and objects(payload) == before_objects
            source_unchanged()
            return dict(case=case, candidateNativePreparationSucceeded=False, liveReplacementInstalled=False,
                        explicitMetadataRefusal=metadata_refusal,
                        installedNativeObjectsUnchanged=True, bothExternalSourcesUnchanged=True, commands=commands, processKills=kills)
        saved = planned['operation']
        reference_path = root / 'source-switch-reference.json'
        if case == 'normal':
            assert planned['planReference']['purpose'] == 'switch-local-source', planned
            reference_path.write_text(json.dumps(planned['planReference']) + '\n')
            reference_bytes = reference_path.read_bytes()
        assert saved['succeeded'] and saved['status'] == 'planned' and saved['localSource']['status'] == 'current', saved
        plan_path = Path(saved['planPath']); frozen_plan = plan_path.read_bytes(); plan = json.loads(frozen_plan)
        assert plan['schemaVersion'] == 6 and plan['purpose'] == 'switch-local-source'
        assert all(key not in plan for key in ('revision', 'localDisposition', 'pinPolicy'))
        transition = plan['localSourceSwitch']; policy = plan['localImport']
        assert transition['previousRecordedPath'] == str(old_source)
        assert transition['capturedSourceRecordedPath'] == str(old_source)
        assert transition['previousPlacement']['path'] == str(old_source)
        assert transition['previousPlacement']['canonicalPath'] == str(old_source)
        placement = transition['previousPlacement']
        assert placement['missingSuffix'] == (['local-fixture'] if case == 'old-source-missing' else [])
        ancestor = placement['existingAncestor']
        assert ancestor['canonicalPath'] == str(placement_ancestor)
        assert ancestor['isDirectory'] and not ancestor['isReparsePoint']
        assert all(ancestor[key] == value for key, value in expected_ancestor_id.items()), ancestor
        assert native_directory_id(placement_ancestor) == expected_ancestor_id
        if case == 'old-source-missing':
            assert not old_source.exists() and placement_ancestor.is_dir()
        assert policy['sourcePath'] == str(source) and policy['canonicalSourcePath'] == str(source)
        assert policy['disposition'] == 'replace-local-source-retain-original'
        witness = policy['gh']
        assert witness['binarySha256'].lower() == digest(gh) and witness['binaryPath'] == str(gh)
        assert witness['arguments'][:3] == ['skill', 'install', '--from-local']
        assert witness['inputArchiveFingerprint'] and witness['outputArchiveFingerprint']
        candidate = Path(saved['candidatePath']); candidate_bytes, candidate_objects = manifest(candidate), objects(candidate)
        assert candidate.joinpath('SKILL.md').read_bytes() == expected_skill
        assert root_security(candidate) == original_root_security
        if case != 'equal-authored':
            assert candidate.joinpath('empty-directory').is_dir() and not candidate.joinpath('installed-only').exists()
            assert candidate.joinpath('scripts/never-run.py').read_bytes() == source.joinpath('scripts/never-run.py').read_bytes()
        if os.name != 'nt':
            assert stat.S_IMODE(candidate.stat().st_mode) == 0o700
        assert manifest(payload) == before and objects(payload) == before_objects
        source_unchanged()
        assert planned['applyArguments'] == ['source-switch-apply', '--id', saved['id'], '--yes', '--disposition',
                                             'replace-local-source-retain-original', '--expect', saved['planFingerprint'], '--allow-hidden-dirs']
        if case == 'normal':
            refusal = run('apply', '--plan-file', reference_path, '--yes', '--disposition',
                          'replace-local-source-retain-original', '--expect', saved['planFingerprint'], expected=1)
            assert 'source-review acknowledgement' in refusal['error'], refusal
            assert manifest(payload) == before and objects(payload) == before_objects
            assert not Path(saved['retainedPath']).exists()
            source_unchanged()
        if case not in ('coordinator-recover', 'coordinator-apply'):
            acknowledge_frozen_source(run, saved, 'source-switch')
        if case == 'normal':
            private_before = manifest(plan_path.parent)
            for family, disposition in [('restore', 'retain-original'), ('update', 'retain-original'),
                                        ('unpin', 'unpin-only'), ('reimport', 'replace-local-retain-original')]:
                refusal = run(family + '-apply', '--id', saved['id'], '--yes', '--disposition', disposition,
                              '--expect', saved['planFingerprint'], expected=1)
                assert not refusal.get('operation', refusal).get('succeeded', False), refusal
                assert manifest(plan_path.parent) == private_before and plan_path.read_bytes() == frozen_plan
                assert manifest(payload) == before and objects(payload) == before_objects
                source_unchanged()
        if case in ('stale-source', 'local-retain-intent'):
            if case == 'local-retain-intent':
                kill_at(saved, 'apply-source-switch', saved['planFingerprint'], case)
            source.joinpath('SKILL.md').write_bytes(authored_skill + b'Changed after review\r\n')
            inspection = run('source-switch-inspect', '--id', saved['id'])['operation']
            assert inspection['localSource']['status'] in ('stale', 'unavailable'), inspection
            command = 'source-switch-recover' if case == 'local-retain-intent' else 'source-switch-apply'
            arguments = ['--id', saved['id'], '--yes', '--expect',
                         inspection['reviewFingerprint'] if command.endswith('recover') else saved['planFingerprint']]
            if command.endswith('apply'):
                arguments += ['--disposition', 'replace-local-source-retain-original']
            refusal = run(command, *arguments, expected=1)
            assert not refusal.get('operation', refusal).get('succeeded', False), refusal
            assert manifest(payload) == before and objects(payload) == before_objects
            assert not Path(saved['retainedPath']).exists() and plan_path.read_bytes() == frozen_plan
            return dict(case=case, candidateNativePreparationSucceeded=True, liveReplacementInstalled=False,
                        staleNewSourceRefusedBeforeRetain=True,
                        persistedRetainIntent=case == 'local-retain-intent', installedNativeObjectsUnchanged=True,
                        immutablePlanUnchanged=True, commands=commands, processKills=kills)
        if case in ('coordinator-recover', 'coordinator-apply'):
            inspection = run('source-switch-inspect', '--id', saved['id'])['operation']
            assert inspection['status'] == 'ready' and not inspection['sourceReviewAcknowledged'], inspection
            assert not list(plan_path.parent.glob('source-review-*.json'))
            process = subprocess.run([str(proof_host), str(root), str(gh), saved['id'], inspection['reviewFingerprint'],
                                      'recover-source-switch-reviewed' if case == 'coordinator-recover' else 'apply-source-switch-reviewed',
                                      'workflow'], env=env, cwd=root,
                                     stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=180)
            assert process.returncode == 0, (process.returncode, process.stdout, process.stderr)
            applied = json.loads(process.stdout)
            commands.append(dict(command='coordinator-source-switch-' + ('recover' if case == 'coordinator-recover' else 'apply') + '-reviewed',
                                 exitCode=process.returncode))
            assert applied['sourceReviewAcknowledged'], applied
            verify_source_receipt(plan_path, plan)
            assert not marker.exists() and not any(path.exists() for path in registries)
        elif case in ('retain-moved', 'install-moved'):
            kill_at(saved, 'apply-source-switch', saved['planFingerprint'], case)
            shutil.rmtree(source); shutil.rmtree(old_source)
            inspection = run('source-switch-inspect', '--id', saved['id'])['operation']
            assert inspection['localSource']['status'] == 'not-required-after-retain', inspection
            applied = run('source-switch-recover', '--id', saved['id'], '--yes', '--expect', inspection['reviewFingerprint'])['operation']
        elif case == 'normal':
            applied = run('apply', '--plan-file', reference_path, '--yes', '--disposition',
                          'replace-local-source-retain-original', '--expect', saved['planFingerprint'])['operation']
        else:
            applied = run('source-switch-apply', '--id', saved['id'], '--yes', '--disposition',
                          'replace-local-source-retain-original', '--expect', saved['planFingerprint'])['operation']
        assert applied['succeeded'] and applied['status'] == 'installed', applied
        assert manifest(payload) == candidate_bytes and objects(payload) == candidate_objects
        assert root_security(payload) == original_root_security
        assert manifest(Path(saved['retainedPath'])) == before and objects(Path(saved['retainedPath'])) == before_objects
        if case == 'normal':
            plan_ids = sorted(path.name for path in plan_path.parent.parent.iterdir())
            same = run('source-switch-plan', '--path', payload, '--new-source', source, expected=1)
            assert not same.get('succeeded', False) and 'reimport' in same.get('error', '').lower(), same
            assert sorted(path.name for path in plan_path.parent.parent.iterdir()) == plan_ids
            assert manifest(payload) == candidate_bytes and objects(payload) == candidate_objects
            source_unchanged()
        inspection = (run('verify', '--plan-file', reference_path) if case == 'normal'
                      else run('source-switch-inspect', '--id', saved['id']))['operation']
        if case in ('displace-moved', 'restore-moved'):
            kill_at(saved, 'undo-source-switch', inspection['reviewFingerprint'], case)
            shutil.rmtree(source); shutil.rmtree(old_source)
            inspection = run('source-switch-inspect', '--id', saved['id'])['operation']
            assert inspection['localSource']['status'] == 'not-required-after-retain', inspection
        undone = (run('undo', '--plan-file', reference_path, '--yes', '--expect', inspection['reviewFingerprint']) if case == 'normal'
                  else run('source-switch-undo', '--id', saved['id'], '--yes', '--expect', inspection['reviewFingerprint']))['operation']
        if case == 'normal':
            assert reference_path.read_bytes() == reference_bytes
        assert undone['succeeded'] and undone['status'] == 'restored', undone
        assert manifest(payload) == before and objects(payload) == before_objects
        assert root_security(payload) == original_root_security
        assert manifest(Path(undone['displacedPath'])) == candidate_bytes and plan_path.read_bytes() == frozen_plan
        if source.exists():
            source_unchanged()
        return dict(case=case, candidateNativePreparationSucceeded=True, liveReplacementInstalled=True,
                    actualGhWitnessVerified=True, oldPlacementNativeAncestorAndMissingBoundaryVerified=True,
                    distinctLocalSourceTrackingReviewed=True, capturedAuthoredBytesPreservedExceptApprovedLocalPath=True,
                    coordinatorRecoverCreatedExactSourceReviewReceipt=case == 'coordinator-recover',
                    coordinatorApplyEnforcedIndependentApprovalsAndCreatedExactReceipt=case == 'coordinator-apply',
                    genericPlanReferenceLifecycleWithIndependentSourceApproval=case == 'normal',
                    equalAuthoredTrackingChangePlans=case == 'equal-authored', missingOldSourceAdmitted=case == 'old-source-missing',
                    sameRecordedSourceRefusedBeforePlan=case == 'normal', legacyCommandFamiliesRefusedWithoutWrites=case == 'normal',
                    installedRootPrivacyPreserved=True,
                    exactOriginalNativeObjectsRestored=True, immutablePlanUnchanged=True,
                    frozenRecoveryWithoutBothExternalSources=case in ('retain-moved', 'install-moved', 'displace-moved', 'restore-moved'),
                    commands=commands, processKills=kills)


def main():
    global _proof_output
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', required=True, type=Path)
    parser.add_argument('--proof-host', required=True, type=Path)
    parser.add_argument('--gh', action='append', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--expect-metadata-refusal', metavar='REASON')
    args = parser.parse_args()
    _proof_output = args.output
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(schemaVersion=1, proofCompleted=False, status='incomplete', fixtures=[])) + '\n')
    load_helpers()
    app, proof_host = executable(args.app), executable(args.proof_host)
    gh_paths = [executable(path) for path in args.gh]
    rows = []
    for gh in gh_paths:
        version = subprocess.check_output([str(gh), '--version'], text=True, timeout=30).splitlines()[0]
        cases = ('metadata-refusal',) if args.expect_metadata_refusal else (
            'normal', 'coordinator-recover', 'coordinator-apply', 'equal-authored', 'old-source-missing', 'stale-source', 'local-retain-intent',
            'retain-moved', 'install-moved', 'displace-moved', 'restore-moved')
        for case in cases:
            row = run_case(app, proof_host, gh, case, args.expect_metadata_refusal)
            row.update(host=str(app), hostSha256=digest(app), proofHostSha256=digest(proof_host),
                       gh=str(gh), ghSha256=digest(gh), ghVersion=version,
                       httpProxiesBlocked=True, githubCredentialsRemoved=True, payloadScriptsExecuted=False,
                       scriptSha256=digest(Path(__file__)), sharedFixtureHelpersSha256=digest(_helpers_path))
            rows.append(row)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(schemaVersion=1, proofCompleted=True, status='completed',
                                          mode='metadata-refusal' if args.expect_metadata_refusal else 'native-source-switch',
                                          fixtures=rows), indent=2) + '\n')
    print(json.dumps(dict(contracts=len(rows), output=str(args.output))))


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        if _proof_output is not None:
            _proof_output.write_text(json.dumps(dict(schemaVersion=1, proofCompleted=False, status='failed',
                                                      errorType=type(error).__name__, error=str(error)[:4096], fixtures=[])) + '\n')
        raise
