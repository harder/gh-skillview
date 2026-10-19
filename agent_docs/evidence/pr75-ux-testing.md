# PR 75 Changes queue UX check

The Changes queue was exercised through the built app in a private tmux session at 80×24, 100×26 and 120×36. The fixture had 13 observations: a broken link, a skill directory without `SKILL.md`, distinct same-name copies, and shared exposure. A local `gh` stub supplied inventory metadata; no remote GH calls or client launches were used.

The main layout fix was checked at all three sizes. The final shorter location labels and count-first footer were rechecked at 80×24: the `one`, `shared` and `two` copies are distinguishable, and the queue shortcuts remain visible. F2 toggles summary and technical details from the table and from the Markdown pane; Tab followed by Page Down scrolls later detail fields into view. Enter opens the selected read-only review and Esc returns to the queue. No action was confirmed. File hashes and link targets matched before and after the walkthrough.

Before and after 80×24 captures:

- [Before](../../assets/screenshots/pr75-changes-80x24-before.txt)
- [After](../../assets/screenshots/pr75-changes-80x24-after.txt)

The 13-row live fixture does not reach the presentation bound; the omitted-row hint is covered by `ChangesTabViewTests.PresentationLimit_RemainsVisibleWhenTogglingDetails`.

The initial full local Release suite had 15 failures (2,493 passed, 32 skipped, 2,540 total) and was not rerun. Its tool output did not preserve all 15 failure names. The prior PR 75 CI run later completed with failures: Windows had the stale `Enter/Open` status assertion; Ubuntu had that assertion plus four `CleanupScreenTests.InterruptedPartialRemovalPreservesSkipsMutationTotalsAndRetry` cases; macOS had that assertion plus `LocalSourceSwitchTests.ProvenMissingOldSourceDoesNotBecomePayloadDependency`. All four AOT smoke jobs failed in later proof steps with GitHub installation API HTTP 403 rate-limit errors. That run tested the earlier PR head.

Final local verification passed: the managed Release app and both test projects built with zero warnings; focused UI classes passed (103 tests total); integration tests passed (9 tests). Full raw build/test/CI logs and 100×26, 120×36 and raw ANSI captures are retained outside the repository under `/var/tmp/skillview-pr75-ux/evidence/`.

Follow-up: the separate Cleanup review at 80×24 still renders Markdown table delimiters literally and shows native IDs in its default pane. This was outside the Changes queue UX change.
