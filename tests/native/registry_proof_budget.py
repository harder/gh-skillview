"""Bounded private proof observations; never native identity or mutation authority."""
from contextlib import contextmanager
import hashlib
import json
import ntpath
import os
from pathlib import Path, PurePosixPath
import re
import stat
import sys
import uuid

MAX_FILES = 16
MAX_LOG_BYTES = 32 * 1024 * 1024
MAX_LOG_FILE = 8 * 1024 * 1024
MAX_LINES = 100000
MAX_INVOCATIONS = 1024
MAX_TEMPLATE_BYTES = 8 * 1024 * 1024
MAX_TEMPLATE_ENTRIES = 1024
MAX_TEMPLATE_DECISIONS = 128
MAX_TEMPLATE_DIAGNOSTIC_BYTES = 32 * 1024
IS_WINDOWS = os.name == 'nt'
IS_DARWIN = sys.platform == 'darwin'
LOG_NAME = re.compile(r'skillview-\d{4}-\d{2}-\d{2}(?:-\d{3,6})?\.log\Z')
LOG_LINE = re.compile(r'\d{2}:\d{2}:\d{2}\.\d{3} (Debug|Info |Warning|Error) \[([^\]\r\n]+)\] (.*)\Z')
_START = re.compile(r'SkillView invocation=(Standalone|GhExtension) dispatch=(Cli|Tui)\Z')
_COMPLETE = re.compile(r'CLI dispatch completed in [0-9]+ms \(exit -?[0-9]+\)\Z')
CLI_ROLES = ('app', 'extension', 'old-app')
_ROLES = (*CLI_ROLES, 'proof-host')
_FIELDS = ('dev', 'ino', 'mode', 'size', 'mtimeNs', 'ctimeNs')
_PRODUCT_REASONS = ('ok', 'ln', 'nh', 'hc', 'nc', 'ro', 'hi', 'ml', 'le', 'oc', 'lr', 'be', 'na', 'gr', 'ex', 'im', 'ia', 'ii', 'ur', 'ot')
_TEMPLATE_REASONS = ('ok', 'nr', 'rp', 'sz', 'io', 'ln', 'nh', 'hc', 'nc', 'ro', 'hi', 'hu', 'hn', 'te', 'tc', 'ly', 'lk', 'en', 'rg', 'rc', 'cf', 'cm', 'tu', 'tm', 'ts', 'mm', 'qf', 'ot')
_REASONS = frozenset((*_PRODUCT_REASONS, *_TEMPLATE_REASONS, 'dp'))
_ERROR_REASONS = frozenset(('io', 'hu', 'rg', 'cf', 'qf', 'le', 'lr', 'dp', 'ot'))
_EXCEPTION_TYPE_LABELS = {OSError: 'OSError', PermissionError: 'PermissionError', FileNotFoundError: 'FileNotFoundError',
                          NotADirectoryError: 'NotADirectoryError', IsADirectoryError: 'IsADirectoryError',
                          ValueError: 'ValueError', TypeError: 'TypeError', UnicodeError: 'UnicodeError',
                          UnicodeDecodeError: 'UnicodeDecodeError'}
_TYPE_LABELS = (*_EXCEPTION_TYPE_LABELS.values(), 'other')


class ObservationError(ValueError):
    """Fixed vocabulary only. The caller, not the exception, owns stage."""
    def __init__(self, reason, fields=()):
        self.reason = reason if reason in _REASONS else 'ot'
        self.fields = tuple(sorted(set(fields).intersection(_FIELDS)))
        super().__init__(self.reason)


def _outcome(reason):
    return 'accepted' if reason == 'ok' else 'error' if reason in _ERROR_REASONS else 'refused'


def _failure(error, fallback):
    if isinstance(error, ObservationError):
        return error.reason, error.fields
    return (fallback if isinstance(error, OSError) or fallback == 'rg' and isinstance(error, (ValueError, UnicodeError)) else 'ot'), ()


def _zeros(keys):
    return {key: 0 for key in keys}


def _number(value):
    return value if type(value) is int and 0 <= value <= 2**63 - 1 else None


