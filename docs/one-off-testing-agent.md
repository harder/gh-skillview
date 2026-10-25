# Agent task: one-off release-candidate acceptance

This is a bounded testing task for Codex or Claude Code on one machine. Follow
the platform prompt in [one-off-testing.md](one-off-testing.md). Test the supplied
full commit SHA. Do not pull more features from PR #73, change product code,
weaken tests, change lock files, merge PRs, or establish recurring automation.

## Prepare and run

1. Read `AGENTS.md`. Create a fresh dedicated clone/worktree at the requested
   commit, outside any dirty development checkout. Fetch the `v1.0.1` tag if
   missing. Record the SHA and `git status --porcelain`; stop on a mismatch or
   dirty source. Do not stash, reset or clean someone else's work.
2. Check Git, Python 3.9+, gh >=2.97.0 and the exact SDK in `global.json`.
   Record OS, filesystem, terminal and architecture. If a prerequisite is
   missing, install only those prerequisites from official sources into user-owned
   locations when your session permits. Use a private SDK installation rather
   than changing the machine's default SDK. If setup requires administrator access
   or a lengthy native-toolchain installation, report the exact commands needed
   and continue independent checks. Keep project dependencies unchanged.
3. Run the Python smoke tests, then `tools/test_once.py --expect-commit SHA
   --live` with the appropriate Python command. Add `--dotnet PATH` if needed.
   Add `--aot` only if the native build toolchain is already available for a
   committed release RID. Use existing gh auth; never print/copy tokens into
   commands, logs, reports or committed files. No auth means remote checks are
   blocked, while offline checks can still run.
4. Read every command result and test summary, including skipped tests. Keep
   failures visible. A runner exit of 0 establishes command success, not that
   all platform-specific tests or UX checks ran. Record which host binaries
   were actually exercised and whether they were managed or AOT.

Let the automated checks finish with their existing timeouts. Then spend at
most 30 minutes on the terminal checks below and 15 minutes reproducing new
failures. Stop investigating a failure after one focused reproduction unless
the next check is necessary to distinguish a fixture error from a product bug.
Do not wait for CI or another reviewer to finish this local report.
Avoid broad code reviews or reading the whole repository. Read only the guides
and failure-specific code needed for this pass. Use an inexpensive model for
command execution and log collection if available, reserving stronger models
for diagnosis. Do not create a swarm of review agents or repeatedly poll CI.

## Drive the actual terminal

Read [the PTY guide](../agent_docs/tui-pty-testing.md) before launching the built
host reported in `report.json`. Use a real PTY and terminal emulator/probe
responses on WSL/macOS, or a native Windows terminal/ConPTY on Windows.
Redirected CLI output, direct key-handler calls and a WSL session do not prove
native Windows TUI behavior. If the available agent tools cannot drive the real
terminal, mark that coverage blocked and provide a short manual checklist.
Do not build a new terminal automation framework for this pass.

Use a separate private fixture workspace, HOME/USERPROFILE, config, state and
temp directories. Strip inherited `GIT_*` and agent overrides as described in
AGENTS.md; keep the parent process environment unchanged. Capture existing auth
privately before isolating HOME when a remote operation requires it. Never
apply, remove, unpin or undo against actual user installations. Do not launch
AI clients to populate inventory or session-loading evidence.

Start at **80×24**, then repeat any clipped or awkward view at a larger size.
Use help/current documentation for keys rather than guessing mutation flags.
After each mutation, check payload hashes, link targets, owner/registry evidence
and unchanged independent copies from the shell. Capture meaningful screen
states and terminal identity. Distinguish fixture creation from actions actually
performed through the TUI.

Prioritize these user journeys, in this order:

| Journey | Evidence to collect |
| --- | --- |
| Start, browse, search, preview | Wait for startup inventory to finish. An empty fixture stays in Discover with query focus; an existing inventory can open Installed. From the Installed list, press `/` to open Discover and focus its query. Verify the whole typed query, Tab, shortcuts, help and quit at 80×24. Search failure has a clear explanation when offline. |
| Install and inspect copies | Install into the fixture using actual gh; inspect two independent copies with differing content and, where supported, a shared link. Explain which physical copy and consumers are selected. Refresh preserves correct scope/workspace context. |
| Review a version/update | Use Updates, review a specific existing tag, cancel first, then approve retention and apply one target. Check exact source/version and local-change messaging, queue/focus behavior, and that independent peers stay unchanged. A pinned fixture must remain protected until separately reviewed unpin. |
| Undo | Inspect the saved operation, cancel, then approve the exact reviewed Undo and verify the original. If time permits, edit after inspection and confirm stale approval refuses. Check refresh and retry guidance. |
| Cleanup | Show an empty leftover directory and a broken link beside a healthy skill. Review/cancel first, then apply the safe fixture cleanup. Verify healthy content and independent copies remain; deliberately review-only items should be explained rather than counted as attempted destructive failures. |

Use [usage](usage.md) and [saved updates](saved-updates.md) for current behavior.
Avoid a large matrix of every supported agent or every adversarial native case;
the automated suites/proofs cover those contracts. Inspect static package-check
output if convenient, but do not displace the journeys above or install real AI
clients just to test them.

On WSL/macOS, the existing
`tests/native/prove-reviewed-update-tui.py` can automate a live reviewed
update journey; inspect its `--help` and retain its captures. It uses POSIX PTYs
and is not a Windows proof. Run it with a new output directory and the built
host; it creates private fixtures. Report the exact shortcut/disposition tested.
Do not rerun every variant automatically. On Windows, if symlink privileges or
security-metadata visibility are unavailable, record the refusal and missing
coverage; do not elevate or bypass guards to manufacture a pass.

## Return a useful report

Write `AGENT-REPORT.md` beside the runner's `REPORT.md`, outside the checkout.
Use this shape:

```markdown
# SkillView acceptance — WINDOWS / WSL / MACOS
Commit: FULL_SHA
Machine: OS, architecture (hardware/process/SDK), filesystem, terminal, SDK, gh
Builds: managed/AOT, standalone/extension paths
Overall: PASS / FAIL / PARTIAL / BLOCKED

## Automated checks
Commands and logs; actual test totals and skipped counts/reasons.
Offline and remote proofs: passed/failed/blocked, separately.
AOT and platform-specific/native coverage actually exercised or missing.

## Terminal journeys
Each journey: passed/failed/not-run/blocked; actual terminal size and input path.
Mutation evidence and unchanged-peer checks; relevant captures/transcripts.

## Findings, most important first
Product / environment / fixture / authentication or network.
Exact reproduction, expected/actual behavior, log/capture paths.
Any risk of wrong target, lost local edits, misleading approval or missing Undo.
Say 'pre-existing' only with baseline reproduction evidence.

## Remaining manual checks
Only the specific checks that could not be automated and why.
```

If there is a new failure, reproduce it once in a fresh fixture. If comparing to
the release is necessary, use a separate checkout and clearly label that result;
never change the requested candidate checkout mid-run. Preserve original failing
logs even if the retry passes. Do not hide skips, retry until green, waive unknown
native failures, or treat authentication/rate-limit failures as successful tests.

Keep reports, logs, machine dumps and captures outside Git; do not commit personal
or device evidence. Return a sanitized summary with aggregate results and missing
coverage, omitting usernames, hostnames, absolute local paths and device
identifiers. Keep investigation paths in the local report. Share raw evidence
separately only when explicitly requested, after checking it for personal data
and credentials; exclude tokens, private homes, state archives and package caches.
Leave fixtures available for investigation. Report any owned process that could
not be stopped.
