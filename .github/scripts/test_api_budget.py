"""Offline diagnostics, failure preservation and immutable proof-body guards."""
import ast
import contextlib
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import types
import unittest
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('api_budget', Path(__file__).with_name('api_budget.py'))
budget = importlib.util.module_from_spec(spec)
spec.loader.exec_module(budget)

BASE_HASHES = {'.github/workflows/ci.yml': 'a9250a7c4d6238b86002f551a47119b81442e456401e25c0b82251cafe5fc692', 'tests/native/prove-registry-recorded-restore.py': '509489c6a38123f3fda245c5d5b26fea7fef6aeee8cea1aef77a1578668afac9', 'tests/native/prove-registry-recorded-retirement.py': '3443b9564fbb5136a967c2909469e2157d6b4851e47e36b055f5965f73b039e9'}

CORE = dict(limit=5000, used=5000, remaining=0, reset=1793219888)
DIAGNOSTICS = """    report['commandDiagnostics'] = dict(
        directGhCommands=dict(skillInstall=0, api=0, other=0),
        scope='Attempted direct gh subprocess.run invocations by this script only',
        productGhApiCommands=None, httpRequests=None,
        unknownReason='No existing product debug request log; gh-internal HTTP and other proofs are unknown')
"""
RESTORE_COUNT = """                        if str(argv[0]) == str(gh):
                            category = 'skillInstall' if tuple(argv[1:3]) == ('skill', 'install') else 'api' if tuple(argv[1:2]) == ('api',) else 'other'
                            report['commandDiagnostics']['directGhCommands'][category] += 1
"""


class BudgetTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        old = Path.cwd()
        os.chdir(self.temp.name)
        self.addCleanup(os.chdir, old)
        self.query = patch.object(budget, 'query_core', return_value=CORE.copy()).start()
        self.addCleanup(patch.stopall)

    def record(self, stage='job-start', result='before'):
        self.assertEqual(budget.observe(stage, 'linux-x64', 'sources', result), 0)
        return json.loads((Path('dist/api-budget') / f'api-budget-{stage}.json').read_text())

    def test_only_fixed_fields_and_numeric_core_leave_the_query(self):
        value = self.record()
        self.assertEqual(value['core'], CORE)
        self.assertEqual(set(value), {'job', 'rid', 'shard', 'stage', 'utcNs', 'proofResult', 'result', 'errorType', 'core'})
        self.assertEqual(value['result'], 'success')
        self.assertIsNone(value['errorType'])
        self.assertIs(type(value['utcNs']), int)
        self.assertLessEqual(Path('dist/api-budget/api-budget-job-start.json').stat().st_size, budget.MAX_RECORD)

    def test_error_is_unknown_not_zero_and_cannot_print_private_text(self):
        self.query.side_effect = budget.ObservationError('private-token-and-response')
        value = self.record()
        self.assertEqual(value['errorType'], 'InvalidResponse')
        self.assertTrue(all(v is None for v in value['core'].values()))
        self.assertNotIn('private-token', json.dumps(value))

    def test_each_failure_type_is_preserved_without_failing_the_proof_step(self):
        for index, kind in enumerate(budget.ERROR_TYPES, 1):
            with self.subTest(kind=kind):
                self.query.side_effect = budget.ObservationError(kind)
                value = self.record(f'before-{index:02}')
                self.assertEqual(value['errorType'], kind)
                self.assertEqual(value['result'], 'error')

    def test_duplicate_stage_never_overwrites_or_queries_again(self):
        self.record()
        path = Path('dist/api-budget/api-budget-job-start.json')
        original = path.read_bytes()
        with contextlib.redirect_stderr(io.StringIO()) as messages:
            self.assertEqual(budget.observe('job-start', 'linux-x64', 'sources', 'before'), 0)
        self.assertEqual(path.read_bytes(), original)
        self.assertEqual(self.query.call_count, 1)
        self.assertEqual(json.loads(messages.getvalue())['errorType'], 'FileError')

    def test_aggregate_has_a_fixed_finite_record_and_byte_budget(self):
        for stage in budget.STAGES:
            self.record(stage)
        paths = list(Path('dist/api-budget').iterdir())
        self.assertEqual(len(paths), len(budget.STAGES))
        self.assertLessEqual(sum(p.stat().st_size for p in paths), len(budget.STAGES) * budget.MAX_RECORD)
        count = self.query.call_count
        with contextlib.redirect_stderr(io.StringIO()):
            budget.observe('before-01', 'linux-x64', 'sources', 'before')
        self.assertEqual(self.query.call_count, count)

    def test_unknown_or_oversize_entry_refuses_before_query(self):
        Path('dist/api-budget').mkdir(parents=True)
        for name, payload in [('foreign.json', b'x'), ('api-budget-after-01.json', b'x' * (budget.MAX_RECORD + 1))]:
            with self.subTest(name=name):
                path = Path('dist/api-budget') / name
                path.write_bytes(payload)
                with contextlib.redirect_stderr(io.StringIO()):
                    budget.observe('job-start', 'linux-x64', 'sources', 'before')
                self.query.assert_not_called()
                self.assertEqual(path.read_bytes(), payload)
                path.unlink()

    @unittest.skipIf(os.name == 'nt', 'Link creation privilege is not assumed on Windows')
    def test_linked_directory_and_linked_record_are_never_followed(self):
        outside = Path('outside'); outside.mkdir()
        Path('dist').symlink_to(outside, target_is_directory=True)
        with contextlib.redirect_stderr(io.StringIO()):
            budget.observe('job-start', 'linux-x64', 'sources', 'before')
        self.query.assert_not_called()
        self.assertEqual(list(outside.iterdir()), [])
        Path('dist').unlink()
        Path('dist/api-budget').mkdir(parents=True)
        target = outside / 'payload'; target.write_text('unchanged')
        Path('dist/api-budget/api-budget-job-start.json').symlink_to(target.resolve())
        with contextlib.redirect_stderr(io.StringIO()):
            budget.observe('job-start', 'linux-x64', 'sources', 'before')
        self.assertEqual(target.read_text(), 'unchanged')
        self.query.assert_not_called()

    def test_invalid_labels_never_become_paths_or_queries(self):
        for stage, rid, shard, result in [('../outside', 'linux-x64', 'sources', 'before'),
                                         ('job-start', '../outside', 'sources', 'before'),
                                         ('job-start', 'linux-x64', 'foreign', 'before'),
                                         ('job-start', 'linux-x64', 'sources', 'token-value')]:
            with self.assertRaises(ValueError):
                budget.observe(stage, rid, shard, result)
        self.query.assert_not_called()
        self.assertFalse(Path('dist').exists())

    def test_primary_failure_exit_and_exception_survive_successful_after_observation(self):
        for index, code in enumerate(["import sys; sys.exit(23)", "raise RuntimeError('primary failure sentinel')"], 1):
            self.record(f'before-{index:02}')
            primary = subprocess.run([sys.executable, '-c', code], capture_output=True)
            after = self.record(f'after-{index:02}', 'failure')
            self.assertEqual(primary.returncode, 23 if index == 1 else 1)
            self.assertEqual(after['proofResult'], 'failure')
            self.assertEqual(after['result'], 'success')
            if index == 2:
                self.assertIn(b'RuntimeError: primary failure sentinel', primary.stderr)


