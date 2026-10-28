"""Narrow checkpoint/diagnostic support for the two registry native proofs."""
import hashlib
import os
from pathlib import Path
import stat
import subprocess
import threading
import time

MARKER_BYTES = b'SkillView disposable process proof\n'
STREAM_LIMIT = 64 * 1024
CASE_LIMIT = 2 * 1024 * 1024
TOTAL_LIMIT = 32 * 1024 * 1024
SECRET_KEYS = ('GH_TOKEN', 'GITHUB_TOKEN', 'GH_ENTERPRISE_TOKEN')
PHASES = frozenset((
    b'maintenance-proof phase: started',
    b'maintenance-proof phase: inventory-started',
    b'maintenance-proof phase: inventory-completed',
    b'READY retain-moved', b'READY install-moved',
    b'READY retirement-removed-candidate', b'READY retirement-removed-original',
    b'READY retirement-removed-displaced', b'READY retirement-removed-review-installed-candidate',
    b'READY retirement-removed-review-source-candidate',
))


def write_proof_marker(root):
    marker = Path(root) / 'disposable-proof-marker'
    marker.write_bytes(MARKER_BYTES)
    assert marker.read_bytes() == MARKER_BYTES, 'Disposable proof marker must be exact LF bytes'


def _error(record, stage, error):
    # Never include exception messages: they can contain credentials or output.
    value = stage + ': ' + type(error).__name__
    record['diagnosticError'] = (record.get('diagnosticError', '') + '; ' + value).strip('; ')[:512]


def _regular(path):
    info = path.lstat()
    if not stat.S_ISREG(info.st_mode) or getattr(info, 'st_file_attributes', 0) & 1024:
        raise ValueError('Not a regular non-reparse file')
    return info


def _identity(info):
    return info.st_dev, info.st_ino, info.st_size, info.st_mtime_ns, info.st_ctime_ns


def _file_evidence(path, marker=False):
    before = _regular(path)
    fd = os.open(path, os.O_RDONLY | getattr(os, 'O_BINARY', 0) | getattr(os, 'O_NOFOLLOW', 0))
    with os.fdopen(fd, 'rb') as source:
        if _identity(os.fstat(source.fileno())) != _identity(before):
            raise ValueError('File changed before hashing')
        digest = hashlib.sha256()
        size, cr = 0, False
        while chunk := source.read(64 * 1024):
            digest.update(chunk); size += len(chunk); cr |= b'\r' in chunk
        if _identity(os.fstat(source.fileno())) != _identity(before) or _identity(_regular(path)) != _identity(before):
            raise ValueError('File changed while hashing')
    result = dict(bytes=size, sha256=digest.hexdigest())
    if marker:
        result['containsCR'] = cr
    return result


def _launch_bindings(argv, gh):
    host = Path(argv[0])
    base = host.stem if host.suffix.lower() == '.exe' else host.name
    names = [host.name, 'SkillView.MaintenanceProof.dll', 'SkillView.Core.dll',
             base + '.deps.json', base + '.runtimeconfig.json',
             'SkillView.Core.deps.json', 'SkillView.Core.runtimeconfig.json']
    result = {}
    for name in dict.fromkeys(names):
        path = host.parent / name
        try:
            result[name] = _file_evidence(path)
        except FileNotFoundError:
            result[name] = dict(absent=True)
        except Exception as ex:
            result[name] = dict(diagnosticError=type(ex).__name__)
    if gh is not None:
        try:
            result['gh'] = dict(path=str(gh), **_file_evidence(Path(gh)))
        except Exception as ex:
            result['gh'] = dict(diagnosticError=type(ex).__name__)
    return result


