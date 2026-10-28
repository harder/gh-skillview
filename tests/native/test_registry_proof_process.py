import ast
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

import registry_proof_process as proof

ROOT = Path(__file__).parent


def script(name):
    spec = importlib.util.spec_from_file_location(name.replace('-', '_'), ROOT / name)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class RegistryProofProcessTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='registry-proof-unit-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.env = dict(PATH=os.defpath, HOME=str(self.root), USERPROFILE=str(self.root))
        self.artifacts = proof.RegistryProofArtifacts(self.root / 'result.json')
        proof.write_proof_marker(self.root)
        self.record = {}

    def child(self, source, artifacts=None, environment=None):
        return proof.run_checkpoint_child([sys.executable, '-I', '-c', source], environment or self.env, self.root,
                                          self.root / 'kill.stdout', self.root / 'kill.stderr', 'x', self.record,
                                          artifacts or self.artifacts, 'case', ready_timeout=1,
                                          failure_message='Proof host did not reach x')

    def published(self, name):
        return (self.artifacts.root / self.record[name]['artifact']['path']).read_bytes()

    def test_actual_marker_helpers_are_exact_under_forced_windows_text_translation_and_normal_io(self):
        original = Path.write_text
        def crlf(path, data, *args, **kwargs):
            kwargs['newline'] = '\r\n'
            return original(path, data, *args, **kwargs)
        for filename in ('prove-registry-recorded-restore.py', 'prove-registry-recorded-retirement.py'):
            module = script(filename)
            for windows in (False, True):
                with self.subTest(script=filename, windows=windows):
                    if windows:
                        with patch.object(Path, 'write_text', crlf):
                            (self.root / 'control').write_text(proof.MARKER_BYTES.decode())
                            self.assertEqual((self.root / 'control').read_bytes(), proof.MARKER_BYTES.replace(b'\n', b'\r\n'))
                            module.write_proof_marker(self.root)
                    else:
                        module.write_proof_marker(self.root)
                    data = (self.root / 'disposable-proof-marker').read_bytes()
                    self.assertEqual(len(data), 35)
                    self.assertEqual(data, proof.MARKER_BYTES)
                    self.assertNotIn(b'\r', data)

    def test_callsites_use_helper_literal_deadline_and_keep_source_guards(self):
        for filename in ('prove-registry-recorded-restore.py', 'prove-registry-recorded-retirement.py'):
            source = (ROOT / filename).read_text()
            tree = ast.parse(source)
            markers = [n for n in ast.walk(tree) if isinstance(n, ast.Call) and isinstance(n.func, ast.Name) and n.func.id == 'write_proof_marker']
            launches = [n for n in ast.walk(tree) if isinstance(n, ast.Call) and isinstance(n.func, ast.Name) and n.func.id == 'run_checkpoint_child']
            self.assertEqual(len(markers), 1)
            self.assertEqual(len(launches), 1)
            self.assertEqual(next(k.value.value for k in launches[0].keywords if k.arg == 'ready_timeout'), 45)
            for n in ast.walk(tree):
                if isinstance(n, ast.Call) and isinstance(n.func, ast.Attribute) and n.func.attr == 'write_text':
                    self.assertNotIn('disposable process proof', ast.get_source_segment(source, n))
        with patch.object(Path, 'read_bytes', return_value=b'wrong'):
            with self.assertRaisesRegex(AssertionError, 'exact LF bytes'):
                proof.write_proof_marker(self.root)

    def test_early_exit_keeps_primary_and_exact_binary_streams_and_phase(self):
        stdout = b'raw stdout\x00\xff\n'
        stderr = b'maintenance-proof phase: inventory-started\nrefusal\xfe\n'
        source = 'import sys;sys.stdout.buffer.write(' + repr(stdout) + ');sys.stdout.flush();sys.stderr.buffer.write(' + repr(stderr) + ');sys.stderr.flush();sys.exit(2)'
        with self.assertRaisesRegex(AssertionError, '^Proof host did not reach x$'):
            self.child(source)
        self.assertEqual(self.record['exitBeforeKill'], 2)
        self.assertFalse(self.record['parentKilled'])
        self.assertEqual(self.record['finalExit'], 2)
        self.assertFalse(self.record['reached'])
        self.assertEqual(self.record['phase'], 'maintenance-proof phase: inventory-started')
        self.assertEqual(self.published('stdout'), stdout)
        self.assertEqual(self.published('stderr'), stderr)
        for name, data in [('stdout', stdout), ('stderr', stderr)]:
            self.assertEqual(self.record[name]['sha256'], hashlib.sha256(data).hexdigest())
            self.assertEqual(self.record[name]['bytes'], len(data))

    def test_ready_binary_undecodable_prefix_crlf_is_detected_and_real_kill_recorded(self):
        self.child("import sys,time;sys.stdout.buffer.write(b'\\xff\\xfe\\nREADY x\\r\\n');sys.stdout.flush();time.sleep(60)")
        self.assertTrue(self.record['reached'])
        self.assertIsNone(self.record['exitBeforeKill'])
        self.assertTrue(self.record['parentKilled'])
        self.assertIsNotNone(self.record['finalExit'])
        self.assertEqual(self.published('stdout'), b'\xff\xfe\nREADY x\r\n')
        self.assertEqual(self.record['marker']['bytes'], 35)
        self.assertFalse(self.record['marker']['containsCR'])
        self.assertIn('-I', self.record['argv'])

    def test_oversize_head_tail_and_full_digest(self):
        data = bytes(range(256)) * 1200
        source = 'import sys;sys.stdout.buffer.write(bytes(range(256))*1200);sys.stdout.flush();sys.exit(2)'
        with self.assertRaisesRegex(AssertionError, 'Proof host did not reach x'):
            self.child(source)
        item = self.record['stdout']
        self.assertEqual(item['bytes'], len(data))
        self.assertEqual(item['sha256'], hashlib.sha256(data).hexdigest())
        self.assertEqual(item['retained'], 65536)
        self.assertEqual(item['omitted'], len(data) - 65536)
        self.assertTrue(item['omittedFlag'])
        self.assertEqual(self.published('stdout'), data[:32768] + data[-32768:])

    def test_case_and_aggregate_budget_exhaustion_no_overwrite(self):
        publisher = proof.RegistryProofArtifacts(self.root / 'tiny.json', stream_limit=16, case_limit=8, total_limit=10)
        p = self.root / 'private-stream'; p.write_bytes(b'abcdefghijklmnop')
        a = publisher.stream(p, self.env, 'one', {})
        b = publisher.stream(p, self.env, 'one', {})
        c = publisher.stream(p, self.env, 'two', {})
        d = publisher.stream(p, self.env, 'three', {})
        self.assertEqual(a['retained'], 8)
        self.assertEqual(b['withheld'], 'budget-exhausted')
        self.assertEqual(c['retained'], 2)
        self.assertEqual(d['withheld'], 'budget-exhausted')
        self.assertEqual(publisher.total, 10)
        self.assertEqual(len(publisher.summary['artifacts']), 2)
        blocked = proof.RegistryProofArtifacts(self.root / 'empty.json', total_limit=0)
        with self.assertRaisesRegex(AssertionError, '^Proof host did not reach x$'):
            self.child("import sys;print('not ready');sys.exit(2)", artifacts=blocked)
        self.assertEqual(self.record['stdout']['withheld'], 'budget-exhausted')
        with self.assertRaises(ValueError): publisher._artifact('../escape', b'x', 'case')
        with self.assertRaises(FileExistsError): publisher._artifact(a['artifact']['path'], b'', 'fresh')

    def test_publication_failures_never_replace_primary_or_change_success(self):
        path = self.root / 'collision-raw'; path.write_bytes(b'untouched')
        blocked = proof.RegistryProofArtifacts(self.root / 'collision.json')
        with self.assertRaisesRegex(AssertionError, '^Proof host did not reach x$'):
            self.child('import sys;sys.exit(2)', artifacts=blocked)
        self.assertIn('diagnosticError', self.record)
        self.assertEqual(path.read_bytes(), b'untouched')
        with patch.object(self.artifacts, '_artifact', side_effect=OSError('never copy exception text')):
            with self.assertRaisesRegex(AssertionError, '^Proof host did not reach x$'):
                self.child("import sys;print('not ready');sys.exit(2)")
        self.assertIn('diagnosticError', self.record)
        self.assertNotIn('never copy exception text', json.dumps(self.record))
        with patch.object(self.artifacts, '_artifact', side_effect=OSError('publication')):
            self.child("import sys,time;print('READY x',flush=True);time.sleep(60)")
        self.assertTrue(self.record['reached'])
        self.assertIn('diagnosticError', self.record)

    def test_credentials_anywhere_are_withheld_and_never_published(self):
        for index, key in enumerate(proof.SECRET_KEYS):
            token = 'fake-credential-for-test-' + str(index)
            environment = dict(self.env, **{key: token})
            publisher = proof.RegistryProofArtifacts(self.root / (key + '.json'))
            source = "import os,sys;sys.stdout.buffer.write(b'A'*70000+os.environ[" + repr(key) + "].encode()+b'Z'*70000);sys.stdout.flush();sys.exit(2)"
            with self.assertRaisesRegex(AssertionError, 'Proof host did not reach x'):
                self.child(source, artifacts=publisher, environment=environment)
            self.assertEqual(self.record['stdout']['withheld'], 'credential-match')
            self.assertEqual(self.record['stdout']['retained'], 0)
            self.assertNotIn(token, json.dumps(self.record))
            for file in publisher.root.iterdir(): self.assertNotIn(token.encode(), file.read_bytes())
        class Broken(dict):
            def get(self, key, default=None): raise RuntimeError('credential-check failed')
        p = self.root / 'private-token'; p.write_bytes(b'do not publish')
        record = {}
        result = self.artifacts.stream(p, Broken(), 'case', record)
        self.assertEqual(result['withheld'], 'credential-check-error')
        self.assertEqual(result['retained'], 0)
        self.assertIn('diagnosticError', record)
        self.assertFalse(list(self.artifacts.root.iterdir()))

    def test_only_named_regular_bindings_and_fixed_phase_are_exposed(self):
        folder = self.root / 'host'; folder.mkdir()
        host = folder / 'SkillView.MaintenanceProof.exe'; host.write_bytes(b'executable')
        (folder / 'SkillView.Core.dll').write_bytes(b'core')
        outside = self.root / 'private'; outside.write_bytes(b'never read or publish')
        (folder / 'SkillView.MaintenanceProof.dll').symlink_to(outside)
        (folder / 'unrelated.deps.json').write_bytes(b'not named')
        bindings = proof._launch_bindings([str(host)], host)
        self.assertEqual(bindings['SkillView.Core.dll']['sha256'], hashlib.sha256(b'core').hexdigest())
        self.assertIn('diagnosticError', bindings['SkillView.MaintenanceProof.dll'])
        self.assertNotIn('unrelated.deps.json', bindings)
        self.assertNotIn('never read', json.dumps(bindings))
        with self.assertRaisesRegex(AssertionError, 'Proof host did not reach x'):
            self.child("import sys;sys.stderr.buffer.write(b'maintenance-proof phase: arbitrary-secret\\n');sys.exit(2)")
        self.assertIsNone(self.record['phase'])


if __name__ == '__main__':
    unittest.main()
