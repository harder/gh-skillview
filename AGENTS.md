# AGENTS.md

SkillView is a .NET 10 / Terminal.Gui v2 TUI and CLI for finding, inspecting,
installing, updating and cleaning up `gh skill` skills. Keep ordinary terminal
workflows understandable. The former all-21 maintenance goal is retired; deferred
features are context, not an implementation backlog. Work only on the user's
approved scope.

## Project map

- `src/SkillView.Core/`: shared CLI, gh adapters, inventory, maintenance and TUI.
- `src/SkillView.App/`: standalone `skillview` entrypoint.
- `src/SkillView.GhExtension/`: `gh skillview` extension entrypoint.
- `tests/SkillView.Tests/` and `tests/SkillView.IntegrationTests/`: xUnit tests.
- `tests/SkillView.MaintenanceProof/`: native proof host.
- `tests/native/`: active Python/C/shell proof tools and their Python tests.
- `agent_docs/`: focused implementation contracts and current developer guides.
- `docs/`: user guidance; `tools/test_once.py`: opt-in acceptance runner.

## Read the relevant contracts before editing

| Area | Required guide |
| --- | --- |
| Inventory, discovery, provider policy, path identity | [Discovery contracts](agent_docs/contracts-discovery.md) |
| gh arguments, ownership, placement and version compatibility | [gh contracts](agent_docs/contracts-gh.md) |
| Saved updates, source review, unpin, reimport, recovery, local merges | [Saved-operation contracts](agent_docs/contracts-saved-operations.md) |
| Removal, native identities, metadata, locks, payload capture | [Native filesystem contracts](agent_docs/contracts-native-filesystem.md) |
| Terminal.Gui, async work, cancellation, dispatch and shutdown | [TUI/lifetime contracts](agent_docs/contracts-tui-lifetimes.md) |
| Test isolation, xUnit, Terminal.Gui packages and AOT | [Testing contracts](agent_docs/contracts-testing.md) |
| CI, releases, dependency automation and documentation | [Repository contracts](agent_docs/contracts-repository.md) |
| Package checks, authoring validation and isolated evaluations | [Package-check contracts](agent_docs/contracts-package-checks.md) |

Changes spanning areas must satisfy every relevant contract. Keep detailed
subsystem rules in these guides; keep this root file short. The
[documentation index](agent_docs/README.md) links the operating procedures.

## Repository-wide safety boundaries

- Ordinary updates use `SavedUpdateCoordinator`: prepare one immutable candidate,
  review it, then apply its saved ID/fingerprint with fresh retention approval.
  Prepare the next sibling only after reconciliation of the previous apply.
- Inventory observations and queue recommendations are not mutation authority.
  Retain physical exposures, owner conflicts, partial coverage and unknown state.
- Real removal requires the supported native backend and captured identities.
  Never add path-recursive deletion or an unsupported-platform fallback. Check
  cancellation before native destructive calls. Read the full native contract.
- Keep saved schema/hash compatibility and retained originals for recovery.
  A historical schema proof remains current coverage while that schema is supported.
- Use captured child CWD/environment; never change the parent process environment
  to implement discovery. Read-only policy inspection must not launch AI clients.
- Use the existing application-owned cancellation, background-task and UI-dispatch
  helpers. Preserve lifetime/ownership checks through queued UI completion.
- Keep gh authentication delegated to gh. Require gh 2.97.0 or newer. Put trusted
  flags before `--` and untrusted repository/skill selectors after it.

## Build and verification

- Keep .NET runtime/AOT at **10.0.13** and the SDK selected by `global.json`
  (**10.0.113**, roll-forward disabled). SDK changes require intentional lock-file
  regeneration because compiler/linker package versions come from the SDK.
- All six projects commit NuGet locks covering the four supported native RIDs.
  Use `dotnet restore --locked-mode`. Dependency changes require intentional
  `dotnet restore --force-evaluate`, lock review, then another locked restore.
- Build: `dotnet build`; style: `dotnet format SkillView.sln --no-restore --verify-no-changes`;
  full tests: `dotnet test --no-build`.
- Run scoped formatting from the repository root or pass absolute `--include`
  paths. Solution-relative and project-relative paths differ.
- Specific test project: `dotnet test --project PATH`. xUnit 4 uses MTP-native
  filters; see [running tests](agent_docs/running-tests.md).
- Product publishes must run sequentially because App and extension share Core
  outputs. Preserve Native AOT/trimming warning checks and native host coverage.
- Native tool checks: `python3 -m unittest discover -s tests/native -p 'test_*.py'`.
  CI scope checks: `node --test .github/scripts/ci-change-scope.test.js`.
- Prefer the built executable for PTY testing. Isolate HOME/state/CWD and Git
  redirection for every mutating fixture. See [PTY testing](agent_docs/tui-pty-testing.md).
- CLI global flags precede the subcommand: `skillview --scan-root /tmp/root list --json`.

## Finishing work

- After a PR merges into `main` and its remote branch is deleted, fetch/prune,
  update the primary checkout to `main`, remove the merged local branch and its
  separate worktree, and remove disposable task files without another prompt.
  Verify merge ancestry and working state; preserve unique edits first. Keep
  recovery archives and unrelated active work.
- Do not create a worktree merely to write a plan. Keep plans proportional to
  the approved task; do not revive retired goals from dated handoffs or evidence.
- Update focused guides when a durable contract changes. User-facing details go
  in `docs/usage.md`, development commands in `CONTRIBUTING.md`; screenshots and
  animations live in `assets/screenshots/` and `assets/animations/`.
