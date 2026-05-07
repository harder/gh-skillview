# SkillView Decisions Log

This file records durable architectural and workflow decisions for SkillView.

## Format

- Decisions are append-only.
- Each decision gets a stable `D-XXX` identifier.
- If a decision changes later, add a new entry that supersedes the old one instead of rewriting history.

---

## D-001: Shared core with two tiny entrypoints (Active)

**Context:** SkillView ships as both a standalone binary (`skillview`) and a GitHub CLI extension (`gh skillview`). The command surface and behavior need to stay aligned across both delivery modes without duplicating business logic.

**Decision:** Keep all domain logic, CLI dispatch, `gh` adapters, logging, inventory, and TUI implementation in `SkillView.Core`. Keep `SkillView.App` and `SkillView.GhExtension` as tiny entrypoint projects that delegate to `EntryPoint.RunAsync(args)`.

**Status:** Active

**Effect:**
- `src/SkillView.App/Program.cs`
- `src/SkillView.GhExtension/Program.cs`
- `src/SkillView.Core/Bootstrapping/EntryPoint.cs`

## D-002: Hand-rolled argument parsing over a framework parser (Active)

**Context:** SkillView needs a small argv surface, AOT-friendly behavior, and custom parsing rules such as global flags that can appear before or after the subcommand (`--debug`) while other globals must stay before the subcommand (`--scan-root`).

**Decision:** Use a hand-rolled `ArgParser` and explicit subcommand parsers in `CliDispatcher` rather than adopting a general-purpose command-line parsing framework.

**Status:** Active

**Effect:**
- `src/SkillView.Core/Bootstrapping/ArgParser.cs`
- `src/SkillView.Core/Cli/CliDispatcher.cs`
- `tests/SkillView.Tests/Bootstrapping/ArgParserTests.cs`
- `tests/SkillView.Tests/Cli/*`

## D-003: Native AOT-safe by default (Active)

**Context:** SkillView ships self-contained Native AOT binaries. That packaging model rewards explicit code paths and penalizes reflection-heavy infrastructure, implicit runtime discovery, and loosely bounded dependencies.

**Decision:** Prefer AOT-safe patterns by default: explicit composition, source-generated or hand-rolled parsing where needed, warnings-as-errors during build, and conservative runtime assumptions such as invariant globalization.

**Status:** Active

**Effect:**
- `Directory.Build.props`
- `src/SkillView.Core/Ui/TuiServices.cs`
- `src/SkillView.Core/Bootstrapping/ArgParser.cs`
- `src/SkillView.Core/Inventory/FrontMatterParser.cs`

## D-004: Capability-gated `gh skill` integration (Active)

**Context:** `gh skill` is still evolving, and supported subcommands/flags differ by installed `gh` version. Blindly emitting flags based on documentation or assumptions would break older client versions.

**Decision:** Probe `gh skill <subcommand> --help`, scan for known flag tokens, and only emit flags that the installed `gh` build actually supports.

**Status:** Active

**Effect:**
- `src/SkillView.Core/Gh/GhSkillCapabilityProbe.cs`
- `src/SkillView.Core/Gh/CapabilityProbeParser.cs`
- `src/SkillView.Core/Gh/CapabilityProfile.cs`
- `src/SkillView.Core/Gh/*Service.cs`

## D-005: TUI lifecycle is owned by the host shell (Active)

**Context:** SkillView’s top-level TUI hosts background work, nested dialogs, and application-wide state such as startup probing and status auto-clear timers. That work needs one clear owner for init, cancellation, crash logging, and disposal.

**Decision:** Let `SkillViewApp` own the Terminal.Gui application lifetime for the main shell: create the app via `Application.Create().Init()`, wire the top-level window, own the run-lifetime cancellation token source, and dispose app/window resources when the shell exits.

**Status:** Active

**Effect:**
- `src/SkillView.Core/Ui/SkillViewApp.cs`
- `src/SkillView.Core/Bootstrapping/EntryPoint.cs`

## D-006: View-local command bindings plus targeted app-level key routing (Active)

**Context:** Terminal.Gui v2 tables consume some printable keys before they bubble, while SkillView still needs single-letter shortcuts for actions like preview, install, log toggle, and global navigation. Warp terminals also often deliver Enter as `Ctrl+J`.

**Decision:** Bind view-local actions through `KeyBindings` where possible, keep selected app-level `KeyDown` routing where bubbling is insufficient, and explicitly support Warp’s `Ctrl+J` path for preview/activation.

**Status:** Active

**Effect:**
- `src/SkillView.Core/Ui/TuiHelpers.cs`
- `src/SkillView.Core/Ui/SkillViewApp.cs`
- `src/SkillView.Core/Ui/InstalledScreen.cs`

## D-007: Disable table type-to-search through the native null path (Active)

**Context:** SkillView uses single-letter shortcuts heavily. Table type-to-search can swallow those keys before they reach shortcut handlers. Older Terminal.Gui prereleases required a custom matcher workaround, but current 2.0.2-develop builds restore the documented `CollectionNavigator = null` behavior.

**Decision:** Disable `TableView` type-to-search by setting `CollectionNavigator = null` and treat the old custom matcher approach as superseded.

**Status:** Active

**Effect:**
- `src/SkillView.Core/Ui/TuiHelpers.cs`
- `tests/SkillView.Tests/Ui/TuiHelpersTests.cs`

## D-008: Best-effort file logging with stderr streaming in CLI debug mode (Active)

**Context:** SkillView needs persistent logs for diagnosing TUI problems, but logging failures must never prevent the app from starting. CLI users also need immediate visibility when debugging command runs.

**Decision:** Attach a file log sink on a best-effort basis, fall back to memory-only logging if sink creation fails, and stream formatted log lines to stderr when `--debug` is used in CLI mode.

**Status:** Active

**Effect:**
- `src/SkillView.Core/Bootstrapping/EntryPoint.cs`
- `src/SkillView.Core/Logging/FileLogSink.cs`
- `src/SkillView.Core/Logging/Logger.cs`