def _exception_label(error):
    return _EXCEPTION_TYPE_LABELS.get(type(error), 'other')


def cli_role(executable, app, old_app=None, proof=None):
    if str(executable) == str(app):
        return 'extension' if app.name.startswith('gh-skillview') else 'app'
    for role, candidate in [('old-app', old_app), ('proof-host', proof)]:
        try:
            if candidate is not None and str(executable) == str(candidate.resolve()):
                return role
        except Exception:
            pass
    return None


def product_environment(env, role):
    return dict(env, SKILLVIEW_LOG='debug') if role in CLI_ROLES else env


def public_stderr(raw, role):
    return 'Private stderr retained in proof receipt' if role in CLI_ROLES else raw.decode(errors='replace')


def _signature(info):
    return (info.st_dev, info.st_ino, info.st_mode, info.st_size, info.st_mtime_ns, info.st_ctime_ns)


def _ordinary(info, maximum):
    if not stat.S_ISREG(info.st_mode):
        raise ObservationError('nr')
    if getattr(info, 'st_file_attributes', 0) & 0x400:
        raise ObservationError('rp')
    if info.st_size < 0 or info.st_size > maximum:
        raise ObservationError('sz', ('size',))
    return _signature(info)


def _same(before, after, reason, indices=range(6)):
    fields = [_FIELDS[i] for i in indices if before[i] != after[i]]
    if fields:
        raise ObservationError(reason, fields)


def _open_bytes(path):
    fd = os.open(path, os.O_RDONLY | getattr(os, 'O_BINARY', 0) | getattr(os, 'O_NOFOLLOW', 0))
    try:
        return os.fdopen(fd, 'rb')
    except BaseException:
        # Ownership transfers only after fdopen succeeds. Cleanup never masks it.
        try:
            os.close(fd)
        except Exception:
            pass
        raise


def _regular_bytes(path, maximum):
    first = second = None
    primary = False
    try:
        before = _ordinary(path.lstat(), maximum)
        first = _open_bytes(path)
        opened = _ordinary(os.fstat(first.fileno()), maximum)
        if IS_WINDOWS:
            missing = [_FIELDS[i] for i in (0, 1) if opened[i] == 0]
            if missing:
                raise ObservationError('hi', missing)
        else:
            _same(before, opened, 'nh')
        data = first.read(maximum + 1)
        if len(data) > maximum:
            raise ObservationError('sz', ('size',))
        if len(data) != opened[3]:
            raise ObservationError('ln', ('size',))
        _same(opened, _ordinary(os.fstat(first.fileno()), maximum), 'hc')
        _same(before, _ordinary(path.lstat(), maximum), 'nc')
        if IS_WINDOWS:
            # Name and handle stat domains stay separate, even for mode/ctime.
            _same(before, _ordinary(path.lstat(), maximum), 'nc')
            second = _open_bytes(path)
            reopened = _ordinary(os.fstat(second.fileno()), maximum)
            _same(opened, reopened, 'ro', (0, 1, 3, 4))
            _same(reopened, _ordinary(os.fstat(second.fileno()), maximum), 'hc')
            _same(before, _ordinary(path.lstat(), maximum), 'nc')
            _same(opened, _ordinary(os.fstat(first.fileno()), maximum), 'hc')
        return data, before
    except OSError:
        primary = True
        raise ObservationError('io') from None
    except BaseException:
        primary = True
        raise
    finally:
        cleanup_failed = False
        for stream in (second, first):
            if stream is not None:
                try:
                    stream.close()
                except Exception:
                    cleanup_failed = True
        if cleanup_failed and not primary:
            raise ObservationError('io') from None


class _LogSnapshot:
    def __init__(self, state, files=None):
        self.state = state
        self.files = {} if files is None else files


