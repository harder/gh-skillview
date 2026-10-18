"""Explicit frozen-review acknowledgement step for native proof fixtures.

This tests user-declared receipt mechanics. It supplies no human source-review,
authenticity, compatibility or behavioral safety evidence for the fixture skill.
"""
import json
import sys


def acknowledge_frozen_source(run, plan, prefix='update'):
    if plan.get('pinPolicy') is not None:
        assert plan.get('sourceReview') is None, plan
        return  # Pin-only policy has no upstream authored source change.
    source = plan['sourceReview']
    assert source['version'] == 1 and len(source['fingerprint']) == 64, source
    assert len(source['installedManifest']) == len(source['candidateManifest']) == 64, source
    report = source['report']
    assert report['changedFileCount'] == len(report['files']) + report['omittedFiles'], report
    # Surface the frozen bounded report before explicitly declaring fixture review.
    print(json.dumps({'proofFixtureSourceReview': report, 'plan': plan['planFingerprint'],
                      'sourceReview': source['fingerprint']}, ensure_ascii=True), file=sys.stderr)
    receipt = run(prefix + '-ack-source', '--id', plan['id'], '--expect', plan['planFingerprint'],
                  '--review', source['fingerprint'], '--yes')['operation']
    assert receipt['sourceReviewAcknowledged'] and not receipt['mutationObserved'], receipt
    assert receipt['planFingerprint'] == plan['planFingerprint'], receipt
