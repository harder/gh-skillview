"""Actual frozen schema5 producer -> current native reimport restart contract.

Run only against disposable private fixtures. The producer is pinned to the
verified final PR68 merge; current-consumer native compatibility remains unproved
until this harness actually executes. HTTP proxy guards
are not an OS network sandbox. No AI client or payload script is launched.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import socket
import stat
import subprocess
import sys
import tempfile
import threading
import time

# Verified corrected PR68 merge (tree 9a58bbdb0a1786328dcb883e829ea5ea56e7cb10).
# Build this exact source as the schema5 producer; never normalize old plan bytes.
BASELINE = '4649d75fc3a4f9fdd7c22ca5593e629a1b559298'
_HELPERS = Path(__file__).with_name('prove-local-reimport.py')
_output = None
_raw = None
_commands = []
_artifacts = {}
_artifact_bytes = 0


def sha(data):
    return hashlib.sha256(data).hexdigest()


def frozen_gh_version(stdout):
    """Match the native producer's complete bounded StdOut.Trim() witness."""
    if len(stdout) > 4096:
        raise ValueError('gh version witness exceeds its bound')
    value = stdout.decode('utf-8', 'strict').strip()
    if not value.startswith('gh version '):
        raise ValueError('gh version witness lacks its banner')
    return value


def publish(status, **fields):
    if _output is not None:
        _output.parent.mkdir(parents=True, exist_ok=True)
        _output.write_text(json.dumps(dict(schemaVersion=1, proofCompleted=False,
            compatibilityVerified=False, status=status, baselineCommit=BASELINE,
            commands=_commands, rawArtifacts=_artifacts, **fields), indent=2) + '\n')


def bootstrap_output(arguments):
    """Invalidate any previous positive BEFORE parser/helper/tool/fixture work."""
    global _output
    candidates = []
    for index, value in enumerate(arguments):
        if value == '--output' and index + 1 < len(arguments):
            candidates.append(arguments[index + 1])
        elif value.startswith('--output='):
            candidates.append(value.split('=', 1)[1])
    for value in candidates:
        _output = Path(value)
        publish('initializing')
    if len(candidates) > 1:
        raise ValueError('Exactly one --output is allowed; all supplied outputs were marked nonpositive')


def artifact(name, data):
    global _artifact_bytes
    assert _raw is not None and '/' not in name and '\\' not in name
    _artifact_bytes += len(data)
    assert _artifact_bytes <= 32 * 1024 * 1024, 'Raw proof artifact budget exceeded'
    path = _raw / name
    assert not path.exists() and not path.is_symlink(), 'Never overwrite an existing raw proof artifact'
    path.write_bytes(data)
    _artifacts[str(path)] = dict(sha256=sha(data), bytes=len(data))
    return str(path)


def child(argv, env, cwd, *, boundary=None, timeout=180):
    """Bound both byte drains; READY must precede an actual nonzero kill."""
    process = subprocess.Popen(list(map(str, argv)), cwd=cwd, env=env, stdin=subprocess.DEVNULL,
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0)
    ready = threading.Event()
    overflow = threading.Event()
    failures = []
    buffers = [bytearray(), bytearray()]
    limits = (4 * 1024 * 1024, 512 * 1024)

    def drain(stream, index):
        pending = bytearray()
        try:
            while True:
                data = stream.read(8192)
                if not data:
                    break
                if len(buffers[index]) + len(data) > limits[index]:
                    overflow.set()
                else:
                    buffers[index].extend(data)
                if index == 0 and boundary is not None:
                    pending.extend(data)
                    while b'\n' in pending:
                        line, _, rest = pending.partition(b'\n')
                        pending[:] = rest
                        if line.rstrip(b'\r') == ('READY ' + boundary).encode():
                            ready.set()
                    if len(pending) > 8192:
                        overflow.set()
                        pending.clear()
        except BaseException as error:
            failures.append(type(error).__name__ + ': ' + str(error)[:512])

    readers = [threading.Thread(target=drain, args=(stream, index), daemon=True)
               for index, stream in enumerate((process.stdout, process.stderr))]
    for reader in readers:
        reader.start()
    killed = False
    alive_at_ready = False
    timed_out = False
    try:
        deadline = time.monotonic() + timeout
        while process.poll() is None:
            if overflow.is_set() or failures:
                process.kill()
                break
            if boundary is not None and ready.is_set():
                alive_at_ready = process.poll() is None
                if alive_at_ready:
                    process.kill()
                    killed = True
                break
            if time.monotonic() >= deadline:
                timed_out = True
                process.kill()
                break
            time.sleep(.02)
        process.wait(timeout=15)
    finally:
        if process.poll() is None:
            process.kill()
            process.wait(timeout=15)
        for reader in readers:
            reader.join(timeout=5)
        drained = all(not reader.is_alive() for reader in readers)
        if drained:
            process.stdout.close()
            process.stderr.close()
    index = len(_commands)
    result = dict(argv=list(map(str, argv)), exitCode=process.returncode,
        stdoutArtifact=artifact(f'{index:02d}-stdout.txt', bytes(buffers[0])),
        stderrArtifact=artifact(f'{index:02d}-stderr.txt', bytes(buffers[1])),
        readersDrained=drained, timedOut=timed_out, outputOverflow=overflow.is_set(),
        readerErrors=failures, readyBoundary=boundary, readyObserved=ready.is_set(),
        processAliveAtReady=alive_at_ready, killRequested=killed)
    _commands.append(result)
    assert drained and not timed_out and not overflow.is_set() and not failures, result
    if boundary is not None:
        assert ready.is_set() and alive_at_ready and killed and process.returncode != 0, result
    return result, bytes(buffers[0]), bytes(buffers[1])


