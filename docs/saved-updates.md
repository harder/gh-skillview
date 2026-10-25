# Review and apply a saved update

Ordinary `update` prepares a saved review when needed and leaves installed payloads unchanged:

```sh
skillview --scan-root /path/to/skills update --path /path/to/skills/demo --json
```

Its JSON includes `savedPlan` and exact `applyArguments`. Retain the same HOME,
state, working directory and global scan/workspace/profile options for apply.
`--force` does not authorize replacement or a disposition of local edits.
With several selected installations, one candidate is prepared; other targets
are explicitly `not-started`. Prepare each next target after applying or skipping
the current review. Applying a sibling changes captured directory generations.

To choose a specific version for one installed copy, select **Choose version...**
in Updates and enter its Git tag (for example, `v1.2.0`). This uses the current row,
independently of marks or All. The equivalent CLI preparation is:

```sh
skillview --scan-root /path/to/skills update-plan --path /path/to/skills/demo --tag v1.2.0 --json
```

The review shows the exact resolved tag, commit, tree, local changes and affected
consumers. Every agent sharing that physical copy sees the change; independent
copies remain separate. Review the saved source and approve retaining the original
before applying through the existing commands below. Canceling review leaves the
installed copy unchanged. A missing or invalid tag refuses; it never selects latest
instead. Apply uses the saved candidate even if the remote tag subsequently moves.

Version selection is a one-time change, including a deliberate downgrade. It
creates no pin; a later ordinary Update follows latest. For a pinned installation,
complete the separately reviewed unpin operation first. `--tag` cannot be combined
with `--unpin`. Branches and raw commits are not selected by this action; a tag
named like a commit is still resolved only as a tag. New pin policy, automatic tag
browsing and persistent version approvals are outside this flow.

Installed's **Review update (u / Enter)** and an Issues **Updates** finding open
one exact physical copy and prepare its saved candidate immediately. Copies'
**Update / version...** opens options for its already selected copy, so choosing
a tag does not first stage latest. The operation surface has no inventory table,
marks, All selection or availability check. Main-view **Check updates (C)** is
read-only and consumes no saved-plan quota.

Each candidate has its own source acknowledgement, retention checkbox and
**Replace reviewed** action. **Skip review** retains an unapplied plan without
starting another copy. Choose another copy in Installed or Issues.
**Preserve installed** verifies the current payload and owner without applying or
recording a maintenance preference. Local edits and source files remain available
for comparison in Details. To keep local files, choose **Preserve installed** or
**Skip review**. To replace them, review the candidate and approve retention of
the original directory. New local merge creation and the built-in editor have
been removed.

Preparation, apply and undo diagnostics for this copy remain available in Details.
Backend/CLI batch callers still prepare each sibling only after the prior
operation reconciles.
Select **Include hidden directories** (CLI `--allow-hidden-dirs`) when hidden
containers would otherwise make inventory incomplete. Main-view availability
checks do not create plans or consume the retained-plan quota.

`update-plan` resolves the latest release (or the default branch when there is
no release) to an exact commit and skill tree. It saves the candidate alongside
the recorded base and shows local changes separately from incoming changes.
Planning leaves the installed payload unchanged.

If the installation is already current, ordinary `update` reports `unchanged`
with `savedPlan: null`, zero changed files and `mutationAttempted: false`.
`update-plan --json` returns `operation: null` and an `unchanged` object instead
of a plan. Current checks preserve installed objects and registry bytes and do
not consume the retained-plan quota. A private staging cleanup failure reports
the exact location and a failed exit so it can be inspected.

For the same immutable revision, YAML mapping order, string presentation and
gh's frontmatter delimiter newlines can compare as current without rewriting
those bytes. Meaningful content, sequence order, file, permission, pin and owner
changes still need review. Ambiguous YAML remains a review rather than being
assumed equivalent. New upstream revisions do not use this presentation check.

At most 16 plan directories are retained. Failed private staging also remains as
evidence. When the quota is reached, planning refuses and asks for inspection;
it never automatically removes local originals or candidates to make room.

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

To keep the installed payload instead, verify the never-started plan with
`update-preserve --id PLAN_ID --expect PLAN_FINGERPRINT`. This leaves payloads,
owner records and preferences unchanged; the unused plan stays available.

Existing schema-3 merge plans remain supported. Use `update-inspect` to inspect
the exact saved candidate, disposition and source baseline. If native metadata
and all saved evidence still validate, apply the existing plan with its immutable
fingerprint and the explicit `merge-reviewed` disposition:

```sh
skillview --scan-root /path/to/skills update-apply --id PLAN_ID --yes --disposition merge-reviewed --expect PLAN_FINGERPRINT
```

This does not create or edit a merge. Saved B/I/C archives, custom-resolution
blobs, choices, result hashes and metadata policies remain part of validation.
The original source C stays the future comparison baseline; customized result M
has separate evidence. Unknown or unsupported metadata still refuses application.
A partial historical review cannot reopen its earlier replacement approval;
retain its evidence and prepare a new ordinary revision plan when appropriate.

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

## Frozen source review

New source-changing plans show captured instruction/script/configuration diffs,
raw type/mode/hash summaries and bounded literal declaration deltas before apply.
Review omissions through the complete captured manifests and raw blobs named in
the report. A literal destination is an observation, not a connection; partial
old coverage cannot prove a newly introduced dependency or destination.

After inspection, explicitly acknowledge that exact source review separately:

```text
skillview update-ack-source --id ID --expect PLAN_FINGERPRINT --review SOURCE_REVIEW_FINGERPRINT --yes
```

Then use the existing apply command with its retention approval. Local reimport
uses `reimport-ack-source`; recorded restoration uses `restore-ack-source`. The
Updates tab and local reimport dialog offer a separate source-review checkbox.
Acknowledgement binds the saved plan, raw manifests, payloads and context. It is
your declaration of review, not authenticity, compatibility or behavioral safety
assurance. Format, agent compatibility and payload integrity remain independent.
Displaying a check or diff never acknowledges review. Legacy saved plans remain
explicitly unreviewed under their original admission rules; prepare a new product
plan to get frozen review evidence. Pin-only unpin has no upstream source changes.

Forward recovery of a new contract also needs its matching receipt; undo uses its
existing fresh physical inspection approval. See the
[source-review contract](../agent_docs/maintenance-update-source-review.md) for
bounds, archive access, legacy compatibility and the remaining M19 work.
