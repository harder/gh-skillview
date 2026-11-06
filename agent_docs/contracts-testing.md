# Testing and dependency contracts

Read these contracts before changing this subsystem. See [the guide index](README.md)
for related workflows and [AGENTS.md](../AGENTS.md) for repository-wide rules.

- Mutating CLI fixtures must isolate HOME/USERPROFILE, state and CWD, and
  neutralize inherited Git redirection. Use explicit private GIT_DIR/WORK_TREE/
  COMMON_DIR and GIT_CONFIG_COUNT=0, or strip inherited GIT_* before configuring
  a dedicated private redirected-worktree fixture.

- Current package compatibility: SkillView is pinned to Terminal.Gui `2.5.0`
  and Terminal.Gui.Editor `2.5.7`. Test projects use `Microsoft.NET.Test.Sdk` `18.11.0`, `xunit.v3`
  `4.0.2`, and `xunit.runner.visualstudio` `4.0.1`. If tests fail to compile on
  missing `TestContext`, rerun `dotnet restore` so stale xUnit 2.x assets are
  replaced. `tests/SkillView.Tests/Build/PackageReferenceTests.cs` and
  `CliDispatcherHelpTests.VersionFlag_IncludesTerminalGuiVersion` hardcode the
  exact Terminal.Gui version string — update both alongside any bump.

- `global.json` selects `Microsoft.Testing.Platform` for xunit 4. Pass
  `--project` for a specific project and use xunit's MTP-native
  `--filter-namespace`/`--filter-class`/`--filter-trait` options for filtered
  runs. For an exact method, use `--filter-query
  "/*/Namespace/Class/Method"`. `--filter-method`, older single-dash xunit
  flags, and `--treenode-filter` can silently select zero tests here. See
  `agent_docs/running-tests.md` for examples.

- `src/SkillView.Core/SkillView.Core.csproj` owns the default
  `TerminalGuiVersion` property. Keep the `PackageReference` on
  `Version="$(TerminalGuiVersion)"` so CI can override it via MSBuild without
  editing source.
  Core also explicitly services seven Terminal.Gui runtime dependency roots
  (Markdig, Microsoft.Extensions, and System.IO.Abstractions). Review those
  overrides together with all six lock files and the four native AOT targets;
  see `agent_docs/dependency-refresh-2026-10.md`.

- CI's standalone AOT smoke publish now promotes `IL2026`, `IL3050`, and
  `IL3053` to errors for `SkillView.App`; keep the gh extension's project-level
  suppression local until that host gets its separate re-evaluation.

- Terminal.Gui 2.5.0 `Application.InstanceCreated`/`InstanceDisposed` subscribers
  are thread-local. Async app-owned task draining can resume teardown on another
  thread, so facade notifications cannot prove disposal after an awaited run.
  Observe the exact instance's `InitializedChanged(false)` notification (emitted
  by Dispose after state/driver cleanup), keep the identity assertion, and verify
  uninitialized state plus released Driver/MainThreadId at RunAsync return.
  Unsubscribe instance/session handlers in finally; do not leave facade handlers
  on an abandoned creation thread. Use held-work gates and a dedicated UI thread
  for deterministic drain handoffs rather than sleeps or repeated runs.

- Seeded ANSI repeated-key tests must verify every selection/focus transition and
  paint both boundary states. Send each direction as one repeat burst and gate
  direction/quit on actual draw completion; avoid imposing a many-paint throughput
  quota through an unrelated event-loop watchdog. Keep the watchdog and exact
  counts/boundary assertions. Isolate startup History via the existing app-owned
  loader seam, and record the quit-request time separately from total teardown.
