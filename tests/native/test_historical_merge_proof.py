import importlib.util
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest

spec = importlib.util.spec_from_file_location('historical_merge_proof', Path(__file__).with_name('historical_merge_proof.py'))
proof = importlib.util.module_from_spec(spec)
spec.loader.exec_module(proof)


class HistoricalMergeProofTests(unittest.TestCase):
    def test_receipt_binds_exact_historical_binary_and_source(self):
        self.assertEqual(proof.BASELINE, "27efe94ddbde31ef3c8e6e7dbbee41c00c8b7177")
        import hashlib
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            old, current, receipt = root / 'old', root / 'current', root / 'receipt.json'
            old.write_bytes(b'old native host')
            current.write_bytes(b'current native host')
            value = dict(sourceCommit=proof.BASELINE, trackedClean=True,
                         binarySha256=hashlib.sha256(old.read_bytes()).hexdigest())
            receipt.write_text(json.dumps(value))
            args = SimpleNamespace(old_app=old, old_checkout=None, old_source_receipt=receipt)
            self.assertEqual(proof.verify(args, current)[0], old)
            for field, replacement in [('sourceCommit', '0' * 40), ('sourceCommit', '0dba59be59776e6e654123aa1ce1e5496074763e'), ('trackedClean', False), ('binarySha256', '0' * 64)]:
                receipt.write_text(json.dumps(dict(value, **{field: replacement})))
                with self.assertRaises(AssertionError):
                    proof.verify(args, current)
            receipt.write_text(json.dumps(value))
            with self.assertRaises(AssertionError):
                proof.verify(args, old)

    def test_reviewed_shape_rejects_legacy_or_wrong_archive_bindings(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            review = dict(version=1, fingerprint='a' * 64, installedManifest='b' * 64,
                          candidateManifest='c' * 64, installedArchive='review-installed-merged-00',
                          candidateArchive='review-source-merged-00')
            record = dict(schemaVersion=3, localDisposition=dict(mergedSlot='merged-00'), sourceReview=review)
            plan = dict(planPath=str(root / 'merge-plan-00.json'), sourceReview=review, sourceReviewAcknowledged=False)
            for name in (review['installedArchive'], review['candidateArchive']):
                (root / name).mkdir()
                (root / name / 'manifest.json').write_text('{}')
            Path(plan['planPath']).write_text(json.dumps(record))
            self.assertEqual(proof.assert_reviewed_shape(plan, 3), record)
            for malformed in [dict(record, sourceReview=None), dict(record, schemaVersion=2),
                              dict(record, sourceReview=dict(review, installedArchive='review-installed-candidate'))]:
                Path(plan['planPath']).write_text(json.dumps(malformed))
                with self.assertRaises((AssertionError, TypeError)):
                    proof.assert_reviewed_shape(plan, 3)
