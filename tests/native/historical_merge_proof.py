"""Bind the frozen source-reviewed schema3 producer used only to author proof fixtures."""
import hashlib
import json
import os
from pathlib import Path
import subprocess

BASELINE = '27efe94ddbde31ef3c8e6e7dbbee41c00c8b7177'


def add_arguments(parser, required=True):
    parser.add_argument('--old-app', required=required, type=Path)
    source = parser.add_mutually_exclusive_group(required=required)
    source.add_argument('--old-checkout', type=Path)
    source.add_argument('--old-source-receipt', type=Path)


def verify(args, current):
    old = args.old_app.resolve(strict=True)
    assert old.is_file() and old != current.resolve(), 'Historical producer must be distinct from current host'
    with old.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    with current.open('rb') as stream:
        assert digest != hashlib.file_digest(stream, 'sha256').hexdigest(), 'Historical and current binaries must differ'
    if args.old_checkout:
        checkout = args.old_checkout.resolve(strict=True)
        assert old.is_relative_to(checkout)
        env = {key: value for key, value in os.environ.items() if not key.upper().startswith('GIT_')}
        revision = subprocess.check_output(['git', '-C', str(checkout), 'rev-parse', 'HEAD'], env=env, text=True).strip()
        assert revision == BASELINE, ('Wrong historical merge producer', revision)
        subprocess.run(['git', '-C', str(checkout), 'diff', '--quiet', 'HEAD', '--'], env=env, check=True)
    else:
        receipt = json.loads(args.old_source_receipt.read_bytes())
        assert receipt['sourceCommit'] == BASELINE and receipt['trackedClean'] is True
        assert receipt['binarySha256'] == digest
    return old, dict(sourceCommit=BASELINE, binarySha256=digest, trackedClean=True)


def assert_reviewed_shape(plan, schema):
    """This producer must emit the retained source-review shape, never legacy."""
    record = json.loads(Path(plan['planPath']).read_bytes())
    assert record['schemaVersion'] == schema
    assert record['sourceReview'] == plan['sourceReview']
    review = record['sourceReview']
    assert review['version'] == 1 and len(review['fingerprint']) == 64
    assert not plan['sourceReviewAcknowledged']
    suffix = 'candidate' if schema == 2 else record['localDisposition']['mergedSlot']
    assert review['installedArchive'] == 'review-installed-' + suffix
    assert review['candidateArchive'] == 'review-source-' + suffix
    for field in ('installedManifest', 'candidateManifest'):
        assert len(review[field]) == 64
    for field in ('installedArchive', 'candidateArchive'):
        assert (Path(plan['planPath']).parent / review[field] / 'manifest.json').is_file()
    return record
