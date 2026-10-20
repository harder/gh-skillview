# Copies and versions TUI evidence

This bounded follow-up to PR #78 adds a Changes drill-in for `NameCollision`
and `Duplicate` observations. It reuses `SkillComparisonService` and the existing
Updates/version and cleanup review flows. It introduces no saved schema or
mutation operation. Base: `4276809d4e186b0501a2cd7fd6fbf746eb7a4c49`.

## Behavior and limits

- One row represents one reconciled physical installation, retaining all recorded
  consumers. Matching names and known source/subpath identities find related
  rows; ambiguous addresses are excluded visibly. The list caps at 128 copies.
- Selected-copy details retain location, scope, source/subpath, recorded ref,
  authored version and pin. Unknown provenance and partial discovery stay
  explicit. Rows and headers abbreviate paths for display only; actions retain
  the captured installation ID and complete path.
- Comparison is explicit, local and read-only. File equality, recorded revision
  and source relationship are separate observations. No version is inferred to
  be newer or preferred. File-byte equality does not establish metadata equality.
  The summary bounds fields/text, changed paths (12) and diagnostics (8), with
  visible omissions; existing CLI comparison retains fuller evidence.
- Update/version review selects exactly one installation. Cleanup recaptures
  inventory and filters findings by both physical ID and complete path. Missing
  or ambiguous installations refuse handoff. Existing mutation validators and
  review requirements remain authoritative; name conflicts stay review-only.
- Comparison owns its worker through queued UI completion. Close/Esc cancels
  and drains the operation before disposing the dialog. Detail-editor Tab and
  Shift+Tab explicitly move focus out of the editor.

## Verification

Focused automated checks and build/format receipts are retained privately under
`/var/tmp/skillview-copies-ux/`. The new tests cover shared consumers, different
sources, renamed skills with a known common source, version/pin distinctions,
unknown/partial results, ambiguity, bounds, exact cleanup filtering, actual
keyboard selection/Tab/Enter and modal cancellation. Modal keyboard/layout
coverage uses both 80×24 and 120×36 and verifies controls fit without overlapping.

Final local results: **49 focused tests and 9 integration tests passed**. Release
builds of the test project and App passed with zero warnings/errors. Full-solution
`dotnet format SkillView.sln --no-restore --verify-no-changes` and whitespace
checks passed. No dependency or lock-file changes are included.

An isolated **real PTY at 80×24** launched the built App executable with private
HOME/config/state/cache/CWD and four same-name installations. A local `gh` stub
returned inventory only; no credentials, network, skill scripts or AI clients
were used. Captures and hash receipts remain under
`/var/tmp/skillview-copies-ux/pty/`.

Confirmed through the main shell:

1. Changes Enter opens Copies and versions; location suffixes distinguish rows.
2. Down and Tab navigate list → details → reference → Compare; Enter executes
   comparison. This caught and fixed the editor's original Tab trap. New detail
   text starts at the beginning rather than leaving its caret at the end.
3. v2/v1 reports different recorded revisions and changed `SKILL.md`/`extra.txt`,
   without guessing which revision to prefer.
4. Independent identical copies report zero differing files and the separate
   metadata caveat. A same-name copy from another repository reports different
   sources rather than implying interchangeability.
5. Use as reference changes the named reference. Update/version opens only
   `older/demo`; Choose version confirms that exact location and its v1 ref.
   The tag dialog was canceled without preparing or applying a candidate.
6. Selecting `other/demo` and Cleanup review opens exactly one candidate for
   that location, with automatic removal unavailable. Esc returns and Ctrl+Q
   exits cleanly.

After the walkthrough all six fixture payload-file SHA-256 hashes matched their
initial values. The stub command log contained only version/help/auth/inventory
calls; no update, install, removal or other mutating `gh` command was requested.
Private SkillView logs/cache/state are outside that payload equality claim.

Representative captures:

- [Different recorded versions](../../assets/screenshots/copies-80x24-differences.txt)
- [Identical captured file bytes](../../assets/screenshots/copies-80x24-identical.txt)
- [Selected cleanup location](../../assets/screenshots/copies-80x24-cleanup-handoff.txt)

This proves bounded local comparison and review navigation. It does not add
automatic consolidation, source adoption, exposure detach or persistent revision
approvals. Native mutation behavior and live `gh` compatibility were not
re-proven by this UI walkthrough. PR #78's deferred native checks remain deferred.