class QueryTests(unittest.TestCase):
    def child(self, payload, code=0):
        child = Mock()
        child.stdout = io.BytesIO(payload)
        child.wait.return_value = code
        child.poll.return_value = code
        return child

    def test_query_executes_once_without_debug_or_stderr_or_secret_publication(self):
        child = self.child(json.dumps(CORE | {'credential': 'private-value'}).encode())
        with patch.dict(os.environ, {'GH_TOKEN': 'private-value', 'GH_DEBUG': 'api'}), patch.object(budget.subprocess, 'Popen', return_value=child) as run:
            self.assertEqual(budget.query_core(), CORE)
        run.assert_called_once()
        argv = run.call_args.args[0]
        self.assertEqual(argv, budget.COMMAND)
        self.assertNotIn('--include', argv)
        self.assertNotIn('GH_DEBUG', run.call_args.kwargs['env'])
        self.assertEqual(run.call_args.kwargs['env']['GH_TOKEN'], 'private-value')
        self.assertEqual(run.call_args.kwargs['stderr'], subprocess.DEVNULL)
        child.kill.assert_not_called()

    def test_nonzero_invalid_oversize_and_non_numeric_responses_are_not_success(self):
        values = [(b'private response', 1, 'CommandError'), (b'private response', 0, 'InvalidResponse'),
                  (b'x' * (budget.MAX_RESPONSE + 1), 0, 'InvalidResponse'),
                  (json.dumps(CORE | {'remaining': True}).encode(), 0, 'InvalidResponse'),
                  (json.dumps(CORE | {'used': -1}).encode(), 0, 'InvalidResponse'),
                  (json.dumps(CORE | {'reset': 2**63}).encode(), 0, 'InvalidResponse'),
                  (b'{}', 0, 'InvalidResponse')]
        for payload, code, expected in values:
            with self.subTest(expected=expected, payloadLength=len(payload)):
                with patch.object(budget.subprocess, 'Popen', return_value=self.child(payload, code)):
                    with self.assertRaisesRegex(budget.ObservationError, '^' + expected + '$'):
                        budget.query_core()

    def test_launch_failure_never_retains_the_exception_message(self):
        with patch.object(budget.subprocess, 'Popen', side_effect=OSError('private-path-and-token')):
            with self.assertRaisesRegex(budget.ObservationError, '^LaunchError$'):
                budget.query_core()

    def test_timeout_kills_only_its_query_and_uses_bounded_waits(self):
        child = self.child(b'')
        child.poll.return_value = None
        reader = Mock()
        reader.is_alive.side_effect = [True, False]
        with patch.object(budget.subprocess, 'Popen', return_value=child), patch.object(budget.threading, 'Thread', return_value=reader):
            with self.assertRaisesRegex(budget.ObservationError, '^Timeout$'):
                budget.query_core()
        child.kill.assert_called_once_with()
        child.wait.assert_called_once_with(timeout=budget.CLEANUP_SECONDS)
        self.assertTrue(all(0 <= c.args[0] <= budget.QUERY_SECONDS for c in reader.join.call_args_list))

    def test_unsettled_owned_query_cleanup_is_an_error_not_a_success(self):
        child = self.child(b'')
        child.poll.return_value = None
        child.wait.side_effect = subprocess.TimeoutExpired('gh', budget.CLEANUP_SECONDS)
        reader = Mock(); reader.is_alive.return_value = True
        with patch.object(budget.subprocess, 'Popen', return_value=child), patch.object(budget.threading, 'Thread', return_value=reader):
            with self.assertRaisesRegex(budget.ObservationError, '^CleanupError$'):
                budget.query_core()
        child.kill.assert_called_once_with()