def _log_entries(directory):
    names = []
    with os.scandir(directory) as children:
        for item in children:
            names.append(item.name)
            if len(names) > MAX_FILES:
                raise ObservationError('be')
    entries = {}
    for name in sorted(names):
        if not LOG_NAME.fullmatch(name):
            raise ObservationError('lr')
        info = (directory / name).lstat()
        if not stat.S_ISREG(info.st_mode) or getattr(info, 'st_file_attributes', 0) & 0x400:
            raise ObservationError('lr')
        entries[name] = _signature(info)
    total = 0
    for signature in entries.values():
        total += signature[3]
        if signature[3] < 0 or signature[3] > MAX_LOG_FILE or total > MAX_LOG_BYTES:
            raise ObservationError('be')
    return entries


def _log_snapshot(env, case_root):
    try:
        try:
            home = Path(env['HOME'])
            base = Path(env['LOCALAPPDATA']) if IS_WINDOWS else home / 'Library/Caches' if IS_DARWIN else Path(env['XDG_CACHE_HOME'])
        except (KeyError, TypeError):
            raise ObservationError('le') from None
        directory = base / 'SkillView/logs'
        try:
            relative = directory.relative_to(case_root)
        except ValueError:
            raise ObservationError('oc') from None
        if '..' in relative.parts or not Path(case_root).is_absolute():
            raise ObservationError('oc')
        current = Path(case_root)
        for name in ('', *relative.parts):
            current = current / name
            try:
                info = current.lstat()
            except FileNotFoundError:
                return _LogSnapshot('absent')
            if not stat.S_ISDIR(info.st_mode) or getattr(info, 'st_file_attributes', 0) & 0x400:
                raise ObservationError('lr')
        entries = _log_entries(directory)
        result = {}; total = 0
        for name in entries:
            try:
                data, signature = _regular_bytes(directory / name, MAX_LOG_FILE)
            except ObservationError as error:
                reason = {'nr': 'lr', 'rp': 'lr', 'io': 'lr', 'sz': 'be'}.get(error.reason, error.reason)
                raise ObservationError(reason, error.fields) from None
            total += len(data)
            if total > MAX_LOG_BYTES:
                raise ObservationError('be')
            result[name] = (signature, data)
        after = _log_entries(directory)
        if set(entries) != set(after) or any(entries[n] != result[n][0] or after[n] != result[n][0] for n in after):
            raise ObservationError('lr')
        return _LogSnapshot('present', result)
    except OSError:
        raise ObservationError('lr') from None


def _new_api_commands(before, after, gh):
    # Dict support preserves the existing direct fake-byte test seam.
    if isinstance(before, dict):
        before = _LogSnapshot('present', before)
    if isinstance(after, dict):
        after = _LogSnapshot('present', after)
    if set(before.files) - set(after.files):
        raise ObservationError('na')
    if before.state == after.state == 'absent':
        raise ObservationError('ml')
    chunks = []
    for name in sorted(after.files):
        signature, data = after.files[name]
        if name in before.files:
            old_signature, old_data = before.files[name]
            if signature[:2] != old_signature[:2] or not data.startswith(old_data):
                raise ObservationError('na')
            chunks.append(data[len(old_data):])
        else:
            chunks.append(data)
    parsed = []; lines = 0
    for chunk in chunks:
        if chunk and not chunk.endswith(b'\n'):
            raise ObservationError('gr')
        try:
            text = chunk.decode('utf-8', errors='strict')
        except UnicodeError:
            raise ObservationError('gr') from None
        for line in text.splitlines():
            lines += 1
            if lines > MAX_LINES or len(line) > 32768:
                raise ObservationError('be')
            match = LOG_LINE.fullmatch(line)
            if match is None:
                raise ObservationError('gr')
            level, category, message = match.groups()
            if category == 'subprocess' and level == 'Debug' and message.startswith('exec: '):
                if message.endswith('… truncated') or '[truncated]' in message:
                    raise ObservationError('gr')
            parsed.append((level, category, message))
    path = str(gh)
    if any(c in path for c in ('\r', '\n', '"', "'")):
        raise ObservationError('ex')
    prefixes = [path + ' ', '"' + path + '" ', "'" + path + "' "]
    count = 0
    for level, category, message in parsed:
        if category != 'subprocess' or level != 'Debug' or not message.startswith('exec: '):
            continue
        command = message[6:]
        prefix = next((p for p in prefixes if command.startswith(p)), None)
        if prefix is not None:
            rest = command[len(prefix):]
            if not rest:
                raise ObservationError('ex')
            count += rest == 'api' or rest.startswith('api ')
        else:
            first = command.split(' ', 1)[0].strip('"\'')
            if ntpath.basename(first).lower() not in ('git', 'git.exe'):
                raise ObservationError('ex')
    starts = []; completions = 0
    for level, category, message in parsed:
        if category == 'startup':
            if level == 'Info ':
                marker = _START.fullmatch(message)
                if marker:
                    starts.append(marker[2])
            elif level == 'Debug' and _COMPLETE.fullmatch(message):
                completions += 1
    if len(starts) > 1 or completions > 1:
        raise ObservationError('ia')
    if 'Cli' not in starts:
        raise ObservationError('im')
    if not completions:
        raise ObservationError('ii')
    return count


