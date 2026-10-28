"""Private proof diagnostics and one-invocation install fixtures, never authority."""
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
LOG_NAME = re.compile(r'skillview-\d{4}-\d{2}-\d{2}(?:-\d{3,6})?\.log\Z')
LOG_LINE = re.compile(r'\d{2}:\d{2}:\d{2}\.\d{3} (Debug|Info |Warn |Error) \[([^\]\r\n]+)\] (.*)\Z')
CLI_ROLES = ('app', 'extension', 'old-app')


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


def _regular_bytes(path, maximum):
    before = path.lstat()
    if not stat.S_ISREG(before.st_mode) or before.st_size > maximum:
        raise ValueError('unsupported file')
    fd = os.open(path, os.O_RDONLY | getattr(os, 'O_BINARY', 0) | getattr(os, 'O_NOFOLLOW', 0))
    with os.fdopen(fd, 'rb') as stream:
        opened = os.fstat(stream.fileno())
        if _signature(before) != _signature(opened):
            raise ValueError('changed file')
        data = stream.read(maximum + 1)
        after = os.fstat(stream.fileno())
    if len(data) > maximum or _signature(after) != _signature(before) or _signature(path.lstat()) != _signature(before):
        raise ValueError('changed file')
    return data, _signature(before)


def _log_snapshot(env, case_root):
    home = Path(env['HOME'])
    base = Path(env['LOCALAPPDATA']) if sys.platform == 'win32' else home / 'Library/Caches' if sys.platform == 'darwin' else Path(env['XDG_CACHE_HOME'])
    directory = base / 'SkillView/logs'
    relative = directory.relative_to(case_root)
    if '..' in relative.parts or not stat.S_ISDIR(case_root.lstat().st_mode):
        raise ValueError('outside private context')
    current = case_root
    for name in relative.parts:
        current = current / name
        if not current.exists() and not current.is_symlink():
            return {}
        if not stat.S_ISDIR(current.lstat().st_mode):
            raise ValueError('unsupported log directory')
    result = {}; total = 0
    with os.scandir(directory) as children:
        for item in children:
            if len(result) >= MAX_FILES or not LOG_NAME.fullmatch(item.name):
                raise ValueError('unbounded logs')
            data, signature = _regular_bytes(directory / item.name, MAX_LOG_FILE)
            total += len(data)
            if total > MAX_LOG_BYTES:
                raise ValueError('unbounded logs')
            result[item.name] = (signature, data)
    # Recheck the namespace as well as the opened files after all bounded reads.
    names = set()
    with os.scandir(directory) as children:
        for item in children:
            if len(names) >= MAX_FILES:
                raise ValueError('unbounded namespace')
            names.add(item.name)
    if set(result) != names:
        raise ValueError('unstable logs')
    if any(_signature((directory / name).lstat()) != value[0] for name, value in result.items()):
        raise ValueError('unstable logs')
    return result


def _new_api_commands(before, after, gh):
    chunks = []
    for name, (signature, data) in after.items():
        if name in before:
            old_signature, old_data = before[name]
            if signature[:2] != old_signature[:2] or not data.startswith(old_data):
                raise ValueError('nonappend log')
            chunks.append(data[len(old_data):])
        else:
            chunks.append(data)
    if set(before) - set(after):
        raise ValueError('lost rollover evidence')
    path = str(gh)
    if any(c in path for c in ('\r', '\n', '"', "'")):
        raise ValueError('ambiguous executable')
    prefixes = [path + ' ', '"' + path + '" ', "'" + path + "' "]
    count = startups = lines = 0
    for chunk in chunks:
        if chunk and not chunk.endswith(b'\n'):
            raise ValueError('partial log')
        for line in chunk.decode('utf-8', errors='strict').splitlines():
            lines += 1
            if lines > MAX_LINES or len(line) > 32768:
                raise ValueError('unbounded parser')
            match = LOG_LINE.fullmatch(line)
            if match is None:
                raise ValueError('ambiguous log line')
            level, category, message = match.groups()
            if category == 'startup' and level == 'Info ' and message.startswith('SkillView invocation=') and 'dispatch=Cli' in message:
                startups += 1
            if category != 'subprocess' or level != 'Debug' or not message.startswith('exec: '):
                continue
            command = message[6:]
            prefix = next((p for p in prefixes if command.startswith(p)), None)
            if prefix is not None:
                rest = command[len(prefix):]
                if '[truncated]' in rest or not rest:
                    raise ValueError('ambiguous command')
                count += rest == 'api' or rest.startswith('api ')
            else:
                # Only a positively recognized non-gh executable can establish 0.
                first = command.split(' ', 1)[0].strip('"\'')
                if ntpath.basename(first).lower() not in ('git', 'git.exe'):
                    raise ValueError('unrecognized executable')
    if startups != 1:
        raise ValueError('missing invocation evidence')
    return count


