"""Portable smoke contracts for the one-off runner, not product verification."""
import importlib.util
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
        if os.name == 'nt':
            self.skipTest('Directory symlink creation depends on native Windows privilege')
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary) / 'repo'
            root.mkdir()
            alias = Path(temporary) / 'alias'
            alias.symlink_to(root, target_is_directory=True)
            with patch.object(runner, 'ROOT', root.resolve()):
                with self.assertRaisesRegex(ValueError, 'outside the checkout'):
                    runner.make_output(alias / 'results')
            self.assertFalse((root / 'results').exists())

    def test_command_failure_keeps_exit_status_and_log(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            result = runner.execute([sys.executable, '-c', 'print("diagnostic"); raise SystemExit(9)'],
                                    cwd=root, env=os.environ.copy(), log=root / 'command.log', timeout=30)
            self.assertEqual('failed', result['status'])
            self.assertEqual(9, result['exitCode'])
            self.assertIn('diagnostic', (root / 'command.log').read_text())

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
        with tempfile.TemporaryDirectory() as temporary:
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