class ProductCommands:
    def __init__(self, diagnostics):
        self.diagnostics = diagnostics
        self.rows = []
        self.overflow = False
        self._publication_failed = False
        self._reasons = {r: _zeros(_PRODUCT_REASONS) for r in _ROLES}
        self._fields = {r: _zeros(sorted(_FIELDS)) for r in _ROLES}
        self._types = {r: _zeros(_TYPE_LABELS) for r in _ROLES}
        self._safe_publish()

    def _publish(self):
        known = sum(row['count'] or 0 for row in self.rows)
        unknown = sum(row['count'] is None for row in self.rows)
        incomplete = self.overflow or self._publication_failed
        maps = [[_number(v) for group in m.values() for v in group.values()]
                for m in (self._reasons, self._fields, self._types)]
        incomplete = incomplete or any(None in values for values in maps)
        d = dict(productGhApiCommands=known if self.rows and not unknown and not incomplete else None,
                 knownProductGhApiCommands=_number(known), unobservedInvocations=unknown,
                 invocationBudgetExceeded=self.overflow, productInvocations=[dict(r) for r in self.rows],
                 productGhApiCommandsUnit='retained-prestart-exec-records-lower-bound',
                 httpRequests=None, httpRequestsReason='transport-unobserved',
                 unknownReason='Command logs are not HTTP counts; missing/unsupported evidence stays unknown; gh pagination/internal and other proofs unknown',
                 productObservationReasonCounts={r: {k: _number(v) for k, v in group.items()} for r, group in self._reasons.items()},
                 productObservationFieldCounts={r: {k: _number(v) for k, v in group.items()} for r, group in self._fields.items()},
                 productObservationExceptionTypeCounts={r: {k: _number(v) for k, v in group.items()} for r, group in self._types.items()},
                 productObservationPublicationFailed=self._publication_failed)
        for key, field in [('byHost', 'host'), ('byCaseKind', 'kind')]:
            groups = {}
            for row in self.rows:
                group = groups.setdefault(row[field], dict(knownCommands=0, unknownInvocations=0, count=0))
                group['knownCommands'] += row['count'] or 0
                group['unknownInvocations'] += row['count'] is None
            for group in groups.values():
                group['count'] = None if group['unknownInvocations'] or incomplete else group['knownCommands']
            d[key] = groups
        self.diagnostics.update(d)

    def _safe_publish(self):
        try:
            self._publish()
        except Exception:
            self._publication_failed = True
            try:
                self.diagnostics.update(productObservationPublicationFailed=True, productGhApiCommands=None)
                for key in ('byHost', 'byCaseKind'):
                    for group in self.diagnostics.get(key, {}).values():
                        group['count'] = None
            except Exception:
                pass

    @contextmanager
    def observe(self, env, gh, role, kind, case_root):
        if role is None:
            yield
            return
        before = error = None
        if role in CLI_ROLES:
            try:
                before = _log_snapshot(env, case_root)
            except Exception as caught:
                error = caught
        try:
            yield
        finally:
            count = None; reason = 'ur'; fields = ()
            try:
                if role in CLI_ROLES:
                    if error is None:
                        try:
                            count = _new_api_commands(before, _log_snapshot(env, case_root), gh)
                        except Exception as caught:
                            error = caught
                    if error is None:
                        reason = 'ok'
                    else:
                        reason, fields = _failure(error, 'lr')
                self._record(role, kind, count, reason, fields, error)
            except Exception:
                # No second row, no secondary exception replacing the command.
                self._publication_failed = True
                self._safe_publish()

    def _record(self, role, kind, count, reason=None, fields=(), error=None):
        if role not in _ROLES or not re.fullmatch('[a-z][a-z0-9-]{0,63}', kind):
            self._publication_failed = True
            self._safe_publish()
            return
        reason = reason if reason in _PRODUCT_REASONS else 'ok' if count is not None else 'ot'
        self._reasons[role][reason] += 1
        for field in fields:
            self._fields[role][field] += 1
        if error is not None:
            self._types[role][_exception_label(error)] += 1
        if len(self.rows) >= MAX_INVOCATIONS:
            self.overflow = True
        else:
            self.rows.append(dict(ordinal=len(self.rows) + 1, host=role, kind=kind, count=count, reason=reason))
        self._safe_publish()