class ProofAndWorkflowGuards(unittest.TestCase):
    def test_ci_runs_api_budget_tests_in_the_existing_contract_step(self):
        text = (ROOT / '.github/workflows/ci.yml').read_text()
        step = re.search(r'^      - name: Test CI scope and native input contracts\n(.*?)(?=^      - |\Z)',
                         text, re.M | re.S)
        self.assertIsNotNone(step)
        self.assertIn('        run: |\n', step.group(1))
        commands = [line.strip() for line in step.group(1).splitlines() if line.startswith('          ')]
        self.assertEqual(commands, [
            'node --test .github/scripts/ci-change-scope.test.js',
            'python3 -m unittest discover -s .github/scripts -p test_native_proof_inputs.py',
            'python3 -m unittest discover -s .github/scripts -p test_api_budget.py',
        ])

    def test_original_ci_matrix_permissions_proof_bodies_argv_env_and_deadlines_are_byte_exact(self):
        text = (ROOT / '.github/workflows/ci.yml').read_text()
        blocks = re.split(r'(?=^      - )', text, flags=re.M)
        observed = [b for b in blocks if b.startswith('      - name: Observe API budget ')]
        self.assertEqual(len(observed), len(budget.STAGES))
        restored = ''.join(b for b in blocks if b not in observed)
        restored = re.sub(r'^        id: api_primary_\d{2}\n', '', restored, flags=re.M)
        self.assertEqual(restored.count('            dist/api-budget/api-budget-*.json\n'), 1)
        restored = restored.replace('            dist/api-budget/api-budget-*.json\n', '')
        self.assertEqual(restored.count('          python3 -m unittest discover -s .github/scripts -p test_api_budget.py\n'), 1)
        restored = restored.replace('          python3 -m unittest discover -s .github/scripts -p test_api_budget.py\n', '')
        self.assertEqual(hashlib.sha256(restored.encode()).hexdigest(), BASE_HASHES['.github/workflows/ci.yml'])
        native = text.split('  aot-proof:\n', 1)[1].split('  aot-smoke:\n', 1)[0]
        self.assertEqual(native.count('          - rid: '), 12)
        self.assertIn('      max-parallel: 4\n', text)

    def test_every_gh_step_has_separate_best_effort_observers_and_failure_after_guard(self):
        text = (ROOT / '.github/workflows/ci.yml').read_text()
        blocks = re.split(r'(?=^      - )', text, flags=re.M)
        primaries = [i for i, b in enumerate(blocks) if '        id: api_primary_' in b]
        self.assertEqual(len(primaries), 18)
        for index, position in enumerate(primaries, 1):
            primary = blocks[position]
            condition = re.search(r'^        if: (.+)$', primary, re.M).group(1)
            before, after = blocks[position - 1], blocks[position + 1]
            self.assertIn(f'--stage before-{index:02}', before)
            self.assertIn(f'        if: {condition}\n', before)
            self.assertIn(f'--stage after-{index:02}', after)
            self.assertIn(f"always() && ({condition}) && steps.api_primary_{index:02}.outcome != 'skipped'", after)
            self.assertIn(f"--result '${{{{ steps.api_primary_{index:02}.outcome }}}}'", after)
            for block in (before, after):
                self.assertIn('        continue-on-error: true\n', block)
                self.assertEqual(block.count('GH_TOKEN: ${{ github.token }}'), 1)
                self.assertEqual(block.count('        run:'), 1)
            self.assertNotIn('continue-on-error:', primary)

    def test_restore_cases_plans_negatives_hashes_markers_and_native_guards_unchanged(self):
        name = 'tests/native/prove-registry-recorded-restore.py'
        text = (ROOT / name).read_text()
        self.assertEqual(text.count(DIAGNOSTICS), 1)
        self.assertEqual(text.count(RESTORE_COUNT), 1)
        restored = text.replace(DIAGNOSTICS, '').replace(RESTORE_COUNT, '')
        self.assertEqual(hashlib.sha256(restored.encode()).hexdigest(), BASE_HASHES[name])

    def test_retirement_cases_plans_negatives_hashes_markers_and_native_guards_unchanged(self):
        name = 'tests/native/prove-registry-recorded-retirement.py'
        text = (ROOT / name).read_text()
        self.assertEqual(text.count(DIAGNOSTICS), 1)
        restored = text.replace(DIAGNOSTICS, '')
        restored = restored.replace('def prepare_live_evidence(root, gh, gh_counts):', 'def prepare_live_evidence(root, gh):')
        restored = restored.replace("        gh_counts['skillInstall'] += 1\n", '')
        restored = restored.replace("prepare_live_evidence(root, gh, report['commandDiagnostics']['directGhCommands'])", 'prepare_live_evidence(root, gh)')
        self.assertEqual(hashlib.sha256(restored.encode()).hexdigest(), BASE_HASHES[name])

    def test_actual_restore_wrapper_counts_direct_attempts_including_failure_but_not_product(self):
        module = ast.parse((ROOT / 'tests/native/prove-registry-recorded-restore.py').read_text())
        command = next(n for n in ast.walk(module) if isinstance(n, ast.FunctionDef) and n.name == 'command')
        command.body = [ast.Global(names=n.names) if isinstance(n, ast.Nonlocal) else n for n in command.body]
        namespace = dict(sequence=0, gh=Path('/fake/gh'), receipt_dir=None, env={}, case_root=None,
                         subprocess=Mock(), json=json, report={'commandDiagnostics': {'directGhCommands': dict(skillInstall=0, api=0, other=0)}})
        namespace['subprocess'].TimeoutExpired = subprocess.TimeoutExpired
        namespace['subprocess'].run.return_value = types.SimpleNamespace(returncode=0, stdout=b'{}', stderr=b'')
        exec(compile(ast.fix_missing_locations(ast.Module(body=[command], type_ignores=[])), '<actual-wrapper>', 'exec'), namespace)
        with tempfile.TemporaryDirectory() as name:
            namespace.update(receipt_dir=Path(name), case_root=Path(name))
            for argv in [[Path('/fake/gh'), 'skill', 'install'], ['/fake/gh', 'api', 'rate_limit'], ['/fake/gh'], ['/product/app', 'registry-restore-plan']]:
                namespace['command'](argv)
            namespace['subprocess'].run.return_value.returncode = 1
            with self.assertRaises(AssertionError):
                namespace['command'](['/fake/gh', 'skill', 'install'])
            counts = namespace['report']['commandDiagnostics']['directGhCommands']
            self.assertEqual(counts, dict(skillInstall=2, api=1, other=1))
            self.assertEqual(namespace['subprocess'].run.call_count, 5)
            self.assertEqual(namespace['subprocess'].run.call_args.kwargs['timeout'], 45)

    def test_actual_retirement_setup_keeps_three_installs_and_counts_attempted_failure(self):
        module = ast.parse((ROOT / 'tests/native/prove-registry-recorded-retirement.py').read_text())
        setup = next(n for n in module.body if isinstance(n, ast.FunctionDef) and n.name == 'prepare_live_evidence')
        commands = []
        def fake_run(argv, **kwargs):
            commands.append((argv, kwargs))
            home = kwargs['cwd'] / 'home/.agents'
            installed = home / 'skills' / ('cli-exercise' if argv[-1].endswith('cli-exercise') else 'gh-skill')
            installed.mkdir(parents=True)
            (installed / 'SKILL.md').write_text('fake unit-test content')
            (installed / '.gitignore').write_text('fake unit-test resource')
            (home / '.skill-lock.json').write_text('{}')
            return types.SimpleNamespace(returncode=0, stdout=b'{}', stderr=b'')
        namespace = dict(environment=lambda base, gh: {}, shutil=shutil, subprocess=types.SimpleNamespace(DEVNULL=subprocess.DEVNULL, run=fake_run))
        exec(compile(ast.fix_missing_locations(ast.Module(body=[setup], type_ignores=[])), '<actual-setup>', 'exec'), namespace)
        with tempfile.TemporaryDirectory() as name:
            counts = dict(skillInstall=0, api=0, other=0)
            namespace['prepare_live_evidence'](Path(name), Path('/fake/gh'), counts)
            self.assertEqual(counts, dict(skillInstall=3, api=0, other=0))
            self.assertEqual(len(commands), 3)
            self.assertEqual(sum('--pin=v2.103.2' in argv for argv, _ in commands), 1)
            self.assertEqual([kwargs['timeout'] for _, kwargs in commands], [180, 180, 180])
        namespace['subprocess'].run = lambda *args, **kwargs: types.SimpleNamespace(returncode=1, stdout=b'', stderr=b'unit-test refusal')
        with tempfile.TemporaryDirectory() as name:
            counts = dict(skillInstall=0, api=0, other=0)
            with self.assertRaises(AssertionError):
                namespace['prepare_live_evidence'](Path(name), Path('/fake/gh'), counts)
            self.assertEqual(counts, dict(skillInstall=1, api=0, other=0))


if __name__ == '__main__':
    unittest.main()
