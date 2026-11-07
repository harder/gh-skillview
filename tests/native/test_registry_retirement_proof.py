import importlib.util
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('registry_retirement', Path(__file__).with_name('prove-registry-recorded-retirement.py'))
proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proof)


class RegistryRetirementProofTests(unittest.TestCase):
    def test_native_receipt_observes_raw_bytes_mode_and_generation(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            item = root / 'raw'
            item.write_bytes(b'\x00original\xff')
            before = proof.native(root)
            self.assertEqual(before[1]['value'], proof.digest(item))
            item.write_bytes(b'\x00different\xff')
            after = proof.native(root)
            self.assertNotEqual(before, after)
            self.assertIn('ctimeNs', after[1])
            self.assertIn('inode', after[1])

    @unittest.skipIf(os.name == 'nt', 'Unix link receipt')
    def test_native_receipt_keeps_links_as_leaves(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            outside = root / 'outside'
            outside.mkdir()
            (outside / 'private').write_text('keep')
            selected = root / 'selected'
            selected.mkdir()
            (selected / 'link').symlink_to(outside, target_is_directory=True)
            receipt = proof.native(selected)
            self.assertEqual(len(receipt), 2)
            self.assertEqual(receipt[1]['kind'], 'link')
            self.assertEqual(receipt[1]['value'], str(outside))

    def test_fixture_environment_replaces_mutation_context_and_git_redirection(self):
        with patch.dict(os.environ, {'GH_HOST': 'foreign.invalid', 'GIT_INDEX_FILE': '/foreign-index',
                                     'CODEX_HOME': '/foreign-agent', 'SKILLVIEW_STATE_HOME': '/foreign-state'}):
            root = Path(tempfile.gettempdir()) / 'proof-private'
            env = proof.environment(root, root / 'gh')
            self.assertNotIn('GH_HOST', env)
            self.assertNotIn('GIT_INDEX_FILE', env)
            self.assertEqual(env['GIT_CONFIG_COUNT'], '0')
            self.assertEqual(env['CODEX_HOME'], str(root / 'home/.codex'))
            self.assertEqual(env['SKILLVIEW_STATE_HOME'], str(root / 'state'))
            self.assertEqual(env['HOME'], str(root / 'home'))


if __name__ == '__main__':
    unittest.main()
