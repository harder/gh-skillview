"""SOURCE ONLY until separately reviewed execution approval.

Twelve fresh POSIX actual-product/real-gh consolidation cases, six per exact gh.
The explicit non-forwarding loopback HTTPS fixture is not an OS firewall.
Never import/launch Gemini, execute skills, rewrite tracking, or retry a case.
REST/CA shape derives from reviewed MAI supervisor SHA256; the fixed UserCurrent
and root-scope probes are separately source-bound in consolidation-native-proof.md.
97c40c097428f0af3489065a16cc779302a6f2e2b00fc94f13ae784124e01d34.
"""
from pathlib import Path
import argparse
import base64
import hashlib
import json
import os
import selectors
import signal
import socketserver
import ssl
import stat
import subprocess
import sys
import threading
import time
import urllib.parse
import uuid

PROCESS_BYTES = 1024 * 1024
CASE_SECONDS = 180
OVERALL_SECONDS = 30 * 60
MAX_EVENTS = 200
MAX_STARTS = 32
MAX_ENTRIES = 512
MAX_BYTES = 8 * 1024 * 1024
# Both pinned gh versions call cli/shurcooL-graphql v0.0.4 QueryNamed.
# Its nil-variable JSON Encoder output is fixed, including the final newline.
AUTH_LOGIN_BODY = b'{"query":"query UserCurrent{viewer{login}}"}\n'
HTTP_HEADER_BYTES = 16384
HTTP_BODY_BYTES = 1024
TOOLS = {'2.97.0': 'ccbb0f14178faefac1cb0f336a853071fa63a1d0df23ef5ab7a304fe3859e082',
         '2.102.0': '93308395c2d296a63a662742c6366e4db413d2a4870d07bd9b84e491c065d65d'}
CA_SHA = '2ff20b079a241dd50135c840bc4b9aae092d1a094a7d571c01299453c68630e2'
SCENARIOS = ('identical', 'chosen-v2', 'kill-intent', 'kill-moved', 'stale-alias', 'local-change')
ACTIVE = set()
OWN_SIGNALS = (signal.SIGTERM, signal.SIGINT, signal.SIGALRM)


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def data_sha(data):
    return hashlib.sha256(data).hexdigest()


def sha(path):
    value = hashlib.sha256()
    with Path(path).open('rb') as stream:
        while chunk := stream.read(65536):
            value.update(chunk)
    return value.hexdigest()


def save(path, value):
    # Evidence stays outside the admitted root, never in a payload/state slot.
    path.write_text(json.dumps(value, indent=2, sort_keys=True) + '\n')


def object_sha(kind, content):
    return hashlib.sha1(kind.encode() + b' ' + str(len(content)).encode() + b'\0' + content).hexdigest()


class Repo:
    """Compute genuine Git object identities without invoking git/hash-object."""
    def __init__(self, files, tag='v1', branch=None):
        require(0 < len(files) <= MAX_ENTRIES and sum(map(len, files.values())) <= MAX_BYTES,
                'Independent source fixture bounds exceeded')
        require(all(isinstance(name, str) and name and not name.startswith('/')
                    and all(part not in ('', '.', '..') for part in name.split('/'))
                    and not any(ord(c) < 32 or c == '\\' for c in name) for name in files),
                'Source fixture path is not a normalized safe relative address')
        self.files = dict(files)
        self.blobs = {object_sha('blob', value): value for value in files.values()}
        self.trees = {}
        self.directories = {}
        nested = {}
        for name, value in files.items():
            current = nested
            pieces = name.split('/')
            for part in pieces[:-1]:
                current = current.setdefault(part, {})
            current[pieces[-1]] = value

        def tree(node, prefix=''):
            entries = []
            for name, value in node.items():
                directory = isinstance(value, dict)
                identifier = tree(value, prefix + name + '/') if directory else object_sha('blob', value)
                entries.append(dict(path=name, mode='040000' if directory else '100644',
                                    type='tree' if directory else 'blob', sha=identifier,
                                    **({} if directory else {'size': len(value)})))
            entries.sort(key=lambda e: (e['path'] + ('/' if e['type'] == 'tree' else '')).encode())
            raw = b''.join((('40000' if e['type'] == 'tree' else '100644') + ' ' + e['path']).encode()
                           + b'\0' + bytes.fromhex(e['sha']) for e in entries)
            identifier = object_sha('tree', raw)
            self.trees[identifier] = entries
            self.directories[prefix.rstrip('/')] = identifier
            return identifier

        self.root_tree = tree(nested)
        self.commit_bytes = ('tree ' + self.root_tree + '\nauthor Fixture <fixture@example.invalid> 0 +0000\n'
                             'committer Fixture <fixture@example.invalid> 0 +0000\n\nInert controlled REST fixture\n').encode()
        self.commit = object_sha('commit', self.commit_bytes)
        self.tag, self.branch = tag, branch

    def tree_response(self, identifier, recursive):
        actual = self.root_tree if identifier == self.commit else identifier
        if actual not in self.trees:
            return None
        result = []

        def append(tree_sha, prefix=''):
            for entry in self.trees[tree_sha]:
                result.append(dict(entry, path=prefix + entry['path']))
                if recursive and entry['type'] == 'tree':
                    append(entry['sha'], prefix + entry['path'] + '/')
        append(actual)
        return dict(sha=actual, truncated=False, tree=result)

    def route(self, path, query):
        if path == '':
            return (200, dict(visibility='public', default_branch=self.branch or 'main')) if not query else None
        if path == 'releases/latest' and not query:
            return 200, dict(tag_name=self.tag)
        if path.startswith('git/ref/') and not query:
            requested = path[len('git/ref/'):]
            if requested == 'tags/' + self.tag or (self.branch and requested == 'heads/' + self.branch):
                return 200, dict(ref='refs/' + requested, object=dict(sha=self.commit, type='commit'))
            # Only the source-proven branch-first probe for supplied v1 is absent.
            if requested == 'heads/v1' and self.tag == 'v1':
                return 404, dict(message='Not Found')
            return None
        if path.startswith('contents/'):
            if query != {'ref': [self.commit]}:
                return None
            name = path[len('contents/'):]
            if name in self.files:
                return 200, dict(type='file', path=name, name=name.rsplit('/', 1)[-1],
                                 sha=object_sha('blob', self.files[name]), encoding='base64',
                                 content=base64.b64encode(self.files[name]).decode())
            if name in self.directories:
                return 200, [dict(name=e['path'], path=name + '/' + e['path'], sha=e['sha'],
                                  type='dir' if e['type'] == 'tree' else 'file')
                             for e in self.trees[self.directories[name]]]
            return None
        if path.startswith('git/trees/'):
            if query not in ({}, {'recursive': ['true']}, {'recursive': ['1']}):
                return None
            response = self.tree_response(path[len('git/trees/'):], bool(query))
            return (200, response) if response is not None else None
        if path.startswith('git/blobs/') and not query:
            identifier = path[len('git/blobs/'):]
            if identifier in self.blobs:
                return 200, dict(sha=identifier, encoding='base64', content=base64.b64encode(self.blobs[identifier]).decode())
        return None


def http_head(request):
    """GET or the single pinned read-only auth query; no general POST surface."""
    require(len(request) <= HTTP_HEADER_BYTES and request.endswith('\r\n\r\n'), 'Bounded HTTP header required')
    lines = request[:-4].split('\r\n')
    first = lines[0].split(' ')
    require(len(first) == 3 and first[0] in ('GET', 'POST') and first[2] == 'HTTP/1.1', 'Unexpected HTTP method/version')
    headers = {}
    for line in lines[1:]:
        name, colon, value = line.partition(':')
        name = name.lower()
        require(colon and name and name not in headers and not any(c.isspace() for c in name),
                'Malformed/duplicate HTTP header')
        headers[name] = value.strip()
    require(headers.get('host') == 'api.github.com', 'Unexpected HTTP host')
    require('transfer-encoding' not in headers, 'Transfer-Encoding is not admitted')
    target = urllib.parse.urlsplit(first[1])
    require(first[1].startswith('/') and not target.scheme and not target.netloc and not target.fragment,
            'Unexpected absolute request target')
    require('?' not in first[1] or target.query, 'Empty query marker is not a listed route')
    if first[0] == 'GET':
        require(headers.get('content-length', '0') == '0', 'Unexpected GET body')
        length = 0
    else:
        require(first[1] == '/graphql' and headers.get('content-type') == 'application/json',
                'Only exact JSON auth-probe POST /graphql is admitted')
        require(headers.get('content-length') == str(len(AUTH_LOGIN_BODY)), 'Unexpected auth-probe body length')
        length = len(AUTH_LOGIN_BODY)
        require(length <= HTTP_BODY_BYTES, 'Auth-probe body exceeds bound')
    return first[0], first[1], length


