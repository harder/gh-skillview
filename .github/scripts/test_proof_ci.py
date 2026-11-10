"""Offline failure evidence, prerequisite ordering and no-authority regressions."""
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import Mock, patch

spec = importlib.util.spec_from_file_location('proof_ci', Path(__file__).with_name('proof_ci.py'))
ci = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ci)


class ProofCiTests(unittest.TestCase):
    def test_retained_http_faults_are_distinct_without_publishing_response_content(self):
        cases = [
            (b'HTTP/1.1 403 Forbidden\r\n\r\n{"message":"API rate limit exceeded for installation PRIVATE"}', 'explicit-provider-rate-limit'),
            (b'HTTP/2 403\n\n{"message":"access forbidden PRIVATE"}', 'provider-http-403-cause-unknown'),
            (b'HTTP/1.1 504 Gateway Timeout\n\nPRIVATE', 'provider-http-5xx'),
            (b'HTTP/1.1 200 OK\n\n{"ref":', 'incomplete-or-invalid-http-evidence'),
            (b'HTTP/1.1 200 OK\nPRIVATE', 'incomplete-or-invalid-http-evidence'),
            (b'{"message":"rate limit exceeded"}', 'incomplete-or-invalid-http-evidence'),
            (b'HTTP/1.1 200 OK\n\n{"message":"rate limit exceeded"}', 'http-200-json-observed'),
        ]
        for raw, expected in cases:
            with self.subTest(expected=expected):
                self.assertEqual(ci.classify_http(raw), expected)
                self.assertNotIn('PRIVATE', ci.classify_http(raw))

    def test_budget_context_does_not_relabel_native_failure_or_pass_a_proof(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / 'api-budget').mkdir()
            (root / 'api-budget/api-budget-after-10.json').write_text(json.dumps(
                dict(result='success', utcNs=1, core=dict(limit=5000, used=5000, remaining=0, reset=123))))
            steps = dict(api_primary_10=dict(outcome='failure', conclusion='success'),
                         container_prerequisite=dict(outcome='failure'),
                         unrelated_secret=dict(outcome='failure', output='PRIVATE'))
            with patch.object(ci.subprocess, 'run', side_effect=AssertionError('Unexpected process')):
                result = ci.summary(root, steps)
            self.assertTrue(result['exhaustedBudgetObserved'])
            self.assertEqual(result['latestBudget']['reset'], 123)
            self.assertFalse(result['proofSuccessClaim'])
            self.assertEqual(len(result['failures']), 2)
            self.assertEqual(result['failures'][1], dict(step='api_primary_10', outcome='failure',
                             phase='native-proof-step', cause='unknown'))
            self.assertNotIn('PRIVATE', json.dumps(result))
            self.assertFalse(ci.summary(root, {})['proofSuccessClaim'])

    def test_invalid_oversize_link_and_partial_evidence_are_unknown(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            target = root / 'source'
            target.write_bytes(b'x' * 32769)
            with self.assertRaises(ValueError): ci.read_record(target, 32768)
            link = root / 'link'
            link.symlink_to(target)
            with self.assertRaises((OSError, ValueError)): ci.read_record(link, 32768)
            (root / 'api-budget').mkdir()
            (root / 'api-budget/api-budget-job-start.json').write_text('{')
            result = ci.summary(root, {})
            self.assertFalse(result['exhaustedBudgetObserved'])
            self.assertEqual(result['repositoryResponse'], 'unavailable')

    def test_preflight_propagates_exit_and_never_launches_proof_or_alternate_image(self):
        for code in (0, 125, 3):
            with self.subTest(code=code), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                with patch.object(ci.subprocess, 'run', return_value=Mock(returncode=code)) as run:
                    self.assertEqual(ci.preflight(root), code)
                run.assert_called_once_with(['docker', 'pull', '--quiet', ci.IMAGE],
                    stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, timeout=240)
                value = json.loads((root / 'native-prerequisite.json').read_text())
                self.assertEqual(value['actualExit'], code)
                self.assertFalse(value['proofSuccessClaim'])

    def test_preflight_timeout_and_launch_failure_are_not_native_assertions(self):
        for fault, code in ((OSError('PRIVATE'), 127), (subprocess.TimeoutExpired('PRIVATE', 240), 124)):
            with self.subTest(code=code), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                with patch.object(ci.subprocess, 'run', side_effect=fault):
                    self.assertEqual(ci.preflight(root), code)
                value = (root / 'native-prerequisite.json').read_text()
                self.assertNotIn('PRIVATE', value)
                self.assertEqual(json.loads(value)['phase'], 'infrastructure-prerequisite')

    def test_diagnostic_publication_failure_preserves_original_prerequisite_exit(self):
        with tempfile.TemporaryDirectory() as tmp:
            with patch.object(ci.subprocess, 'run', return_value=Mock(returncode=125)):
                with patch.object(ci, 'publish', side_effect=OSError('PRIVATE')):
                    self.assertEqual(ci.preflight(Path(tmp)), 125)

    def test_workflow_queues_all_proofs_and_checks_prerequisites_before_expensive_work(self):
        root = Path(__file__).resolve().parents[2]
        text = (root / '.github/workflows/ci.yml').read_text()
        ci.validate_scheduling(text)
        native = text.split('  aot-proof:\n', 1)[1].split('  aot-smoke:\n', 1)[0]
        self.assertIn('      group: skillview-native-proof-api\n      queue: max\n      cancel-in-progress: false\n', native)
        self.assertEqual(native.count('          - rid: '), 12)
        self.assertLess(native.index('id: registry_tool'), native.index('      - name: Prove '))
        self.assertLess(native.index('id: container_prerequisite'), native.index('      - name: Prove '))
        preflight = native.split('      - name: Verify privileged registry execution image', 1)[1].split('      - ', 1)[0]
        self.assertNotIn('continue-on-error', preflight)
        launcher = (root / 'tests/native/run-registry-recorded-restore-docker.py').read_text()
        self.assertIn("parser.add_argument('--image', default=" + repr(ci.IMAGE) + ')', launcher)

    def test_queue_compatibility_guard_rejects_replacement_cancellation_and_extra_keys(self):
        root = Path(__file__).resolve().parents[2]
        text = (root / '.github/workflows/ci.yml').read_text()
        for bad in (text.replace('queue: max', 'queue: single'),
                    text.replace('      cancel-in-progress: false', '      cancel-in-progress: true'),
                    text.replace('group: skillview-native-proof-api', 'group: per-run-${{ github.run_id }}'),
                    text + '\nqueue: max\n'):
            with self.subTest(bad=bad[-50:]):
                with self.assertRaises(ValueError): ci.validate_scheduling(bad)
        config = (root / '.github/actionlint.yaml').read_text()
        self.assertEqual(config.count('unexpected key'), 1)
        expected = "  .github/workflows/ci.yml:\n    ignore:\n      - '^unexpected key \"queue\" for \"concurrency\" section\\. expected one of \"cancel-in-progress\", \"group\"$'"
        self.assertTrue(config.rstrip().endswith(expected))
        self.assertIn('^unexpected key "queue" for "concurrency" section\\. expected one of "cancel-in-progress", "group"$', config)


if __name__ == '__main__':
    unittest.main()
