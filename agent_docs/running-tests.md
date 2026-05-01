# Running tests

- **Default verification:** `dotnet build` then `dotnet test --no-build`
- **UI-focused tests:** `dotnet test tests/SkillView.Tests/SkillView.Tests.csproj --filter "FullyQualifiedName~SkillView.Tests.Ui"`
- **Contract tests:** require a real `gh` binary plus auth. CI runs them with `SKILLVIEW_CONTRACT_TESTS=true dotnet test --configuration Release --no-build --filter "Category=Contract"` via `.github/workflows/contract-tests.yml`.
- **PTY startup smoke:** opt-in only. Run `SKILLVIEW_PTY_TESTS=true dotnet test tests/SkillView.Tests/SkillView.Tests.csproj --filter "Category=PTY"` after `dotnet build`. The test uses the built `src/SkillView.App/bin/Debug/net10.0/<rid>/skillview` host under `/usr/bin/script` and expects either `GH_TOKEN` or a working `gh auth token`.
- The contract-test workflow runs nightly and on `workflow_dispatch` against a pinned `gh` `2.92.0` lane that must pass plus a `latest` lane allowed to fail.
- For PTY-driven TUI checks, prefer the built binary and follow `agent_docs/tui-pty-testing.md`.