class RegistryProofArtifacts:
    def __init__(self, output, stream_limit=STREAM_LIMIT, case_limit=CASE_LIMIT, total_limit=TOTAL_LIMIT):
        output = Path(output)
        self.root = output.with_name(output.stem + '-raw')
        self.stream_limit, self.case_limit, self.total_limit = stream_limit, case_limit, total_limit
        self.total, self.cases, self.counter = 0, {}, 0
        self.summary = dict(directory=str(self.root), artifacts=[], retainedBytes=0)
        try:
            self.root.mkdir(exist_ok=False)
        except Exception as ex:
            _error(self.summary, 'raw-directory', ex)
            self.root = None  # Refuse reuse/overwrite, but never mask a proof result.

    def _artifact(self, name, data, case_key):
        if '/' in name or '\\' in name or name in ('', '.', '..'):
            raise ValueError('Unsafe artifact name')
        if self.root is None:
            raise OSError('Raw publication unavailable')
        if len(data) > self.case_limit - self.cases.get(case_key, 0) or len(data) > self.total_limit - self.total:
            return None
        # Charge the complete requested bytes before writing. A partial write
        # failure cannot leave uncharged files that evade aggregate/case limits.
        self.total += len(data); self.cases[case_key] = self.cases.get(case_key, 0) + len(data)
        self.summary['budgetUsedBytes'] = self.total
        with (self.root / name).open('xb') as target:
            target.write(data)
        item = dict(path=name, bytes=len(data), sha256=hashlib.sha256(data).hexdigest())
        self.summary['retainedBytes'] += len(data)
        self.summary['artifacts'].append(item)
        return item

    def stream(self, path, environment, case_key, record):
        result = dict(bytes=None, sha256=None, retained=0, omitted=None, omittedFlag=True, withheld=None)
        try:
            before = _regular(path)
            try:
                secrets = [environment.get(key, '').encode('utf-8') for key in SECRET_KEYS if environment.get(key, '')]
                overlap = max((len(value) for value in secrets), default=1) - 1
                check_error = False
            except Exception as ex:
                secrets, overlap, check_error = [], 0, True
                _error(record, 'credential-check', ex)
            size, matched, tail = 0, False, b''
            digest = hashlib.sha256()
            with path.open('rb') as source:
                while chunk := source.read(64 * 1024):
                    digest.update(chunk); size += len(chunk)
                    combined = tail + chunk
                    matched |= any(value in combined for value in secrets)
                    tail = combined[-overlap:] if overlap else b''
                if _identity(os.fstat(source.fileno())) != _identity(before) or _identity(_regular(path)) != _identity(before):
                    raise ValueError('Stream changed while inspecting')
            result.update(bytes=size, sha256=digest.hexdigest(), omitted=size, omittedFlag=bool(size))
            if check_error or matched:
                result['withheld'] = 'credential-check-error' if check_error else 'credential-match'
                return result
            if self.root is None:
                raise OSError('Raw publication unavailable')
            available = min(self.stream_limit, self.case_limit - self.cases.get(case_key, 0), self.total_limit - self.total)
            if available <= 0:
                result['withheld'] = 'budget-exhausted'
                return result
            with path.open('rb') as source:
                if size <= available:
                    data = source.read()
                else:
                    head = (available + 1) // 2
                    data = source.read(head)
                    source.seek(size - (available - head))
                    data += source.read(available - head)
                if _identity(os.fstat(source.fileno())) != _identity(before) or _identity(_regular(path)) != _identity(before):
                    raise ValueError('Stream changed before publication')
            # Check retained bytes too: joining head+tail can create a token.
            if any(value in data for value in secrets):
                result['withheld'] = 'credential-match'
                return result
            self.counter += 1
            artifact = self._artifact(f'{self.counter:04d}-{path.name}', data, case_key)
            if artifact is None:
                result['withheld'] = 'budget-exhausted'
                return result
            result.update(artifact=artifact, retained=len(data), omitted=size - len(data), omittedFlag=size != len(data))
        except Exception as ex:
            result['withheld'] = 'diagnostic-error'
            _error(record, 'stream-publication', ex)
        return result


def run_checkpoint_child(argv, environment, cwd, stdout_path, stderr_path, boundary, record,
                         artifacts, case_key, gh=None, ready_timeout=45, failure_message=None):
    argv = list(map(str, argv))
    record.update(argv=argv, cwd=str(cwd), reached=False, exitBeforeKill=None, parentKilled=False, finalExit=None, phase=None)
    try:
        record['bindings'] = _launch_bindings(argv, gh)
    except Exception as ex:
        _error(record, 'launch-bindings', ex)
    try:
        record['marker'] = _file_evidence(Path(cwd) / 'disposable-proof-marker', marker=True)
    except Exception as ex:
        _error(record, 'marker-evidence', ex)
    ready, reader_errors = threading.Event(), []
    phase = [None]
    started = time.monotonic()
    primary = None
    try:
        with Path(stdout_path).open('wb') as stdout, Path(stderr_path).open('wb') as stderr:
            child = subprocess.Popen(argv, env=environment, cwd=cwd, stdin=subprocess.DEVNULL,
                                     stdout=subprocess.PIPE, stderr=stderr)
            def read():
                try:
                    with child.stdout:
                        for line in child.stdout:
                            stdout.write(line); stdout.flush()
                            if line.rstrip(b'\r\n') in PHASES:
                                phase[0] = line.rstrip(b'\r\n')
                            if line.strip() == ('READY ' + boundary).encode():
                                ready.set()
                except Exception as ex:
                    reader_errors.append(ex)
            thread = threading.Thread(target=read, daemon=True); thread.start()
            try:
                assert ready.wait(ready_timeout), failure_message or 'Proof host did not reach ' + boundary
            except BaseException as ex:
                primary = ex
                raise
            finally:
                try:
                    record['exitBeforeKill'] = child.poll()  # BEFORE parent kill.
                    if record['exitBeforeKill'] is None:
                        child.kill(); record['parentKilled'] = True
                    record['finalExit'] = child.wait(timeout=10)
                    thread.join(timeout=10)
                    assert not thread.is_alive(), failure_message or 'Proof output drain did not finish'
                    if reader_errors:
                        raise reader_errors[0]
                except BaseException as ex:
                    if primary is None:
                        raise
                    _error(record, 'cleanup', ex)
    finally:
        record['reached'] = ready.is_set()
        record['elapsedSeconds'] = time.monotonic() - started
        try:
            # The host's fixed stderr phases precede READY; arbitrary text never
            # becomes a phase or gets decoded with a locale-dependent codec.
            if phase[0] is None:
                with Path(stderr_path).open('rb') as source:
                    for line in source:
                        if line.rstrip(b'\r\n') in PHASES:
                            phase[0] = line.rstrip(b'\r\n')
            tokens = [environment.get(key, '').encode('utf-8') for key in SECRET_KEYS if environment.get(key, '')]
            record['phase'] = phase[0].decode('ascii') if phase[0] is not None and not any(t in phase[0] for t in tokens) else None
        except Exception as ex:
            _error(record, 'phase', ex)
        for name, path in [('stdout', Path(stdout_path)), ('stderr', Path(stderr_path))]:
            try:
                record[name] = artifacts.stream(path, environment, case_key, record)
            except Exception as ex:
                _error(record, name, ex)