def _home_manifest(home):
    result = {}; total = 0
    def visit(parent, prefix='', depth=0):
        nonlocal total
        if depth > 16:
            raise ObservationError('tc')
        with os.scandir(parent) as children:
            bounded = []
            for child in children:
                if len(bounded) + len(result) >= MAX_TEMPLATE_ENTRIES:
                    raise ObservationError('tc')
                bounded.append(child)
            for child in sorted(bounded, key=lambda e: e.name):
                name = prefix + child.name
                if len(result) >= MAX_TEMPLATE_ENTRIES or not (name in ('.agents', '.agents/skills', '.agents/.skill-lock.json') or name.startswith('.agents/skills/gh-skill/')) and name != '.agents/skills/gh-skill':
                    raise ObservationError('ly')
                p = home / name; info = p.lstat()
                if getattr(info, 'st_file_attributes', 0) & 0x400 and not stat.S_ISLNK(info.st_mode):
                    raise ObservationError('rp')
                mode = stat.S_IMODE(info.st_mode) if not IS_WINDOWS else None
                value = dict(kind=None, mode=mode, size=None, sha256=None, target=None)
                if stat.S_ISLNK(info.st_mode):
                    target = os.readlink(p)
                    if ntpath.isabs(target) or PurePosixPath(target).is_absolute() or ntpath.splitdrive(target)[0]:
                        raise ObservationError('lk')
                    lexical = os.path.normpath(os.path.join(os.path.dirname(name), target.replace('\\', '/'))).replace('\\', '/')
                    if lexical.startswith('../') or lexical == '..':
                        raise ObservationError('lk')
                    if _signature(p.lstat()) != _signature(info):
                        raise ObservationError('lk')
                    value.update(kind='link', target=target, size=len(os.fsencode(target)))
                elif stat.S_ISDIR(info.st_mode):
                    value['kind'] = 'directory'
                elif stat.S_ISREG(info.st_mode):
                    data, _ = _regular_bytes(p, MAX_TEMPLATE_BYTES - total)
                    total += len(data)
                    value.update(kind='file', size=len(data), sha256=hashlib.sha256(data).hexdigest())
                else:
                    raise ObservationError('en')
                result[name] = value
                if value['kind'] == 'directory':
                    visit(p, name + '/', depth + 1)
        return result
    root_info = home.lstat()
    if not stat.S_ISDIR(root_info.st_mode) or getattr(root_info, 'st_file_attributes', 0) & 0x400:
        raise ObservationError('hu')
    visit(home)
    for name, value in result.items():
        if value['kind'] == 'link':
            target = os.path.normpath(os.path.join(os.path.dirname(name), value['target'].replace('\\', '/'))).replace('\\', '/')
            if target not in result or result[target]['kind'] == 'link':
                raise ObservationError('lk')
    return result