def parse_http(request, body=b''):
    _, target, length = http_head(request)
    require(isinstance(body, bytes) and len(body) == length, 'Incomplete or excess HTTP body')
    require(length == 0 or body == AUTH_LOGIN_BODY, 'Unlisted GraphQL operation or encoding')
    return target


def route_http(repositories, request, body=b''):
    target = parse_http(request, body)
    method = request.split(' ', 1)[0]
    if method == 'POST':
        return method, target, (200, dict(data=dict(viewer=dict(login='disposable-fixture'))))
    # GetScopes reads the API root's response header, not GET /user.
    if target == '/':
        return method, target, (200, {})
    if target == '/user':
        return method, target, (200, dict(login='disposable-fixture', id=1))
    url = urllib.parse.urlsplit(target)
    parts = urllib.parse.unquote(url.path).split('/')
    query = urllib.parse.parse_qs(url.query, keep_blank_values=True, strict_parsing=True)
    response = None
    if len(parts) >= 4 and parts[1] == 'repos':
        repository = repositories.get('/'.join(parts[2:4]))
        if repository:
            response = repository.route('/'.join(parts[4:]), query)
    return method, target, response


def request_line_evidence(raw):
    """Bounded first-line diagnostic only: never persist headers or bodies."""
    line = raw.split(b'\r\n', 1)[0][:1024]
    text = line.decode('iso-8859-1')
    # Redact URL query/userinfo even in malformed/absolute targets. Tokens in
    # Authorization and other headers are never part of this input.
    if '?' in text:
        prefix, _, tail = text.partition('?')
        text = prefix + '?[redacted]' + (' ' + tail.rsplit(' ', 1)[1] if ' ' in tail else '')
    if '://' in text and '@' in text:
        prefix, _, tail = text.partition('://')
        text = prefix + '://[redacted-userinfo]@' + tail.rsplit('@', 1)[1]
    printable = text.encode('unicode_escape').decode('ascii')
    return dict(firstRequestLine=printable[:256], firstRequestLinePrefixBytes=len(line),
                firstRequestLinePrefixSha256=data_sha(line), firstRequestLineTruncated=len(raw) >= 1024 or len(printable) > 256)


def fixture_response_headers(target, body):
    headers = {'Content-Type': 'application/json', 'Content-Length': str(len(body)), 'Connection': 'close'}
    if target in ('/', '/user'):
        # Fixed disposable fixture declarations only; no real token capabilities.
        headers['X-OAuth-Scopes'] = 'repo, read:org' if target == '/' else 'repo'
    return headers


def parse_connect(request):
    require(len(request) <= HTTP_HEADER_BYTES and request.endswith('\r\n\r\n'), 'Bounded CONNECT header required')
    lines = request[:-4].split('\r\n')
    require(lines[0] == 'CONNECT api.github.com:443 HTTP/1.1', 'Unexpected CONNECT target/method/version')
    headers = {}
    for line in lines[1:]:
        name, colon, value = line.partition(':')
        name = name.lower()
        require(colon and name and name not in headers and not any(c.isspace() for c in name),
                'Malformed/duplicate CONNECT header')
        headers[name] = value.strip()
    require(headers.get('host') == 'api.github.com:443', 'Unexpected CONNECT Host')
    require('transfer-encoding' not in headers and headers.get('content-length', '0') == '0', 'Unexpected CONNECT body')
    return lines[0]


class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = False
    daemon_threads = False
    block_on_close = True
    request_queue_size = 8

    def __init__(self, repositories, transcript, deadline, certificate, private_key):
        self.repositories, self.transcript, self.deadline = repositories, transcript, deadline
        self.events, self.failures, self.lock = [], [], threading.Lock()
        self.total_events = self.omitted_events = self.overflow_events = self.denied_events = 0
        self.context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        self.context.load_cert_chain(certificate, private_key)
        self.context.set_alpn_protocols(['http/1.1'])
        self.slots = threading.BoundedSemaphore(8)
        self.closing = threading.Event()
        super().__init__(('127.0.0.1', 0), Handler)

    def process_request(self, request, client_address):
        if not self.slots.acquire(blocking=False):
            self.record(dict(kind='connection-limit'), 'Eight active TLS connections exceeded')
            request.close()
            return
        try:
            super().process_request(request, client_address)
        except BaseException:
            self.slots.release()
            request.close()
            raise

    def process_request_thread(self, request, client_address):
        try:
            super().process_request_thread(request, client_address)
        finally:
            self.slots.release()

    def record(self, event, failure=None):
        with self.lock:
            self.total_events += 1
            full = len(self.events) >= MAX_EVENTS
            if full:
                self.overflow_events += 1
            refused = full or self.closing.is_set() or bool(self.failures) or time.monotonic() >= self.deadline
            if refused:
                self.omitted_events += 1
                failure = failure or ('200-event request/CONNECT budget exceeded' if full
                                      else 'Proxy admission closed or deadline exhausted')
            else:
                self.events.append(dict(event, sequence=self.total_events))
            if failure and not self.failures:
                self.failures.append(failure)  # Retain only the first fatal reason.
            admitted = not refused and failure is None
            if not admitted:
                self.denied_events += 1
            self.transcript.write_text(json.dumps(self._evidence_locked(), indent=2) + '\n')
            return admitted

    def _evidence_locked(self):
        return dict(events=list(self.events), failures=list(self.failures), totalEvents=self.total_events,
                    omittedEvents=self.omitted_events, overflowEvents=self.overflow_events,
                    deniedEvents=self.denied_events)

    def evidence(self):
        with self.lock:
            return self._evidence_locked()


class Handler(socketserver.BaseRequestHandler):
    def handle(self):
        server = self.server
        connection = self.request
        connection.settimeout(max(.1, min(1, server.deadline - time.monotonic())))
        first_line = b''
        phase = 'connect'

        def header(sock):
            nonlocal first_line
            data = bytearray()
            while not data.endswith(b'\r\n\r\n'):
                require(not server.closing.is_set() and time.monotonic() < server.deadline,
                        'Proxy closed or deadline exhausted')
                byte = sock.recv(1)
                if not byte:
                    raise IOError('Unexpected header EOF')
                data.extend(byte)
                if not first_line and (b'\r\n' in data or len(data) >= 1024):
                    first_line = bytes(data).split(b'\r\n', 1)[0][:1024]
                if len(data) > HTTP_HEADER_BYTES or time.monotonic() >= server.deadline:
                    raise IOError('Header/deadline budget exceeded')
            return data.decode('iso-8859-1')

        try:
            connect = parse_connect(header(connection))
            if not server.record(dict(kind='CONNECT', request=connect)):
                return
            connection.sendall(b'HTTP/1.1 200 Connection Established\r\n\r\n')
            phase, first_line = 'tls-handshake', b''
            with server.context.wrap_socket(connection, server_side=True) as secured:
                phase = 'request'
                request = header(secured)
                _, _, length = http_head(request)  # Bound/admit framing before reading any body.
                request_body = bytearray()
                while len(request_body) < length:
                    require(not server.closing.is_set() and time.monotonic() < server.deadline,
                            'Proxy closed or body deadline exhausted')
                    chunk = secured.recv(length - len(request_body))
                    require(chunk, 'Unexpected body EOF')
                    request_body.extend(chunk)
                method, request_target, response = route_http(server.repositories, request, bytes(request_body))
                if response is None:
                    server.record(dict(kind='route-refused', phase=phase, **request_line_evidence(first_line)),
                                  'Unknown controlled fixture route')
                    return
                status, document = response
                body = json.dumps(document, separators=(',', ':')).encode()
                response_headers = fixture_response_headers(request_target, body)
                if not server.record(dict(kind=method, request=request_target, host='api.github.com', status=status,
                                          requestBodyBytes=len(request_body), requestBodySha256=data_sha(request_body),
                                          requestBodyBase64=base64.b64encode(request_body).decode(),
                                          responseHeaders=response_headers,
                                          responseSha256=data_sha(body), responseBytes=len(body),
                                          responseBase64=base64.b64encode(body).decode())):
                    return
                reason = {200: 'OK', 404: 'Not Found', 500: 'Fixture Refusal'}[status]
                secured.sendall(('HTTP/1.1 ' + str(status) + ' ' + reason + '\r\n'
                                 + ''.join(key + ': ' + value + '\r\n' for key, value in response_headers.items())
                                 + '\r\n').encode() + body)
        except Exception as error:
            server.record(dict(kind='proxy-error', phase=phase, error=str(error),
                               **request_line_evidence(first_line)), 'Proxy protocol/TLS failure')


