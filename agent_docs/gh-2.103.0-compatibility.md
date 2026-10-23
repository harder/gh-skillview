# GitHub CLI 2.103.0 compatibility review

Reviewed on October 22, 2026. The latest stable release is
[2.103.0, published October 21](https://github.com/cli/cli/releases/tag/v2.103.0).
The previous documented SkillView baseline was 2.102.0; the intervening
[2.102.1 patch release](https://github.com/cli/cli/releases/tag/v2.102.1)
updates Go and `golang.org/x/net` without changing skill commands.

SkillView passes the checks below after correcting its existing versioned
preview arguments. The supported minimum remains **gh 2.97.0**.

## Complete commit review

The [comparison](https://github.com/cli/cli/compare/v2.102.0...v2.103.0)
contains 243 unique commits, including both patch-release commits. Retrieved
all three comparison pages (100/100/43) and each individual commit. Reviewed
every subject and changed-file list, then inspected relevant skill,
authentication, and extension changes and final source. The
[complete ledger](evidence/gh-2.103.0-commit-review.json) retains every commit
SHA, subject, changed-file list, review area, binary checksum, and verification
summary. Empty commits and merges are included.

Both release endpoints have identical skill implementation trees:

| Directory | Git tree SHA at both endpoints |
| --- | --- |
| `internal/skills` | `ebe70235f23e47faac50c7e7c0f64ab877cea084` |
| `pkg/cmd/skills` | `39560bf716d38e83c18a9704a76831c9374934ab` |

This covers discovery, frontmatter, source resolution, installation, lock
records, agent placements, search, preview, list, update, and publish.
Historical merge `a6435ab5` includes search files relative to its older first
parent; their blobs already match 2.102.0. It does not introduce a new shipped
skill change. The ledger records the matching blobs separately.

## Relevant upstream changes

- **Expiring credentials:** 2.103.0 can refresh stored OAuth credentials during
  API requests, `gh auth status`, and `gh auth token`. Environment-supplied
  tokens are not refreshed. Exported tokens must not be cached. SkillView's
  production code already delegates authentication and API access to gh and
  does not export/cache an access token. Added fixtures for expiry details and
  rejected refresh credentials. `auth status` can update credential state;
  it is not a zero-write probe. See the
  [release notes](https://github.com/cli/cli/releases/tag/v2.103.0) and
  [status implementation](https://github.com/cli/cli/blob/v2.103.0/pkg/cmd/auth/status/status.go).
- **Extension startup:** [#14620](https://github.com/cli/cli/pull/14620) avoids
  unnecessary Git processes while identifying official extensions. SkillView
  benefits automatically. Its existing `GH_PATH` selection remains correct.
- **Upstream skill documents:** `skills/gh/SKILL.md` gains issue-artifact and
  authentication guidance and clarifies output modes and retry behavior.
  These are changes to the upstream skill's content. SkillView's existing
  saved update flow can present them. The `.github/skills/cli-exercise`
  shell-quote update and agentic-workflow changes affect upstream repository
  tooling; SkillView does not embed those tools.
- **Agents and flags:** all six skill help outputs are byte-identical across
  gh 2.97.0, 2.102.0, 2.102.1, and 2.103.0. All 48 advertised agents match
  `InstallAgentCatalog` exactly. Corrected the stale documentation count of 52.
  No new flags, placements, or tracking formats require adoption.

## SkillView fix discovered during verification

The actual CLI smoke test
`skillview preview cli/cli skills/gh-skill --version v2.103.0 --json`
failed because SkillView emitted `cli/cli@v2.103.0` as the repository. gh
interpreted that as a repository name and returned HTTP 404. Its unchanged
[preview contract](https://github.com/cli/cli/blob/v2.103.0/pkg/cmd/skills/preview/preview.go)
resolves `@ref` on the skill selector.

The adapter now emits:

```text
gh skill preview -- cli/cli skills/gh-skill@v2.103.0
```

Trusted flags precede `--`; repository and skill selectors follow it. A
versioned preview without a skill selector is rejected as invalid usage.
The existing repository shorthand is normalized even when `--version`
overrides its ref. Live contracts preview `skills/gh` at both 2.102.0 and
2.103.0 and distinguish their content with the new `refresh_expired` marker.

## Verification

Used Ubuntu 26.04 ARM64, SDK 10.0.113, and runtime 10.0.13. Downloaded binaries
were checked against SHA-256 digests from their GitHub release asset metadata.
Fixture HOME, state, CWD, and Git environment are isolated; tests use
`TMPDIR=/var/tmp` to avoid the sandbox's read-only `/tmp/.git` marker.

| Check | Result |
| --- | --- |
| Build (`--no-restore -p:SelfContained=false`) | Passed, zero warnings/errors |
| Formatting verification | Passed |
| Full default suite | 1,996 passed, zero failed/skipped |
| Opt-in contracts, gh 2.97.0 | 19 passed |
| Opt-in contracts, gh 2.102.1 | 19 passed |
| Opt-in contracts, gh 2.103.0 | 19 passed |
| Actual app doctor/search/versioned preview with 2.103.0 | Passed |
| Actual extension launch with 2.103.0 and older gh on PATH | Passed; `GhExtension` invocation and exact `GH_PATH` confirmed |

The dedicated contract runs explicitly set `SKILLVIEW_CONTRACT_TESTS=true`;
their results are separate from default-suite opt-in early returns. Contracts
cover installed metadata, pinning, raw discovery/list reconciliation, owner
locking, recorded-base updates, local-change protection, partial failures,
saved-update UI outcomes, and requested preview revisions.

Checks used managed builds on Linux ARM64. Windows/macOS behavior and new
Native AOT publishes were not exercised here. Actual OAuth credential
rotation was not forced; new auth-status shapes were checked with fixtures
derived from upstream source, alongside real authentication with the current
account. Initial fixture/assertion corrections and the original preview
failure are recorded in the evidence file.
