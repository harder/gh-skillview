# Contextual ownership CLI proof fixtures

`ContextualOwnershipCliTests` exercises actual filesystem discovery,
reconciliation, CLI dispatch/JSON and removal refusal. It does not invoke gh,
another package manager, Git, an AI client or a payload script. Its fixture owns
HOME/USERPROFILE, XDG/Windows/config/plugin/state directories and CWD. Every
inherited `GIT_*` value is neutralized before explicit private Git context values
are applied. Empty child PATH prevents Git discovery; the test also supplies a
no-result `GhBinaryLocator` because that locator normally reads parent PATH and
GH_PATH independently of the child runner. Console capture uses the shared gate.

Ownership coverage must retain these distinctions:

| Original clause | Fixture evidence |
| --- | --- |
| Same name in user and two projects with three sources | `ThreeSameNameSourcesKeepExactUserAndProjectOwnersInActualCli` runs with home-global v3 and XDG-global v3. It checks three physical installation IDs, exact paths, sources, owner records/provider/context, user/project exposures, distinct source IDs, no metadata conflicts, and ambiguous-name CLI refusal. |
| Current global/project/XDG locations | The same fixtures discover real home `.agents/.skill-lock.json`, explicit XDG `skills/.skill-lock.json` and project-root `skills-lock.json` files through inventory. They never attach a home record to the unrelated project copy. |
| Legacy version1/2 fields and unknown versions | `LegacyRootParentDeclaredVersionsRetainOnlyPresentFieldsAndCannotAuthorizeRemoval` exercises SkillView's existing legacy-root version1/2 reader contract with only common present source fields. `UnsupportedDeclaredVersionsRemainReadOnlyDiagnosticsInCli` checks older v1/v2 at current global/XDG locations and unsupported project/unknown v99 declarations. No absent hash/ref/path/mode or repository commit is fabricated. |
| Package/repository group does not include an unrelated same-name copy | `PackageAndRepositoryGroupsFromInventoryExcludeSameNameOtherSourcesAndRefuseDeletion` derives both distinct groups from reconciled inventory. A same-package sibling joins the package group, a different-provider repository peer joins only the repository group, and the other two `demo` sources join neither. Every selected owned target remains refused. |
| Conflict retention and read-only unknowns | `ConflictingGlobalAndXdgClaimsSurviveCliAndBothRemovalModes` retains both raw owner observations, marks source identity conflicted and leaves the ID unavailable. Unsupported versions retain per-row and provider diagnostics. Preview/execution refusals retain the exact installation subject, zero deletions and zero duplicated runtime errors. |

The declared-version inputs are deterministic compatibility fixtures, not
receipts from executing historical native managers. Their shared source fields
are observations only; the fixtures do not assign a modern hash domain to an old
field. The pinned Vercel source used by the original contextual-reader work
declares current global schema3 and resets older schemas on native reads:
[`skill-lock.ts` at 13e4063](https://github.com/vercel-labs/skills/blob/13e4063a1cf913f5606d57d42ab83a86f5001e04/src/skill-lock.ts).
Accordingly, older version declarations in the current global/XDG providers are
diagnosed read-only by SkillView; an advertised legacy-root reader branch remains
separate. Do not equate saved maintenance-plan schema1/2/3 compatibility with
these lockfile schema versions.

Each fixture hashes every payload/lock file and re-enumerates the file set after
its operations. This proves final regular-file paths and bytes are identical;
it does not attest native metadata, directory identities or transient writes.
The fixture separately requires that the configured maintenance-state directory
is absent and no subprocess is admitted. Its child context isolates the listed
homes and CWD; it does not replace the test host's temp environment. Validation
can supply a private TMPDIR/TMP/TEMP for the test host. The intentionally
unavailable gh provider remains explicit in list JSON; this is not a claim of
complete native-manager inventory or a native AOT proof. Tests must execute
rather than return early on a missing manager.

Run with SDK10.0.402 after a scoped locked restore:

```sh
dotnet test --project tests/SkillView.Tests/SkillView.Tests.csproj \
  --configuration Release --no-build \
  --filter-class SkillView.Tests.Inventory.ContextualOwnershipCliTests
```

These are proof additions only. Owner-aware removal/coordinated ownership
migration, native plugin lifecycles, final platform/native verification and the
broader native-owner lifecycle behavior are not established by these fixtures.
Keep the production owner refusal unless an approved transition proves authority.
