# Running tests

The solution runs on xunit v3/v4's `dotnet test` integration with the .NET 10 SDK's
`Microsoft.Testing.Platform` runner (opted in via `global.json`'s `test.runner`).
A bare project path (e.g. `dotnet test tests/Foo/Foo.csproj`) no longer works —
pass `--project <path>` explicitly, or run `dotnet test` from the repo root to
target the whole solution.

**Filtered runs:** pass xunit's MTP-native `--filter-*` flags directly to
`dotnet test` (no `--` needed): `--filter-namespace`, `--filter-not-namespace`,
`--filter-class`, `--filter-not-class`, `--filter-method`, `--filter-not-method`,
`--filter-trait`, `--filter-not-trait`, or `--filter-query "query"` for the
query-filter language. Do **not** use xunit's older single-dash console-runner
flags (`-namespace`, `-trait`, `-class`, `-method`, `-filter`) with `dotnet test`
— those are only recognized when running the built dll directly (`dotnet exec` /
the standalone runner); `dotnet test`'s MTP handshake doesn't know about them and
the run silently reports "Zero tests ran" (exit 5) even though `--diagnostic`
logs show the args reached the process. `--treenode-filter` (the platform-level
filter option) also doesn't work against xunit v3/v4 here — use the `--filter-*`
flags instead.

- **Default verification:** `dotnet build` then `dotnet test --no-build` (from repo root; runs both test projects).
- **UI-focused tests:** `dotnet test --project tests/SkillView.Tests/SkillView.Tests.csproj --filter-namespace "SkillView.Tests.Ui"`
- **Contract tests:** require a real `gh` binary plus auth. CI runs them with `SKILLVIEW_CONTRACT_TESTS=true dotnet test --project tests/SkillView.Tests/SkillView.Tests.csproj --configuration Release --no-build --filter-trait "Category=Contract"` via `.github/workflows/contract-tests.yml`.
- **PTY startup smoke:** opt-in only. Run `dotnet build`, then `SKILLVIEW_PTY_TESTS=true dotnet test --project tests/SkillView.Tests/SkillView.Tests.csproj --filter-trait "Category=PTY"`. The test uses the built `src/SkillView.App/bin/Debug/net10.0/<rid>/skillview` host under `/usr/bin/script` and expects either `GH_TOKEN` or a working `gh auth token`.
- The contract-test workflow runs nightly and on `workflow_dispatch` against a pinned `gh` lane (kept in step with the latest adopted release — see `.github/workflows/contract-tests.yml`) that must pass plus a `latest` lane allowed to fail. Keep the pinned lane at or above `GhBinaryLocator.MinimumVersion` — pinning below SkillView's own hard minimum tests a `gh` version the app refuses to run against.
- For PTY-driven TUI checks, prefer the built binary and follow `agent_docs/tui-pty-testing.md`.
