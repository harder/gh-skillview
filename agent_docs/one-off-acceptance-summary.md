# One-off acceptance summary

PR #86 adds manually invoked testing tools, fixture corrections and startup
documentation. It changes no product source or dependency locks. Personal
reports, machine dumps, command logs and terminal captures are excluded from
the PR and its commit history. This record contains aggregate results only.

## Fixes retained

- The runner uses CI-compatible sequential locked AOT publishes, rejects result
  directories beneath any ancestor `.git` marker, and reports partial or
  interrupted coverage explicitly.
- The collision-guard fixture isolates Git context so a short output budget
  exercises the intended listing rather than an ambient checkout path.
- Windows fixture owner normalization avoids redundant owner writes that can
  remove inherited OWNER RIGHTS access. File, directory and link regressions
  failed with the original helper and passed with the correction.
- Startup documentation explains the inventory-driven transition to Installed.
  Integration tests exercise initial Discover query input at two terminal sizes.
  No product focus change was needed.

## Verification

| Coverage | Result | Limit |
| --- | --- | --- |
| Combined WSL unit suite | 2,698 passed, 30 explicitly skipped, 0 failed | Windows-only test bodies that return early do not establish Windows coverage. |
| Combined WSL integration suite | 11 passed | ANSI-driver tests do not establish native Windows terminal behavior. |
| Python runner smoke tests | 10 passed | Runner contracts, not product acceptance. |
| Combined Release builds and scoped formatting | Passed | No dependency lock changes. |
| Focused macOS collision/startup tests | 6 passed | Focused confirmation rather than a repeated full suite. |
| Settled-startup macOS PTY checks | 2 passed; complete queries and successful Ctrl+Q exits | Early input during delayed inventory was not tested. |
| Windows fixture regressions | 3 passed after failing with the original helper | Focused native verification. |
| Windows metadata test class | 21 passed | Includes refusal paths; does not prove complete SACL publication. |
| Supplemental native AOT CLI proofs | 16 passed on each of Windows and macOS | Original proof matrices were not repeated after test-only corrections. |

## Remaining release acceptance

- Verify positive Windows security-metadata-dependent retention and unpin
  publication in a context that already has `SeSecurityPrivilege`. Restricted
  runs failed those positive assertions; passing refusal paths are not a waiver.
- Complete a native Windows terminal walkthrough for search, install, reviewed
  update, Undo, cleanup and keyboard quit. Process startup alone is insufficient.
- A full Windows suite pass after the fixture correction remains unverified.
  An earlier suite timed out; a later diagnostic run completed with failures,
  and the focused follow-up did not repeat the full suite.

These gaps remain release follow-ups, separate from merging the testing kit.
Required PR checks must pass before merge. Future device results stay outside
Git; retain only sanitized aggregate conclusions and durable regression tests.
