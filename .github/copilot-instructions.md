# Copilot Instructions — gh-skillview

Terminal UI and CLI for viewing/managing AI agent skills. Ships as a GitHub CLI
extension (`gh skillview`) and a standalone binary (`skillview`). C# / .NET 10,
Native AOT, Terminal.Gui v2.

## Project status

Phases 0–8 are complete. Current phase roadmap:

| Phase | Scope | Status |
|-------|-------|--------|
| 0 | TG2 + .NET 10 Native AOT feasibility spike | ✅ Done |
| 1 | Environment probe, capability layer, structured logging, Doctor | ✅ Done |
| 2 | Local inventory discovery, SKILL.md parsing, symlink dedup, `gh skill list` adapter | ✅ Done |
| 3 | Search + preview adapters, `SearchScreen` TUI, CLI subcommands | ✅ Done |
| 4 | Install adapter, `InstallScreen` TUI, search→install handoff, CLI subcommand | ✅ Done |
| 5 | Update adapter, dry-run parsing, `UpdateScreen` TUI, TreeSha-axis diff | ✅ Done |
| 6 | §12.1 safe-remove validator, `RemoveService`, cleanup classifier, `RemoveScreen` + `CleanupScreen` TUIs, CLI subcommands | ✅ Done |
| 7 | Argv parser polish, exit-code contract, snapshot tests, dispatcher coverage | ✅ Done |
| 8 | Release engineering — six-RID AOT matrix, SLSA attestations, end-to-end install verification | ✅ Done |
| 9 | Hardening — contract tests, error classification, TG2 upstream review, scan diagnostics | ✅ Done |

Consult `implementation-plan.md` (§22) for detailed phase specs. Each completed
phase has a `PHASE<N>_NOTES.md` with findings, test counts, and carry-forwards.

## Build, test, publish

```bash
dotnet restore
dotnet build
dotnet test                         # full suite
dotnet test --filter "FullyQualifiedName~RedactorTests"   # single test class
dotnet test --filter "FullyQualifiedName~RedactorTests.RedactsGhTokens"  # single test

# AOT publish (Linux needs clang + zlib1g-dev):
dotnet publish src/SkillView.App -c Release -r osx-arm64 \
  -p:PublishAot=true -p:StripSymbols=true -o dist/app
```

No separate lint step — `TreatWarningsAsErrors` and `EnforceCodeStyleInBuild`
are enabled in `Directory.Build.props`, so `dotnet build` catches style issues.

## Architecture

Two thin entry-point projects (`SkillView.App`, `SkillView.GhExtension`) each
call `EntryPoint.RunAsync(args)` in `SkillView.Core`. The binary name
determines `InvocationMode` (Standalone vs GhExtension); presence of a
subcommand determines `DispatchMode` (CLI vs TUI).

```
EntryPoint.RunAsync
 ├─ ArgParser.Parse        → AppOptions (mode, debug, scan-roots, subcommand)
 ├─ TuiServices.Build()    → composition root (manual DI, no framework)
 └─ DispatchMode switch
      ├─ Cli → CliDispatcher.RunAsync  (pattern-match on subcommand name)
      └─ Tui → SkillViewApp.Run       (Terminal.Gui v2 event loop)
```

`TuiServices` is the composition root — a sealed record with a static
`Build()` factory. No DI container; all wiring is explicit and AOT-safe.

### Layer map

| Layer | Responsibility |
|---|---|
| `Bootstrapping` | Argv parsing, entry point, `AppOptions` |
| `Cli` | `CliDispatcher` routes subcommands to handlers; JSON rendering helpers |
| `Environment` | `EnvironmentProbe` — locates `gh`, checks version, probes auth + capabilities |
| `Gh` | Subprocess adapters for `gh skill {search,preview,install,update,list}` |
| `Gh.Models` | Result records for each adapter (`SearchResultSkill`, `InstallResult`, …) |
| `Inventory` | Filesystem scan, SKILL.md front-matter parsing, symlink dedup, `gh skill list` merge |
| `Inventory.Models` | `InstalledSkill`, `SkillFrontMatter`, `Scope`, `ValidityState`, `Provenance` |
| `Logging` | Ring-buffer `Logger`, `Redactor`, `FileLogSink` (daily rotation, 14-day retention) |
| `Subprocess` | `ProcessRunner` — argv-array execution, never shell composition |
| `Ui` | TG2 screens: main window, `SearchScreen`, `InstallScreen`, `UpdateScreen`, `RemoveScreen`, `CleanupScreen` |

### Capability gating

`GhSkillCapabilityProbe` parses `gh skill <sub> --help` output to detect which
flags the installed `gh` version supports. That covers preview probing/support
for shared flags like `--allow-hidden-dirs` as well as install-time gating.
Adapters only emit flags that the probe confirmed — this keeps the app
forward-compatible with evolving `gh` releases.

## Key conventions

### AOT compatibility (critical)

Every code path must be Native AOT safe. This means:

- **No reflection**: no `Type.GetProperties()`, `Activator.CreateInstance()`,
  or attribute scanning.
- **JSON**: use `System.Text.Json` source generators (`GhJsonContext`).
  Deserialize via `GhJsonContext.Default.<TypeName>`, never `typeof(T)`.
