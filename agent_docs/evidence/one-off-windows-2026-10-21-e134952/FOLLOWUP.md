# Windows findings: analysis and bounded follow-up

The original [acceptance report](AGENT-REPORT.md) tests product commit
`e13495244c73fc4fd6c14d834de03692c5fd29dc`. Results-only commit
`574c1ca6f2b589edca9c82389cdada97982fb8eb` preserves that evidence. Its failures,
timeout, skips and missing terminal coverage remain part of the record.

## Fixed in the one-off test kit

- **AOT restore:** use CI's `dotnet publish -r RID
  -p:RestoreLockedMode=true` recipe, including standalone AOT warning checks.
  Remove the separate `dotnet restore -r RID`: that command overrides the
  multi-RID restore set and caused the reported NU1004 failures. No dependency
  or lock-file change is needed.
- **Contaminated fixture placement:** reject output/temp locations beneath any
  ancestor `.git` marker before creating them. This includes invalid gitfiles,
  empty directories and broken marker links, independently of whether Git
  recognizes the ancestor as a repository. Preserve the existing marker and
  choose another location.
- **Timeout evidence:** add optional `--diagnostic-tests`, using MTP-native
  long-running-test, progress and diagnostic logging options. Assertions and
  command deadlines remain unchanged.
- **Architecture reporting:** report Python's build platform rather than
  labeling Windows ARM64 hardware detection as the Python process architecture.

The Python smoke suite includes regression coverage for the actual AOT command
sequence and contaminated output rejection. Native Windows ARM64 locked restores
for both product projects also succeeded using `RuntimeIdentifier=win-arm64`
and `RestoreLockedMode=true` through MSBuild, matching publish's restore
properties. The original report already contains successful full native
publishes and all 16 CLI proof commands.

## Still unresolved or unverified

**Full-suite timeout:** the original corrected run has no final test summary.
It is a failed/incomplete run; passing CLI proofs do not resolve it. A bounded
native Windows diagnostic follow-up is recorded separately below.

**Complete security metadata:** the original corrected run emitted 32 failures
mentioning missing `SeSecurityPrivilege`, including a separate three-case
reproduction. Complete SACL preservation cannot be inferred from ordinary file
access or symlink creation rights. These tests request positive metadata proof;
the kit retains that requirement and does not suppress failures, grant
privileges or weaken product refusal. Metadata-dependent unpin/link preservation
needs separate verification in an admitted context. Ordinary update/restore CLI
success does not establish that coverage.

**Terminal acceptance:** Windows Terminal startup was observed, but no keyboard
journey or visual capture was obtained. The manual checklist in the original
report remains required. The window-targeting tool failure is not evidence of
a SkillView rendering defect. Mac testing remains an independent pass.

## Native Windows diagnostic follow-up

The same laptop's Windows SDK and existing ARM64 test binaries were accessible
through WSL interop. Execution remained native Windows. Before running, the
controller required results commit `574c1ca` and verified that its diff from
`e134952` changed only `agent_docs/evidence`; product/test source was unchanged.
No checkout reset, elevation or security-policy change was performed.

The unit run used the original private-environment helper and unchanged positive
link-proof requirement, with `--no-build --no-restore --long-running 60
--xunit-diagnostics on --progress on --diagnostic`. Its external deadline was
240 seconds. It **completed without timing out in 163.1 seconds**:

| Outcome | Count |
| --- | ---: |
| Total | 2,725 |
| Passed | 2,583 |
| Failed | 63 |
| Skipped | 79 |

Exit code was 2. No test exceeded the 60-second long-running threshold; the
longest successful observed test took 34.778 seconds. The earlier timeout was
not reproduced, and its cause remains unproven.

This invocation emitted additional access-denied errors when opening fixture
payloads/links, often followed by fixture cleanup errors. Its failure set differs
from the original report's 32 SACL failures. The interop process also lacks
`SeSecurityPrivilege`; these observations do not establish a common root cause
or a successful metadata-preservation result. Keep this failed run separate
from the original report and from the successful AOT CLI proofs.

The diagnostic command exited normally, with no remaining owned child process.
Raw local evidence is at
`C:\Users\kevin\AppData\Local\Temp\skillview-windows-diagnose-0ne420wi`.
Only selected summaries are suitable for sharing; private fixture homes/state
and raw diagnostic environment data should stay local.

A fresh focused run of the three
`NativeExposureLinkTests.TargetBackedRoundTripRetainsExactNativeLinkOrExplicitlyRefusesUnsupportedMetadata`
cases failed at the same relative-open access check. Read-only ACL inspection
found the fixture link owned by the current user, with a protected DACL granting
FullControl only to SYSTEM and Administrators. Its parent still had an inherited
OWNER RIGHTS grant. This confirms a fixture access restriction, separately from
the original SACL refusal. Its origin is not established, and it cannot justify
relaxing production permissions or calling the original failures fixed.

Selected results are retained in
[selectedresult.json](../one-off-windows-followup-2026-10-21/selectedresult.json)
and [unitfinalsummary.txt](../one-off-windows-followup-2026-10-21/unitfinalsummary.txt).
The updated Python runner tests passed **10/10 on Linux and 10/10 on native
Windows**, including real directory-alias containment. The alias test now skips
only when Windows actually refuses fixture creation, rather than skipping every
Windows host. No product code, product test assertions, package locks or Windows
security settings changed.
