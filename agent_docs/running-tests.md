# Running tests

The solution runs on xunit v4's `dotnet test` integration with the .NET 10 SDK's
`Microsoft.Testing.Platform` runner (opted in via `global.json`'s `test.runner`).

Install the exact SDK pinned in `global.json`. SDK roll-forward is disabled
because Native AOT adds SDK-selected compiler/linker dependencies to the NuGet
lock files. When upgrading the SDK, run `dotnet restore --force-evaluate` with
that SDK, commit the updated lock files alongside `global.json`, and verify
`dotnet restore --locked-mode` before building. CI and release workflows read
the same `global.json`.

Use the serviced .NET 10.0.1xx SDK feature band so contributors can use Ubuntu's
apt-managed SDK. SDK 10.0.113 includes runtime 10.0.13; keep the target framework
at `net10.0`. A feature-band change still requires lock-file regeneration and
verification of the four native release targets.

A bare project path (e.g. `dotnet test tests/Foo/Foo.csproj`) no longer works —
pass `--project <path>` explicitly, or run `dotnet test` from the repo root to
target the whole solution.

Run product publish commands sequentially. Both product hosts build the shared
`SkillView.Core` project into the same intermediate/output locations, and
parallel publish processes can contend on files such as
`SkillView.Core.deps.json`. That contention is a verification artifact, not a
product failure.

Frozen native compatibility checkouts explicitly use `core.autocrlf=false` and
`core.eol=lf` when creating their worktrees. Their later source-cleanliness checks
disable inherited global Git configuration; a checkout converted by a Windows
global setting can otherwise appear modified under that verification environment.
Keep the clean-source refusal intact, including for genuine source edits.

On a Linux ARM64 development host, solution restore can infer `linux-arm64` for
the self-contained product hosts, outside the committed release RID matrix. For
focused unit/UI verification, locked-restore and test `SkillView.Tests.csproj`
directly; it references Core without restoring the product hosts. Do not modify
release lock files merely to run these scoped tests.

Some Codex sandboxes expose an empty read-only `/tmp/.git` mount. Native state
placement then correctly refuses fixtures beneath `/tmp` as Git-contained. Check
the mount with `findmnt -T /tmp/.git` and run with an approved private `TMPDIR`
outside those ancestors, for example beneath `/var/tmp`. Keep the placement guard
and sandbox marker intact. Record the initial environment refusal separately
from the isolated rerun.

Archive-copy fixtures compare their recorded address with the held native
lease's canonical path, then require the copy to preserve that exact address.
Lexical temporary paths can differ on macOS (`/var` versus `/private/var`).
Exercise a real scan-root alias on Unix rather than normalizing away a mismatch;
keep exact archive fingerprints, native identities and blob comparisons.
For generic directory placement controls, use read-only native identity capture;
relocation leases require an actual skill directory. Derive canonical expectations
from that opened object so macOS `/var` aliases remain meaningful test inputs.
TUI source-path fixtures must be fully qualified on the executing OS: `/new/demo`
is root-relative on Windows and correctly fails preparation admission.

**Filtered runs:** pass xunit's MTP-native `--filter-*` flags directly to
`dotnet test` (no `--` needed): `--filter-namespace`, `--filter-not-namespace`,
`--filter-class`, `--filter-not-class`, `--filter-trait`,
`--filter-not-trait`, or `--filter-query "query"` for the query-filter language.
For a single exact method, use a four-segment query such as
`--filter-query "/*/SkillView.Tests.Ui/SearchAgentMetadataLoaderTests/FilterAsync_TimeoutContainsCallbackFailure"`.
On the .NET 10/xunit 4 integration, both simple and fully qualified
`--filter-method` values have been observed to reach the runner but silently
select zero tests; do not count such a run as verification. Do **not** use
xunit's older single-dash console-runner
flags (`-namespace`, `-trait`, `-class`, `-method`, `-filter`) with `dotnet test`
— those are only recognized when running the built dll directly (`dotnet exec` /
the standalone runner); `dotnet test`'s MTP handshake doesn't know about them and
the run silently reports "Zero tests ran" (exit 5) even though `--diagnostic`
logs show the args reached the process. `--treenode-filter` (the platform-level
filter option) also doesn't work against xunit v3/v4 here — use the `--filter-*`
flags instead.

- **Default verification:** `dotnet restore --locked-mode`, `dotnet build`, `dotnet format SkillView.sln --no-restore --verify-no-changes`, then `dotnet test --no-build` (from repo root; runs both test projects).
- **UI-focused tests:** `dotnet test --project tests/SkillView.Tests/SkillView.Tests.csproj --filter-namespace "SkillView.Tests.Ui"`
- **Resource stress tests:** `dotnet test --project tests/SkillView.Tests/SkillView.Tests.csproj --filter-class "SkillView.Tests.Ui.ResourceStressTests"`. These allocation/process tests are serialized through the `ResourceStress` collection.
- **Contract tests:** require a real `gh` binary plus auth. CI runs them with `SKILLVIEW_CONTRACT_TESTS=true dotnet test --project tests/SkillView.Tests/SkillView.Tests.csproj --configuration Release --no-build --filter-trait "Category=Contract"` via `.github/workflows/contract-tests.yml`.
- **PTY startup smoke:** opt-in only. Run `dotnet build`, then `SKILLVIEW_PTY_TESTS=true dotnet test --project tests/SkillView.Tests/SkillView.Tests.csproj --filter-trait "Category=PTY"`. The test uses the built `src/SkillView.App/bin/Debug/net10.0/<rid>/skillview` host under `/usr/bin/script`, supplies the terminal-probe responses that `script` itself cannot provide, and expects either `GH_TOKEN` or a working `gh auth token`. Keep failure cleanup bounded: terminate and wait for the child before reading redirected streams to EOF.
- The contract-test workflow runs daily and on `workflow_dispatch` against
  **two required lanes**: the `GhBinaryLocator.MinimumVersion` (currently
  2.97.0) and the latest GitHub CLI release. A manual version override applies
  only to the latest lane. A failure opens an assigned compatibility issue.
  Keep the minimum lane equal to the app's hard minimum.
- **Automation checks:** `node --test .github/scripts/critical-dependencies.test.js .github/scripts/critical-dependency-assessment.test.js`,
  `python site/build.py`, and `node --check site/app.js`. CI also runs
  `actionlint` and dependency review for PRs.
- For PTY-driven TUI checks, prefer the built binary and follow `agent_docs/tui-pty-testing.md`.

The solution also builds `tests/SkillView.MaintenanceProof`, a proof-only console
host for deterministic process death at production maintenance checkpoints. It
requires a marked disposable HOME/CWD/state root and has no shipping product
fault flags. After building, run `tests/native/prove-shared-revision-update.py`
with `--app <built skillview> --proof-host <built SkillView.MaintenanceProof>` and
one or more `--gh <binary>` arguments plus `--output <evidence.json>`. The parent
kills the paused process without teardown, then uses the actual product CLI for
fresh inspection, explicit torn-image repair, recovery and undo. Auth is captured
before HOME isolation, passed only through the child environment, and never
included in proof output. The proof host pause is bounded to two minutes.
Write exact marker and registry-image fixtures as bytes: Python text output
translates LF on Windows. Keep a positive disposable-workspace admission control
alongside linked HOME/state refusals, so an invalid marker cannot make negative
containment checks appear to pass. Drain host stderr with stdout and report an
early exit before waiting for a checkpoint timeout.
The shared-owner proof gives each admission guard a 60-second subprocess deadline, including
managed host startup and positive-control inventory. It logs the case and elapsed
time; a timeout fails with bounded captured output and never counts as a refusal
or triggers an automatic retry. Proof-host stderr marks startup and inventory
capture phases. Windows descendant-held pipes may delay subprocess cleanup;
the deadline does not bound that inherited-pipe drain.

The cross-platform `tests/native/prove-inventory-filter.py` proof runs
eleven actual CLI scope/agent queries against one physical payload exposed by
user/project links, an ancestor root alias and a named profile. It requires
complete inventory, stable identity/consumer evidence and unchanged installed
objects/bytes. HOME/CWD/state are private, network proxies are blocked and Git
transport is disabled. Use `--app <built host> --output <evidence.json>` and repeat
`--gh <binary>` for minimum/current contracts. CI runs it on both native hosts
for every RID.

### Reviewed update PTY proof (POSIX)

After building, run `tests/native/prove-reviewed-update-tui.py` with an
absolute `--app`, `--gh`, `--output`, and `--shortcut u`, `U`, or `button`.
Only `--disposition replace` is supported. `--local-edits` adds authored changes
that must remain in the retained original; `--preserve-first` verifies keep-local
before preparing another replacement review. The retired editor is not exercised.
The script uses a private HOME/state/CWD and actual gh remote installation,
answers terminal probes, and locates visible controls through a small ANSI screen
reader. It verifies preparation and unapproved Enter leave payload/registry
unchanged, then opens frozen source Details, acknowledges source review, explicitly
approves retention and applies. Filesystem manifests
independently compare candidate bytes/modes/native objects; CLI inspection and
undo prove restoration of the original objects and exact registry bytes. An
unselected manual installation remains unchanged, including when the all checkbox
is enabled for row/marked shortcuts. Ctrl+Q must exit successfully. The script
retains JSON screen/evidence output and a raw terminal transcript at the supplied
output path; all skill, agent and manager fixture state is disposable.

Use the built host, never `dotnet run`. This opt-in proof requires gh authentication
and network access to `cli/cli`; it does not execute the installed skill.

### Inventory failure and coverage acceptance

`InventoryProviderOutcomeTests` drives actual `LocalInventoryService.CaptureAsync`
with the list adapter's internal process-result seam. It distinguishes process
errors, incompatible/truncated JSON, valid empty inventory, failed per-root
coverage, denied enumeration and missing required custom roots. Failures remain
retryable and cannot establish absence; a valid empty result may produce only a
review-only Untracked observation.

`python tests/native/prove-inventory-denied-root.py --app /absolute/host
--output /tmp/denied-root-proof.json` additionally uses actual POSIX permission
denial under a private HOME/CWD. Run as a non-root user on Linux or macOS. It
restores the denied root's mode in `finally`, verifies the readable control and
compares freshly enumerated object/byte/mode manifests. CI runs both native
hosts on those platforms; the deterministic enumeration-failure test also runs
on Windows. Pass `--gh /absolute/gh` to select another supported gh version.


### Actual gh metadata acceptance

`MetadataAcceptanceTests` exercises bounded YAML and scanner validity without
network access. `GhMetadataContractTests` is opt-in (`SKILLVIEW_CONTRACT_TESTS=true`)
and uses actual gh remote, exact-pin and `--from-local` installs in private
HOME/config/state. It validates raw tracking fields before catalog enrichment,
then inventory JSON and Installed details. Run the class with `--filter-class
SkillView.Tests.Gh.GhMetadataContractTests`; the scheduled/manual contract workflow
includes it in both minimum and current gh lanes. Default-suite discovery of
these four cases does not substitute for running the opt-in contract.

`tests/native/prove-tracking-roundtrip.py --app /absolute/native/skillview
--output /tmp/tracking-proof.json` compares native inventory with actual gh list,
including authored local descriptions/version and distinct repository refs/pins.
Repeat `--gh /absolute/gh` to prove multiple gh versions. CI runs both native
hosts on each AOT RID and retains the results with maintenance proof artifacts.
The fixture never invokes skill code.

### Arrow-navigation stress

The ANSI Installed regression sends all 50 Down and 50 Up events through real
keyboard routing in five-event bursts. It checks the selected row and focus after
every event, requires drawing in both directions, and checks the final boundary
on the following tick. Keep the 20-second watchdog and progress diagnostics.
Requiring 101 separate timer/redraw cycles made this correctness test sensitive
to loaded Windows runners; batching preserves the key/boundary checks without
turning redraw throughput into the acceptance criterion. A watchdog timeout alone
does not establish a product navigation failure or reproduce the original crash.

### Native proof failures from GitHub API quota

When a native proof host fails during an actual `gh` source request, inspect the
runner's HTTP status and `X-RateLimit-Reset` evidence before classifying it as a
product failure. The GitHub Actions installation token quota is separate from
the local `gh auth` quota, so a healthy local `gh api rate_limit` does not clear a
runner-side 403. Preserve the failed attempt's logs and partial artifacts. After
the recorded reset, confirm the reviewed PR head has not changed and rerun only
the failed jobs. Identify replacement artifacts by artifact ID, `created_at`,
run attempt and digest; artifact names may be reused across attempts. Do not log
tokens or authorization headers.
