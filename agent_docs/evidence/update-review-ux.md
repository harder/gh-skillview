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

## PR #80 review follow-up

The [first review](https://github.com/harder/gh-skillview/pull/80#issuecomment-6304100364)
identified stale availability labels, lost mnemonics, misleading final-skip
guidance after an earlier write, and unwanted focus changes after availability
checks. The follow-up aligns Changes/loading/footer guidance with Check updates
and Review update, restores Alt+C/Alt+R and Alt+U for the unpin checkbox, and
keeps modified keys out of the plain-character shortcut handler. Availability
checks restore initiating focus; preserving installed content returns to the
table when it is visible. Final skip now explains the required inventory refresh
while keeping saved evidence available.

The summary emits the latest outcome once, retains its notice, then gives the
current plan and recovery guidance priority over bounded earlier batch outcomes.
Source review again states the safety/authenticity caveat on the checkbox.
Opening F2 is intentionally not an approval gate: the existing explicit user
declaration remains the contract, and merely opening a pane cannot establish
that its contents were reviewed.

The similar-issue check covered modified-key routing, preserve completion,
pending-refresh evidence, row-dependent pin actions and older keyboard tests.
Table navigation now reflows the footer only when pin-action visibility changes;
initial layout uses an explicit ready flag. Regression coverage includes final
skip with and without an attempted write, renamed Changes guidance, actual
availability/mnemonic focus and continued table marking, and bounded batch
outcomes without repeated statuses. The two managed-CI keyboard failures were
outdated assumptions about the now-hidden drill-in table and technical-first
preview; their exact-target, comparison and independent-approval assertions now
run through the summary and F2 views.

Existing native owner/inspection CI failures sampled during review were GitHub
installation API HTTP 403 rate limits. No native jobs were retried for this UI
follow-up; these failures do not constitute native-platform verification.

Review-fix validation: **87 focused tests and 9 integration tests passed**.
Release test/App builds had zero warnings/errors; full-solution format verification
and whitespace checks passed. A single broader local unit run reported 2,640
passed, 32 skipped and 13 failed (2,685 total). All 13 failed cases reproduced
with the same signatures in a separate detached worktree at pre-fix head
`feee3998fc16e4d9d900d268ab53d2c778568de6`: native placement/source-switch/root
scan cases rejected the host's `/tmp` mount boundary, and shared-selector/owner
guard assertions also failed unchanged. They remain unresolved baseline failures,
outside this UI follow-up. Logs and exact baseline filters are private under
`/var/tmp/skillview-update-review-ux/review1/validation/`.

An additional 80×24 PTY run used actual Alt+R, F2, ordinary-review skip and
selected-tag prepare/skip. The source-review caveat fits beside its checkbox;
no approvals were granted and no apply was requested. All six fixture payload
hashes remained baseline-identical. The saved-review capture above is refreshed
from this run; prior apply/result captures retain the earlier native walkthrough.
