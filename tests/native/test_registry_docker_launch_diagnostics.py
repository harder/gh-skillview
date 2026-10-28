import ast
import contextlib
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).with_name('run-registry-recorded-restore-docker.py')
spec = importlib.util.spec_from_file_location('registry_docker_launcher', SCRIPT)
launcher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(launcher)

# This executable is the only child main() can find. It is not Docker and never
# runs the supplied container command, payload, gh or product binaries.
FAKE = r'''
import os, sys
from pathlib import Path
args = sys.argv[1:]
mount = next(args[i + 1] for i, value in enumerate(args) if value == '--mount' and ',dst=/proof-output' in args[i + 1])
output = Path(mount.split('src=', 1)[1].split(',dst=', 1)[0])
Path(os.environ['FAKE_CALLED']).write_bytes(b'called')
mode = os.environ.get('FAKE_MODE', 'normal')
token = os.environ.get(os.environ.get('FAKE_TOKEN_KEY', 'GH_TOKEN'), '').encode()
data = b'out\xff\x00\n'
if mode == 'oversize': data = bytes(range(256)) * 1200
if mode == 'token-middle': data = b'A' * 70000 + token + b'Z' * 70000
if mode == 'token-boundary': data = b'A' * (65536 - 5) + token + b'Z' * 70000
if mode == 'token-join':
    half = len(token) // 2
    data = b'A' * (32768 - half) + token[:half] + b'M' * 80000 + token[half:] + b'Z' * (32768 - len(token) + half)
sys.stdout.buffer.write(data); sys.stdout.flush()
sys.stderr.buffer.write(b'err\xfe\n'); sys.stderr.flush()
path = output / 'stdout.log'
if mode == 'symlink':
    path.unlink(); path.symlink_to(os.environ['FAKE_SENTINEL'])
if mode == 'replace':
    other = output / 'replacement'; other.write_bytes(b'NEVER-PUBLISH-SENTINEL')
    path.unlink(); other.rename(path)
if mode == 'hardlink': os.link(path, output / 'alias')
if mode == 'same-inode': path.write_bytes(b'same inode output')
(output / 'private-originals').mkdir()
(output / 'private-originals' / 'sentinel').write_bytes(b'NEVER-PUBLISH-SENTINEL')
sys.exit(int(os.environ.get('FAKE_EXIT', '0')))
'''


class RegistryDockerDiagnosticsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='registry-docker-diagnostics-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.bin = self.root / 'bin'; self.bin.mkdir()
        self.docker = self.bin / 'docker'
        self.docker.write_text('#!' + sys.executable + ' -I\n' + FAKE)
        self.docker.chmod(0o700)
        self.app = self.root / 'skillview'; self.app.write_bytes(b'not executable product')
        self.gh = self.root / 'gh'; self.gh.write_bytes(b'not executable gh')
        self.proof = self.root / 'SkillView.MaintenanceProof'; self.proof.write_bytes(b'not executable proof')
        self.called = self.root / 'called'
        self.sentinel = self.root / 'sentinel'; self.sentinel.write_bytes(b'NEVER-PUBLISH-SENTINEL')
        self.config = self.root / 'config'; self.config.mkdir()
        self.home = self.root / 'home'; self.home.mkdir()
        for folder in (self.config, self.home): (folder / 'sentinel').write_bytes(self.sentinel.read_bytes())
        for name in ('registry', 'payload', 'archive', 'source'): (self.root / name).write_bytes(self.sentinel.read_bytes())
        self.env = dict(PATH=str(self.bin), HOME=str(self.home), USERPROFILE=str(self.home),
                        HTTP_PROXY='http://127.0.0.1:9', HTTPS_PROXY='http://127.0.0.1:9', ALL_PROXY='http://127.0.0.1:9',
                        FAKE_CALLED=str(self.called), FAKE_SENTINEL=str(self.sentinel))
        environment = patch.dict(os.environ, self.env, clear=True)
        environment.start(); self.addCleanup(environment.stop)
        self.sequence = 0

    def invoke(self, code=0, mode='normal', retirement=False, extra_env=None, output=None):
        self.sequence += 1
        self.output = output or self.root / ('proof-' + str(self.sequence))
        self.launch = self.output.with_name(self.output.name + '-launch')
        args = [str(SCRIPT), '--app', str(self.app), '--gh', str(self.gh), '--proof-host', str(self.proof),
                '--gh-config-dir', str(self.config), '--output', str(self.output)]
        if retirement: args.append('--retirement')
        env = dict(self.env, FAKE_EXIT=str(code), FAKE_MODE=mode, **(extra_env or {}))
        with contextlib.chdir(self.root), patch.dict(os.environ, env, clear=True), patch.object(sys, 'argv', args):
            return launcher.main()

    def record(self):
        return json.loads((self.launch / 'launch.json').read_bytes())

    def public_bytes(self):
        return b''.join(p.read_bytes() for p in self.launch.iterdir() if p.is_file())

    def test_real_main_fake_docker_preserves_exit_0_125_3_and_both_roles(self):
        for retirement in (False, True):
            for code in (0, 125, 3):
                with self.subTest(role=retirement, code=code):
                    if code:
                        with self.assertRaises(SystemExit) as raised: self.invoke(code, retirement=retirement)
                        self.assertEqual(raised.exception.code, code)
                    else: self.invoke(retirement=retirement)
                    record = self.record()
                    self.assertEqual(record['stage'], 'returned')
                    self.assertEqual(record['returnCode'], code)
                    self.assertEqual(record['kind'], 'retirement' if retirement else 'restore')
                    self.assertEqual(json.loads((self.output / 'exit.json').read_bytes()), {'exit': code})
                    self.assertEqual(record['timeoutSeconds'], 1800)
                    for name, data in [('stdout', b'out\xff\x00\n'), ('stderr', b'err\xfe\n')]:
                        self.assertEqual((self.launch / (name + '.bin')).read_bytes(), data)
                        self.assertEqual(record[name]['sha256'], hashlib.sha256(data).hexdigest())
                        self.assertEqual(record[name]['bytes'], len(data))
                    self.assertNotIn(str(self.root).encode(), self.public_bytes())
                    self.assertNotIn(b'apt-get', self.public_bytes())
                    self.assertNotIn(b'NEVER-PUBLISH-SENTINEL', self.public_bytes())
                    self.assertEqual(stat.S_IMODE(self.launch.stat().st_mode), 0o700)
                    self.assertTrue(all(stat.S_IMODE(p.stat().st_mode) == 0o600 for p in self.launch.iterdir()))

    def test_oserror_timeout_same_object_and_no_diagnostic_masking(self):
        for error, stage in [(OSError('private message'), 'exception'), (subprocess.TimeoutExpired(['private command'], 1800), 'timeout')]:
            with self.subTest(stage=stage), patch.object(launcher.subprocess, 'run', side_effect=error) as run:
                with self.assertRaises(type(error)) as raised: self.invoke()
                self.assertIs(raised.exception, error)
                self.assertEqual(run.call_args.kwargs['timeout'], 1800)
                record = self.record()
                self.assertEqual(record['stage'], stage)
                self.assertIsNone(record['returnCode'])
                self.assertNotIn(b'private message', self.public_bytes())
                self.assertNotIn(b'private command', self.public_bytes())
        error = OSError('original')
        with patch.object(launcher.subprocess, 'run', side_effect=error), patch.object(launcher, '_publish', side_effect=RuntimeError('diagnostic')):
            with self.assertRaises(OSError) as raised: self.invoke()
            self.assertIs(raised.exception, error)

    def test_fresh_sibling_file_directory_link_and_mkdir_error_abort_before_launch(self):
        for kind in ('file', 'directory', 'link', 'error'):
            output = self.root / kind; sibling = output.with_name(output.name + '-launch')
            if kind == 'file': sibling.write_bytes(b'old')
            if kind == 'directory': sibling.mkdir(); (sibling / 'launch.json').write_bytes(b'old')
            if kind == 'link': sibling.symlink_to(self.config, target_is_directory=True)
            original = Path.mkdir
            def mkdir(path, *args, **kwargs):
                if path == sibling and kind == 'error': raise PermissionError('private mkdir')
                return original(path, *args, **kwargs)
            with self.subTest(kind=kind), patch.object(Path, 'mkdir', mkdir):
                with self.assertRaises(OSError): self.invoke(output=output)
            self.assertFalse(self.called.exists())
            self.assertEqual(list(output.iterdir()), [])
            if kind == 'file': self.assertEqual(sibling.read_bytes(), b'old')
            if kind == 'directory': self.assertEqual((sibling / 'launch.json').read_bytes(), b'old')
            if kind == 'link': self.assertTrue(sibling.is_symlink())
            self.assertEqual((self.config / 'sentinel').read_bytes(), b'NEVER-PUBLISH-SENTINEL')

    def test_existing_output_is_untouched_before_sibling_or_launch(self):
        output = self.root / 'existing'; output.mkdir(); (output / 'sentinel').write_bytes(b'old')
        with self.assertRaises(FileExistsError): self.invoke(output=output)
        self.assertFalse(self.called.exists())
        self.assertEqual((output / 'sentinel').read_bytes(), b'old')
        self.assertFalse(output.with_name(output.name + '-launch').exists())

    def test_original_inode_substitutions_never_open_or_publish_sentinel(self):
        original = launcher.os.open
        for mode in ('symlink', 'replace', 'hardlink'):
            def guarded(path, *args, **kwargs):
                if Path(path) == self.sentinel or (Path(path).name == 'stdout.log'):
                    raise AssertionError('must reject before reading another inode')
                return original(path, *args, **kwargs)
            with self.subTest(mode=mode), patch.object(launcher.os, 'open', guarded): self.invoke(mode=mode)
            record = self.record()['stdout']
            self.assertEqual(record['withheld'], 'substituted-or-unstable')
            self.assertIsNone(record['sha256'])
            self.assertFalse(record['captureComplete'])
            self.assertFalse((self.launch / 'stdout.bin').exists())
            self.assertNotIn(b'NEVER-PUBLISH-SENTINEL', self.public_bytes())
        self.invoke(mode='same-inode')
        self.assertEqual((self.launch / 'stdout.bin').read_bytes(), b'same inode output')

    def stream_fixture(self):
        path = self.root / ('stream-' + str(self.sequence)); self.sequence += 1
        path.write_bytes(b'example')
        info = path.stat()
        folder = self.root / ('raw-' + str(self.sequence)); folder.mkdir()
        return path, (info.st_dev, info.st_ino), folder

    def test_unstable_missing_and_hash_error_are_honest_unknown(self):
        path, identity, folder = self.stream_fixture()
        original = launcher._unchanged
        def changing(source, current, before):
            with current.open('ab') as target: target.write(b'x')
            return original(source, current, before)
        with patch.object(launcher, '_unchanged', changing):
            result = launcher._stream(path, identity, folder, 'stdout.bin', {}, [144 * 1024])
        self.assertEqual(result['withheld'], 'substituted-or-unstable')
        self.assertIsNone(result['sha256'])
        self.assertIsNone(result['bytes'])
        path.unlink()
        result = launcher._stream(path, identity, folder, 'stdout.bin', {}, [144 * 1024])
        self.assertEqual(result['withheld'], 'substituted-or-unstable')
        path, identity, folder = self.stream_fixture()
        with patch.object(launcher.hashlib, 'sha256', side_effect=OSError('hash unavailable')):
            result = launcher._stream(path, identity, folder, 'stdout.bin', {}, [144 * 1024])
        self.assertIsNone(result['sha256'])
        self.assertFalse(result['captureComplete'])
        self.assertEqual(result['withheld'], 'diagnostic-error')
        self.assertEqual(list(folder.iterdir()), [])

    def test_oversize_full_hash_head_tail_and_all_caps(self):
        self.invoke(mode='oversize')
        data = bytes(range(256)) * 1200
        record = self.record(); stream = record['stdout']
        self.assertEqual(stream['bytes'], len(data))
        self.assertEqual(stream['sha256'], hashlib.sha256(data).hexdigest())
        self.assertEqual(stream['retained'], 65536)
        self.assertEqual(stream['omitted'], len(data) - 65536)
        self.assertEqual((self.launch / 'stdout.bin').read_bytes(), data[:32768] + data[-32768:])
        self.assertLessEqual((self.launch / 'launch.json').stat().st_size, 16384)
        self.assertLessEqual(sum(p.stat().st_size for p in self.launch.iterdir()), 144 * 1024)

    def test_tokens_middle_chunk_boundary_and_head_tail_join_are_withheld(self):
        for key in launcher.TOKEN_NAMES:
            token = 'fake-secret-for-diagnostics-' + key
            for mode in ('token-middle', 'token-boundary', 'token-join'):
                with self.subTest(key=key, mode=mode):
                    self.invoke(mode=mode, extra_env={key: token, 'FAKE_TOKEN_KEY': key})
                    result = self.record()['stdout']
                    self.assertEqual(result['withheld'], 'credential-match')
                    self.assertEqual(result['retained'], 0)
                    self.assertTrue(result['captureComplete'])
                    self.assertFalse((self.launch / 'stdout.bin').exists())
                    self.assertNotIn(token.encode(), self.public_bytes())

    def test_token_access_encoding_matching_and_metadata_fail_closed(self):
        path, identity, folder = self.stream_fixture()
        for mechanism in ('access', 'encoding', 'matching'):
            context = patch.object(launcher.os.environ, 'get', side_effect=RuntimeError('private')) if mechanism == 'access' else patch.object(launcher.os.environ, 'get', return_value='\ud800') if mechanism == 'encoding' else patch.object(launcher, '_contains_secret', side_effect=RuntimeError('private'))
            with context:
                result = launcher._stream(path, identity, folder, 'stdout.bin', {}, [144 * 1024])
            self.assertEqual(result['withheld'], 'credential-check-error')
            self.assertEqual(result['retained'], 0)
        self.assertFalse(list(folder.iterdir()))
        with patch.object(launcher, '_tokens', side_effect=RuntimeError('private')): self.invoke()
        self.assertEqual(self.record()['withheld'], 'credential-check-error')
        self.assertEqual(sorted(p.name for p in self.launch.iterdir()), ['launch.json'])
        self.invoke(extra_env={'GH_TOKEN': 'docker'})
        self.assertEqual(self.record()['withheld'], 'credential-match')
        self.assertNotIn(b'docker', self.public_bytes())
        contains = launcher._contains_secret
        def metadata_error(data, tokens):
            if data.startswith(b'{'): raise RuntimeError('private')
            return contains(data, tokens)
        with patch.object(launcher, '_contains_secret', metadata_error): self.invoke()
        self.assertEqual(self.record()['withheld'], 'credential-check-error')

    def test_budget_partial_write_no_overwrite_and_primary_result(self):
        path, identity, folder = self.stream_fixture()
        result = launcher._stream(path, identity, folder, 'stdout.bin', {}, [0])
        self.assertEqual(result['withheld'], 'budget-exhausted')
        self.assertFalse(list(folder.iterdir()))
        budget = [144 * 1024]; fdopen = launcher.os.fdopen
        class Partial:
            def __init__(self, fd, mode): self.file = fdopen(fd, mode)
            def __enter__(self): return self
            def __exit__(self, *args): self.file.close()
            def write(self, data): self.file.write(data[:2]); self.file.flush(); raise OSError('private partial')
        with patch.object(launcher.os, 'fdopen', Partial):
            with self.assertRaises(OSError): launcher._write_leaf(folder, 'stdout.bin', b'safe bytes', budget)
        self.assertEqual(budget[0], 144 * 1024 - len(b'safe bytes'))
        self.assertEqual((folder / 'stdout.bin').read_bytes(), b'sa')
        with self.assertRaises(FileExistsError): launcher._write_leaf(folder, 'stdout.bin', b'new', budget)
        self.assertEqual((folder / 'stdout.bin').read_bytes(), b'sa')
        self.assertFalse(launcher._write_leaf(folder, '../escape', b'x', budget))
        self.assertFalse(launcher._write_leaf(folder, 'launch.json', b'x' * 16385, budget))
        for code in (0, 125):
            with patch.object(launcher, '_write_leaf', side_effect=OSError('never publish message')):
                if code:
                    with self.assertRaises(SystemExit) as raised: self.invoke(code)
                    self.assertEqual(raised.exception.code, code)
                else: self.invoke()
            self.assertFalse(list(self.launch.iterdir()))

    def test_later_stream_collision_and_metadata_errors_do_not_mask_result(self):
        write = launcher._write_leaf
        def fail_stream(root, name, data, budget):
            if name == 'stdout.bin': raise FileExistsError('sensitive collision')
            return write(root, name, data, budget)
        for code in (0, 125):
            with patch.object(launcher, '_write_leaf', fail_stream):
                if code:
                    with self.assertRaises(SystemExit) as raised: self.invoke(code)
                    self.assertEqual(raised.exception.code, code)
                else: self.invoke()
            record = self.record()
            self.assertEqual(record['stdout']['withheld'], 'diagnostic-error')
            self.assertIn('FileExistsError', record['diagnosticError'])
            self.assertNotIn('sensitive collision', json.dumps(record))
        describe = launcher._describe
        def big(record, args, command):
            describe(record, args, command); record['bindings']['oversize'] = 'x' * 20000
        with patch.object(launcher, '_describe', big): self.invoke()
        self.assertTrue(self.record()['bindingsOmitted'])
        self.assertLessEqual((self.launch / 'launch.json').stat().st_size, 16384)

    def test_named_bindings_only_and_private_sentinels_unread_unpublished(self):
        core = self.root / 'SkillView.Core.dll'; core.write_bytes(b'core')
        (self.root / 'SkillView.MaintenanceProof.dll').symlink_to(self.sentinel)
        (self.root / 'unlisted.deps.json').write_bytes(b'NEVER-PUBLISH-SENTINEL')
        protected = [self.sentinel, self.config, self.home] + [self.root / n for n in ('registry', 'payload', 'archive', 'source')]
        original = launcher.os.open; violations = []
        def guard(path, *args, **kwargs):
            path = Path(path)
            if any(path == item or item in path.parents for item in protected) or 'private-originals' in path.parts:
                violations.append(str(path)); raise AssertionError('private sentinel read')
            return original(path, *args, **kwargs)
        with patch.object(launcher.os, 'open', guard): self.invoke()
        self.assertEqual(violations, [])
        bindings = self.record()['bindings']
        self.assertEqual(bindings['app-core']['sha256'], hashlib.sha256(b'core').hexdigest())
        self.assertTrue(bindings['app-proof']['unavailable'])
        self.assertNotIn(b'NEVER-PUBLISH-SENTINEL', self.public_bytes())
        self.assertNotIn(b'unlisted', self.public_bytes())
        for path in [self.sentinel, self.config / 'sentinel', self.home / 'sentinel']:
            self.assertEqual(path.read_bytes(), b'NEVER-PUBLISH-SENTINEL')

    def test_source_guards_exact_execution_construction_and_six_named_paths(self):
        source = SCRIPT.read_text()
        segment = source[source.index('    container = '):source.index('    record = dict(schema=1')]
        self.assertEqual(hashlib.sha256(segment.encode()).hexdigest(), 'a48c77190da27aa0cd4dba4dfe7305a2fc5f4bfc58a8caba25986e6696d41065')
        self.assertIn('result = subprocess.run(command, stdout=stdout, stderr=stderr, timeout=1800)', source)
        self.assertIn("(output / 'exit.json').write_text(json.dumps(dict(exit=result.returncode)))", source)
        self.assertIn('if result.returncode: raise SystemExit(result.returncode)', source)
        tree = ast.parse(source)
        main = next(n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == 'main')
        self.assertEqual(main.args.args, [])
        self.assertIn("output = args.output.resolve(); output.mkdir(parents=True, exist_ok=False)\n    launch = output.with_name(output.name + '-launch')\n    launch.mkdir(mode=0o700, exist_ok=False)", source)
        workflow = (SCRIPT.parents[2] / '.github/workflows/ci.yml').read_text()
        lines = [line.strip() for line in workflow.splitlines() if 'dist/registry-' in line and 'privileged-' in line and '-proof-launch/' in line]
        self.assertEqual(lines, ['dist/registry-' + role + '-privileged-*-proof-launch/' + name for role in ('restore', 'retirement') for name in ('launch.json', 'stdout.bin', 'stderr.bin')])
        self.assertIn('            dist/registry-restore-*-proof-raw/**\n', workflow)
        self.assertIn('            dist/registry-retirement-*-proof-raw/**\n', workflow)
        self.assertNotIn('dist/registry-restore-privileged-*-proof/**', workflow)
        self.assertNotIn('dist/registry-retirement-privileged-*-proof/**', workflow)


if __name__ == '__main__':
    unittest.main()