class ProductCommands:
    def __init__(self, diagnostics):
        self.diagnostics = diagnostics
        self.rows = []
        self.overflow = False
        self._publish()

    def _publish(self):
        d = self.diagnostics
        known = sum(row['count'] or 0 for row in self.rows)
        unknown = sum(row['count'] is None for row in self.rows)
        d.update(productGhApiCommands=known if self.rows and not unknown and not self.overflow else None,
                 knownProductGhApiCommands=known, unobservedInvocations=unknown,
                 invocationBudgetExceeded=self.overflow, productInvocations=list(self.rows),
                 httpRequests=None, unknownReason='Command logs are not HTTP counts; missing/unsupported evidence stays unknown; gh pagination/internal and other proofs unknown')
        for key, field in [('byHost', 'host'), ('byCaseKind', 'kind')]:
            groups = {}
            for row in self.rows:
                group = groups.setdefault(row[field], dict(knownCommands=0, unknownInvocations=0, count=0))
                group['knownCommands'] += row['count'] or 0
                group['unknownInvocations'] += row['count'] is None
            for group in groups.values():
                group['count'] = None if group['unknownInvocations'] or self.overflow else group['knownCommands']
            d[key] = groups

    @contextmanager
    def observe(self, env, gh, role, kind, case_root):
        if role is None:
            yield
            return
        before = None
        try:
            if role in CLI_ROLES:
                before = _log_snapshot(env, case_root)
        except Exception:
            pass
        try:
            yield
        finally:
            # No diagnostic exception may replace a primary outcome or exception.
            try:
                count = None
                if before is not None and role in CLI_ROLES:
                    count = _new_api_commands(before, _log_snapshot(env, case_root), gh)
                self._record(role, kind, count)
            except Exception:
                self.diagnostics['productGhApiCommands'] = None
                try:
                    self._record(role, kind, None)
                except Exception:
                    pass

    def _record(self, role, kind, count):
        if len(self.rows) >= MAX_INVOCATIONS:
            self.overflow = True
        else:
            if role not in (*CLI_ROLES, 'proof-host') or not re.fullmatch('[a-z][a-z0-9-]{0,63}', kind):
                raise ValueError('unsupported fixed label')
            self.rows.append(dict(ordinal=len(self.rows) + 1, host=role, kind=kind, count=count))
        self._publish()


MAX_TEMPLATE_BYTES = 8 * 1024 * 1024
MAX_TEMPLATE_ENTRIES = 1024


def _home_manifest(home):
    result = {}; total = 0
    def visit(parent, prefix='', depth=0):
        nonlocal total
        if depth > 16:
            raise ValueError('template depth')
        with os.scandir(parent) as children:
            bounded = []
            for child in children:
                if len(bounded) + len(result) >= MAX_TEMPLATE_ENTRIES:
                    raise ValueError('template entries')
                bounded.append(child)
            for child in sorted(bounded, key=lambda e: e.name):
                name = prefix + child.name
                if len(result) >= MAX_TEMPLATE_ENTRIES or not (name in ('.agents', '.agents/skills', '.agents/.skill-lock.json') or name.startswith('.agents/skills/gh-skill/')) and name != '.agents/skills/gh-skill':
                    raise ValueError('nonfixture HOME entry')
                p = home / name; info = p.lstat()
                if getattr(info, 'st_file_attributes', 0) & 0x400 and not stat.S_ISLNK(info.st_mode):
                    raise ValueError('unsupported reparse entry')
                mode = stat.S_IMODE(info.st_mode) if os.name != 'nt' else None
                value = dict(kind=None, mode=mode, size=None, sha256=None, target=None)
                if stat.S_ISLNK(info.st_mode):
                    target = os.readlink(p)
                    if ntpath.isabs(target) or PurePosixPath(target).is_absolute() or ntpath.splitdrive(target)[0]:
                        raise ValueError('context link')
                    lexical = os.path.normpath(os.path.join(os.path.dirname(name), target.replace('\\', '/'))).replace('\\', '/')
                    if lexical.startswith('../') or lexical == '..':
                        raise ValueError('external link')
                    if _signature(p.lstat()) != _signature(info):
                        raise ValueError('changed link')
                    value.update(kind='link', target=target, size=len(os.fsencode(target)))
                elif stat.S_ISDIR(info.st_mode):
                    value['kind'] = 'directory'
                elif stat.S_ISREG(info.st_mode):
                    data, _ = _regular_bytes(p, MAX_TEMPLATE_BYTES - total)
                    total += len(data)
                    value.update(kind='file', size=len(data), sha256=hashlib.sha256(data).hexdigest())
                else:
                    raise ValueError('unsupported fixture entry')
                result[name] = value
                if value['kind'] == 'directory':
                    visit(p, name + '/', depth + 1)
        return result
    if not stat.S_ISDIR(home.lstat().st_mode):
        raise ValueError('unsafe HOME')
    visit(home)
    for name, value in result.items():
        if value['kind'] == 'link':
            target = os.path.normpath(os.path.join(os.path.dirname(name), value['target'].replace('\\', '/'))).replace('\\', '/')
            if target not in result or result[target]['kind'] == 'link':
                raise ValueError('uncertain link')
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


