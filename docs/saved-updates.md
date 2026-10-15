# Review and apply a saved update

`update-plan` resolves the latest release (or the default branch when there is
no release) to an exact commit and skill tree. It saves the candidate alongside
the recorded base and shows local changes separately from incoming changes.
Planning leaves the installed payload unchanged.

```sh
skillview --scan-root /path/to/skills update-plan --path /path/to/skills/demo --json
```

Pinned installations require `--unpin`. Review the saved manifest, exact paths,
consumers, changed files, original/candidate directory modes and pin change.
Apply with its plan fingerprint and the explicit disposition to retain the
original directory:

```sh
skillview --scan-root /path/to/skills update-apply --id PLAN_ID --yes --disposition retain-original --expect PLAN_FINGERPRINT
```

The reviewed candidate is promoted directly. Apply does not resolve the source
again. Local edits and extra files remain in the retained original; they are
not merged into the candidate. This is an explicit replacement disposition.

Inspect before recovery or undo, then use that inspection's review fingerprint:

```sh
skillview --scan-root /path/to/skills update-inspect --id PLAN_ID --json
skillview --scan-root /path/to/skills update-recover --id PLAN_ID --yes --expect REVIEW_FINGERPRINT
skillview --scan-root /path/to/skills update-undo --id PLAN_ID --yes --expect REVIEW_FINGERPRINT
```

Use the same scan roots, workspace/profile context and `--allow-hidden-dirs`
setting throughout. Recovery and undo refuse changed payloads, native objects,
consumers or ownership evidence. Retained originals and displaced candidates
remain available; removal is a separate reviewed action.

The current branch supports ownerless remote installations. Shared manager-owned
updates still require the pending coordinated registry transition. Source
switches, renamed skills, local-source reimport and plugin updates need their
own migration/owner workflows. Existing running agent sessions may retain loaded
instructions; filesystem verification does not prove session reload.