class Repository:
    """Only latest-release selection moves. Frozen Git objects never do."""
    def __init__(self, versions):
        self.versions = dict(versions)
        self.latest = 'v1'

    def advance(self):
        require(self.latest == 'v1' and 'v2' in self.versions, 'Latest selection may advance once only')
        before = self.latest
        self.latest = 'v2'
        return dict(before=before, after=self.latest,
                    immutableCommits={tag: repo.commit for tag, repo in self.versions.items()})

    def route(self, path, query):
        if path == 'releases/latest' and not query:
            return 200, dict(tag_name=self.latest)
        if path.startswith('git/ref/') and not query:
            name = path[len('git/ref/'):]
            for tag, repo in self.versions.items():
                if name == 'tags/' + tag:
                    return 200, dict(ref='refs/' + name, object=dict(sha=repo.commit, type='commit'))
                if name == 'heads/' + tag:
                    return 404, dict(message='Not Found')
            return None
        if path.startswith('commits/') and not query:
            identifier = path[len('commits/'):]
            for repo in self.versions.values():
                if identifier == repo.commit:
                    # Actual commit bytes and hash were independently computed;
                    # this response binds exactly their root tree.
                    return 200, dict(sha=repo.commit, commit=dict(tree=dict(sha=repo.root_tree)))
            return None
        if path.startswith('git/commits/') and not query:
            identifier = path[len('git/commits/'):]
            for repo in self.versions.values():
                if identifier == repo.commit:
                    return 200, dict(sha=repo.commit, tree=dict(sha=repo.root_tree), parents=[],
                                     message='Inert controlled REST fixture\n',
                                     author=dict(name='Fixture', email='fixture@example.invalid', date='1970-01-01T00:00:00Z'),
                                     committer=dict(name='Fixture', email='fixture@example.invalid', date='1970-01-01T00:00:00Z'))
            return None
        # Object-addressed routes must be unambiguous. All versions are retained.
        for repo in self.versions.values():
            response = repo.route(path, query)
            if response is not None:
                return response
        return None

    def evidence(self):
        return {tag: dict(commit=repo.commit, commitBase64=base64.b64encode(repo.commit_bytes).decode(),
                          rootTree=repo.root_tree, directories=repo.directories, trees=repo.trees,
                          files={path: dict(bytes=len(value), sha256=data_sha(value),
                                            base64=base64.b64encode(value).decode()) for path, value in repo.files.items()})
                for tag, repo in self.versions.items()}


def fixture_files(version):
    return {'skills/demo/SKILL.md': ('---\nname: demo\ndescription: Inert consolidation fixture\nversion: ' + version
            + '\nmetadata:\n  authored-purpose: bounded-proof\n---\nRead references/facts.txt; never execute scripts/inert.sh.\n').encode(),
            'skills/demo/references/facts.txt': ('Independent recorded facts ' + version + '\n').encode(),
            'skills/demo/scripts/inert.sh': b'# Inert fixture bytes; this file is never executed.\n'}


def repositories():
    return {'fixture/source': Repository({tag: Repo(fixture_files(tag), tag=tag) for tag in ('v1', 'v2')}),
            'different-author/source': Repository({'v1': Repo(fixture_files('v1'))})}


