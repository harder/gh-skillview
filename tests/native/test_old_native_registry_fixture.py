"""Exercise actual fixture byte transitions without running a product or network."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('old_native_plans', Path(__file__).with_name('prove-old-native-plans.py'))
proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proof)


class OldNativeRegistryFixtureTests(unittest.TestCase):
    before = b'{\r\n "version": 3, "skills": {"gh-skill": {"source":"cli/cli","sourceType":"github","pinnedRef":"old","installedAt":"keep"},"peer":{"source":"peer/repo","sourceType":"github"}}\r\n}\r\n'

    def test_schema1_must_finish_before_extensions_can_be_written(self):
        with tempfile.TemporaryDirectory(prefix='skillview-old-schema-fixture-') as directory:
            registry = Path(directory) / '.skill-lock.json'
            registry.write_bytes(self.before)
            with self.assertRaisesRegex(AssertionError, 'schema-1 recovery/undo'):
                proof.extend_registry_fixture(registry, self.before, schema1_verified=False)
            self.assertEqual(self.before, registry.read_bytes())

    def test_verified_schema1_transitions_to_extended_schema2_and3_raw_baseline(self):
        with tempfile.TemporaryDirectory(prefix='skillview-old-schema-fixture-') as directory:
            registry = Path(directory) / '.skill-lock.json'
            registry.write_bytes(self.before)
            after = proof.extend_registry_fixture(registry, self.before, schema1_verified=True)
            self.assertEqual(after, registry.read_bytes())
            original, extended = json.loads(self.before), json.loads(after)
            self.assertEqual(original['version'], extended['version'])
            self.assertEqual(original['skills']['peer'], extended['skills']['peer'])
            selected = dict(extended['skills']['gh-skill'])
            self.assertEqual({'preserve': True}, selected.pop('selectedExtension'))
            self.assertEqual(original['skills']['gh-skill'], selected)
            self.assertEqual({'preserve': True}, extended['extension'])
            self.assertEqual([1, 2], extended['skills']['unrelated']['extension'])
            self.assertNotEqual(self.before, after)

    def test_changed_raw_baseline_refuses_without_overwriting_later_bytes(self):
        with tempfile.TemporaryDirectory(prefix='skillview-old-schema-fixture-') as directory:
            registry = Path(directory) / '.skill-lock.json'
            changed = self.before + b' '
            registry.write_bytes(changed)
            with self.assertRaisesRegex(AssertionError, 'Registry changed'):
                proof.extend_registry_fixture(registry, self.before, schema1_verified=True)
            self.assertEqual(changed, registry.read_bytes())


if __name__ == '__main__':
    unittest.main()
