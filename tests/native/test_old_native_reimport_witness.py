"""Verify saved schema5/schema6 witnesses and checkout framing without product hosts."""
import importlib.util
import contextlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('old_native_reimport_witness', Path(__file__).with_name('prove-old-native-reimport.py'))
proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proof)
docker_spec = importlib.util.spec_from_file_location('old_native_source_switch_docker', Path(__file__).with_name('run-local-source-switch-docker.py'))
docker = importlib.util.module_from_spec(docker_spec)
docker_spec.loader.exec_module(docker)
native_spec = importlib.util.spec_from_file_location('native_ci_inputs', Path(__file__).resolve().parents[2] / '.github/scripts/native_proof_inputs.py')
native = importlib.util.module_from_spec(native_spec)
native_spec.loader.exec_module(native)


class OldNativeReimportWitnessTests(unittest.TestCase):
    def test_container_proof_cannot_omit_or_substitute_the_schema6_producer(self):
        with tempfile.TemporaryDirectory(prefix='skillview-producer-admission-') as temporary:
            root = Path(temporary)
            output = root / 'no-output'
            common = [str(root / 'wrapper.py'), '--app', 'absent-current', '--proof-host-dir', 'absent-proof',
                      '--gh', 'absent-gh', '--legacy-app', 'absent-schema5', '--legacy-checkout', 'absent-checkout5',
                      '--legacy-kill-boundary', 'retain-moved', '--platform', 'linux/amd64', '--output', str(output)]
            producer = ['--source-switch-legacy-app', 'absent-schema6', '--source-switch-legacy-checkout', 'absent-checkout6']
            for captured6, extra, error in [('b' * 40, [], '--source-switch-legacy-app'),
                                           ('', producer, 'schema6 baseline is unavailable'),
                                           ('a' * 40, producer, 'distinct actual native producers')]:
                with self.subTest(schema6=captured6, omitted=not extra):
                    (root / 'prove-old-native-reimport.py').write_text(
                        'BASELINE = ' + repr('a' * 40) + '\nSOURCE_SWITCH_BASELINE = ' + repr(captured6) + '\n')
                    diagnostics = io.StringIO()
                    with patch.object(docker, '__file__', str(root / 'wrapper.py')), \
                         patch.object(sys, 'argv', common + extra), contextlib.redirect_stderr(diagnostics), \
                         patch.object(docker.shutil, 'which', side_effect=AssertionError('Looked up Docker before producer admission')):
                        with self.assertRaises(SystemExit) as refused:
                            docker.main()
                    self.assertNotEqual(0, refused.exception.code)
                    self.assertIn(error, diagnostics.getvalue())
                    self.assertFalse(output.exists())

    def test_unbound_schema6_cannot_open_tools_or_leave_positive_evidence(self):
        with tempfile.TemporaryDirectory(prefix='skillview-unbound-proof-') as temporary:
            output = Path(temporary) / 'proof.json'
            output.write_text('{"proofCompleted": true, "compatibilityVerified": true}')
            arguments = [proof.__file__, '--legacy-schema', '6', '--old-app', 'absent-old',
                         '--old-checkout', 'absent-checkout', '--app', 'absent-current',
                         '--proof-host', 'absent-host', '--gh', 'absent-gh',
                         '--kill-boundary', 'retain-moved', '--output', str(output)]
            with patch.object(proof, 'SOURCE_SWITCH_BASELINE', ''), patch.object(sys, 'argv', arguments), \
                 patch.object(proof, '_output', None), patch.object(proof, '_raw', None), \
                 patch.object(proof, '_baseline', proof.BASELINE), \
                 patch.object(proof, 'native_tool', side_effect=AssertionError('Opened a tool before binding')):
                with self.assertRaisesRegex(ValueError, 'final merged baseline'):
                    proof.main()
            recorded = json.loads(output.read_text())
            self.assertFalse(recorded['proofCompleted'])
            self.assertFalse(recorded['compatibilityVerified'])
            self.assertFalse(output.with_name('proof-raw').exists())

    def test_fixture_environment_neutralizes_mixed_case_inherited_git_and_auth(self):
        root = Path('/fixture')
        with patch.dict(os.environ, {'git_dir': 'poison', 'Git_Work_Tree': 'poison',
                                     'gh_token': 'poison', 'Github_Token': 'poison'}):
            captured = proof.fixture_environment(root, root / 'home', root / 'state', root / 'gh', 'http://127.0.0.1:1')
        self.assertFalse(any(value == 'poison' for value in captured.values()))
        self.assertEqual(str(root / 'private-git'), captured['GIT_DIR'])
        self.assertEqual(str(root), captured['GIT_WORK_TREE'])
        self.assertEqual('0', captured['GIT_CONFIG_COUNT'])

    def test_ci_baseline_checkouts_stay_clean_when_global_config_converts_line_endings(self):
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
            git(['remote', 'add', 'origin', str(repo)])
            # The helper neutralizes inherited global config. Local conversion
            # remains enabled, so checkout must explicitly preserve LF bytes.
            git(['config', 'core.autocrlf', 'true'])
            # Match the proof helper: inherited conversion settings are absent
            # during source verification, and status cannot refresh the index.
            proof_env = dict(env, GIT_CONFIG_GLOBAL=os.devnull, GIT_OPTIONAL_LOCKS='0')
            for index, family in enumerate(native.FAMILIES):
                with self.subTest(baseline=family[0]), patch.object(native, 'ROOT', repo), patch.dict(
                        os.environ, {**env, 'RUNNER_TEMP': str(root)}):
                    spec = dict(family=family[0], sourceCommit=revision, directory=f'baseline-{index}')
                    checkout = native.checkout(spec)
                    sample = checkout / 'sample.txt'
                    self.assertEqual(authored, sample.read_bytes())
                    timestamp = sample.stat().st_mtime + 2
                    os.utime(sample, (timestamp, timestamp))
                    status = ['--no-optional-locks', '-c', 'core.fsmonitor=false', 'status',
                              '--porcelain=v1', '--untracked-files=no', '--ignore-submodules=all']
                    self.assertEqual(b'', git(status, cwd=checkout, environment=proof_env))
                    self.assertEqual(checkout, native.checkout(spec))
                    sample.write_bytes(authored + b'actual source edit\n')
                    self.assertIn(b' M sample.txt', git(status, cwd=checkout, environment=proof_env))
                    with self.assertRaisesRegex(ValueError, 'tracked-clean'):
                        native.checkout(spec)

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