def child_env(root, gh, proxy, certificate):
    """No ambient auth/proxy/Git/loader/config variables enter the child."""
    home = root / 'home-0'
    env = dict(PATH=str(gh.parent) + ':/usr/bin:/bin', LANG='C.UTF-8', HOME=str(home), USERPROFILE=str(home),
               SKILLVIEW_STATE_HOME=str(root / 'state'), TMPDIR=str(root / 'tmp'), TMP=str(root / 'tmp'), TEMP=str(root / 'tmp'),
               GH_TOKEN='disposable-controlled-api-token', GH_CONFIG_DIR=str(root / 'gh-config'), GH_EXTENSION='1',
               GH_PATH=str(gh), GH_HOST='github.com', GH_TELEMETRY='false', GH_PROMPT_DISABLED='1',
               GH_NO_UPDATE_NOTIFIER='1', GH_NO_EXTENSION_UPDATE_NOTIFIER='1', GH_PAGER='cat',
               GIT_CONFIG_COUNT='0', GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL=str(root / 'empty-git-config'),
               GIT_TERMINAL_PROMPT='0', GIT_OPTIONAL_LOCKS='0',
               CLAUDE_CONFIG_DIR=str(home / '.claude'), CODEX_HOME=str(home / '.codex'),
               PI_CODING_AGENT_DIR=str(home / '.pi/agent'),
               GEMINI_CLI_HOME=str(home), GEMINI_SANDBOX='false', GEMINI_TELEMETRY_ENABLED='false',
               GEMINI_CLI_SYSTEM_SETTINGS_PATH=str(root / 'system/settings.json'),
               GEMINI_CLI_SYSTEM_DEFAULTS_PATH=str(root / 'system/defaults.json'),
               SSL_CERT_FILE=str(certificate), SSL_CERT_DIR=str(root / 'empty-ca'),
               DOTNET_CLI_HOME=str(root / 'dotnet-home'), DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',
               DOTNET_NOLOGO='1', DOTNET_CLI_TELEMETRY_OPTOUT='1')
    for name in ('CONFIG', 'STATE', 'DATA', 'CACHE'):
        env['XDG_' + name + '_HOME'] = str(root / ('xdg-' + name.lower()))
    for key in ('HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'http_proxy', 'https_proxy', 'all_proxy'):
        env[key] = proxy
    env['NO_PROXY'] = env['no_proxy'] = ''
    return env


def native(path):
    value = path.lstat()
    return dict(device=value.st_dev, inode=value.st_ino, ctimeNs=value.st_ctime_ns,
                mtimeNs=value.st_mtime_ns, mode=value.st_mode, uid=value.st_uid, gid=value.st_gid)


def snapshot(root):
    """Bounded lstat walk. Never traverse links; retain xattr failures explicitly."""
    rows, stack, total = [], [(root, '')], 0
    while stack:
        path, relative = stack.pop()
        require(len(rows) < MAX_ENTRIES + 1, 'Snapshot entry bound exceeded')
        row = dict(path=relative, native=native(path))
        mode = row['native']['mode']
        if stat.S_ISLNK(mode):
            row['target'] = os.readlink(path)
        elif stat.S_ISDIR(mode):
            with os.scandir(path) as entries:
                names = []
                for item in entries:
                    require(len(names) < MAX_ENTRIES, 'Snapshot directory bound exceeded')
                    names.append(item.name)
            for name in sorted(names, reverse=True):
                stack.append((path / name, (relative + '/' + name).lstrip('/')))
        elif stat.S_ISREG(mode):
            size = path.lstat().st_size
            require(0 <= size <= MAX_BYTES - total, 'Snapshot byte bound exceeded')
            total += size
            row.update(bytes=size, sha256=sha(path))
        else:
            raise RuntimeError('Unexpected special fixture object: ' + str(path))
        if hasattr(os, 'listxattr'):
            try:
                names = sorted(os.listxattr(path, follow_symlinks=False))
                require(len(names) <= 128, 'Snapshot xattr bound exceeded')
                attrs, attr_bytes = {}, 0
                for name in names:
                    value = os.getxattr(path, name, follow_symlinks=False)
                    attr_bytes += len(value)
                    require(attr_bytes <= 256 * 1024, 'Snapshot xattr byte bound exceeded')
                    attrs[name] = dict(bytes=len(value), sha256=data_sha(value))
                row['observedXattrs'] = attrs
            except OSError as error:
                row['xattrObservationError'] = dict(errno=error.errno, message=str(error))
        else:
            row['xattrObservationError'] = dict(message='Python xattr interface unavailable')
        rows.append(row)
    return rows


def stable(rows, renamed=False):
    # A rename changes the directory's ctime; preserve every other observation.
    result = json.loads(json.dumps(rows))
    if renamed:
        result[0]['native'].pop('ctimeNs')
    return result


def protected_tmp_git():
    path = Path('/tmp/.git')
    try:
        return native(path)
    except FileNotFoundError:
        return None


def kill_group(process):
    try:
        os.killpg(process.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass


class CleanupSignals:
    """Defer controller cancellation until owned cleanup/receipts finish."""
    def __init__(self, cancel_timer=False):
        self.cancel_timer = cancel_timer
        self.signum = None

    def defer(self, signum, _frame):
        if self.signum is None:
            self.signum = signum

    def __enter__(self):
        previous_mask = signal.pthread_sigmask(signal.SIG_BLOCK, OWN_SIGNALS)
        try:
            self.prior_error = sys.exception()
            self.previous = {kind: signal.getsignal(kind) for kind in OWN_SIGNALS}
            for kind in OWN_SIGNALS:
                signal.signal(kind, self.defer)
            if self.cancel_timer:
                signal.setitimer(signal.ITIMER_REAL, 0)
        finally:
            signal.pthread_sigmask(signal.SIG_SETMASK, previous_mask)
        return self

    def __exit__(self, exception_type, _error, _traceback):
        previous_mask = signal.pthread_sigmask(signal.SIG_BLOCK, OWN_SIGNALS)
        try:
            for kind, handler in self.previous.items():
                signal.signal(kind, handler)
        finally:
            signal.pthread_sigmask(signal.SIG_SETMASK, previous_mask)
        if self.signum is not None and exception_type is None and self.prior_error is None:
            # Cleanup has finished. Close further admission and propagate the
            # original cancellation through the normal outer failure receipt.
            interrupt(self.signum, None)
        return False  # Never replace an already propagating failure.


class Executor:
    def __init__(self, output, deadline, server):
        self.output, self.deadline, self.server = output, deadline, server
        self.starts = 0
        self.receipts = []

    def run(self, argv, env, cwd, pause=None):
        require(self.starts < MAX_STARTS, '32 direct child-start budget exhausted')
        require(time.monotonic() < self.deadline, 'Case deadline exhausted before child start')
        self.starts += 1
        directory = self.output / ('process-%02d' % self.starts)
        directory.mkdir()
        values = [bytearray(), bytearray()]
        process = None
        failure, ready, code = None, False, None
        cleanup_error = None
        try:
            # Defer controller signals across fork/exec admission until the
            # exact owned Popen is tracked and the enclosing finally can reap it.
            previous_mask = signal.pthread_sigmask(signal.SIG_BLOCK, OWN_SIGNALS)
            try:
                process = subprocess.Popen([str(a) for a in argv], env=env, cwd=cwd, stdin=subprocess.DEVNULL,
                                           stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=True)
                ACTIVE.add(process)
            finally:
                signal.pthread_sigmask(signal.SIG_SETMASK, previous_mask)
            with selectors.DefaultSelector() as selector:
                for index, stream in enumerate((process.stdout, process.stderr)):
                    os.set_blocking(stream.fileno(), False)
                    selector.register(stream, selectors.EVENT_READ, index)
                drain_deadline = None
                while selector.get_map() or process.poll() is None:
                    if self.server.failures:
                        failure = 'fatal-proxy-refusal'
                    elif time.monotonic() >= self.deadline:
                        failure = 'case-deadline'
                    if failure:
                        kill_group(process)
                    if process.poll() is not None and drain_deadline is None:
                        kill_group(process)  # Descendants must not retain pipes or outlive the owned group.
                        drain_deadline = time.monotonic() + 2
                    if drain_deadline is not None and time.monotonic() >= drain_deadline:
                        if selector.get_map():
                            failure = failure or 'descendant-pipe-drain-timeout'
                        break
                    for key, _ in selector.select(.02):
                        chunk = os.read(key.fd, 8192)
                        if not chunk:
                            selector.unregister(key.fileobj)
                            continue
                        remaining = PROCESS_BYTES - sum(map(len, values))
                        values[key.data].extend(chunk[:max(0, remaining)])
                        if len(chunk) > remaining:
                            failure = 'combined-output-overflow'
                            kill_group(process)
                        if pause and not ready and (b'READY ' + pause.encode()) in values[0].splitlines():
                            ready = True
                            kill_group(process)
                code = process.wait(timeout=5)
        except BaseException as error:
            failure = failure or type(error).__name__ + ': ' + str(error)
            raise
        finally:
            with CleanupSignals() as cleanup:
                if process is not None:
                    # Always reap the direct child, including controller interruption.
                    try:
                        kill_group(process)
                        code = process.wait(timeout=5)
                        ACTIVE.discard(process)
                    except (subprocess.TimeoutExpired, OSError) as error:
                        failure = failure or 'owned-child-could-not-be-reaped'
                        cleanup_error = error
                    finally:
                        for stream in (process.stdout, process.stderr):
                            stream.close()
                for name, value in zip(('stdout.bin', 'stderr.bin'), values):
                    (directory / name).write_bytes(value)
                receipt = dict(argv=[str(a) for a in argv], cwd=str(cwd), exitCode=code, failure=failure,
                               cleanupSignal=cleanup.signum,
                               cleanupFailure=str(cleanup_error) if cleanup_error is not None else None,
                               ready=ready, expectedPause=pause, outputBytes=sum(map(len, values)),
                               stdoutSha256=data_sha(values[0]), stderrSha256=data_sha(values[1]))
                self.receipts.append(receipt)
                save(directory / 'command.json', receipt)
                if cleanup_error is not None and cleanup.prior_error is None:
                    raise cleanup_error
        require(failure is None, 'Child failed bounded supervision: ' + str(receipt))
        if pause:
            require(ready and code == -signal.SIGKILL, 'Expected READY and supervisor SIGKILL were not observed')
        return code, values[0].decode('utf-8'), values[1].decode('utf-8')


def tracking(path):
    text = path.read_text(encoding='utf-8')
    require(len(text.encode()) < 32768 and text.startswith('---\n'), 'Unexpected installed SKILL framing')
    result = {}
    header, separator, body = text[4:].partition('\n---\n')
    require(separator, 'Installed SKILL has no closing YAML delimiter')
    for line in header.splitlines():
        key, colon, value = line.strip().partition(':')
        if key in ('github-repo', 'github-ref', 'github-path', 'github-tree-sha', 'github-pinned') and colon:
            require(key not in result, 'Duplicate tracking field')
            result[key] = value.strip().strip('"\'')
    return result, body


def check_installed(path, repo_name, repo, pinned=False):
    """Real installer bytes are evidence; never generate or repair their tracking."""
    observed, body = tracking(path / 'SKILL.md')
    require(observed.get('github-repo') == 'https://github.com/' + repo_name, 'Actual gh source differs')
    require(observed.get('github-ref') == 'refs/tags/' + repo.tag, 'Actual gh immutable ref differs')
    require(observed.get('github-tree-sha') == repo.directories['skills/demo'], 'Actual gh recorded tree differs')
    require(observed.get('github-path') == 'skills/demo', 'Actual gh source path differs')
    require(observed.get('github-pinned', '') == ('v1' if pinned else ''), 'Required actual pin/unpinned shape unavailable')
    source_body = repo.files['skills/demo/SKILL.md'].split(b'\n---\n', 1)[1].decode()
    require(body == source_body, 'Installer changed authored body')
    expected = {name[len('skills/demo/'):]: value for name, value in repo.files.items()}
    rows = snapshot(path)
    require({row['path'] for row in rows if 'sha256' in row} == set(expected), 'Installed file set differs')
    for name, value in expected.items():
        if name != 'SKILL.md':
            require((path / name).read_bytes() == value, 'Installed resource differs from independent Git blob')
    return dict(tracking=observed, entries=rows)


def subjects(paths):
    return {name: snapshot(path) if os.path.lexists(path) else None for name, path in paths.items()}


def alias_evidence(aliases):
    return [dict(path=str(path), parent=native(path.parent), link=native(path), rawTarget=os.readlink(path),
                 canonical=str(path.resolve(strict=True)), canonicalNative=native(path.resolve(strict=True))) for path in aliases]


def owner_evidence(root):
    # This fixture's known actual gh roots only. Actual saved inventory remains
    # authority for all owner records/topologies; absence here grants no permission.
    rows = []
    for home in ('home-0', 'home-1', 'home-d', 'home-e'):
        for relative in ('.agents/.skill-lock.json', '.agents/skills/.skill-lock.json', '.gemini/skills/.skill-lock.json'):
            path = root / home / relative
            rows.append(dict(path=str(path), evidence=registry_evidence(path)))
    project_lock = root / 'pinned-project/.agents/.skill-lock.json'
    rows.append(dict(path=str(project_lock), evidence=registry_evidence(project_lock)))
    return rows


def registry_evidence(path):
    try:
        before = native(path)
    except FileNotFoundError:
        return None  # Only definite missing; access/I/O failures propagate.
    require(stat.S_ISREG(before['mode']), 'Setup registry must be a regular non-link file')
    with path.open('rb') as stream:
        raw = stream.read(1024 * 1024 + 1)
    require(len(raw) <= 1024 * 1024, 'Setup registry evidence byte bound exceeded')
    observed = snapshot(path)
    require(observed[0]['native'] == before and observed[0]['sha256'] == data_sha(raw),
            'Setup registry changed during observation')
    return dict(observation=observed, rawBase64=base64.b64encode(raw).decode(), sha256=data_sha(raw))


def check_canonical_record(path, repo):
    # Inspect actual gh output. Never create, normalize or repair this entry.
    evidence = registry_evidence(path)
    require(evidence is not None, 'Actual canonical gh record was not produced')
    value = json.loads(base64.b64decode(evidence['rawBase64']))
    require(value.get('version') == 3 and set(value.get('skills', {})) == {'demo'}, 'Unexpected actual registry shape')
    entry = value['skills']['demo']
    require(entry.get('source') == 'fixture/source' and entry.get('sourceType') == 'github'
            and entry.get('sourceUrl') == 'https://github.com/fixture/source.git'
            and entry.get('skillPath') == 'skills/demo/SKILL.md'
            and entry.get('skillFolderHash') == repo.directories['skills/demo']
            and 'pinnedRef' not in entry, 'Actual canonical record differs from selected unpinned source')
    return evidence


class Case:
    def __init__(self, root, output, bindings, env, executor, repos):
        self.root, self.output, self.bindings, self.env, self.executor, self.repos = root, output, bindings, env, executor, repos
        self.paths = dict(A=root / 'shared/skills/demo', B=root / 'home-0/.gemini/skills/demo',
                          C=root / 'home-1/.gemini/skills/demo', D=root / 'pinned-project/.agents/skills/demo',
                          E=root / 'other-author/demo')
        self.aliases = [root / ('home-%d/.agents/skills' % i) for i in (0, 1)]
        self.globals = ['--workspace', str(root / 'pinned-project'), '--scan-root', str(root / 'other-author')]
        self.plan = None

    def cli(self, command, *args, expected=True):
        code, text, error = self.executor.run([*self.bindings['product'], *self.globals, command, *args, '--json'], self.env, self.root)
        try:
            document = json.loads(text)
        except json.JSONDecodeError as problem:
            raise RuntimeError('Actual CLI did not produce a single JSON report: ' + error) from problem
        require(isinstance(document, dict), 'Unexpected CLI JSON root')
        if expected:
            require(code == 0 and document.get('outcome') == 'observed', 'CLI operation refused: ' + str(document))
            if document.get('result') is not None:
                require(document['result']['succeeded'] and not document['result']['canceled'], 'CLI lifecycle incomplete')
        return code, document

    def setup(self, scenario):
        # Only setup creates fixture directories/aliases. Actual product/gh
        # calls own all skill materialization, tracking and manager records.
        for name in ('home-0', 'home-1', 'home-d', 'home-e', 'state', 'tmp', 'gh-config', 'empty-ca',
                     'dotnet-home', 'system', 'shared/skills', 'other-author', 'pinned-project'):
            (self.root / name).mkdir(mode=0o700, parents=True, exist_ok=True)
        (self.root / 'empty-git-config').write_bytes(b'')
        (self.root / 'disposable-proof-marker').write_bytes(b'SkillView disposable process proof\n')
        (self.root / 'disposable-proof-marker').chmod(0o600)
        code, version, _ = self.setup_call('gh-version', [self.env['GH_PATH'], '--version'], self.env, self.root)
        require(code == 0 and version.startswith('gh version ' + self.executor.output.name.split('-', 1)[0] + ' '),
                'Retained real gh version report differs')
        save(self.output / 'observed-gh-version.json', dict(report=version))
        for index in (0, 1):
            home = self.root / ('home-%d' % index)
            for relative in ('.gemini/skills', '.agents'):
                (home / relative).mkdir(parents=True, exist_ok=True)
            (home / '.agents/skills').symlink_to(self.root / 'shared/skills', target_is_directory=True)
        # A genuine isolated Git workspace is needed only for pinned project D;
        # proof CWD/state are deliberately outside this repository.
        empty_template = self.root / 'empty-template'
        empty_template.mkdir()
        code, _, _ = self.setup_call('private-D-git-init', [self.bindings['git'], 'init', '--quiet',
            '--template=' + str(empty_template), '--', str(self.root / 'pinned-project')], self.env, self.root)
        require(code == 0, 'Private pinned-project git init failed')
        for name, index in (('B', 0), ('C', 1)):
            self.install('fixture/source', self.paths[name].parent, home=self.root / ('home-%d' % index))
            check_installed(self.paths[name], 'fixture/source', self.repos['fixture/source'].versions['v1'])
        personal_before = subjects({name: self.paths[name] for name in ('B', 'C')})
        if scenario != 'identical':
            transition = self.repos['fixture/source'].advance()
            save(self.output / 'latest-selection-transition.json', transition)
        selected = 'v1' if scenario == 'identical' else 'v2'
        self.product_canonical_refusal()
        chosen = self.repos['fixture/source'].versions[selected]
        # Genuine fragmented initial conditions, explicitly allowed by AGENTS.
        # Same physical A, two unchanged real consumer HOME contexts. All writes
        # finish before any product plan/before-image; this is not migration.
        self.raw_canonical(self.root / 'home-0', force=False)
        check_installed(self.paths['A'], 'fixture/source', chosen)
        check_canonical_record(self.root / 'home-0/.agents/.skill-lock.json', chosen)
        require(subjects({name: self.paths[name] for name in ('B', 'C')}) == personal_before,
                'First raw canonical setup changed a personal original')
        self.raw_canonical(self.root / 'home-1', force=True)
        check_installed(self.paths['A'], 'fixture/source', chosen)
        for index in (0, 1):
            check_canonical_record(self.root / ('home-%d/.agents/.skill-lock.json' % index), chosen)
        require(subjects({name: self.paths[name] for name in ('B', 'C')}) == personal_before,
                'Second raw canonical setup changed a personal original')
        self.install('fixture/source', None, pinned=True, home=self.root / 'home-d', cwd=self.root / 'pinned-project')
        check_installed(self.paths['D'], 'fixture/source', self.repos['fixture/source'].versions['v1'], pinned=True)
        self.install('different-author/source', self.paths['E'].parent, home=self.root / 'home-e')
        check_installed(self.paths['E'], 'different-author/source', self.repos['different-author/source'].versions['v1'])
        request = dict(canonicalPath=str(self.paths['A']), geminiEntrypoint=self.bindings['entrypoint'],
                       nodeExecutable=self.bindings['node'], obsoleteCopies=[
                           dict(path=str(self.paths[name]), homeDirectory=str(self.root / ('home-%d' % index)),
                                workingDirectory=str(self.root), profile=None) for name, index in (('B', 0), ('C', 1))])
        save(self.root / 'request.json', request)
        self.before = subjects(self.paths)
        self.owners = owner_evidence(self.root)
        self.expected_aliases = alias_evidence(self.aliases)
        save(self.output / 'before.json', dict(subjects=self.before, owners=self.owners,
                                              aliases=self.expected_aliases, childEnvironment=self.env, request=request))

    def setup_call(self, label, argv, env, cwd):
        # Retain both real registries around every install/refusal setup call,
        # even a failed call. No actual owner record is hidden or hand-edited.
        def records():
            return {name: registry_evidence(self.root / name / '.agents/.skill-lock.json')
                    for name in ('home-0', 'home-1')}
        before = dict(registries=records(), personal=subjects({name: self.paths[name] for name in ('B', 'C')}))
        receipt = self.output / ('setup-%02d.json' % (self.executor.starts + 1))
        try:
            return self.executor.run(argv, env, cwd)
        finally:
            save(receipt, dict(label=label, childEnvironment=env, cwd=str(cwd), before=before,
                               after=dict(registries=records(), personal=subjects({name: self.paths[name] for name in ('B', 'C')}))))

    def home_env(self, home=None):
        env = dict(self.env)
        if home is not None:
            env.update(HOME=str(home), USERPROFILE=str(home), GEMINI_CLI_HOME=str(home))
        return env

    def product_canonical_refusal(self):
        before, owners = subjects(self.paths), owner_evidence(self.root)
        code, text, _ = self.setup_call('product-A-selector-refusal', [*self.bindings['product'], 'install',
            'fixture/source', 'demo', '--json', '--path', str(self.paths['A'].parent)], self.env, self.root)
        document = json.loads(text)
        reason = 'An existing shared selector would be replaced. Use update or a coordinated maintenance plan before reinstalling.'
        require(code != 0 and document.get('succeeded') is False and document.get('errorMessage') == reason,
                'Product A did not reach its exact existing-selector refusal')
        require(subjects(self.paths) == before and owner_evidence(self.root) == owners,
                'Product existing-selector refusal mutated a participant or registry')

    def raw_canonical(self, home, force):
        argv = [self.env['GH_PATH'], 'skill', 'install', '--dir', str(self.paths['A'].parent)]
        if force:
            argv.append('--force')
        argv += ['--', 'fixture/source', 'demo']
        code, _, _ = self.setup_call('raw-A-force' if force else 'raw-A-first', argv, self.home_env(home), self.root)
        require(code == 0, 'Actual raw gh canonical setup failed; no retry or record repair is permitted')

    def install(self, repo, parent, pinned=False, home=None, cwd=None):
        env = self.home_env(home)
        args = ['install', repo, 'demo', '--json']
        if parent is not None:
            # Both retained gh versions accept only project/user scope. Product
            # --path emits gh --dir, which already overrides scope and agent.
            args += ['--path', str(parent)]
        else:
            args += ['--scope', 'project', '--agent', 'universal']
        if pinned:
            args += ['--version', 'v1', '--pin']
        code, text, _ = self.setup_call('product-install', [*self.bindings['product'], *args], env, cwd or self.root)
        document = json.loads(text)
        require(code == 0 and document.get('succeeded') is True, 'Actual service install did not succeed: ' + str(document))
        require(document.get('pin') == pinned, 'CLI pin request/report mismatch')
        command = document['commandLine']
        require('--' in command and command[command.index('--') + 1] == repo, 'Trusted option separator missing')
        if parent is not None:
            require('--scope' not in command and '--dir' in command
                    and command.index('--dir') < command.index('--')
                    and command[command.index('--dir') + 1] == str(parent), 'Actual custom-directory command boundary differs')
        require(('--pin=v1' in command) == pinned, 'Actual service pin argument differs')
        require(pinned or all('@' not in value for value in command[command.index('--') + 1:]),
                'Unpinned install accidentally requested a selector version')

    def preserved(self):
        for name in ('A', 'D', 'E'):
            require(snapshot(self.paths[name]) == self.before[name], name + ' changed unexpectedly')
        require(owner_evidence(self.root) == self.owners, 'Actual owner record changed')
        require(alias_evidence(self.aliases) == self.expected_aliases, 'An unreviewed alias/root namespace changed')
        for alias in self.aliases:
            require(alias.is_symlink() and alias.resolve(strict=True) == self.paths['A'].parent, 'Shared route broken')

    def next(self, undo=False):
        _, document = self.cli('consolidate-next', '--id', self.plan['id'], '--expect', self.plan['fingerprint'],
                               *(['--undo'] if undo else []))
        return document['result']

    def apply(self, review, expected=True):
        return self.cli('consolidate-apply', '--id', self.plan['id'], '--step-id', review['id'],
                        '--expect', review['fingerprint'], '--yes', '--retain-original', '--accept-provider-contract', expected=expected)

    def position(self, result, count):
        require(len(result['retainedPaths']) == count, 'Unexpected retained native-position count')
        for index, name in enumerate(('B', 'C')):
            if index < count:
                require(not os.path.lexists(self.paths[name]), 'Retained original slot remains occupied')
                retained = Path(result['retainedPaths'][index])
                require(retained.is_relative_to(self.root / 'state'), 'Retained path escaped private state')
                require(stable(snapshot(retained), renamed=True) == stable(self.before[name], renamed=True),
                        'Retained original bytes/objects/metadata differ')
            else:
                require(snapshot(self.paths[name]) == self.before[name], 'Unprocessed sibling changed')
        self.preserved()

    def run(self, scenario):
        if scenario == 'local-change':
            changed = self.paths['B'] / 'references/facts.txt'
            changed.write_bytes(changed.read_bytes() + b'Local private edit\n')
            changed_before = subjects(self.paths)
            save(self.output / 'injected-local-edit.json', changed_before)
            code, document = self.cli('consolidate-plan', '--request', str(self.root / 'request.json'), '--inspect-node', expected=False)
            errors = ' '.join(document.get('errors', []))
            require(code != 0 and document.get('outcome') == 'refused'
                    and 'differs from its independently materialized recorded base' in errors,
                    'Local edit did not reach independent baseline refusal: ' + str(document))
            require(subjects(self.paths) == changed_before, 'Refused local comparison moved/changed participants')
            self.preserved()
            return 'passed-independent-baseline-refusal'
        _, document = self.cli('consolidate-plan', '--request', str(self.root / 'request.json'), '--inspect-node')
        self.plan = document['plan']
        require(self.plan['provider'].get('bundle') is not None, 'Actual published-bundle witness not captured')
        require(self.plan['provider']['bundle']['runtime']['observation']['version'] == 'v22.22.1',
                'This proof requires the reviewed exact actual Node22.22.1 observation')
        save(self.output / 'aggregate.json', self.plan)
        operation = self.root / 'state/SkillView/consolidations' / self.plan['id']
        independent = self.repos['fixture/source'].versions['v1']
        for index, name in enumerate(('B', 'C')):
            participant = self.plan['obsolete'][index]
            require(participant['recordedTree'] == independent.directories['skills/demo'], 'Recorded baseline tree differs')
            require(not participant['subject']['owners'], 'Saved obsolete participant is not ownerless')
            base = operation / ('base-%d' % index)
            check_installed(base, 'fixture/source', independent)
            require((base / 'SKILL.md').read_bytes() == (self.paths[name] / 'SKILL.md').read_bytes(),
                    'Independently staged SKILL differs from actual installed original')
            save(self.output / ('independent-base-%d.json' % index), snapshot(base))
        canonical_owners = self.plan['canonical']['subject']['owners']
        expected_records = {str((self.root / ('home-%d/.agents/.skill-lock.json' % index)).resolve(strict=True)) for index in (0, 1)}
        require({str(Path(owner['lockFilePath']).resolve(strict=True)) for owner in canonical_owners} == expected_records,
                'Actual canonical owner contexts differ from both preserved setup records')
        chosen = self.repos['fixture/source'].versions['v1' if scenario == 'identical' else 'v2']
        require(all(owner['selector'] == 'demo' and owner['contentHash'] == chosen.directories['skills/demo']
                    and owner.get('pinnedRef') is None for owner in canonical_owners), 'Canonical claim differs from selected setup')
        # Retain actual provider/exclusivity/covered-root classification as
        # captured by the unmodified inventory; never relabel legacy claims.
        save(self.output / 'actual-canonical-owner-claims.json', canonical_owners)
        first = self.next()['review']
        require(first['action'] == 'retain' and first['index'] == 0, 'First fresh step must select B')
        refusals = self.plan['obsolete'][0]['metadataRefusals']
        hidden = 'Privileged extended-attribute names may be hidden; complete namespace coverage is unavailable without CAP_SYS_ADMIN. Caller-visible attributes remain captured for review.'
        if refusals:
            # The sole permitted environment qualification: preserve the exact
            # native refusal and prove no durable approval/intent or target move.
            allowed = lambda reason: reason == hidden or reason == 'Root native metadata coverage is unsupported.' or reason.startswith('Entry native metadata coverage is unsupported: ')
            require(hidden in refusals and all(allowed(reason) for reason in refusals),
                    'Unanticipated metadata refusal; no automatic skip: ' + str(refusals))
            code, refused = self.apply(first, expected=False)
            require(code != 0 and refused.get('outcome') == 'refused'
                    and any('Native metadata coverage remains unknown/unsupported' in error for error in refused.get('errors', [])),
                    'Expected native coverage refusal was not observed')
            require(subjects(self.paths) == self.before, 'Metadata-gated refusal moved/changed a participant')
            require(not (operation / 'review-00/intent.json').exists()
                    and not (operation / 'review-00/approval.json').exists(), 'Metadata refusal published approval/intent')
            self.preserved()
            save(self.output / 'native-environment-refusal.json', dict(refusals=refusals, actual=refused,
                                                                     positiveRetainRecoveryUndo='NOT PROVED'))
            return 'qualified-native-trusted-xattr-refusal-positive-skipped'
        if scenario == 'stale-alias':
            before_aliases = alias_evidence(self.aliases)
            target = os.readlink(self.aliases[0])
            self.aliases[0].unlink()
            self.aliases[0].symlink_to(target, target_is_directory=True)
            self.expected_aliases = alias_evidence(self.aliases)
            save(self.output / 'injected-alias-replacement.json', dict(before=before_aliases, after=self.expected_aliases))
            code, refused = self.apply(first, expected=False)
            require(code != 0 and refused.get('outcome') == 'refused'
                    and any('evidence changed' in error.lower() or 'changed' in error.lower()
                            for error in refused.get('errors', [])), 'Stale alias did not refuse native/policy freshness')
            require(subjects(self.paths) == self.before, 'Stale review moved or changed a target')
            require(not (operation / 'review-00/intent.json').exists()
                    and not (operation / 'review-00/approval.json').exists(), 'Stale apply published approval/intent')
            self.preserved()
            return 'passed-stale-native-alias-refusal'
        if scenario in ('kill-intent', 'kill-moved'):
            pause = 'intent.json' if scenario == 'kill-intent' else 'native-moved'
            self.executor.run([*self.bindings['proof'], '--consolidation-pause', str(self.root), self.plan['id'],
                               first['id'], first['fingerprint'], pause, '--retain-original', '--accept-provider-contract',
                               *self.globals], self.env, self.root, pause=pause)
            require((operation / 'review-00/intent.json').is_file()
                    and not (operation / 'review-00/completed.json').exists(), 'Killed boundary journal differs')
            if pause == 'intent.json':
                require(subjects(self.paths) == self.before, 'Intent-only kill already moved an original')
            else:
                require(not os.path.lexists(self.paths['B']), 'Native-moved kill did not vacate B')
                require(stable(snapshot(operation / 'review-00/payload'), renamed=True) == stable(self.before['B'], renamed=True),
                        'Killed retained original object/metadata differs')
            self.preserved()
            _, inspection = self.cli('consolidate-inspect', '--id', self.plan['id'])
            observed = inspection['result']
            require(observed['review']['id'] == first['id'] and observed['observationFingerprint'], 'Restart did not inspect exact step')
            _, recovered = self.cli('consolidate-recover', '--id', self.plan['id'], '--step-id', first['id'],
                                    '--expect', observed['observationFingerprint'], '--yes')
            result = recovered['result']
        else:
            _, applied = self.apply(first)
            result = applied['result']
        require(result['status'] == 'retained', 'First retain was not reconciled')
        self.position(result, 1)
        second = self.next()['review']  # Prepare only after B's reconciliation.
        require(second['index'] == 1 and second['action'] == 'retain' and second['id'] != first['id'], 'No fresh exact C review')
        _, applied = self.apply(second)
        self.position(applied['result'], 2)
        require(self.next()['status'] == 'consolidated', 'Final conditional shared routes not verified')
        # Reverse separately reviewed undo via actual product CLI, never pause host.
        for index, name in ((1, 'C'), (0, 'B')):
            review = self.next(undo=True)['review']
            require(review['action'] == 'restore' and review['index'] == index, 'Undo did not select reverse exact step')
            _, restored = self.apply(review)
            require(restored['result']['status'] == 'restored', 'Actual CLI restore incomplete')
            require(stable(snapshot(self.paths[name]), renamed=True) == stable(self.before[name], renamed=True),
                    'Undo did not restore original native objects/bytes/metadata')
            self.preserved()
        require(self.next(undo=True)['status'] == 'restored', 'Final undo routes/native positions not verified')
        return 'passed-actual-retain-restart-undo' if scenario.startswith('kill-') else 'passed-actual-retain-undo'


def finish_case(server, thread, executor, case, case_output, output, report):
    """Always quiesce owned resources before propagating deferred signals."""
    with CleanupSignals(cancel_timer=True) as cleanup:
        cleanup_failures = []
        server.closing.set()
        try:
            for process in tuple(ACTIVE):
                try:
                    kill_group(process)
                    process.wait(timeout=5)
                    ACTIVE.discard(process)
                except (subprocess.TimeoutExpired, OSError) as error:
                    cleanup_failures.append('Owned child could not be killed/reaped: ' + str(process.pid) + ': ' + str(error))
        finally:
            try:
                if thread.ident is not None:
                    server.shutdown()
            finally:
                try:
                    server.server_close()
                finally:
                    if thread.ident is not None:
                        thread.join(timeout=10)
                    if thread.is_alive():
                        cleanup_failures.append('REST server failed to quiesce')
        case.update(directChildStarts=executor.starts, proxyEvents=server.total_events,
                    proxyFailures=server.failures, cleanupFailures=cleanup_failures,
                    cleanupSignal=cleanup.signum)
        if cleanup.signum is not None:
            case['status'] = 'stopped-no-automatic-retry'
            case.setdefault('failure', 'Controller cancellation deferred through cleanup: ' + str(cleanup.signum))
        save(case_output / 'requests.json', server.evidence())
        save(case_output / 'case.json', case)
        save(output / 'summary.json', report)
        if cleanup.prior_error is None:
            require(not cleanup_failures, 'Controller cleanup incomplete: ' + str(cleanup_failures))
            require(not server.failures, 'Controlled REST failure during final quiescence: ' + str(server.failures))


def read_bindings(path, expected):
    """One small operator-frozen input list, not an authority/payload manifest."""
    require(path.is_absolute() and not path.is_symlink() and stat.S_ISREG(path.stat().st_mode)
            and path.stat().st_size <= 1024 * 1024,
            'Bindings need a bounded absolute regular file')
    with path.open('rb') as stream:
        raw = stream.read(1024 * 1024 + 1)
    require(len(raw) <= 1024 * 1024 and data_sha(raw) == expected, 'Wrapper-frozen bindings hash differs')
    value = json.loads(raw.decode('utf-8'))
    require(set(value) == {'product', 'proof', 'git', 'node', 'entrypoint', 'files'}, 'Unknown/missing binding fields')
    require(isinstance(value['files'], list) and 78 <= len(value['files']) <= 512, 'Bound input count exceeded')
    files, total = {}, 0
    for row in value['files']:
        require(set(row) == {'path', 'sha256'}, 'Only exact local file/hash bindings are accepted')
        name, digest = row['path'], row['sha256']
        require(isinstance(name, str) and Path(name).is_absolute() and name not in files, 'Duplicate/relative input')
        require(isinstance(digest, str) and len(digest) == 64 and all(c in '0123456789abcdef' for c in digest), 'Malformed bound digest')
        item = Path(name)
        require(stat.S_ISREG(item.lstat().st_mode) and item.resolve(strict=True) == item, 'Input must be a native canonical regular file')
        total += item.stat().st_size
        require(total <= 512 * 1024 * 1024 and sha(item) == digest, 'Frozen input hash/aggregate byte bound failed')
        files[name] = digest
    for kind in ('product', 'proof'):
        command = value[kind]
        require(isinstance(command, list) and 1 <= len(command) <= 2 and all(isinstance(p, str) and p in files for p in command),
                'Product/proof command must name frozen native apphost or dotnet+DLL')
    for key in ('git', 'node', 'entrypoint'):
        require(value[key] in files, 'Selected input missing from the exact bindings: ' + key)
    require(Path(value['entrypoint']).parts[-2:] == ('bundle', 'gemini.js'), 'Select the reviewed original published layout')
    package = Path(value['entrypoint']).parent.parent
    code = {str(package / 'package.json'), *[name for name in files if Path(name).is_relative_to(package / 'bundle')]}
    require(len(code) == 78 and code.issubset(files), 'Exactly the original78 release catalog files must be frozen')
    # The product remains authority for the catalog, optional absence and native
    # runtime/metadata gates. A wrapper list or directory label cannot admit it.
    for name in {value['product'][0], value['proof'][0], value['git'], value['node']}:
        with Path(name).open('rb') as stream:
            header = stream.read(4)
        require(header in (b'\x7fELF', b'\xcf\xfa\xed\xfe'), 'Refuse script/shim executable input')
    return value


def interrupt(signum, _frame):
    # One interruption owns cleanup; a second signal must not tear down its
    # kill/reap/server-join finally blocks halfway through.
    for kind in OWN_SIGNALS:
        signal.signal(kind, signal.SIG_IGN)
    for process in tuple(ACTIVE):
        kill_group(process)
    raise InterruptedError('Bounded controller interrupted by signal ' + str(signum))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--execute-root-approved-reviewed-consolidation-fixture', action='store_true')
    parser.add_argument('--bindings', type=Path, required=True)
    parser.add_argument('--bindings-sha256', required=True)
    parser.add_argument('--certificate', type=Path, required=True)
    parser.add_argument('--private-key', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    require(args.execute_root_approved_reviewed_consolidation_fixture, 'Source only: separate root-reviewed runtime approval required')
    require(os.name == 'posix' and hasattr(signal, 'setitimer') and hasattr(signal, 'pthread_sigmask'),
            'This owned process-group supervisor supports POSIX only')
    bindings = read_bindings(args.bindings, args.bindings_sha256)
    certificate, private_key = args.certificate, args.private_key
    for path in (certificate, private_key):
        require(path.is_absolute() and stat.S_ISREG(path.lstat().st_mode), 'External CA files must be absolute native regular files')
    require(sha(certificate) == CA_SHA, 'Exact previously reviewed certificate required')
    require(private_key not in [Path(row['path']) for row in bindings['files']], 'Never copy/freeze private CA material into evidence bindings')
    output = args.output.absolute()
    require(not os.path.lexists(output), 'Refuse output overwrite or implicit retry')
    require(output.parent.resolve(strict=True) == output.parent, 'Output parent must already be canonical')
    output.mkdir(mode=0o700)
    overall = time.monotonic() + OVERALL_SECONDS
    protected = protected_tmp_git()
    previous = {kind: signal.getsignal(kind) for kind in OWN_SIGNALS}
    for kind in previous:
        signal.signal(kind, interrupt)
    report = dict(status='running', maximumCases=12, perCaseSeconds=CASE_SECONDS, overallSeconds=OVERALL_SECONDS,
                  maximumEventsPerCase=MAX_EVENTS, maximumDirectChildStartsPerCase=MAX_STARTS,
                  combinedProcessOutputBytes=PROCESS_BYTES, bindingsSha256=args.bindings_sha256,
                  scriptSha256=sha(Path(__file__)), certificateSha256=CA_SHA, cases=[],
                  protectedTmpGitBefore=protected,
                  qualifications=['Controlled local REST using exact retained real gh; not live GitHub or real authentication.',
                                  'Non-forwarding loopback CONNECT fixture is not an OS firewall or complete syscall/no-write audit.',
                                  'Gemini is never imported/launched. Node runs only the explicit saved-plan builtin-only probe.',
                                  'Conditional local provider policy and sampled native evidence; session activation/publisher authenticity remain unknown.',
                                  'Python xattr observations do not upgrade product native metadata authority.',
                                  'Original M03/M14 and final actual-base/SourceReview integration remain open.'])
    try:
        for version, digest in TOOLS.items():
            gh = Path('/tmp/skillview-unpin-tools/extracted') / version / 'gh'
            require(sha(gh) == digest and stat.S_ISREG(gh.lstat().st_mode), 'Retained real gh hash differs')
            for scenario in SCENARIOS:
                require(len(report['cases']) < 12, 'Fresh case bound exceeded')
                deadline = min(overall, time.monotonic() + CASE_SECONDS)
                require(time.monotonic() < deadline, 'Whole-run deadline exhausted')
                signal.setitimer(signal.ITIMER_REAL, max(.001, deadline - time.monotonic()))
                read_bindings(args.bindings, args.bindings_sha256)
                require(sha(certificate) == CA_SHA, 'Frozen certificate changed before case')
                case_output = output / (version + '-' + scenario)
                case_output.mkdir()
                root = Path('/var/tmp') / ('skillview-consolidation-proof-' + uuid.uuid4().hex)
                root.mkdir(mode=0o700)  # Never reuse/repair/delete an old fixture.
                repos = repositories()
                save(case_output / 'independent-git-objects.json', {name: repo.evidence() for name, repo in repos.items()})
                server = Server(repos, case_output / 'requests.json', deadline, certificate, private_key)
                thread = threading.Thread(target=server.serve_forever, kwargs={'poll_interval': .02}, daemon=False)
                executor = Executor(case_output, deadline, server)
                env = child_env(root, gh, 'http://127.0.0.1:' + str(server.server_address[1]), certificate)
                case = dict(version=version, ghSha256=digest, scenario=scenario, fixture=str(root), status='running')
                report['cases'].append(case)
                try:
                    previous_mask = signal.pthread_sigmask(signal.SIG_BLOCK, OWN_SIGNALS)
                    try:
                        thread.start()
                    finally:
                        signal.pthread_sigmask(signal.SIG_SETMASK, previous_mask)
                    fixture = Case(root, case_output, bindings, env, executor, repos)
                    fixture.setup(scenario)
                    case['status'] = fixture.run(scenario)
                    fixture.preserved()
                    save(case_output / 'after.json', dict(subjects=subjects(fixture.paths), owners=owner_evidence(root),
                                                         aliases=alias_evidence(fixture.aliases)))
                    require(not server.failures and server.total_events <= MAX_EVENTS, 'Controlled REST contract failed')
                    require(protected_tmp_git() == protected, 'Protected /tmp/.git sentinel changed')
                    read_bindings(args.bindings, args.bindings_sha256)
                    require(sha(gh) == digest, 'Real gh changed during case')
                    require(sha(certificate) == CA_SHA, 'Frozen certificate changed during case')
                except BaseException as error:
                    case.update(status='stopped-no-automatic-retry', failure=type(error).__name__ + ': ' + str(error))
                    raise
                finally:
                    # No admission after work ends. Existing bounded workers are
                    # joined by server_close; every owned process is killed/reaped.
                    finish_case(server, thread, executor, case, case_output, output, report)
                signal.setitimer(signal.ITIMER_REAL, 0)
                require(time.monotonic() < overall, 'Whole-run deadline exhausted')
        require(len(report['cases']) == 12, 'All twelve fresh cases are required')
        qualified = any(case['status'].startswith('qualified-') for case in report['cases'])
        report['status'] = 'qualified-native-positives-not-proved' if qualified else 'passed-twelve-bounded-cases'
    except BaseException as error:
        report.update(status='stopped-no-automatic-retry', failure=type(error).__name__ + ': ' + str(error))
        raise
    finally:
        signal.setitimer(signal.ITIMER_REAL, 0)
        for kind, handler in previous.items():
            signal.signal(kind, handler)
        report['protectedTmpGitAfter'] = protected_tmp_git()
        save(output / 'summary.json', report)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
