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

The saved workflow supports remote installations without an owner and shared
installations tracked by one verified gh global registry entry. Shared consumers
keep their existing links. Apply and undo hold gh's data-file lock through the
payload transition and registry publication. Only the selected entry changes;
unknown extensions, installation timestamps and fresh unrelated records survive.
Undo restores the selected entry into the current registry, preserving other
manager changes made since apply. Competing gh writes are refused while the
transaction holds the lock.

If a killed process leaves malformed registry bytes, inspection can display a
retained full repair image: its hash, byte count, selected-entry state and causal
write ID. Review the corresponding intent in the plan's `owner-journal` directory.
Then authorize that exact inspection:

```sh
skillview --scan-root /path/to/skills update-repair-owner --id PLAN_ID --yes --expect REVIEW_FINGERPRINT
```

This repairs only the registry. Inspect again before recovery or undo. A valid
registry is never replaced wholesale: selected-entry updates preserve fresh
unrelated bytes. Torn repair requires an unfinished write bound to this plan,
the same native file object, attributable write-prefix evidence and an unchanged
reviewed snapshot. A short or empty prefix does not prove which process changed
the file; the explicit approval authorizes restoration of the displayed image.
Foreign bytes, unsupported schemas, completed writes, ambiguous later writes and
unrelated inventory failures refuse repair. Partially published journal records
remain untouched and count against bounded recovery allowances.

Source switches, renamed skills, local-source reimport and plugin updates need
their own migration/owner workflows. Existing running agent sessions may retain
loaded instructions; filesystem verification does not prove session reload.