def _copy_entries(source, destination, manifest):
    for name, value in sorted(manifest.items(), key=lambda item: (item[0].count('/'), item[0])):
        p = destination / name
        if value['kind'] == 'directory':
            p.mkdir()
        elif value['kind'] == 'link':
            target = os.path.normpath(os.path.join(os.path.dirname(name), value['target'].replace('\\', '/'))).replace('\\', '/')
            os.symlink(value['target'], p, target_is_directory=manifest[target]['kind'] == 'directory')
        else:
            data, _ = _regular_bytes(source / name, MAX_TEMPLATE_BYTES)
            with p.open('xb') as stream:
                stream.write(data)
        if value['mode'] is not None and value['kind'] == 'file':
            p.chmod(value['mode'])
    for name, value in sorted(manifest.items(), key=lambda item: item[0].count('/'), reverse=True):
        if value['kind'] == 'directory' and value['mode'] is not None:
            (destination / name).chmod(value['mode'])


class _TemplateDecisions:
    """Bounded observation state, deliberately separate from template authority."""
    def __init__(self, diagnostics):
        self.diagnostics = diagnostics
        self._rows = []
        self._observed = 0
        self._reasons = _zeros(_TEMPLATE_REASONS)
        self._outcomes = _zeros(('accepted', 'refused', 'error'))
        self._fields = _zeros(sorted(_FIELDS))
        self._types = _zeros(_TYPE_LABELS)
        self._tools = {}; self._keys = {}
        self._truncated = self._publication_failed = self._counter_overflow = False
        self._storage_closed = False
        self._safe_publish()

    def _id(self, mapping, key):
        if key is None:
            return None
        if key in mapping:
            return mapping[key]
        if len(mapping) >= MAX_TEMPLATE_DECISIONS:
            self._truncated = True
            return None
        mapping[key] = len(mapping) + 1
        return mapping[key]

    def _bounded(self, component):
        wrapper = {'commandDiagnostics': {'templateDecisions': component}}
        return (len(json.dumps(wrapper, ensure_ascii=True, separators=(',', ':')).encode('utf-8')) <= MAX_TEMPLATE_DIAGNOSTIC_BYTES
                and len(json.dumps(wrapper, ensure_ascii=True, indent=2).encode('utf-8')) <= MAX_TEMPLATE_DIAGNOSTIC_BYTES)

    def _component(self):
        maps = (self._reasons, self._outcomes, self._fields, self._types)
        if _number(self._observed) is None or any(_number(v) is None for m in maps for v in m.values()):
            self._counter_overflow = self._truncated = True
        complete = not (self._truncated or self._publication_failed or self._counter_overflow)
        totals = {key: sum(row['outcome'] == key for row in self._rows) if complete else None for key in self._outcomes}
        return dict(version=1, rows=[dict(row, fields=list(row['fields'])) for row in self._rows],
                    truncated=self._truncated, publicationFailed=self._publication_failed,
                    counterOverflow=self._counter_overflow, observedDecisions=_number(self._observed),
                    reasonCounts={k: _number(v) for k, v in self._reasons.items()},
                    outcomeCounts={k: _number(v) for k, v in self._outcomes.items()},
                    fieldCounts={k: _number(v) for k, v in self._fields.items()},
                    exceptionTypeCounts={k: _number(v) for k, v in self._types.items()}, totals=totals)

    def _safe_publish(self):
        try:
            component = self._component()
            while not self._bounded(component) and self._rows:
                self._storage_closed = True
                self._truncated = True
                self._rows.pop()
                component = self._component()
            if not self._bounded(component):
                raise ObservationError('dp')
            self.diagnostics.update(templateDecisions=component)
        except Exception:
            self._publication_failed = self._truncated = True
            minimal = dict(version=1, rows=[], truncated=True, publicationFailed=True,
                           counterOverflow=self._counter_overflow, observedDecisions=None,
                           reasonCounts=None, outcomeCounts=None, fieldCounts=None,
                           exceptionTypeCounts=None, totals=None)
            try:
                if self._bounded(minimal):
                    self.diagnostics.update(templateDecisions=minimal)
                else:
                    self.diagnostics.pop('templateDecisions', None)
            except Exception:
                pass

    def observe(self, phase, gh, pinned, key, reason, fields, stage, home_state, error=None, fatal=False):
        reason = reason if reason in _TEMPLATE_REASONS else 'ot'
        outcome = 'error' if fatal else _outcome(reason)
        self._observed += 1
        self._reasons[reason] += 1
        self._outcomes[outcome] += 1
        for field in fields:
            self._fields[field] += 1
        if error is not None:
            self._types[_exception_label(error)] += 1
        tool_id = self._id(self._tools, str(gh))
        key_id = self._id(self._keys, key)
        try:
            raw_count = self.diagnostics['directGhCommands']['skillInstall']
            count = _number(raw_count)
            if count is None:
                self._truncated = True
                if type(raw_count) is int:
                    self._counter_overflow = True
        except Exception:
            count = None
            self._truncated = True
        row = dict(phase=phase, pinned=bool(pinned), toolOrdinal=tool_id, keyOrdinal=key_id,
                   installCount=count, outcome=outcome, reason=reason, fields=list(fields),
                   stage=stage, homeState=home_state)
        if phase == 'reuse':
            row['fallbackRequired'] = reason != 'ok' and not fatal
        # Unknown metadata makes totals incomplete, but does not discard its row.
        # Only actual storage bounds or publication faults close row admission.
        if not self._storage_closed and not self._publication_failed:
            if len(self._rows) >= MAX_TEMPLATE_DECISIONS:
                self._storage_closed = True
                self._truncated = True
            else:
                self._rows.append(row)
        self._safe_publish()