def native_tool(helpers, requested):
    path = helpers.executable(requested)
    assert 0 < path.stat().st_size <= 512 * 1024 * 1024, 'Tool size outside bound'
    with path.open('rb') as source:
        magic = source.read(4)
    kind = 'elf' if magic == b'\x7fELF' else 'pe' if magic[:2] == b'MZ' else 'mach-o' if magic in (
        b'\xcf\xfa\xed\xfe', b'\xfe\xed\xfa\xcf', b'\xca\xfe\xba\xbe', b'\xbe\xba\xfe\xca') else None
    assert kind is not None and path.suffix.lower() != '.dll', 'Expected a native executable, not a script or managed DLL'
    return path, dict(path=str(path), sha256=helpers.digest(path), observedExecutableFormat=kind,
                     qualification='Native/AOT build provenance is supplied by the external frozen-source CI publisher receipt; magic alone is not AOT proof.')


def fixture_environment(root, home, state, gh, proxy):
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
        GIT_CONFIG_GLOBAL=os.devnull, GIT_CONFIG_COUNT='0', GIT_OPTIONAL_LOCKS='0',
        GIT_DIR=str(root / 'private-git'), GIT_WORK_TREE=str(root), GIT_COMMON_DIR=str(root / 'private-git'),
        DOTNET_EnableDiagnostics='0', GH_TELEMETRY='false', GH_NO_UPDATE_NOTIFIER='1',
        GH_NO_EXTENSION_UPDATE_NOTIFIER='1', NO_PROXY='', no_proxy='')
    for name in ('HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'http_proxy', 'https_proxy', 'all_proxy'):
        env[name] = proxy
    return env


def native_objects(directory):
    """Full Windows128-bit IDs; Unix device/inode. Rename times are separate."""
    paths = [directory, *sorted(directory.rglob('*'))]
    assert len(paths) <= 128 and all(not path.is_symlink() for path in paths)
    if os.name != 'nt':
        return [(str(path.relative_to(directory)), path.stat().st_dev, path.stat().st_ino) for path in paths]
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
    rows = []
    for path in paths:
        handle = kernel.CreateFileW(str(path), 0, 7, None, 3, 0x02200000, None)
        assert handle != ctypes.c_void_p(-1).value, ctypes.get_last_error()
        try:
            identity = FileIdInfo()
            assert kernel.GetFileInformationByHandleEx(handle, 18, ctypes.byref(identity), ctypes.sizeof(identity)), ctypes.get_last_error()
            rows.append((str(path.relative_to(directory)), identity.volume, bytes(identity.fileId).hex()))
        finally:
            assert kernel.CloseHandle(handle), ctypes.get_last_error()
    return rows


def main():
    global _output, _raw
    bootstrap_output(sys.argv[1:])
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--old-app', required=True, type=Path)
    binding = parser.add_mutually_exclusive_group(required=True)
    binding.add_argument('--old-checkout', type=Path)
    binding.add_argument('--old-source-receipt', type=Path)
    parser.add_argument('--app', required=True, type=Path)
    parser.add_argument('--proof-host', required=True, type=Path)
    parser.add_argument('--gh', required=True, type=Path)
    parser.add_argument('--kill-boundary', required=True, choices=('retain-moved', 'install-moved'))
    parser.add_argument('--expect-metadata-refusal', metavar='REASON')
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    _output = args.output
    if len(BASELINE) != 40 or any(value not in '0123456789abcdef' for value in BASELINE):
        raise RuntimeError('Old schema5 proof is disabled until the corrected final merged baseline is locked')
    # A fresh raw packet makes old success artifacts unavailable for reuse.
    _raw = args.output.with_name(args.output.stem + '-raw')
    _raw.mkdir(parents=True, exist_ok=False)
    spec = importlib.util.spec_from_file_location('old_native_reimport_fixture_helpers', _HELPERS)
    helpers = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(helpers)
    old, old_tool = native_tool(helpers, args.old_app)
    app, app_tool = native_tool(helpers, args.app)
    host, host_tool = native_tool(helpers, args.proof_host)
    gh, gh_tool = native_tool(helpers, args.gh)
    assert old != app and old_tool['sha256'] != app_tool['sha256'], 'Producer and current consumer must be distinct binaries'
    with tempfile.TemporaryDirectory(prefix='skillview-old-reimport-proof-') as temporary, socket.socket() as blocked:
        root = Path(temporary).resolve()
        blocked.bind(('127.0.0.1', 0))
        home, state = root / 'home', root / 'state'
        home.mkdir(); state.mkdir()
        root.joinpath('disposable-proof-marker').write_bytes(b'SkillView disposable process proof\n')
        env = fixture_environment(root, home, state, gh, 'http://127.0.0.1:' + str(blocked.getsockname()[1]))
        if args.old_checkout:
            checkout = args.old_checkout.resolve(strict=True)
            assert old.is_relative_to(checkout), 'Frozen binary is not beneath its source checkout'
            git_env = {key: value for key, value in env.items() if not key.startswith('GIT_')}
            git_env.update(GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL=os.devnull, GIT_CONFIG_COUNT='0', GIT_OPTIONAL_LOCKS='0')
            def git(*arguments):
                observed, stdout, _ = child(['git', '--no-optional-locks', '-c', 'core.fsmonitor=false', '-C', checkout, *arguments], git_env, root, timeout=30)
                assert observed['exitCode'] == 0, observed
                return stdout.decode('utf-8', 'strict').strip()
            source_receipt = dict(sourceCommit=git('rev-parse', 'HEAD'), trackedClean=not git('status', '--porcelain=v1', '--untracked-files=no', '--ignore-submodules=all'), binarySha256=old_tool['sha256'])
        else:
            assert args.old_source_receipt.stat().st_size <= 128 * 1024
            receipt_bytes = args.old_source_receipt.read_bytes()
            source_receipt = json.loads(receipt_bytes)
            artifact('old-source-receipt.json', receipt_bytes)
        assert source_receipt['sourceCommit'] == BASELINE and source_receipt['trackedClean'] is True, 'Frozen source commit or clean status mismatch'
        assert source_receipt['binarySha256'].lower() == old_tool['sha256'], 'Frozen binary differs from source build receipt'
        version_result, version_bytes, _ = child([gh, '--version'], env, root, timeout=30)
        assert version_result['exitCode'] == 0
        gh_version = frozen_gh_version(version_bytes)
        source = root / 'source-parent' / 'legacy-local'
        source.mkdir(parents=True)
        source.joinpath('SKILL.md').write_bytes(b'---\nname: legacy-local\ndescription: Old source\n---\nInitial C\n')
        custom = root / 'custom'
        installed, _, _ = child([gh, 'skill', 'install', '--from-local', '--dir', custom, '--', source, 'legacy-local'], env, root, timeout=90)
        assert installed['exitCode'] == 0, installed
        payload = custom / 'legacy-local'
        payload.joinpath('installed-only').write_bytes(b'Exact old installed-only payload\x00\xff')
        payload.joinpath('.skillview-ignore').write_bytes(b'Original legacy exclusion marker\r\n')
        payload.joinpath('old-empty').mkdir()
        if os.name != 'nt':
            payload.chmod(0o700)
        helpers.prepare_windows_fixture_owner(payload)
        if os.name == 'nt':
            Path(str(payload) + ':skillview-root-proof').write_bytes(b'Exact original root alternate stream\x00\xff')
        expected_skill = ('---\r\n# Real captured C comment\r\nname: legacy-local\r\ndescription: New local source\r\nmetadata:\r\n  local-path: '
            + json.dumps(str(source)) + ' # Keep comment\r\n  custom-field: original-native-schema5\r\n---\r\nUpdated C bytes\r\n').encode()
        source.joinpath('SKILL.md').write_bytes(expected_skill)
        source.joinpath('empty-directory').mkdir()
        scripts = source / 'scripts'
        scripts.mkdir()
        marker = root / 'PAYLOAD_SCRIPT_RAN'
        scripts.joinpath('never-run.py').write_text('from pathlib import Path\nPath(' + repr(str(marker)) + ').write_text("unexpected")\n')
        if os.name != 'nt':
            scripts.joinpath('never-run.py').chmod(0o751)
        helpers.prepare_windows_fixture_owner(source)
        before, before_ids, before_security = helpers.manifest(payload), native_objects(payload), helpers.root_security(payload)
        source_before, source_ids = helpers.manifest(source), native_objects(source)
        registries = [home / '.agents' / '.skill-lock.json', home / '.copilot' / '.skill-lock.json']
        assert not any(path.exists() for path in registries)

        def run(binary, command, *arguments, expected=0):
            row, stdout, _ = child([binary, '--scan-root', custom, command, *arguments, '--allow-hidden-dirs', '--json'], env, root)
            assert row['exitCode'] == expected, row
            assert not marker.exists() and not any(path.exists() for path in registries)
            return json.loads(stdout)

        prepared = run(old, 'reimport-plan', '--path', payload, expected=1 if args.expect_metadata_refusal else 0)
        result = dict(oldSource=source_receipt, oldHost=old_tool, currentHost=app_tool,
            proofHost=host_tool, gh=gh_tool, ghVersion=gh_version, savedSchemaVersion=5,
            killBoundary=args.kill_boundary, httpProxiesBlocked=True, githubCredentialsRemoved=True,
            privateHomeStateCwdAndGit=True, payloadScriptsExecuted=False,
            scriptSha256=helpers.digest(Path(__file__)), sharedFixtureHelpersSha256=helpers.digest(_HELPERS))
        if args.expect_metadata_refusal:
            assert prepared.get('succeeded') is False and args.expect_metadata_refusal in prepared.get('error', ''), prepared
            assert helpers.manifest(payload) == before and native_objects(payload) == before_ids
            assert helpers.manifest(source) == source_before and native_objects(source) == source_ids
            assert not list(state.rglob('plan.json'))
            result.update(compatibilityVerified=False, oldNativePublicationRefused=True,
                actualOldNativePlanCreated=False, currentConsumerLifecycleExecuted=False,
                explicitMetadataRefusal=args.expect_metadata_refusal, originalAndSourceUnchanged=True)
        else:
            saved = prepared['operation']
            assert saved['succeeded'] and saved['status'] == 'planned'
            plan_path = Path(saved['planPath'])
            frozen = plan_path.read_bytes()
            artifact('old-schema5-plan.json', frozen)
            plan = json.loads(frozen)
            assert plan['schemaVersion'] == 5 and plan['purpose'] == 'local-reimport'
            assert all(field not in plan for field in ('localSourceSwitch', 'revision', 'localDisposition', 'pinPolicy'))
            policy = plan['localImport']
            assert policy['sourcePath'] == str(source) and policy['canonicalSourcePath'] == str(source)
            assert policy['disposition'] == 'replace-local-retain-original'
            assert policy['gh']['binarySha256'].lower() == gh_tool['sha256'], 'Saved gh binary hash differs from the staged native tool'
            assert policy['gh']['version'] == gh_version, 'Saved gh version differs from the complete native version witness'
            frozen_archives = {}
            for slot, fingerprint in [('installed-capture', policy['installedArchiveFingerprint']), ('local-source-capture', policy['sourceArchiveFingerprint']),
                                      ('gh-input-capture', policy['gh']['inputArchiveFingerprint']), ('gh-output-capture', policy['gh']['outputArchiveFingerprint'])]:
                directory = plan_path.parent / slot
                assert helpers.digest(directory / 'manifest.json').lower() == fingerprint.lower()
                frozen_archives[slot] = {str(path.relative_to(directory)): helpers.digest(path) for path in directory.rglob('*') if path.is_file()}
            candidate = Path(saved['candidatePath'])
            candidate_bytes, candidate_ids = helpers.manifest(candidate), native_objects(candidate)
            assert candidate.joinpath('SKILL.md').read_bytes() == expected_skill
            assert candidate.joinpath('empty-directory').is_dir() and not candidate.joinpath('installed-only').exists()
            assert not candidate.joinpath('.skillview-ignore').exists()
            assert helpers.root_security(candidate) == before_security
            if os.name != 'nt':
                assert stat.S_IMODE(candidate.joinpath('scripts/never-run.py').stat().st_mode) == 0o751
            assert helpers.manifest(payload) == before and native_objects(payload) == before_ids
            assert helpers.manifest(source) == source_before and native_objects(source) == source_ids
            inspected = run(app, 'reimport-inspect', '--id', saved['id'])['operation']
            assert inspected['status'] == 'ready' and inspected['planFingerprint'] == saved['planFingerprint']
            assert 'localSourceSwitch' not in inspected and inspected['localImport'] == saved['localImport']
            assert plan_path.read_bytes() == frozen
            killed, _, _ = child([host, root, gh, saved['id'], saved['planFingerprint'], 'apply-reimport', args.kill_boundary], env, root,
                                 boundary=args.kill_boundary, timeout=90)
            interrupted = run(app, 'reimport-inspect', '--id', saved['id'])['operation']
            assert interrupted['status'] == ('original-retained' if args.kill_boundary == 'retain-moved' else 'installed')
            assert interrupted['localSource']['status'] == 'not-required-after-retain'
            assert helpers.manifest(source) == source_before and native_objects(source) == source_ids
            # Delete only the known generated ordinary source after actual retain.
            assert source.is_relative_to(root) and not source.is_symlink()
            shutil.rmtree(source)
            frozen_inspection = run(app, 'reimport-inspect', '--id', saved['id'])['operation']
            assert frozen_inspection['localSource']['status'] == 'not-required-after-retain'
            recovered = run(app, 'reimport-recover', '--id', saved['id'], '--yes', '--expect', frozen_inspection['reviewFingerprint'])['operation']
            assert recovered['succeeded'] and recovered['status'] == 'installed'
            assert helpers.manifest(payload) == candidate_bytes and native_objects(payload) == candidate_ids
            assert helpers.manifest(Path(saved['retainedPath'])) == before and native_objects(Path(saved['retainedPath'])) == before_ids
            assert helpers.root_security(payload) == before_security
            fresh = run(app, 'reimport-inspect', '--id', saved['id'])['operation']
            restored = run(app, 'reimport-undo', '--id', saved['id'], '--yes', '--expect', fresh['reviewFingerprint'])['operation']
            assert restored['succeeded'] and restored['status'] == 'restored'
            assert helpers.manifest(payload) == before and native_objects(payload) == before_ids
            assert helpers.root_security(payload) == before_security
            assert helpers.manifest(Path(saved['displacedPath'])) == candidate_bytes
            assert plan_path.read_bytes() == frozen
            assert restored['planFingerprint'] == saved['planFingerprint']
            assert all('localSourceSwitch' not in item for item in (interrupted, frozen_inspection, recovered, fresh, restored))
            for slot, expected_files in frozen_archives.items():
                directory = plan_path.parent / slot
                assert {str(path.relative_to(directory)): helpers.digest(path) for path in directory.rglob('*') if path.is_file()} == expected_files
            result.update(compatibilityVerified=True, actualOldNativePlanCreated=True,
                currentConsumerLifecycleExecuted=True, actualReadyKillAndDrains=killed,
                frozenRecoveryWithoutExternalSource=True, forwardRecoveryVerified=True,
                exactOriginalNativeObjectsAndBytesRestored=True, originalExclusionAndExtrasRestored=True,
                originalRootPrivacyRestored=True, oldPlanAndArchiveBytesUnchanged=True,
                absentLocalSourceSwitchRemainsOmitted=True, savedPlanSha256=sha(frozen),
                savedPlanFingerprint=saved['planFingerprint'], sourceUnchangedUntilExplicitPostRetainDeletion=True)
        assert not marker.exists() and not any(path.exists() for path in registries)
        for tool in (old_tool, app_tool, host_tool, gh_tool):
            assert helpers.digest(Path(tool['path'])) == tool['sha256'], 'Executable changed during proof'
        result.update(schemaVersion=1, proofCompleted=True, status='completed', commands=_commands,
                      rawArtifacts=_artifacts, mode='metadata-refusal' if args.expect_metadata_refusal else 'actual-old-native-schema5')
        args.output.write_text(json.dumps(result, indent=2) + '\n')
        print(json.dumps(dict(output=str(args.output), compatibilityVerified=result['compatibilityVerified'],
                              mode=result['mode'], commands=len(_commands))))


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        publish('failed', errorType=type(error).__name__, error=str(error)[:4096])
        raise
