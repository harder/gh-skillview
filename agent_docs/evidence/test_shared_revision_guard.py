"""Admission-guard timeout diagnostics without product or network execution."""
import ast
import contextlib
import io
from pathlib import Path
import subprocess
import time
import unittest
from unittest.mock import patch


SCRIPT = Path(__file__).with_name('prove-shared-revision-update.py')


def load_guard():
    tree = ast.parse(SCRIPT.read_text())
    definition = next(node for node in tree.body
                      if isinstance(node, ast.FunctionDef) and node.name == 'run_guard_host')
    namespace = {'subprocess': subprocess, 'time': time}
    exec(compile(ast.Module(body=[definition], type_ignores=[]), str(SCRIPT), 'exec'), namespace)
    return namespace['run_guard_host']


class SharedRevisionGuardTests(unittest.TestCase):
    def test_guard_preserves_child_result_and_reports_case(self):
        result = subprocess.CompletedProcess(['proof-host'], 2, '', 'maintenance-proof: refused')
        with patch('subprocess.run', return_value=result) as run, \
                contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertIs(load_guard()(['proof-host'], env={'HOME': 'private'}, cwd='workspace',
                                      case='linked-home'), result)
        run.assert_called_once_with(['proof-host'], env={'HOME': 'private'}, cwd='workspace',
                                    capture_output=True, text=True, timeout=60)
        self.assertIn('linked-home: starting', output.getvalue())
        self.assertIn('linked-home: exited 2', output.getvalue())

    def test_timeout_fails_without_retry_and_preserves_bounded_diagnostics(self):
        for stdout, stderr in ((None, None), ('early output', 'native diagnostic'),
                               (b'x' * 5000 + b'last output', b'bad utf8: \xff'),
                               ('\x1b\r\n\x00' * 4096 + 'early output', '\x00' * 4096 + 'native diagnostic')):
            with self.subTest(stdout_type=type(stdout).__name__):
                failure = subprocess.TimeoutExpired(['proof-host'], 60, output=stdout, stderr=stderr)
                with patch('subprocess.run', side_effect=failure) as run, \
                        contextlib.redirect_stdout(io.StringIO()):
                    with self.assertRaisesRegex(RuntimeError, 'positive-control.*no refusal was proved') as raised:
                        load_guard()(['proof-host'], env={}, cwd='workspace', case='positive-control')
                run.assert_called_once()
                self.assertIs(raised.exception.__cause__, failure)
                message = str(raised.exception)
                self.assertLess(len(message), 8500)
                self.assertNotIn('\x1b', message)
                self.assertNotIn('\x00', message)
                self.assertNotIn('\r', message)
                self.assertNotIn('\n', message)
                if stdout:
                    self.assertIn('early output' if isinstance(stdout, str) else 'last output', message)
                if stderr:
                    self.assertIn('native diagnostic' if isinstance(stderr, str) else 'bad utf8:', message)


if __name__ == '__main__':
    unittest.main()
