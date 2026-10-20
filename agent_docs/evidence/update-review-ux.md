# Updates review TUI evidence

Bounded follow-up to merged PR #79, based on
`96ddf90a329562f8c6adcbd4d4a200243511445d`. Selected-copy and saved-review
summaries use the full reading width; controls appear when applicable. Existing
saved plans, source review, retention approval, local dispositions, sequential
batch processing and recovery remain authoritative. No saved schema, dependency
or mutation operation is added.

## Behavior

- Summaries distinguish recorded ref from authored version, identify the exact
  location and affected consumers, and describe local changes and retention.
  Authored fields are sanitized and bounded, with visible omissions.
- F2 or Details switches to existing fingerprints, frozen source diffs, complete
  diagnostic output and recovery evidence. Switching views neither recaptures a
  candidate nor grants approval. Local-editor input retains its own shortcuts.
- Source review and retention are separate declarations. Only applicable local
  review, unpin and apply controls appear. Batch candidates are still prepared
  one at a time after reconciliation.
- After an attempted write, outcome evidence stays visible. Refresh inventory
  captures current metadata before another review/version choice; a failed
  capture leaves this requirement in place and can be retried. Exact selected
  subject resolution still refuses missing or ambiguous installations.

## Verification

**80 focused tests and 9 integration tests passed (89 total).** Release test and
App builds passed with zero warnings/errors. Full-solution format verification
and whitespace checks passed; dependency and lock files are unchanged. Tests
cover contextual control bounds at narrow/wider sizes, summary/details identity,
untrusted/bounded fields, source and retention approvals, local dispositions,
tag choices, unpin, batch continuation, outcome evidence and refresh retry.
The ANSI keyboard test routes actual F2, Space and Enter through the owning
application and verifies post-apply refresh before another version choice.

An isolated real **80×24 PTY** used the built App, private HOME/config/state/cache
and CWD, four same-name copies, and a local `gh`/source fixture. No credentials,
external network, skill scripts or AI clients were used. Private captures,
validation logs and payload hash receipts are under
`/var/tmp/skillview-update-review-ux/`.

The walkthrough covered Changes → Copies → exact Updates selection, availability,
ordinary prepare/cancel, selected-tag review, F2 frozen source diffs and separate
source/retention declarations. Before apply all six payload files matched their
baseline hashes. Native Linux apply replaced only the selected copy (v2 → v1),
retaining its exact original including local edits; the four files in independent
copies stayed unchanged. The final result kept its outcome and Details available,
and keyboard Refresh inventory showed the same location at v1 before enabling
another review. Explicit saved-plan undo restored the original six baseline
payload files. Retained state and logs are outside this payload equality claim.

Representative captures:

- [Selected installation](../../assets/screenshots/updates-review-80x24-selected.txt)
- [Saved change and separate approvals](../../assets/screenshots/updates-review-80x24-review.txt)
- [Applied outcome](../../assets/screenshots/updates-review-80x24-result.txt)
- [Fresh installed context](../../assets/screenshots/updates-review-80x24-refreshed.txt)

This is local UI and isolated Linux mutation evidence. It does not prove native
Windows/macOS behavior, live upstream compatibility, agent-session reload or
release availability. PR #78's deferred native checks remain deferred. Persistent
revision approvals, automatic consolidation and exposure detach remain deferred.
