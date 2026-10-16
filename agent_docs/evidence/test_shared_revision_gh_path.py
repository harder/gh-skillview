"""Proof-tool path binding tests; no product, authentication or network runs."""
import ast
import contextlib
import io
import os
from pathlib import Path
import runpy
import shutil
import tempfile
import unittest
from unittest.mock import Mock, patch


SCRIPT = Path(__file__).with_name('prove-shared-revision-update.py')


def load_resolver():
    # This executable proof intentionally runs at top level. Load its actual
    # resolver alone for portable Windows lookup injection, without fixtures.
    tree = ast.parse(SCRIPT.read_text())
    definition = next(node for node in tree.body
                      if isinstance(node, ast.FunctionDef) and node.name == 'resolve_gh_executable')
    namespace = {'Path': Path, 'os': os, 'shutil': shutil}
    exec(compile(ast.Module(body=[definition], type_ignores=[]), str(SCRIPT), 'exec'), namespace)
    return namespace['resolve_gh_executable']


class SharedRevisionGhPathTests(unittest.TestCase):
    def test_windows_suffix_lookup_binds_actual_file_for_read_and_launch(self):
        with tempfile.TemporaryDirectory(prefix='skillview-gh-path-test-') as directory:
            requested = Path(directory) / 'GitHub CLI' / 'gh'
            actual = requested.with_suffix('.exe')
            actual.parent.mkdir()
            actual.write_bytes(b'Never executed fixture binary')
            lookup = Mock(return_value=str(actual))
            resolved = load_resolver()(requested, windows=True, lookup=lookup)
            lookup.assert_called_once_with(str(requested.resolve()))
            self.assertEqual(actual.resolve(), resolved)
            self.assertEqual(actual.read_bytes(), resolved.read_bytes())

    def test_existing_explicit_download_preserved_without_lookup(self):
        with tempfile.TemporaryDirectory(prefix='skillview-gh-path-test-') as directory:
            for name in ('gh', 'gh.exe'):
                with self.subTest(name=name):
                    actual = Path(directory) / name
                    actual.write_bytes(name.encode())
                    lookup = Mock(side_effect=AssertionError('Must retain exact explicit file'))
                    self.assertEqual(actual.resolve(), load_resolver()(actual, windows=True, lookup=lookup))
                    lookup.assert_not_called()

    def test_missing_explicit_path_is_not_replaced_by_path_gh(self):
        with tempfile.TemporaryDirectory(prefix='skillview-gh-path-test-') as directory:
            missing = Path(directory) / 'missing' / 'gh'
            unrelated = Path(directory) / 'path-bin' / ('gh.exe' if os.name == 'nt' else 'gh')
            unrelated.parent.mkdir()
            unrelated.write_bytes(b'Never execute unrelated PATH gh')
            unrelated.chmod(0o755)
            with patch.dict(os.environ, {'PATH': str(unrelated.parent)}):
                with self.assertRaisesRegex(FileNotFoundError, 'Explicit gh executable'):
                    load_resolver()(missing)
            lookup = Mock(return_value=None)
            with self.assertRaises(FileNotFoundError):
                load_resolver()(missing, windows=True, lookup=lookup)
            lookup.assert_called_once_with(str(missing.resolve()))

    def test_all_paths_validated_before_authentication_or_fixture_creation(self):
        with tempfile.TemporaryDirectory(prefix='skillview-gh-path-test-') as directory:
            root = Path(directory)
            valid = root / 'gh.exe'
            valid.write_bytes(b'Never executed fixture binary')
            missing = root / 'missing' / 'gh'
            output = root / 'output' / 'proof.json'
            argv = [str(SCRIPT), '--app', str(root / 'app'), '--proof-host', str(root / 'host'),
                    '--gh', str(valid), '--gh', str(missing), '--output', str(output)]
            with patch('sys.argv', argv), patch.dict(os.environ, {}, clear=True), \
                    patch('subprocess.run') as run, patch('subprocess.Popen') as popen, \
                    patch('tempfile.TemporaryDirectory') as fixture, \
                    contextlib.redirect_stderr(io.StringIO()) as errors:
                with self.assertRaises(SystemExit) as exit_result:
                    runpy.run_path(str(SCRIPT), run_name='__main__')
            self.assertEqual(2, exit_result.exception.code)
            self.assertIn('Explicit gh executable', errors.getvalue())
            run.assert_not_called()
            popen.assert_not_called()
            fixture.assert_not_called()
            self.assertFalse(output.parent.exists())
            self.assertEqual({'gh.exe'}, {path.name for path in root.iterdir()})


if __name__ == '__main__':
    unittest.main()
