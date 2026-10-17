"""Verify saved schema5 witnesses and checkout framing without product hosts."""
import importlib.util
import os
from pathlib import Path
import shlex
import subprocess
import sys
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('old_native_reimport_witness', Path(__file__).with_name('prove-old-native-reimport.py'))
proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proof)


class OldNativeReimportWitnessTests(unittest.TestCase):
    def test_ci_baseline_checkouts_stay_clean_when_global_config_converts_line_endings(self):
        workflow = Path(__file__).resolve().parents[2] / '.github/workflows/ci.yml'
        commands = [shlex.split(line.strip()) for line in workflow.read_text().splitlines()
                    if ' worktree add --detach ' in line]
        self.assertTrue(commands, 'No frozen native baseline checkout commands found')
        temporary_parent = '/var/tmp' if sys.platform.startswith('linux') else None
        with tempfile.TemporaryDirectory(prefix='skillview-checkout-test-', dir=temporary_parent) as temporary:
            root = Path(temporary)
            config = root / 'global-config'
            config.write_text('[core]\n\tautocrlf = true\n')
            env = {key: value for key, value in os.environ.items() if not key.upper().startswith('GIT_')}
            env.update(GIT_CONFIG_GLOBAL=str(config), GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_COUNT='0')
            repo = root / 'repo'
            repo.mkdir()

            def git(arguments, *, cwd=repo, environment=env):
                return subprocess.run(['git', *arguments], cwd=cwd, env=environment,
                                      check=True, capture_output=True, timeout=30).stdout

            git(['init', '--template='])
            authored = b'first\nsecond\n'
            (repo / 'sample.txt').write_bytes(authored)
            git(['add', 'sample.txt'])
            git(['-c', 'user.name=Fixture', '-c', 'user.email=fixture@example.invalid',
                 'commit', '-m', 'Frozen LF source'])
            revision = git(['rev-parse', 'HEAD']).decode().strip()
            # Match the proof helper: inherited conversion settings are absent
            # during source verification, and status cannot refresh the index.
            proof_env = dict(env, GIT_CONFIG_GLOBAL=os.devnull, GIT_OPTIONAL_LOCKS='0')
            for index, command in enumerate(commands):
                with self.subTest(baseline=index):
                    checkout = root / f'baseline-{index}'
                    arguments = [str(checkout) if value == '$baseline_path' else
                                 revision if value == '$baseline_sha' else value for value in command[1:]]
                    git(arguments)
                    sample = checkout / 'sample.txt'
                    self.assertEqual(authored, sample.read_bytes())
                    timestamp = sample.stat().st_mtime + 2
                    os.utime(sample, (timestamp, timestamp))
                    status = ['--no-optional-locks', '-c', 'core.fsmonitor=false', 'status',
                              '--porcelain=v1', '--untracked-files=no', '--ignore-submodules=all']
                    self.assertEqual(b'', git(status, cwd=checkout, environment=proof_env))
                    sample.write_bytes(authored + b'actual source edit\n')
                    self.assertIn(b' M sample.txt', git(status, cwd=checkout, environment=proof_env))

    def test_native_version_witness_preserves_release_url_and_internal_line_endings(self):
        for newline in ['\n', '\r\n']:
            with self.subTest(newline=repr(newline)):
                # Schema5's frozen producer stores version.StdOut.Trim(), not
                # its first banner line or only its parsed semantic version.
                saved = 'gh version 2.102.0 (2026-09-30)' + newline + 'https://github.com/cli/cli/releases/tag/v2.102.0'
                captured = (saved + newline).encode()
                self.assertEqual(saved, proof.frozen_gh_version(captured))
                self.assertNotEqual(saved, captured.decode().splitlines()[0])

    def test_invalid_or_unbounded_version_evidence_refuses(self):
        for captured in [b'x' * 4097, b'not a gh version\n', b'gh version \xff']:
            with self.subTest(captured=captured[:20]):
                with self.assertRaises((ValueError, UnicodeDecodeError)):
                    proof.frozen_gh_version(captured)


if __name__ == '__main__':
    unittest.main()
