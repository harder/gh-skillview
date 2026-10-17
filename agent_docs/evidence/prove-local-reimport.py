"""Actual native-host, actual-gh local reimport and process-death contracts.

All fixture writes are beneath disposable private HOME/state/CWD. HTTP proxy
configuration is evidence of blocked HTTP proxies, not OS network isolation.
No authentication, remote fixture, AI client or payload script is required.
"""
import argparse
import hashlib
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
import uuid


from source_review_proof import acknowledge_frozen_source

def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def manifest(directory):
    rows = []
    for path in [directory, *sorted(directory.rglob('*'))]:
        native = path.lstat()
        assert not path.is_symlink(), 'Disposable local fixture unexpectedly contains a link'
        kind = 'directory' if path.is_dir() else 'file'
        attrs = sorted((name, hashlib.sha256(os.getxattr(path, name, follow_symlinks=False)).hexdigest())
                       for name in os.listxattr(path, follow_symlinks=False)) if hasattr(os, 'listxattr') else []
        rows.append((str(path.relative_to(directory)), kind,
                     stat.S_IMODE(native.st_mode) if os.name != 'nt' else None,
                     digest(path) if kind == 'file' else None, attrs))
    return rows


def objects(directory):
    return [(str(path.relative_to(directory)), path.lstat().st_dev, path.lstat().st_ino)
            for path in [directory, *sorted(directory.rglob('*'))]]


def root_security(directory):
    if os.name != 'nt':
        return dict(unixMode=stat.S_IMODE(directory.stat().st_mode))
    import ctypes
    from ctypes import wintypes
    security = ctypes.WinDLL('advapi32', use_last_error=True)
    security.GetFileSecurityW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, ctypes.c_void_p,
                                        wintypes.DWORD, ctypes.POINTER(wintypes.DWORD)]
    security.GetFileSecurityW.restype = wintypes.BOOL
    needed = wintypes.DWORD()
    security.GetFileSecurityW(str(directory), 7, None, 0, ctypes.byref(needed))  # Owner/group/DACL.
    assert 0 < needed.value <= 65536, ('Root security size', needed.value, ctypes.get_last_error())
    data = ctypes.create_string_buffer(needed.value)
    assert security.GetFileSecurityW(str(directory), 7, data, len(data), ctypes.byref(needed)), ctypes.get_last_error()
    return dict(ownerGroupDaclSha256=hashlib.sha256(data.raw[:needed.value]).hexdigest(),
                fixtureRootStreamSha256=digest(Path(str(directory) + ':skillview-root-proof')))


def executable(requested):
    path = requested.absolute()
    if path.is_file():
        return path.resolve(strict=True)
    resolved = shutil.which(str(path)) if os.name == 'nt' else None
    if resolved and Path(resolved).is_file():
        return Path(resolved).resolve(strict=True)
    raise ValueError('Requested executable is not an existing file: ' + str(path))


def prepare_windows_fixture_owner(path):
    """Establish the supported owner only on disposable fixture objects, before review."""
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



