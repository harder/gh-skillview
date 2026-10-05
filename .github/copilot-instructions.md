# GitHub Copilot instructions

Read [`../AGENTS.md`](../AGENTS.md) before reviewing or changing this repository.
It is the source of truth for project structure, supported versions, safety
invariants, build commands, and focused guides in `agent_docs/`. Do not copy
general project facts into this file; use the current code and `AGENTS.md` when
they disagree with an old comment, issue, or generated summary.

## For automated reviews

- Prioritize correctness, security, data loss, deadlocks, cross-platform
  behavior, Native AOT compatibility, and user-visible regressions. Give the
  exact triggering path or sequence, impact, and a small actionable fix.
- Review changed code against adjacent call sites and tests. Trace async
  ownership through cancellation, queued UI dispatch, completion, and disposal.
  A completed worker does not imply its UI completion callback has run.
- Treat removal as a destructive security boundary. Apply the complete
  `AGENTS.md` removal contract before suggesting a simpler path or symlink
  implementation. Do not recommend path-recursive deletion as a fallback.
- Check adapters against the **minimum supported** `gh` version as well as the
  newest release. The minimum-version check replaces old per-flag capability
  probing. Flag removals, JSON schema changes, and agent selector changes need
  explicit evidence and focused tests.
- For Terminal.Gui updates, inspect lifecycle, UI-thread dispatch, key input,
  scrolling, modal ownership, trimming, and both executable hosts. Do not
  reintroduce obsolete `ConfigurationManager` APIs or static app lifecycle.
- For dependency PRs, read upstream release notes and the lock-file diff;
  identify API or behavior changes that affect this code. A package version
  change with no relevant change warrants a short review, not speculative
  migration work. Never suggest a version bump that is already present.
- Review workflow edits for least-privilege permissions, pinned action SHAs,
  untrusted PR input in shell expressions, concurrency, checksum integrity,
  and whether a failure can silently pass. Release assets must remain
  immutable once published.
- Confirm a finding against the current diff. Cite the file and line, explain
  an observable failure, and separate proven defects from questions. Avoid
  style-only comments already enforced by `dotnet build` and `dotnet format`.

## For automated coding agents

- Make the smallest change that solves the requested issue. Do not edit
  `AGENTS.md` or other guidance solely to accommodate a speculative review.
- Preserve the existing public CLI, JSON, exit-code, and safety contracts
  unless the request explicitly changes them. Add focused regression tests
  for changed behavior, especially cancellation and removal boundaries.
- Run the narrow relevant tests first, then `dotnet restore --locked-mode`,
  `dotnet build`, and `dotnet test --no-build` before reporting completion.
  Run the two product publishes **sequentially** when a change can affect AOT.
- For a package update, update `packages.lock.json` through an intentional
  restore, then verify locked restore. Keep the dependency monitor's version
  parser, package pins, and version-sensitive tests in sync.
- For a GitHub CLI or Terminal.Gui compatibility issue, use the issue's
  checklist as a starting point, inspect upstream evidence, and propose or
  implement focused tests. Do not increase the `gh` minimum automatically.
- Do not execute filesystem removal against a developer's real skill roots
  while validating a change. Follow the isolated PTY procedure in
  `agent_docs/tui-pty-testing.md` when interactive testing is needed.
- In the PR description, state what changed, what was run, and any remaining
  platform or live-terminal verification limits. Never claim a release or
  deployment succeeded from a local build alone.

## For Copilot issue and PR automation

- Dependency monitor issues and Dependabot PRs are review inputs, not
  authorization to merge or publish. Link the relevant issue and upstream
  release notes when proposing a compatibility change.
- Keep bot-generated suggestions concrete: name affected SkillView code paths,
  expected behavior, and a test that would detect a regression. Do not invent
  breaking changes from a version number alone.
- For critical-dependency assessments, distinguish published release facts,
  source-code inference, and observed test results. A release-note keyword or
  passing help/flag contract test cannot establish full compatibility. Cite
  upstream advisories and name any untested interactive or TUI paths.
- If GitHub's automated Copilot review is enabled, apply these instructions to
  its comments too. A human maintainer retains the decision to merge releases
  and dependency updates.
