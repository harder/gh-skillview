"""Portable smoke contracts for the one-off runner, not product verification."""
import importlib.util
import contextlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location('test_once', Path(__file__).resolve().parents[1] / 'test_once.py')
runner = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(runner)
TEMP_BASE = '/var/tmp' if sys.platform.startswith('linux') else None


class TestOnceTests(unittest.TestCase):
    def test_environment_is_private_and_parent_is_unchanged(self):
        original = dict(os.environ, GH_TOKEN='secret', GIT_DIR='foreign', CODEX_HOME='foreign',
                        SKILLVIEW_CONTRACT_TESTS='true', CLAUDE_CODE_PLUGIN_DIRS='foreign')
        before = dict(original)
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            env = runner.isolated_environment(original, root)
            self.assertNotIn('GH_TOKEN', env)
            self.assertNotIn('GIT_DIR', env)
            self.assertNotIn('SKILLVIEW_CONTRACT_TESTS', env)
            self.assertNotIn('CLAUDE_CODE_PLUGIN_DIRS', env)
            for key in ('HOME', 'USERPROFILE', 'TMPDIR', 'SKILLVIEW_STATE_HOME', 'CODEX_HOME'):
                self.assertTrue(Path(env[key]).is_relative_to(root))
            self.assertEqual('1', env['SKILLVIEW_EXPOSURE_LINK_PROOF'])
        self.assertEqual(before, original)

    def test_results_cannot_write_through_an_alias_into_the_checkout(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / 'repo'
            root.mkdir()
            alias = Path(temporary) / 'alias'
            try:
                alias.symlink_to(root, target_is_directory=True)
            except OSError as error:
                if getattr(error, 'winerror', None) == 1314:
                    self.skipTest('Windows token cannot create the directory-alias fixture')
                raise
            with patch.object(runner, 'ROOT', root.resolve()):
                with self.assertRaisesRegex(ValueError, 'outside the checkout'):
                    runner.make_output(alias / 'results')
            self.assertFalse((root / 'results').exists())

    def test_results_reject_even_invalid_ancestor_git_markers_before_creating_output(self):
        for kind in ('directory', 'file', 'broken-link'):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory(dir=TEMP_BASE) as temporary:
                root = Path(temporary)
                marker = root / '.git'
                if kind == 'directory':
                    marker.mkdir()
                elif kind == 'file':
                    marker.write_text('not a valid gitfile', encoding='utf-8')
                else:
                    try:
                        marker.symlink_to(root / 'missing')
                    except OSError as error:
                        if getattr(error, 'winerror', None) == 1314:
                            self.skipTest('Windows token cannot create the broken-marker fixture')
                        raise
                output = root / 'nested' / 'results'
                with self.assertRaisesRegex(ValueError, 'beneath a .git marker'):
                    runner.make_output(output)
                self.assertFalse(output.parent.exists())

    def test_results_accept_a_new_directory_outside_repository_markers(self):
        with tempfile.TemporaryDirectory(dir=TEMP_BASE) as temporary:
            output = Path(temporary) / 'results'
            self.assertEqual(output.resolve(), runner.make_output(output))
            self.assertTrue(output.is_dir())

    def test_command_failure_keeps_exit_status_and_log(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            result = runner.execute([sys.executable, '-c', 'print("diagnostic"); raise SystemExit(9)'],
                                    cwd=root, env=os.environ.copy(), log=root / 'command.log', timeout=30)
            self.assertEqual('failed', result['status'])
            self.assertEqual(9, result['exitCode'])
            self.assertIn('diagnostic', (root / 'command.log').read_text())

    def test_fixture_symlink_refusal_is_an_explicit_limitation(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            with patch.object(Path, 'symlink_to', side_effect=OSError('fixture permission denied')):
                reason = runner.symlink_limitation(root)
            self.assertIn('fixture permission denied', reason)
            self.assertEqual([], list(root.iterdir()))
            self.assertIn('recorded-replacement', runner.LINK_PROOFS)
            self.assertNotIn('revision-update', runner.LINK_PROOFS)

    def test_interruption_keeps_a_step_result_after_owned_tree_cleanup(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            with patch.object(runner.subprocess, 'Popen') as start, patch.object(runner, 'stop_tree') as stop:
                start.return_value.wait.side_effect = KeyboardInterrupt
                result = runner.execute(['fixture-command'], cwd=root, env={}, log=root / 'interrupt.log', timeout=30)
                stop.assert_called_once()
            self.assertEqual('interrupted', result['status'])
            self.assertEqual(['fixture-command'], result['argv'])

    def test_aot_run_uses_locked_ci_publish_instead_of_single_rid_restore(self):
        with tempfile.TemporaryDirectory(dir=TEMP_BASE) as temporary:
            root = Path(temporary)
            repo = root / 'repo'
            repo.mkdir()
            (repo / 'global.json').write_text(json.dumps({'sdk': {'version': '10.0.402'}}))
            commands = []

            def fake_execute(argv, *, log, **unused):
                commands.append(argv)
                contents = {'git-head': 'a' * 40, 'sdk-version': '10.0.402',
                            'sdk-info': 'RID: win-arm64', 'gh-version': 'gh version 2.102.0'}
                log.write_text(contents.get(log.stem, ''), encoding='utf-8')
                return dict(argv=argv, log=str(log), status='passed', exitCode=0, seconds=0)

            with patch.object(runner, 'ROOT', repo), patch.object(runner, 'execute', side_effect=fake_execute), \
                    patch.object(runner.shutil, 'which', return_value='fixture-gh'), contextlib.redirect_stdout(io.StringIO()):
                code = runner.main(['--expect-commit', 'a' * 40, '--output', str(root / 'results'), '--aot', '--diagnostic-tests'])
            self.assertEqual(2, code)  # Live proofs were deliberately not requested.
            restores = [command for command in commands if command[1] == 'restore']
            self.assertEqual([runner.PROJECTS['unit'], runner.PROJECTS['integration']], [command[2] for command in restores])
            publishes = [command for command in commands if command[1] == 'publish']
            self.assertEqual([runner.PROJECTS['app'], runner.PROJECTS['extension']], [command[2] for command in publishes])
            for command in publishes:
                self.assertIn('-p:RestoreLockedMode=true', command)
                self.assertNotIn('--no-restore', command)
                self.assertEqual('win-arm64', command[command.index('-r') + 1])
            self.assertIn('-warnaserror:IL3050', publishes[0])
            tests = [command for command in commands if command[1] == 'test']
            self.assertEqual(2, len(tests))
            for command in tests:
                self.assertIn('--long-running', command)
                self.assertIn('--diagnostic', command)
                self.assertNotIn('--', command)  # MTP flags are forwarded directly.

    def test_timeout_stops_the_owned_descendant_before_it_writes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            sentinel = root / 'must-not-appear'
            child = 'import time; from pathlib import Path; time.sleep(2.5); Path(' + repr(str(sentinel)) + ').write_text("late")'
            parent = ('import subprocess,sys,time; subprocess.Popen([sys.executable,"-c",' + repr(child)
                      + ']); print("ready",flush=True); time.sleep(30)')
            result = runner.execute([sys.executable, '-c', parent], cwd=root, env=os.environ.copy(),
                                    log=root / 'timeout.log', timeout=1)
            self.assertEqual('timeout', result['status'])
            self.assertIn('ready', (root / 'timeout.log').read_text())
            time.sleep(1.8)
            self.assertFalse(sentinel.exists())

    def test_wrong_commit_blocks_before_sdk_or_product_commands(self):
        with tempfile.TemporaryDirectory(dir=TEMP_BASE) as temporary:
            root = Path(temporary) / 'repo'
            root.mkdir()
            env = {key: value for key, value in os.environ.items() if not key.upper().startswith('GIT_')}
            env.update(GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL=os.devnull)
            subprocess.run(['git', 'init', '--quiet', str(root)], check=True, env=env)
            (root / 'README.md').write_text('fixture\n')
            subprocess.run(['git', '-C', str(root), 'add', '.'], check=True, env=env)
            subprocess.run(['git', '-C', str(root), '-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid',
                            '-c', 'commit.gpgsign=false', 'commit', '--quiet', '-m', 'fixture'], check=True, env=env)
            output = Path(temporary) / 'results'
            with patch.object(runner, 'ROOT', root):
                code = runner.main(['--expect-commit', '0' * 40, '--output', str(output)])
            report = json.loads((output / 'report.json').read_text())
            self.assertEqual(2, code)
            self.assertEqual('blocked', report['result'])
            self.assertEqual(['git-head'], [step['name'] for step in report['steps']])
            self.assertIn('does not match', report['blocked'])


if __name__ == '__main__':
    unittest.main()
