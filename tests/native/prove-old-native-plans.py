"""Create real schema 1/2/3 plans with the frozen old AOT host, then restart current.

All product writes and process kills occur inside one disposable HOME/CWD/state.
Ordinary Linux may prove an explicit schema-3 metadata refusal; that result is
never labeled successful schema-3 compatibility. Each invocation proves its
selected boundary for all three schemas. CI pairs App retain-moved with extension
install-moved; the privileged lane omits the refusal option for both hosts.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import stat
import subprocess
import tempfile
import threading
import time

BASELINE = '0dba59be59776e6e654123aa1ce1e5496074763e'
OLD_REVISION = '33789149b9def6b66ddf247438a79fac94e22074'


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def manifest(directory):
    result = []
    for path in [directory, *sorted(directory.rglob('*'))]:
        native = path.lstat()
        kind = 'link' if path.is_symlink() else 'directory' if path.is_dir() else 'file'
        content = os.readlink(path).encode() if kind == 'link' else path.read_bytes() if kind == 'file' else b''
        result.append((str(path.relative_to(directory)), kind, native.st_dev, native.st_ino,
                       stat.S_IMODE(native.st_mode), hashlib.sha256(content).hexdigest()))
    return result


def extend_registry_fixture(registry, expected_before, schema1_verified):
    """Only preserving owner plans may use extensions; legacy schema 1 refuses them."""
    assert schema1_verified, 'Complete schema-1 recovery/undo verification before adding owner extensions'
    assert registry.read_bytes() == expected_before, 'Registry changed before fixture transition'
    owner = json.loads(expected_before)
    owner['extension'] = {'preserve': True}
    owner['skills']['gh-skill']['selectedExtension'] = {'preserve': True}
    owner['skills']['unrelated'] = {'source': 'other/repo', 'sourceType': 'github', 'extension': [1, 2]}
    desired = (json.dumps(owner, indent=2) + '\n').encode()
    registry.write_bytes(desired)
    return desired


def prepare_windows_fixture_owner(path):
    # Same supported disposable-owner setup as the existing native merge proof.
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
        for target in [path, *sorted(path.rglob('*'))]:
            assert not target.lstat().st_file_attributes & 0x400
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


def kill_at(host, root, gh, env, saved, action, boundary):
    process = subprocess.Popen([str(host), str(root), str(gh), saved['id'], saved['planFingerprint'], action, boundary],
                               cwd=root, env=env, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                               stderr=subprocess.STDOUT, text=True)
    ready, exited = threading.Event(), threading.Event()
    output = []

    def drain():
        try:
            for line in process.stdout:
                if len(output) < 128:
                    output.append(line[:4096])
                if line.strip() == 'READY ' + boundary:
                    ready.set()
        finally:
            exited.set()

    reader = threading.Thread(target=drain, daemon=True)
    reader.start()
    try:
        deadline = time.monotonic() + 90
        while not ready.wait(0.05) and not exited.is_set() and time.monotonic() < deadline:
            pass
        assert ready.is_set() and process.poll() is None, ('Current proof host missed checkpoint', action, boundary, ''.join(output))
        process.kill()
        assert process.wait(timeout=15) != 0
    finally:
        if process.poll() is None:
            process.kill()
            process.wait(timeout=15)
        reader.join(timeout=5)
        assert not reader.is_alive(), 'Proof output drain did not finish'
        process.stdout.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--old-app', required=True, type=Path)
    source = parser.add_mutually_exclusive_group(required=True)
    source.add_argument('--old-checkout', type=Path)
    source.add_argument('--old-source-receipt', type=Path)
    parser.add_argument('--app', required=True, type=Path)
    parser.add_argument('--proof-host', required=True, type=Path)
    parser.add_argument('--gh', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--kill-boundary', required=True, choices=('retain-moved', 'install-moved'),
                        help='Prove this boundary for each old schema; CI pairs opposite boundaries across current hosts.')
    parser.add_argument('--expect-schema3-metadata-refusal', metavar='REASON')
    args = parser.parse_args()
    old, app, host, gh = (path.resolve(strict=True) for path in (args.old_app, args.app, args.proof_host, args.gh))
    assert all(path.is_file() for path in (old, app, host, gh))
    assert digest(old) != digest(app), 'The old and current product binaries must be distinct'
    clean_env = {key: value for key, value in os.environ.items() if not key.startswith('GIT_')}
    if args.old_checkout:
        checkout = args.old_checkout.resolve(strict=True)
        assert old.is_relative_to(checkout), 'The baseline binary must come from its isolated checkout'
        revision = subprocess.check_output(['git', '-C', str(checkout), 'rev-parse', 'HEAD'], env=clean_env, text=True).strip()
        assert revision == BASELINE, ('Wrong old-native source commit', revision)
        subprocess.run(['git', '-C', str(checkout), 'diff', '--quiet', 'HEAD', '--'], env=clean_env, check=True)
        source_receipt = dict(sourceCommit=revision, trackedClean=True, binarySha256=digest(old), sourceVerification='direct-checkout')
    else:
        receipt_bytes = args.old_source_receipt.read_bytes()
        source_receipt = json.loads(receipt_bytes)
        assert source_receipt['sourceCommit'] == BASELINE and source_receipt['trackedClean'] is True
        assert source_receipt['binarySha256'] == digest(old), 'Outer checkout receipt does not bind this old binary'
        source_receipt = dict(source_receipt, sourceVerification='outer-checkout-receipt',
                              receiptSha256=hashlib.sha256(receipt_bytes).hexdigest())
    auth = os.environ.get('GH_TOKEN') or os.environ.get('GITHUB_TOKEN')
    if not auth:
        auth = subprocess.check_output([str(gh), 'auth', 'token', '--hostname', 'github.com'], text=True).strip()
    rows, commands = [], []
    with tempfile.TemporaryDirectory(prefix='skillview-old-native-proof-') as temporary:
        root = Path(temporary).resolve()
        home = root / 'home'
        home.mkdir()
        (root / 'disposable-proof-marker').write_bytes(b'SkillView disposable process proof\n')
        env = {key: value for key, value in os.environ.items()
               if not key.startswith(('GIT_', 'SKILLVIEW_', 'CLAUDE_CODE_PLUGIN_'))
               and key not in ('CODEX_HOME', 'CLAUDE_CONFIG_DIR', 'PI_CODING_AGENT_DIR')}
        env.update(HOME=str(home), USERPROFILE=str(home), GH_TOKEN=auth, GH_EXTENSION='1', GH_PATH=str(gh),
                   GH_CONFIG_DIR=str(root / 'gh-config'), SKILLVIEW_STATE_HOME=str(root / 'state'),
                   XDG_CONFIG_HOME=str(root / 'config'), XDG_STATE_HOME=str(root / 'xdg-state'),
                   XDG_DATA_HOME=str(root / 'data'), XDG_CACHE_HOME=str(root / 'cache'),
                   APPDATA=str(root / 'appdata'), LOCALAPPDATA=str(root / 'localappdata'),
                   CODEX_HOME=str(home / '.codex'), CLAUDE_CONFIG_DIR=str(home / '.claude'),
                   PI_CODING_AGENT_DIR=str(home / '.pi' / 'agent'), DOTNET_CLI_HOME=str(root / 'dotnet-home'),
                   GIT_CONFIG_COUNT='0', GH_TELEMETRY='false', GH_NO_UPDATE_NOTIFIER='1', GH_NO_EXTENSION_UPDATE_NOTIFIER='1')
        custom = root / 'custom'
        for placement in (['--dir', str(custom)], ['--agent', 'universal', '--scope', 'user']):
            installed = subprocess.run([str(gh), 'skill', 'install', *placement, '--pin=' + OLD_REVISION,
                                        '--', 'cli/cli', 'skills/gh-skill'], cwd=root, env=env, capture_output=True, text=True, timeout=180)
            assert installed.returncode == 0, installed.stderr
        payload = home / '.agents' / 'skills' / 'gh-skill'
        exposure = home / '.claude' / 'skills' / 'gh-skill'
        exposure.parent.mkdir(parents=True)
        exposure.symlink_to(payload, target_is_directory=True)
        registry = home / '.agents' / '.skill-lock.json'
        payload.joinpath('SKILL.md').write_bytes(payload.joinpath('SKILL.md').read_bytes() + b'\nLocal compatibility instructions\n')
        payload.joinpath('user-extra').write_bytes(b'\x00\xffOriginal extra')
        payload.joinpath('empty-user-directory').mkdir()
        if os.name != 'nt':
            payload.chmod(0o700)
        windows_owner = prepare_windows_fixture_owner(payload)
        original, unrelated, registry_bytes = manifest(payload), manifest(custom), registry.read_bytes()
        exposure_before = (exposure.lstat().st_dev, exposure.lstat().st_ino, os.readlink(exposure))

        def run(binary, command, *arguments, ok=True):
            process = subprocess.run([str(binary), '--scan-root', str(custom), command, *map(str, arguments),
                                      '--allow-hidden-dirs', '--json'], cwd=root, env=env, capture_output=True, text=True, timeout=180)
            assert (process.returncode == 0) == ok, (command, process.returncode, process.stdout, process.stderr)
            commands.append(dict(host='old' if binary == old else 'current', command=command, exitCode=process.returncode))
            return json.loads(process.stdout)

        def unchanged():
            assert manifest(payload) == original and manifest(custom) == unrelated
            assert registry.read_bytes() == registry_bytes
            assert exposure.is_symlink() and exposure.resolve() == payload
            assert (exposure.lstat().st_dev, exposure.lstat().st_ino, os.readlink(exposure)) == exposure_before

        schema1_verified = False
        for schema in (1, 2, 3):
            if schema == 2:
                # The frozen schema-1 owner guard intentionally rejects unknown
                # fields. Preserve native gh bytes for that entire lifecycle,
                # then exercise extension preservation with schemas 2 and 3.
                unchanged()
                registry_bytes = extend_registry_fixture(registry, registry_bytes, schema1_verified)
            for boundary in (args.kill_boundary,):
                family = 'restore' if schema == 1 else 'update'
                if schema == 1:
                    saved = run(old, 'restore-plan', '--path', payload)['operation']
                else:
                    saved = run(old, 'update-plan', '--path', payload, '--unpin')['operation']
                frozen_source = Path(saved['planPath']).read_bytes()
                source_record = json.loads(frozen_source)
                assert source_record['schemaVersion'] == (1 if schema == 1 else 2) and 'pinPolicy' not in source_record
                if schema == 3:
                    reviewed = run(old, 'update-merge-review', '--id', saved['id'], '--expect', saved['planFingerprint'])
                    chosen = run(old, 'update-merge-choose', '--id', saved['id'], '--expect', reviewed['reviewFingerprint'], '--choose-installed', 'SKILL.md')
                    if args.expect_schema3_metadata_refusal:
                        reason = args.expect_schema3_metadata_refusal
                        assert not chosen['canMaterialize'] and chosen['metadataRefusals'] and all(reason in item for item in chosen['metadataRefusals'])
                        refusal = run(old, 'update-merge-plan', '--id', saved['id'], '--expect', chosen['reviewFingerprint'], ok=False)
                        assert reason in refusal['error']
                        assert Path(saved['planPath']).read_bytes() == frozen_source
                        unchanged()
                        rows.append(dict(schema=3, boundary=boundary, proofKind='explicit-unsupported-metadata', compatibilityVerified=False,
                                         reason=reason, oldNativeMaterializationRefused=True, originalAndOwnerUnchanged=True))
                        break
                    assert chosen['canMaterialize'], chosen
                    source_path = Path(saved['planPath'])
                    saved = run(old, 'update-merge-plan', '--id', saved['id'], '--expect', chosen['reviewFingerprint'])
                    assert source_path.read_bytes() == frozen_source
                frozen = Path(saved['planPath']).read_bytes()
                record = json.loads(frozen)
                assert record['schemaVersion'] == schema and 'pinPolicy' not in record
                assert ('revision' in record) is (schema >= 2) and ('localDisposition' in record) is (schema == 3)
                approved = manifest(Path(saved['candidatePath']))
                unchanged()
                inspected = run(app, family + '-inspect', '--id', saved['id'])['operation']
                assert inspected['status'] == 'ready' and inspected['planFingerprint'] == saved['planFingerprint'], inspected
                assert 'pinPolicy' not in inspected
                kill_at(host, root, gh, env, saved, 'apply-merged' if schema == 3 else 'apply', boundary)
                interrupted = run(app, family + '-inspect', '--id', saved['id'])['operation']
                assert interrupted['status'] == ('original-retained' if boundary == 'retain-moved' else 'installed'), interrupted
                recovered = run(app, family + '-recover', '--id', saved['id'], '--yes', '--expect', interrupted['reviewFingerprint'])['operation']
                assert recovered['succeeded'] and recovered['status'] == 'installed', recovered
                assert manifest(payload) == approved and manifest(Path(recovered['retainedPath'])) == original
                if schema == 1:
                    assert registry.read_bytes() == registry_bytes
                else:
                    entry = json.loads(registry.read_bytes())['skills']['gh-skill']
                    assert entry['skillFolderHash'] == saved['revision']['source']['tree'] and 'pinnedRef' not in entry
                fresh = run(app, family + '-inspect', '--id', saved['id'])['operation']
                restored = run(app, family + '-undo', '--id', saved['id'], '--yes', '--expect', fresh['reviewFingerprint'])['operation']
                assert restored['succeeded'] and restored['status'] == 'restored', restored
                unchanged()
                assert manifest(Path(restored['displacedPath'])) == approved
                assert Path(saved['planPath']).read_bytes() == frozen
                if schema == 3:
                    assert source_path.read_bytes() == frozen_source
                if schema == 1:
                    schema1_verified = True
                rows.append(dict(schema=schema, proofKind='old-native-plan-restart', boundary=boundary,
                                 oldNativePlanCreated=True, currentNativePlanInspected=True, actualCurrentProcessKilled=True,
                                 forwardRecoveryVerified=True, exactOriginalNativeObjectsAndBytesRestored=True,
                                 exactOriginalRegistryBytesRestored=True, exposureNativeLinkRestored=True,
                                 unrelatedSameNameUnchanged=True, planBytesAndFingerprintUnchanged=True,
                                 pinPolicyAbsent=True, registryExtensionsIncluded=schema >= 2,
                                 savedPlanSha256=hashlib.sha256(frozen).hexdigest()))
    successful = [(row['schema'], row['boundary']) for row in rows if row['proofKind'] == 'old-native-plan-restart']
    expected = [(schema, boundary) for schema in (1, 2, 3) if schema != 3 or not args.expect_schema3_metadata_refusal
                for boundary in (args.kill_boundary,)]
    assert successful == expected
    result = dict(proofKind='actual-old-native-saved-plans', killBoundary=args.kill_boundary, oldSource=source_receipt,
                  oldHostSha256=digest(old), currentHostSha256=digest(app), proofHostSha256=digest(host), ghSha256=digest(gh),
                  gh=subprocess.check_output([str(gh), '--version'], text=True).splitlines()[0],
                  windowsSupportedFixtureOwnerPrepared=windows_owner, schema3CompatibilityVerified=not bool(args.expect_schema3_metadata_refusal),
                  fixtures=rows, commands=commands)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