def run_case(app, proof_host, gh, case, metadata_refusal):
    with tempfile.TemporaryDirectory(prefix='skillview-local-reimport-proof-') as temporary, socket.socket() as blocked:
        root = Path(temporary).resolve()
        blocked.bind(('127.0.0.1', 0))  # Held, deliberately not listening.
        proxy = 'http://127.0.0.1:' + str(blocked.getsockname()[1])
        home, state = root / 'home', root / 'state'
        home.mkdir(); state.mkdir()
        root.joinpath('disposable-proof-marker').write_bytes(b'SkillView disposable process proof\n')
        removed = {'CODEX_HOME', 'CLAUDE_CONFIG_DIR', 'CLAUDE_CODE_PLUGIN_CACHE_DIR', 'CLAUDE_CODE_PLUGIN_DIRS',
                   'CLAUDE_CODE_PLUGIN_SEED_DIR', 'GITHUB_TOKEN', 'GITHUB_AUTH_TOKEN', 'GITHUB_ENTERPRISE_TOKEN'}
        env = {key: value for key, value in os.environ.items()
               if key not in removed and not key.startswith(('GIT_', 'GH_'))}
        env.update(HOME=str(home), USERPROFILE=str(home), GH_EXTENSION='1', GH_PATH=str(gh),
                   GH_CONFIG_DIR=str(root / 'gh-config'), SKILLVIEW_STATE_HOME=str(state),
                   XDG_CONFIG_HOME=str(root / 'config'), XDG_STATE_HOME=str(root / 'xdg-state'),
                   XDG_DATA_HOME=str(root / 'data'), XDG_CACHE_HOME=str(root / 'cache'),
                   APPDATA=str(root / 'appdata'), LOCALAPPDATA=str(root / 'localappdata'),
                   CLAUDE_CONFIG_DIR=str(home / '.claude'), PI_CODING_AGENT_DIR=str(home / '.pi' / 'agent'),
                   GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL=os.devnull,
                   DOTNET_EnableDiagnostics='0', GH_TELEMETRY='false', GH_NO_UPDATE_NOTIFIER='1',
                   GH_NO_EXTENSION_UPDATE_NOTIFIER='1', NO_PROXY='', no_proxy='')
        for name in ('HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'http_proxy', 'https_proxy', 'all_proxy'):
            env[name] = proxy
        source = root / 'source-parent' / 'local-fixture'
        source.mkdir(parents=True)
        source.joinpath('SKILL.md').write_text('---\nname: local-fixture\ndescription: Local fixture\n---\nOriginal source\n')
        custom = root / 'custom'
        installed = subprocess.run([str(gh), 'skill', 'install', '--from-local', '--dir', str(custom),
                                    '--', str(source), 'local-fixture'], env=env, cwd=root,
                                   capture_output=True, text=True, timeout=90)
        assert installed.returncode == 0, (installed.returncode, installed.stdout, installed.stderr)
        payload = custom / 'local-fixture'
        payload.joinpath('installed-only').write_bytes(b'Installed-only bytes must survive undo\x00\xff')
        if os.name != 'nt':
            payload.chmod(0o700)
        prepare_windows_fixture_owner(payload)
        if os.name == 'nt':
            Path(str(payload) + ':skillview-root-proof').write_bytes(b'Installed root stream retained exactly\x00\xff')
        # Exact authored C includes a durable path already encoded as JSON/YAML.
        # The final stage must preserve every other byte, comments and CRLF too.
        expected_skill = ('---\r\n# Authored comment survives gh YAML reserialization\r\n'
                          'name: local-fixture\r\ndescription: Local fixture\r\nmetadata:\r\n'
                          '  local-path: ' + json.dumps(str(source)) + ' # Keep trailing comment\r\n'
                          '  custom-field: "authored"\r\n---\r\nUpdated authored source\r\n').encode()
        source.joinpath('SKILL.md').write_bytes(expected_skill)
        source.joinpath('empty-directory').mkdir()
        scripts = source / 'scripts'
        scripts.mkdir()
        marker = root / 'PAYLOAD_SCRIPT_RAN'
        scripts.joinpath('never-run.py').write_text('from pathlib import Path\nPath(' + repr(str(marker)) + ').write_text("unexpected")\n')
        if os.name != 'nt':
            scripts.joinpath('never-run.py').chmod(0o751)
        prepare_windows_fixture_owner(source)
        before, before_objects, source_before = manifest(payload), objects(payload), manifest(source)
        original_root_security = root_security(payload)
        registries = [home / '.agents' / '.skill-lock.json', home / '.copilot' / '.skill-lock.json']
        assert not any(path.exists() for path in registries), 'Local custom-dir fixture must be ownerless'
        commands = []

        def run(command, *arguments, expected=0):
            process = subprocess.run([str(app), '--scan-root', str(custom), command, *map(str, arguments),
                                      '--allow-hidden-dirs', '--json'], env=env, cwd=root,
                                     capture_output=True, text=True, timeout=180)
            assert process.returncode == expected, (command, process.returncode, process.stdout, process.stderr)
            result = json.loads(process.stdout)
            commands.append(dict(command=command, exitCode=process.returncode))
            assert not marker.exists(), 'Payload code was executed'
            assert not any(path.exists() for path in registries), 'Ownerless flow created a registry'
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
                assert ready.is_set() and process.poll() is None, ('Checkpoint missing', boundary, ''.join(output))
                process.kill()
                assert process.wait(timeout=15) != 0
            finally:
                if process.poll() is None:
                    process.kill(); process.wait(timeout=15)
                reader.join(timeout=5)
                process.stdout.close()
                assert not reader.is_alive(), 'Proof output reader did not drain'

        planned = run('reimport-plan', '--path', payload, expected=1 if metadata_refusal else 0)
        if metadata_refusal:
            assert planned.get('succeeded') is False and metadata_refusal in planned.get('error', ''), planned
            assert manifest(payload) == before and objects(payload) == before_objects
            assert manifest(source) == source_before
            return dict(case=case, nativePublicationSucceeded=False, explicitMetadataRefusal=metadata_refusal,
                        installedNativeObjectsUnchanged=True, sourceUnchanged=True, commands=commands)
        saved = planned['operation']
        assert saved['succeeded'] and saved['status'] == 'planned' and saved['localSource']['status'] == 'current', saved
        plan_path = Path(saved['planPath'])
        frozen_plan = plan_path.read_bytes()
        plan = json.loads(frozen_plan)
        assert plan['schemaVersion'] == 5 and plan['purpose'] == 'local-reimport'
        assert all(key not in plan for key in ('revision', 'localDisposition', 'pinPolicy'))
        policy = plan['localImport']
        assert policy['sourcePath'] == str(source) and policy['canonicalSourcePath'] == str(source)
        assert policy['disposition'] == 'replace-local-retain-original'
        witness = policy['gh']
        assert witness['binarySha256'].lower() == digest(gh) and witness['binaryPath'] == str(gh)
        assert witness['arguments'][:3] == ['skill', 'install', '--from-local']
        assert witness['inputArchiveFingerprint'] and witness['outputArchiveFingerprint']
        candidate = Path(saved['candidatePath'])
        candidate_bytes, candidate_objects = manifest(candidate), objects(candidate)
        assert candidate.joinpath('SKILL.md').read_bytes() == expected_skill
        assert candidate.joinpath('empty-directory').is_dir() and not candidate.joinpath('installed-only').exists()
        assert candidate.joinpath('scripts/never-run.py').read_bytes() == scripts.joinpath('never-run.py').read_bytes()
        assert root_security(candidate) == original_root_security
        if os.name != 'nt':
            assert stat.S_IMODE(candidate.stat().st_mode) == 0o700
            assert stat.S_IMODE(candidate.joinpath('scripts/never-run.py').stat().st_mode) == 0o751
        assert manifest(payload) == before and objects(payload) == before_objects
        assert manifest(source) == source_before
        assert planned['applyArguments'] == ['reimport-apply', '--id', saved['id'], '--yes', '--disposition',
                                             'replace-local-retain-original', '--expect', saved['planFingerprint'], '--allow-hidden-dirs']
        acknowledge_frozen_source(run, saved, 'reimport')
        if case in ('stale-source', 'local-retain-intent'):
            if case == 'local-retain-intent':
                kill_at(saved, 'apply-reimport', saved['planFingerprint'], case)
            source.joinpath('SKILL.md').write_bytes(expected_skill + b'Changed after review\r\n')
            inspection = run('reimport-inspect', '--id', saved['id'])['operation']
            assert inspection['localSource']['status'] in ('stale', 'unavailable'), inspection
            command = 'reimport-recover' if case == 'local-retain-intent' else 'reimport-apply'
            arguments = ['--id', saved['id'], '--yes', '--expect',
                         inspection['reviewFingerprint'] if command.endswith('recover') else saved['planFingerprint']]
            if command.endswith('apply'):
                arguments += ['--disposition', 'replace-local-retain-original']
            refusal = run(command, *arguments, expected=1)
            assert not refusal.get('operation', refusal).get('succeeded', False), refusal
            assert manifest(payload) == before and objects(payload) == before_objects
            assert not Path(saved['retainedPath']).exists()
            assert plan_path.read_bytes() == frozen_plan
            return dict(case=case, nativePublicationSucceeded=True, staleSourceRefusedBeforeRetain=True,
                        persistedRetainIntent=case == 'local-retain-intent', installedNativeObjectsUnchanged=True,
                        immutablePlanUnchanged=True, commands=commands)
        if case in ('retain-moved', 'install-moved'):
            kill_at(saved, 'apply-reimport', saved['planFingerprint'], case)
            shutil.rmtree(source)  # Owned generated fixture, outside captured I/C archives.
            inspection = run('reimport-inspect', '--id', saved['id'])['operation']
            assert inspection['localSource']['status'] == 'not-required-after-retain', inspection
            applied = run('reimport-recover', '--id', saved['id'], '--yes', '--expect', inspection['reviewFingerprint'])['operation']
        else:
            applied = run('reimport-apply', '--id', saved['id'], '--yes', '--disposition',
                          'replace-local-retain-original', '--expect', saved['planFingerprint'])['operation']
        assert applied['succeeded'] and applied['status'] == 'installed', applied
        assert manifest(payload) == candidate_bytes and objects(payload) == candidate_objects
        assert root_security(payload) == original_root_security
        assert manifest(Path(saved['retainedPath'])) == before and objects(Path(saved['retainedPath'])) == before_objects
        assert root_security(Path(saved['retainedPath'])) == original_root_security
        if case == 'normal':
            plans_root = plan_path.parent.parent
            placeholders = []
            try:
                # Real retained plan + fifteen owned incomplete UUID operations
                # fill MaxPlans=16. No product metadata or existing plan is edited.
                for _ in range(15):
                    path = plans_root / uuid.uuid4().hex
                    path.mkdir(mode=0o700)
                    information = path.lstat()
                    placeholders.append((path, information.st_dev, information.st_ino))
                plans_before = sorted(path.name for path in plans_root.iterdir())
                active_objects = objects(payload)
                unchanged = run('reimport-plan', '--path', payload)
                assert unchanged.get('operation') is None and unchanged['unchanged'] and unchanged['succeeded'], unchanged
                assert sorted(path.name for path in plans_root.iterdir()) == plans_before
                assert objects(payload) == active_objects and manifest(payload) == candidate_bytes
                assert manifest(source) == source_before
                if os.name == 'nt':
                    Path(str(scripts / 'never-run.py') + ':skillview-source-proof').write_bytes(b'Metadata-only source stream\x00\xff')
                else:
                    scripts.joinpath('never-run.py').chmod(0o750)
                refused = run('reimport-plan', '--path', payload, expected=1)
                assert refused.get('succeeded') is False and 'quota' in refused.get('error', '').lower(), refused
                assert sorted(path.name for path in plans_root.iterdir()) == plans_before
                assert objects(payload) == active_objects and manifest(payload) == candidate_bytes
            finally:
                # Remove only the known, still-identical, empty generated leaves.
                for path, device, inode in placeholders:
                    information = path.lstat()
                    assert not path.is_symlink() and stat.S_ISDIR(information.st_mode)
                    assert (information.st_dev, information.st_ino) == (device, inode)
                    path.rmdir()
            metadata_plan = run('reimport-plan', '--path', payload)['operation']
            assert metadata_plan['succeeded'] and 'scripts/never-run.py' in metadata_plan['changedPaths'], metadata_plan
            assert metadata_plan['localSource']['status'] == 'current'
            metadata_candidate = Path(metadata_plan['candidatePath'])
            assert metadata_candidate.joinpath('SKILL.md').read_bytes() == expected_skill
            assert metadata_candidate.joinpath('scripts/never-run.py').read_bytes() == scripts.joinpath('never-run.py').read_bytes()
            if os.name == 'nt':
                assert Path(str(metadata_candidate / 'scripts/never-run.py') + ':skillview-source-proof').read_bytes() == b'Metadata-only source stream\x00\xff'
            else:
                assert stat.S_IMODE(metadata_candidate.joinpath('scripts/never-run.py').stat().st_mode) == 0o750
            assert objects(payload) == active_objects and manifest(payload) == candidate_bytes
        inspection = run('reimport-inspect', '--id', saved['id'])['operation']
        if case in ('displace-moved', 'restore-moved'):
            kill_at(saved, 'undo-reimport', inspection['reviewFingerprint'], case)
            shutil.rmtree(source)
            inspection = run('reimport-inspect', '--id', saved['id'])['operation']
            assert inspection['localSource']['status'] == 'not-required-after-retain', inspection
        undone = run('reimport-undo', '--id', saved['id'], '--yes', '--expect', inspection['reviewFingerprint'])['operation']
        assert undone['succeeded'] and undone['status'] == 'restored', undone
        assert manifest(payload) == before and objects(payload) == before_objects
        assert root_security(payload) == original_root_security
        assert manifest(Path(undone['displacedPath'])) == candidate_bytes
        assert plan_path.read_bytes() == frozen_plan
        assert not marker.exists() and not any(path.exists() for path in registries)
        return dict(case=case, nativePublicationSucceeded=True, actualGhWitnessVerified=True,
                    capturedSourceRawBytesModesAndEmptyDirectoriesPreserved=True, installedRootPrivacyPreserved=True,
                    observedRootPrivacy=original_root_security,
                    exactOriginalNativeObjectsRestored=True, immutablePlanUnchanged=True,
                    noOpAllocatesNoPlanAndPreservesObjects=case == 'normal',
                    fullRetainedQuotaNoOpVerified=case == 'normal', metadataOnlySourceChangeRequiresReview=case == 'normal',
                    frozenRecoveryWithoutExternalSource=case in ('retain-moved', 'install-moved', 'displace-moved', 'restore-moved'),
                    commands=commands)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', required=True, type=Path)
    parser.add_argument('--proof-host', required=True, type=Path)
    parser.add_argument('--gh', action='append', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--expect-metadata-refusal', metavar='REASON')
    args = parser.parse_args()
    app, proof_host = executable(args.app), executable(args.proof_host)
    gh_paths = [executable(path) for path in args.gh]  # Validate before any fixture write.
    rows = []
    for gh in gh_paths:
        version = subprocess.check_output([str(gh), '--version'], text=True, timeout=30).splitlines()[0]
        cases = ('metadata-refusal',) if args.expect_metadata_refusal else (
            'normal', 'stale-source', 'local-retain-intent', 'retain-moved', 'install-moved', 'displace-moved', 'restore-moved')
        for case in cases:
            row = run_case(app, proof_host, gh, case, args.expect_metadata_refusal)
            row.update(host=str(app), hostSha256=digest(app), proofHostSha256=digest(proof_host),
                       gh=str(gh), ghSha256=digest(gh), ghVersion=version,
                       httpProxiesBlocked=True, githubCredentialsRemoved=True, payloadScriptsExecuted=False)
            rows.append(row)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(rows, indent=2) + '\n')
    print(json.dumps(dict(contracts=len(rows), output=str(args.output))))


if __name__ == '__main__':
    main()
