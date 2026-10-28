"""Run bounded registry restore proofs with full Linux metadata authority.

Only binaries, proof tools and a private result directory are mounted. With
--gh-config-dir, gh receives its normal existing delegated configuration; the
harness never reads/copies credentials. CI passes its already supplied token
through Docker's environment-name forwarding, without extracting its value.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import time
import uuid


STREAM_LIMIT = 64 * 1024
METADATA_LIMIT = 16 * 1024
INVOCATION_LIMIT = 144 * 1024
TOKEN_NAMES = ('GH_TOKEN', 'GITHUB_TOKEN', 'GH_ENTERPRISE_TOKEN')


class _Unstable(Exception):
    pass


def _error(record, stage, error):
    # Exception messages/args can contain command, output or credentials.
    record['diagnosticError'] = (record.get('diagnosticError', '') + '; ' + stage + ': ' + type(error).__name__.encode('ascii', 'replace')[:64].decode()).strip('; ')[:512]


def _snapshot(info):
    if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1 or getattr(info, 'st_file_attributes', 0) & 1024:
        raise _Unstable()
    return info.st_dev, info.st_ino, info.st_nlink, info.st_size, info.st_mtime_ns, info.st_ctime_ns


def _open_original(path, identity=None):
    before = _snapshot(path.lstat())
    if identity is not None and before[:2] != identity:
        raise _Unstable()
    fd = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | getattr(os, 'O_CLOEXEC', 0))
    try:
        if _snapshot(os.fstat(fd)) != before:
            raise _Unstable()
        return os.fdopen(fd, 'rb'), before
    except BaseException:
        os.close(fd)
        raise


def _unchanged(source, path, before):
    try:
        if _snapshot(os.fstat(source.fileno())) != before or _snapshot(path.lstat()) != before:
            raise _Unstable()
    except OSError:
        raise _Unstable() from None


def _binding(path):
    try:
        with_file, before = _open_original(Path(path))
        with with_file as source:
            digest = hashlib.sha256(); size = 0
            while chunk := source.read(64 * 1024):
                digest.update(chunk); size += len(chunk)
            _unchanged(source, Path(path), before)
        return dict(sha256=digest.hexdigest(), bytes=size)
    except BaseException as ex:
        return dict(unavailable=True, diagnosticError=type(ex).__name__[:64])


def _describe(record, args, command):
    roles = [dict(role=role, destination=dest, readOnly=readonly) for role, dest, readonly in
             [('app', '/proof-app', True), ('tools', '/proof-tools', True), ('output', '/proof-output', False)]]
    bindings = dict(launcher=_binding(Path(__file__)))
    docker = shutil.which('docker')
    bindings['docker'] = _binding(Path(docker)) if docker else dict(unavailable=True)
    for role, item in [('app', args.app), ('old-app', args.old_app), ('proof-host', args.proof_host)]:
        if item:
            item = Path(item)
            bindings[role] = _binding(item)
            base = item.stem if item.suffix == '.exe' else item.name
            for name in dict.fromkeys(('SkillView.Core.dll', 'SkillView.MaintenanceProof.dll', base + '.deps.json', base + '.runtimeconfig.json')):
                # Names are lookup inputs only; arbitrary executable names are never metadata keys.
                label = 'core' if name == 'SkillView.Core.dll' else 'proof' if name == 'SkillView.MaintenanceProof.dll' else 'deps' if name.endswith('.deps.json') else 'runtimeconfig'
                bindings[role + '-' + label] = _binding(item.parent / name)
            if role != 'app': roles.append(dict(role=role, destination='/proof-' + role, readOnly=True))
    for index, item in enumerate(args.gh[:2]):
        bindings['gh-' + str(index)] = _binding(Path(item))
        roles.append(dict(role='gh-' + str(index), destination='/proof-gh-' + str(index), readOnly=True))
    if args.gh_config_dir: roles.append(dict(role='gh-config', destination='/proof-gh-config', readOnly=False))
    if args.dotnet_root: roles.append(dict(role='dotnet', destination='/proof-dotnet', readOnly=True))
    default_image = 'mcr.microsoft.com/dotnet/sdk:10.0.113'
    record.update(bindings=bindings, argvSha256=hashlib.sha256(json.dumps(command).encode()).hexdigest(),
                  argvSafe=dict(redacted=True, executable='docker', action='run', containerName=command[3], privileged=True, network='host',
                                mounts=roles, forwardedEnvironmentNames=['GH_TOKEN', 'GITHUB_TOKEN'],
                                image=default_image if args.image == default_image else dict(sha256=hashlib.sha256(args.image.encode()).hexdigest()),
                                shell=['bash', '-c', '<omitted-script>']),
                  rolesTruncated=len(args.gh) > 2)


def _tokens():
    return [value.encode('utf-8') for name in TOKEN_NAMES if (value := os.environ.get(name, ''))]


def _contains_secret(data, tokens):
    return any(value in data for value in tokens)


def _write_leaf(root, name, data, budget):
    limits = {'launch.json': METADATA_LIMIT, 'stdout.bin': STREAM_LIMIT, 'stderr.bin': STREAM_LIMIT}
    if name not in limits or len(data) > limits[name] or len(data) > budget[0]:
        return False
    budget[0] -= len(data)  # Reserve even if a partial write fails.
    fd = os.open(root / name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    with os.fdopen(fd, 'wb') as target:
        if target.write(data) != len(data): raise OSError()
    return True


def _stream(path, identity, root, name, record, budget):
    result = dict(bytes=None, sha256=None, captureComplete=False, retained=0, omitted=None, omittedFlag=True, withheld=None)
    try:
        if identity is None: raise _Unstable()
        try:
            source, before = _open_original(path, identity)
        except (OSError, _Unstable):
            result['withheld'] = 'substituted-or-unstable'
            return result
        with source:
            try:
                tokens = _tokens(); overlap = max((len(value) for value in tokens), default=1) - 1
            except BaseException as ex:
                tokens = None; overlap = 0; _error(record, 'credential-check', ex)
            digest = hashlib.sha256(); size = 0; head = b''; tail = b''; previous = b''; matched = False
            while chunk := source.read(64 * 1024):
                digest.update(chunk); size += len(chunk)
                head = (head + chunk)[:STREAM_LIMIT]
                tail = (tail + chunk)[-(STREAM_LIMIT // 2):] if STREAM_LIMIT >= 2 else b''
                if tokens is not None:
                    try:
                        combined = previous + chunk
                        matched |= _contains_secret(combined, tokens)
                        previous = combined[-overlap:] if overlap else b''
                    except BaseException as ex:
                        tokens = None; _error(record, 'credential-check', ex)
            _unchanged(source, path, before)
        result.update(bytes=size, sha256=digest.hexdigest(), captureComplete=True, omitted=size, omittedFlag=bool(size))
        data = head if size <= STREAM_LIMIT else head[:STREAM_LIMIT - len(tail)] + tail
        if tokens is not None:
            try: matched |= _contains_secret(data, tokens)
            except BaseException as ex:
                tokens = None; _error(record, 'credential-check', ex)
        if tokens is None or matched:
            result['withheld'] = 'credential-check-error' if tokens is None else 'credential-match'
        elif not _write_leaf(root, name, data, budget):
            result['withheld'] = 'budget-exhausted'
        else:
            result.update(retained=len(data), omitted=size - len(data), omittedFlag=size != len(data),
                          artifact=dict(name=name, bytes=len(data), sha256=hashlib.sha256(data).hexdigest()))
    except _Unstable:
        result['withheld'] = 'substituted-or-unstable'
    except BaseException as ex:
        result['withheld'] = 'diagnostic-error'
        if result['captureComplete']: result['publicationMayBePartial'] = True
        _error(record, 'stream', ex)
    return result


def _publish(root, output, identities, record):
    budget = [INVOCATION_LIMIT]
    for name in ('stdout', 'stderr'):
        record[name] = _stream(output / (name + '.log'), identities.get(name), root, name + '.bin', record, budget)
    record['budgetUsedBytesBeforeMetadata'] = INVOCATION_LIMIT - budget[0]
    data = json.dumps(record, sort_keys=True).encode()
    if len(data) > METADATA_LIMIT:
        record.pop('bindings', None); record['bindingsOmitted'] = True
        data = json.dumps(record, sort_keys=True).encode()
    try:
        reason = 'credential-match' if _contains_secret(data, _tokens()) else None
    except BaseException:
        reason = 'credential-check-error'
    if reason:
        data = json.dumps(dict(schema=1, withheld=reason, bytes=len(data), sha256=hashlib.sha256(data).hexdigest())).encode()
    if len(data) > METADATA_LIMIT:
        data = b'{"schema":1,"withheld":"metadata-budget-exhausted"}'
    _write_leaf(root, 'launch.json', data, budget)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app', required=True, type=Path)
    parser.add_argument('--gh', action='append', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--gh-config-dir', type=Path)
    parser.add_argument('--dotnet-root', type=Path)
    parser.add_argument('--old-app', type=Path)
    parser.add_argument('--proof-host', type=Path)
    parser.add_argument('--retirement', action='store_true', help='Run the dedicated F1/H2 interoperability proof instead')
    parser.add_argument('--image', default='mcr.microsoft.com/dotnet/sdk:10.0.113')
    args = parser.parse_args()
    app = args.app.resolve()
    output = args.output.resolve(); output.mkdir(parents=True, exist_ok=False)
    launch = output.with_name(output.name + '-launch')
    launch.mkdir(mode=0o700, exist_ok=False)
    tools = Path(__file__).resolve().parent
    container = 'skillview-registry-restore-' + uuid.uuid4().hex[:12]
    command = ['docker', 'run', '--name', container, '--privileged', '--network=host',
               '--mount', f'type=bind,src={app.parent},dst=/proof-app,readonly',
               '--mount', f'type=bind,src={tools},dst=/proof-tools,readonly',
               '--mount', f'type=bind,src={output},dst=/proof-output',
               '--env', 'GH_TOKEN', '--env', 'GITHUB_TOKEN']
    proof = ['python3', '/proof-tools/prove-registry-recorded-restore.py', '--app', '/proof-app/' + app.name,
             '--output', '/proof-output/result.json']
    if args.retirement:
        if not args.proof_host or len(args.gh) != 1 or args.old_app:
            parser.error('--retirement requires one --gh and --proof-host, without --old-app')
        proof = ['python3', '/proof-tools/prove-registry-recorded-retirement.py', '--app', '/proof-app/' + app.name,
                 '--output', '/proof-output/result.json']
    for i, item in enumerate(args.gh):
        gh = item.resolve()
        command += ['--mount', f'type=bind,src={gh.parent},dst=/proof-gh-{i},readonly']
        proof += ['--gh', f'/proof-gh-{i}/{gh.name}']
    for key, item in [('old-app', args.old_app), ('proof-host', args.proof_host)]:
        if item:
            item = item.resolve()
            command += ['--mount', f'type=bind,src={item.parent},dst=/proof-{key},readonly']
            proof += ['--proof' if args.retirement and key == 'proof-host' else '--' + key, f'/proof-{key}/{item.name}']
    if args.gh_config_dir:
        command += ['--mount', f'type=bind,src={args.gh_config_dir.resolve()},dst=/proof-gh-config',
                    '--env', 'GH_CONFIG_DIR=/proof-gh-config']
    if args.dotnet_root:
        command += ['--mount', f'type=bind,src={args.dotnet_root.resolve()},dst=/proof-dotnet,readonly',
                    '--env', 'DOTNET_ROOT=/proof-dotnet']
    # Keep private originals inside the container until copied to the bounded
    # output at exit. Do not put fixtures on a bind mount (NO_XDEV authority).
    import shlex
    script = ('set -eu; apt-get update -qq > /proof-output/container-tools.log 2>&1; '
              'apt-get install -y -qq python3 git libunwind8 >> /proof-output/container-tools.log 2>&1; '
              'mkdir -m 700 /proof-private; export TMPDIR=/proof-private; set +e; '
              + shlex.join(proof) + '; result=$?; '
              'mkdir -p /proof-output/private-originals; '
              'tar -C /proof-private --exclude="*/frozen-host" --exclude="*/frozen-app" --exclude="*/frozen-proof" -cf - . | tar -xf - -C /proof-output/private-originals; '
              f'chown -R {os.getuid()}:{os.getgid()} /proof-output; exit "$result"')
    command += [args.image, 'bash', '-c', script]
    record = dict(schema=1, kind='retirement' if args.retirement else 'restore', stage='prelaunch',
                  returnCode=None, launchAttempted=False, elapsedSeconds=None, timeoutSeconds=1800, containerState='unknown')
    identities = {}; started = None
    try:
        _describe(record, args, command)
    except BaseException as ex:
        _error(record, 'description', ex)
    try:
        (output / 'invocation.json').write_text(json.dumps(command, indent=2))
        with (output / 'stdout.log').open('wb') as stdout, (output / 'stderr.log').open('wb') as stderr:
            try:
                for name, handle in [('stdout', stdout), ('stderr', stderr)]:
                    info = os.fstat(handle.fileno()); identities[name] = (info.st_dev, info.st_ino)
            except BaseException as ex:
                _error(record, 'original-handles', ex)
            record.update(stage='attempted', launchAttempted=True); started = time.monotonic()
            result = subprocess.run(command, stdout=stdout, stderr=stderr, timeout=1800)
            record.update(stage='returned', returnCode=result.returncode)
        (output / 'exit.json').write_text(json.dumps(dict(exit=result.returncode)))
        if result.returncode: raise SystemExit(result.returncode)
    except BaseException as ex:
        record['exceptionType'] = type(ex).__name__[:64]
        if record['stage'] != 'returned': record['stage'] = 'timeout' if isinstance(ex, subprocess.TimeoutExpired) else 'exception'
        raise
    finally:
        try:
            if started is not None: record['elapsedSeconds'] = time.monotonic() - started
            _publish(launch, output, identities, record)
        except BaseException as ex:
            _error(record, 'publication', ex)  # Never replace the primary outcome.


if __name__ == '__main__':
    main()