class InstallTemplates:
    def __init__(self, root, diagnostics=None):
        root.mkdir(mode=0o700)
        self.root = root
        self.templates = {}
        self.used = set()
        self.mode_evidence = 'windows-mode-unknown' if IS_WINDOWS else 'posix-mode'
        self._decisions = None
        if diagnostics is not None:
            try:
                self._decisions = _TemplateDecisions(diagnostics)
            except Exception:
                pass

    def _observe(self, *args, **kwargs):
        # This boundary is always outside the result-bearing copy/registry try.
        if self._decisions is not None:
            try:
                self._decisions.observe(*args, **kwargs)
            except Exception:
                try:
                    self._decisions._publication_failed = True
                    self._decisions._safe_publish()
                except Exception:
                    pass

    @staticmethod
    def key(gh, pinned, source):
        data, _ = _regular_bytes(gh, 128 * 1024 * 1024)
        return hashlib.sha256(json.dumps([hashlib.sha256(data).hexdigest(), pinned, source], separators=(',', ':')).encode()).hexdigest()

    @staticmethod
    def before(home):
        try:
            return _home_manifest(home)
        except Exception:
            return None

    def capture(self, gh, pinned, source, home, cwd, case_root, before):
        if source != 'skills/gh-skill':
            return False
        stage = 'before-home'; key = None; home_state = 'not-checked'
        error = None; fields = ()
        def perform():
            nonlocal stage, key, home_state
            if before is None:
                home_state = 'unobserved'
                return False, 'hu'
            if before != {}:
                home_state = 'nonempty'
                return False, 'hn'
            home_state = 'empty'; stage = 'key'
            key = self.key(gh, pinned, source)
            if key in self.templates:
                return False, 'te'
            if len(self.templates) >= 4:
                return False, 'tc'
            stage = 'home'; home_state = 'unobserved'
            manifest = _home_manifest(home)
            home_state = 'nonempty' if manifest else 'empty'
            stage = 'required-layout'
            required = {'.agents': 'directory', '.agents/skills': 'directory', '.agents/skills/gh-skill': 'directory', '.agents/.skill-lock.json': 'file'}
            if any(manifest.get(n, {}).get('kind') != kind for n, kind in required.items()):
                return False, 'ly'
            stage = 'registry-read'
            raw, _ = _regular_bytes(home / '.agents/.skill-lock.json', MAX_TEMPLATE_BYTES)
            stage = 'registry-json'; registry = json.loads(raw)
            contexts = {str(p).replace('\\', '/').casefold() for p in (home, cwd, case_root)}
            def bound(value):
                if isinstance(value, str):
                    return any(s in value.replace('\\', '/').casefold() for s in contexts)
                if isinstance(value, dict):
                    return any(bound(k) or bound(v) for k, v in value.items())
                return isinstance(value, list) and any(bound(v) for v in value)
            stage = 'context'
            if bound(registry):
                return False, 'rc'
            stage = 'store'
            parent = self.root / key; parent.mkdir(mode=0o700)
            payload = parent / 'home'; payload.mkdir(mode=0o700)
            stage = 'copy'; _copy_entries(home, payload, manifest)
            if _home_manifest(home) != manifest or _home_manifest(payload) != manifest:
                return False, 'cm'
            body = json.dumps(manifest, sort_keys=True).encode()
            with (parent / 'manifest.json').open('xb') as stream:
                stream.write(body)
            self.templates[key] = (manifest, hashlib.sha256(body).hexdigest())
            stage = 'done'
            return True, 'ok'
        try:
            result, reason = perform()
        except Exception as caught:
            error = caught
            reason, fields = _failure(caught, 'hu' if stage == 'home' else 'rg' if stage in ('registry-read', 'registry-json') else 'cf' if stage in ('copy', 'store') else 'io')
            result = False
        self._observe('capture', gh, pinned, key, reason, fields, stage, home_state, error)
        return result

    def reuse(self, gh, pinned, source, home):
        if source != 'skills/gh-skill':
            return None
        copied = False; key = None; stage = 'home'; home_state = 'unobserved'
        error = None; fields = ()
        def perform():
            nonlocal copied, key, stage, home_state
            manifest_now = _home_manifest(home)
            home_state = 'nonempty' if manifest_now else 'empty'
            if manifest_now != {}:
                return None, 'hn'
            stage = 'key'; key = self.key(gh, pinned, source)
            if key in self.used:
                return None, 'tu'
            if key not in self.templates:
                return None, 'tm'
            manifest, expected = self.templates[key]; parent = self.root / key
            stage = 'store'
            for p in (self.root, parent, parent / 'home'):
                info = p.lstat()
                if not stat.S_ISDIR(info.st_mode) or getattr(info, 'st_file_attributes', 0) & 0x400:
                    return None, 'ts'
            names = []
            with os.scandir(parent) as children:
                for child in children:
                    if len(names) >= 2:
                        return None, 'ts'
                    names.append(child.name)
            if set(names) != {'home', 'manifest.json'}:
                return None, 'ts'
            stage = 'manifest-read'
            raw, _ = _regular_bytes(parent / 'manifest.json', 1024 * 1024)
            stage = 'manifest-check'
            if hashlib.sha256(raw).hexdigest() != expected or _home_manifest(parent / 'home') != manifest:
                return None, 'mm'
            stage = 'copy'; copied = True
            _copy_entries(parent / 'home', home, manifest)
            if _home_manifest(home) != manifest or _home_manifest(parent / 'home') != manifest:
                raise ObservationError('cm')
            self.used.add(key)
            stage = 'done'
            return 'template:' + key, 'ok'
        try:
            result, reason = perform()
        except Exception as caught:
            error = caught
            reason, fields = _failure(caught, 'hu' if stage == 'home' else 'cf' if stage in ('copy', 'store') else 'io')
            result = None
        fatal_error = None
        if error is not None and copied:
            try:
                # Preserve partial evidence, never recursively delete it.
                mode = stat.S_IMODE(home.lstat().st_mode)
                home.rename(home.with_name('refused-template-home-' + uuid.uuid4().hex))
                home.mkdir(mode=mode)
            except Exception as caught:
                fatal_error = caught
        self._observe('reuse', gh, pinned, key, reason, fields, stage, home_state, error, fatal_error is not None)
        if fatal_error is not None:
            self._observe('quarantine', gh, pinned, key, 'qf', (), 'quarantine', 'unobserved', fatal_error)
            raise RuntimeError('Template quarantine failed') from None
        return result