- **Regex**: use `[GeneratedRegex]` partial methods, never `new Regex(...)`.
- **YAML**: hand-rolled subset parser (`FrontMatterParser`), not YamlDotNet.
- **Arg parsing**: hand-rolled `ArgParser`, not any third-party library.
- **TG2 trimming**: both exe projects root `Terminal.Gui` as a
  `TrimmerRootAssembly` and suppress `IL2026`/`IL3050`/`IL3053`. Mark new
  TG2 call sites with `[UnconditionalSuppressMessage]` if the analyzer flags
  them, and add a `// TODO(tg2):` comment referencing the upstream issue.

### Immutability

- Prefer `record` (or `record struct`) over `class` for data types.
- Use `required` + `init` properties, not mutable setters.
- Collections: `ImmutableArray<T>`, `ImmutableDictionary<K,V>`,
  `IReadOnlyList<T>`. Avoid `List<T>` in public API surfaces.

### Error handling

- Service methods return result records (`SearchResponse`, `InstallResult`,
  etc.) with `ExitCode` / `ErrorMessage` fields — they do not throw.
- `ProcessRunner` returns a `ProcessResult` on startup failure instead of
  throwing.
- Infrastructure failures (logging, file I/O) degrade gracefully and log a
  warning.

### Subprocess calls

Always go through `ProcessRunner.RunAsync(executable, argsArray)`.
Never compose shell strings. The `Redactor` runs automatically on log output.

### Logging

Call `logger.Debug/Info/Warn/Error(category, message)` where `category` is a
dot-separated label (e.g., `"gh.skill.search"`). Redaction is applied at the
log-writer layer — never log raw tokens, but also don't manually redact.

### Naming and file layout

- Namespace mirrors directory: `SkillView.Gh.Models` → `src/SkillView.Core/Gh/Models/`.
- One type per file; filename matches type name.
- Private fields: `_camelCase`. Parameters: `camelCase`.
- Async methods end in `Async` and accept `CancellationToken`.
- Use `.ConfigureAwait(false)` on awaited calls in library code.

### Exit codes

Use `ExitCodes` constants (`Success = 0`, `UserError = 1`,
`InvalidUsage = 2`, `EnvironmentError = 10`, `NoMatches = 20`). These are
part of the public contract — scripts and agent hooks depend on them.

### Testing

- xUnit with `[Fact]` and `[Theory]`/`[InlineData]`. No mocking libraries.
- Embed test data as raw string literals; no fixture files.
- Test `internal` members via `InternalsVisibleTo` (set in `SkillView.Core.csproj`).
- Snapshot tests for JSON-emitting subcommands live in
  `CliDispatcherJsonSnapshotTests`.

### Terminal.Gui v2

- Create screens as `Dialog` subclasses or inline `Dialog` instances with
  `Dim.Percent` / `Dim.Fill` sizing.
- Run modals via `_app.Run(dialog)`.
- Bind key handlers on the `Window.KeyDown` event (not `AddCommand` — it's
  `protected` in the current TG2 RC surface).
- Use `SelectedCellChanged` on `TableView` for live detail updates.
- Track upstream issues with `// TODO(tg2):` comments.

### Cross-references

Code comments use `§N` notation (e.g., `§12.1`) to reference sections in
`implementation-plan.md`. Consult that file for detailed design rationale
behind validators, classifiers, and reconciliation logic.

## Implementation rules (from §24)

These rules govern all changes — they are the project's architectural invariants:

1. Exactly three production projects plus one test project.
2. Domain logic, services, and most UI code live in `SkillView.Core`.
3. Entrypoints are tiny (two lines each).
4. Composition over inheritance.
5. Capability probes via flag-token membership scans, not help-text structural parsing.
6. Never delete anything above a validated skill directory.
7. Resolve symlinks via realpath before any mutation; require the resolved path to remain inside a known root.
8. Never follow symlinks that escape the scan root after resolution.
9. JSON parsing only where `gh` documents JSON output.
10. Always rescan inventory post-op (install / update / remove).
11. Subprocess invocation uses the argv-array API — never shell composition.
12. Apply redaction at the log-writer layer.
13. No central config file. State persistence lives in `.skillview-ignore` markers or flags.
14. Prefer TG2 v2 components over manual implementations. Reference the TG2 `Examples/` directory before hand-rolling UI.
15. Any TG2 workaround must be marked `// TODO(tg2): upstream` and reviewed in Phase 9.
16. Default log level is Info+; Debug requires `--debug` or `SKILLVIEW_LOG=debug`.

## Key upstream dependencies and gating

- **`gh` minimum**: v2.92.0. Hard-enforced in `GhBinaryLocator.MinimumVersion`.
- **`gh skill list --json`** (cli/cli#13215): not yet landed. `GhSkillListAdapter` is gated on `capabilities.HasSkillList` — flips automatically when `gh` adds the flags.
- **`gh skill update --yes`**: not in v2.92.0. The `UpdateScreen` has guardrails for the interactive-prompt quirk.
- **`gh skill install --repo-path`** (community discussion #192851): gated on `capabilities.SupportsRepoPath`.
- **Terminal.Gui v2**: pinned at `2.0.0-rc.6`. Known AOT workarounds documented in `PHASE0_NOTES.md`; `TrimmerRootAssembly` required for the full assembly.