class InstallTemplates:
    def __init__(self, root):
        root.mkdir(mode=0o700)
        self.root = root
        self.templates = {}
        self.used = set()
        self.mode_evidence = 'windows-mode-unknown' if os.name == 'nt' else 'posix-mode'

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
        try:
            if source != 'skills/gh-skill' or before != {}:
                return False
            key = self.key(gh, pinned, source)
            if key in self.templates or len(self.templates) >= 4:
                return False
            manifest = _home_manifest(home)
            required = {'.agents': 'directory', '.agents/skills': 'directory', '.agents/skills/gh-skill': 'directory', '.agents/.skill-lock.json': 'file'}
            if any(manifest.get(n, {}).get('kind') != kind for n, kind in required.items()):
                return False
            raw, _ = _regular_bytes(home / '.agents/.skill-lock.json', MAX_TEMPLATE_BYTES)
            registry = json.loads(raw)
            contexts = {str(p).replace('\\', '/').casefold() for p in (home, cwd, case_root)}
            def bound(value):
                if isinstance(value, str):
                    return any(s in value.replace('\\', '/').casefold() for s in contexts)
                if isinstance(value, dict):
                    return any(bound(k) or bound(v) for k, v in value.items())
                return isinstance(value, list) and any(bound(v) for v in value)
            if bound(registry):
                return False
            parent = self.root / key; parent.mkdir(mode=0o700)
            payload = parent / 'home'; payload.mkdir(mode=0o700)
            _copy_entries(home, payload, manifest)
            if _home_manifest(home) != manifest or _home_manifest(payload) != manifest:
                return False
            body = json.dumps(manifest, sort_keys=True).encode()
            with (parent / 'manifest.json').open('xb') as stream:
                stream.write(body)
            self.templates[key] = (manifest, hashlib.sha256(body).hexdigest())
            return True
        except Exception:
            return False

    def reuse(self, gh, pinned, source, home):
        copied = False
        try:
            if source != 'skills/gh-skill' or _home_manifest(home) != {}:
                return None
            key = self.key(gh, pinned, source)
            if key in self.used or key not in self.templates:
                return None
            manifest, expected = self.templates[key]; parent = self.root / key
            if any(not stat.S_ISDIR(p.lstat().st_mode) for p in (self.root, parent, parent / 'home')):
                return None
            names = []
            with os.scandir(parent) as children:
                for child in children:
                    if len(names) >= 2:
                        return None
                    names.append(child.name)
            if set(names) != {'home', 'manifest.json'}:
                return None
            raw, _ = _regular_bytes(parent / 'manifest.json', 1024 * 1024)
            if hashlib.sha256(raw).hexdigest() != expected or _home_manifest(parent / 'home') != manifest:
                return None
            copied = True
            _copy_entries(parent / 'home', home, manifest)
            if _home_manifest(home) != manifest or _home_manifest(parent / 'home') != manifest:
                raise ValueError('copy mismatch')
            self.used.add(key)
            return 'template:' + key
        except Exception:
            if copied:
                # Preserve partial evidence, never recursively delete it. The
                # original case HOME was empty and no product/native work ran.
                mode = stat.S_IMODE(home.lstat().st_mode)
                home.rename(home.with_name('refused-template-home-' + uuid.uuid4().hex))
                home.mkdir(mode=mode)
            return None
