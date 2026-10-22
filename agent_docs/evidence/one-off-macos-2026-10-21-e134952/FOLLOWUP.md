# Mac findings: analysis and focused follow-up

The [original report](AGENT-REPORT.md) tested product commit `e134952` and was
preserved by results commit `76bb122`. Its failure status remains unchanged.
Both native hosts passed all 16 supplemental CLI proofs, and the real-terminal
install, exact-version review/apply, unpin, Undo and mixed cleanup journeys passed
with their documented coverage limits.

## AOT kit failure: already fixed

The NU1004 failure has the same cause as the Windows report: `dotnet restore -r`
overrides the committed four-RID restore set. Commit `87810ba` changed the kit to
CI's locked publish recipe. The Mac run tested the older kit, so its original
failure does not mean the fix regressed. Native Windows locked restore and both
platforms' original supplemental native publishes support that diagnosis.

## Collision fixture: fixed, targeted tests pass on Linux

`GhSharedLockGuardTests.NamespacedAliasesAndTruncatedListingCannotBypassCollisionGuard`
gave every child a 64-character output budget in its truncation case. The
Mac checkout's successful Git root output needed 68 characters, so the operation
correctly refused at Git discovery before reaching the intended gh-listing seam.

The fixture now supplies its own working directory, HOME/USERPROFILE and private
Git redirects. The small output budget still exercises actual gh-listing
truncation; the assertions still require refusal and no mutation marker. The
entire `GhSharedLockGuardTests` class passed **20/20** on Linux ARM64 after locked
restore and Release build. Native Mac confirmation of the corrected fixture is
still needed; product collision guards and output bounds were not relaxed.

## Startup focus: separate automatic navigation from input focus

The application intentionally begins painting Discover while startup inventory
runs, then opens Installed when skills are found and the user has not interacted.
The existing startup tests explicitly require this behavior. The Installed list
uses `f` to focus its filter, consuming that shortcut. `/` from a list opens
Discover and focuses its query.

The Mac's initial capture shows Discover with the initial `ready` status; the
later capture shows Installed with `ocusprobe` in its filter. That is consistent
with startup inventory completing between the capture and the first `f`. The
evidence does not establish whether this transition occurred before input, so
it does not independently prove a Discover text-field focus defect. The original
observation remains valid; its root cause is not confirmed.

Two new ANSI event-loop integration cases send `focusprobe` through the actual
application keyboard at **80×24 and 120×40**, without Tab, `/` or `SetFocus`.
They require the whole query, active Discover and recorded user interaction.
Both passed on Linux ARM64 with the startup environment probe disabled, isolating
initial Discover focus from the inventory-driven tab change. This is not a
native Mac PTY retest or proof about delayed startup inventory.

The user guide and agent/PTY instructions now explain the two startup states
and require waiting for inventory before deciding which input path to test.
No speculative product focus change was made.

Final scoped verification on Linux ARM64 passed: **11/11 integration tests**,
**69/69 SkillViewAppTests**, and **20/20 GhSharedLockGuardTests**. Both modified
C# files passed scoped format verification; package locks stayed unchanged.

## Short Mac confirmation task

Use the updated branch in a clean dedicated checkout and the existing exact SDK.
Keep the original report and logs. Do not repeat the remote proof matrix or set
up new automation.

1. Locked-restore/build the unit and integration projects sequentially, then run:

   ```sh
   dotnet test --project tests/SkillView.Tests/SkillView.Tests.csproj -c Release --no-build --no-restore --filter-query '/*/SkillView.Tests.Maintenance/GhSharedLockGuardTests/NamespacedAliasesAndTruncatedListingCannotBypassCollisionGuard'
   dotnet test --project tests/SkillView.IntegrationTests/SkillView.IntegrationTests.csproj -c Release --no-build --no-restore --filter-query '/*/SkillView.IntegrationTests.Ui/SkillViewAppIntegrationTests/RunAsync_DiscoverStartupTypingReachesQueryWithoutFocusShortcut'
   ```

2. Reuse the existing native binary and PTY helper in private fixtures, with
   isolated HOME/config/state/CWD and Git overrides. Let startup inventory finish
   before typing. An empty inventory should remain in Discover and accept the
   whole `focusprobe` directly. A seeded inventory should settle on Installed;
   press `/` from its list, then verify Discover receives the whole query.
   Record the active view immediately before typing and confirm Ctrl+Q exits.

If either focused check fails, retain its exact state/transcript and investigate
that case. Do not label the original native focus finding resolved solely from
the Linux integration results. Windows permission-sensitive test failures and
its missing interactive terminal coverage also remain separate open items.
