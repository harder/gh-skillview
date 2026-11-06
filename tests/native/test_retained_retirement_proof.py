import importlib.util
import pathlib
import tempfile
import unittest


spec = importlib.util.spec_from_file_location('retirement_proof', pathlib.Path(__file__).with_name('prove-retained-retirement.py'))
proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proof)


class RetirementProofTests(unittest.TestCase):
    def test_fixture_documents_do_not_confuse_nested_native_objects_with_plans(self):
        text = 'startup\n{\n"id":"one", "subject":{"name":"ready"}}\nnative-inspect\n{"id":"two"}'
        self.assertEqual(['one', 'two'], [value['id'] for value in proof.documents(text)])

    def test_audit_retains_absence_and_detects_replaced_equal_byte_files(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            file = root / 'file'
            absent = root / 'absent'
            file.write_bytes(b'payload')
            before = proof.audit([root, absent])
            self.assertIsNone(before[str(absent)])
            file.rename(root / 'retained')
            file.write_bytes(b'payload')
            self.assertNotEqual(before[str(file)], proof.audit([file])[str(file)])
