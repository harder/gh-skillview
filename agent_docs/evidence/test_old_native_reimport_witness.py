"""Verify saved schema5 version framing without launching any product/tool."""
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('old_native_reimport_witness', Path(__file__).with_name('prove-old-native-reimport.py'))
proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proof)


class OldNativeReimportWitnessTests(unittest.TestCase):
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
