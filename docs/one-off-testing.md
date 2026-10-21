# Test this release candidate on three machines

This is a one-time acceptance pass after the changes since `v1.0.1`. Run one
Codex or Claude Code session on each machine, in parallel. Each session tests the
same commit and produces logs plus a short report. There is no scheduled job or
service to set up.

Copy one of the prompts below. Replace `TEST_SHA` with the **same full commit
SHA** on all machines, including these testing files. Give the agent a fresh
clone or dedicated worktree; keep your development checkout as it is.

## Windows 11 prompt

```text
Do a one-off SkillView release-candidate test on native Windows 11.
Repository: https://github.com/harder/gh-skillview
Exact commit: TEST_SHA
Use a fresh dedicated checkout at this commit, with the v1.0.1 tag available.
Read AGENTS.md and docs/one-off-testing-agent.md and execute that test task.
Use native Windows Python, .NET, gh and a native Windows terminal. WSL results
do not count as Windows results. Use py -3 for the Python runner.
Do not develop features, fix product code, or set up recurring automation.
Return AGENT-REPORT.md and paths to logs/captures, with failures and blocked
coverage stated plainly. Keep credentials and private fixture homes out of uploads.
```

## WSL Ubuntu prompt

```text
Do a one-off SkillView release-candidate test in WSL Ubuntu.
Repository: https://github.com/harder/gh-skillview
Exact commit: TEST_SHA
Use a fresh dedicated checkout at this commit, with the v1.0.1 tag available.
Read AGENTS.md and docs/one-off-testing-agent.md and execute that test task.
Keep the checkout and fixtures in the Linux filesystem, not /mnt/c. Use Linux
Python, .NET and gh; record the WSL/kernel version and whether openat2 works.
Use python3 for the runner. Record /mnt/c behavior only as an additional probe.
Do not develop features, fix product code, or set up recurring automation.
Return AGENT-REPORT.md and paths to logs/captures, with failures and blocked
coverage stated plainly. Keep credentials and private fixture homes out of uploads.
```

## MacBook prompt

```text
Do a one-off SkillView release-candidate test on this MacBook.
Repository: https://github.com/harder/gh-skillview
Exact commit: TEST_SHA
Use a fresh dedicated checkout at this commit, with the v1.0.1 tag available.
Read AGENTS.md and docs/one-off-testing-agent.md and execute that test task.
Use python3 for the runner. Record hardware, process and .NET architectures.
On Apple Silicon use native ARM64, not Rosetta. On Intel report native operations
that correctly refuse unsupported ABI; do not bypass the platform checks.
Do not develop features, fix product code, or set up recurring automation.
Return AGENT-REPORT.md and paths to logs/captures, with failures and blocked
coverage stated plainly. Keep credentials and private fixture homes out of uploads.
```

## What the agent will run

Prerequisites are Git, Python 3.9+, the exact .NET SDK from `global.json`, and
`gh` 2.97.0+. Existing GitHub authentication enables the remote proofs. Agents
should check what is already installed before installing anything. Missing
prerequisites may be installed from official sources into user-owned locations
when the session permits; lengthy native-toolchain setup can be reported instead.
The checked-out commit must be clean, including untracked files.
The output/temp fixture location must also be outside **every** ancestor `.git`
marker. Even an empty `.git` directory, invalid gitfile or dangling marker link
changes product containment policy. The runner rejects such locations before
creating fixtures; choose another `--output` without removing the marker.

On WSL/macOS, from the dedicated checkout:

```sh
python3 -m unittest discover -s tools/tests -v
python3 tools/test_once.py --expect-commit TEST_SHA --live
```

On Windows PowerShell:

```powershell
py -3 -m unittest discover -s tools/tests -v
py -3 tools/test_once.py --expect-commit TEST_SHA --live
```

Use `--dotnet /absolute/path/to/dotnet` if the required SDK host is outside
PATH. That host's runtime must also be discoverable by built apphosts; set
`DOTNET_ROOT` to its installation directory if necessary. Add `--aot` when the
native compiler/linker prerequisites are already installed. AOT publishes the
two products sequentially and tests those binaries instead of managed hosts.
Without it, report native AOT coverage as not run. Supported release RIDs are
Windows x64/ARM64, Linux x64 and macOS ARM64; other hosts can run managed checks.
Native publish uses CI's locked restore recipe; do not run a separate
`dotnet restore -r RID`, which overrides the four-RID set in the committed locks.

The runner performs locked restores, Release builds, the full unit and integration
suites, and existing CLI proofs for package inspection, native identity,
gitfile protection, inventory filtering, discovery and quarantine/Undo. With
`--live`, it also exercises real remote install/update/restore/Undo fixtures for
both standalone and extension binaries. It retrieves existing gh authentication
without logging the token. Live proofs use the public repositories selected by
the existing proof scripts; network, authentication and remote changes can block
them independently of product correctness.

Each subprocess gets private HOME/config/state/temp directories and neutralized
Git/agent overrides. Mutating proof scripts create their own fixture working
directories. Builds use the dedicated checkout and the ordinary NuGet cache.
The runner does not scan or modify your actual installed skills. The agent task
adds real terminal testing; command success alone does not complete acceptance.
On Windows, a private file/directory symlink probe identifies missing fixture
permissions. Affected CLI proofs are marked not run, making coverage partial;
unrelated checks still run. Test-suite failures remain failures to investigate,
including missing security-metadata privileges; the kit does not waive them.

Results default to a unique temp directory outside the checkout (`/var/tmp` on
Linux). `--output PATH` chooses a new directory instead. The runner prints its
location and writes `REPORT.md`, `report.json`, command logs and proof JSON.
Exit status 0 means requested commands passed, 1 means a command failed, and 2
means blocked or partial coverage. Test skips still need to be read in the logs.
Each command has a timeout; after a timeout the run stops for cleanup inspection.
Run one kit instance per checkout to avoid shared build outputs racing.
For a timeout investigation, add `--diagnostic-tests`: xUnit reports tests taking
over 60 seconds, and MTP writes diagnostic files beneath the result directory.
This adds evidence, without extending the command timeout or changing assertions.

The kit requests positive native link metadata proof, as CI does. On Windows,
complete SACL metadata needs an already assigned `SeSecurityPrivilege`; symlink
creation rights alone do not supply it. A restricted test process can therefore
fail those positive assertions while separate CLI workflows pass. Keep those
failures visible, retain the exact refusal and investigate them separately; do
not elevate, disable metadata guards or treat CLI success as a full-suite pass.

Bring back the three `AGENT-REPORT.md` files, command reports and relevant logs
or screen captures. Do not upload entire result directories: they also hold
private fixture homes and state. Compare results by commit, platform and failure
reproduction, then fix the confirmed release blockers in a separate change.
