# Restore a recorded revision

Use this workflow when an installed GitHub skill has local changes and you want
its recorded revision back. SkillView stages the recorded Git tree through
`gh api`, verifies the Git tree and blob identifiers, and saves a plan. Applying
the plan retains the original directory and promotes the exact reviewed stage.
Source, revision, pin policy and manager records remain unchanged.

```sh
skillview --scan-root /path/to/skills restore-plan --path /path/to/skills/demo --json
```

Repeat the same discovery flags for each command. If hidden agent containers
(such as Codex’s `.system`) make inventory partial, add `--allow-hidden-dirs`
to each restore command. Fresh transition checks always inspect hidden entries
within their bounded configured-root scan.

Review the returned `changedPaths`, root modes, `subject` (source, pin and all
captured consumers), and the complete original/candidate manifests in `planPath`.
A `/` changed path means the root Unix mode differs. The plan binds the original
and staged native objects, file bytes, links, modes and known ownership context.
Planning writes private state and downloads source; it leaves the installation
untouched. It never executes skill code. Staging failure retains private evidence
and reports its location.

```sh
skillview --scan-root /path/to/skills restore-apply --id PLAN_ID \
  --yes --disposition retain-original --expect PLAN_FINGERPRINT --json
```

`--disposition retain-original` explicitly approves replacing local files,
extras, deletions and exclusions with the recorded payload while retaining the
original live directory. Consumers of the physical installation are affected
together. Unrelated same-name copies remain untouched. An occupied destination,
changed payload, changed owner entry, new consumer or incomplete inventory
refuses the operation.

Inspect before undoing or resuming an interrupted operation:

```sh
skillview --scan-root /path/to/skills restore-inspect --id PLAN_ID --json
skillview --scan-root /path/to/skills restore-recover --id PLAN_ID \
  --yes --expect CURRENT_REVIEW_FINGERPRINT --json
skillview --scan-root /path/to/skills restore-undo --id PLAN_ID \
  --yes --expect CURRENT_REVIEW_FINGERPRINT --json
```

Recovery fingerprints bind current locations and native evidence. They differ
from initial plan fingerprints. Undo retains the installed replacement in a
separate `displacedPath`, then returns the original object, including local
extras, `.skillview-ignore`, empty directories, permissions and leaf links.
Modified originals or replacements require inspection; they are never overwritten.
An already completed undo verifies as a no-op. Partial journal files remain
unchanged; recovery appends fresh evidence after reviewing live locations. Forward
admission reserves journal capacity, and an exhausted forward journal has a
separate bounded undo journal.

State defaults beside the existing maintenance backup store. Set
`SKILLVIEW_STATE_HOME` to a private directory on the same filesystem as the
installation. State must remain outside installed/discovery roots and Git working
trees. Retained data is not automatically purged. There are at most 16 plans,
10,000 entries/256 MiB per payload, 32 records per journal and a 16 MiB record
limit. Inspect retained state if a quota is reached. Apply/undo use exclusive
native renames, never a copy/delete fallback. Unix staging flushes file and
directory namespaces before plan publication; Windows staging creates directories
and writes files with write-through and flushes file contents. Hardware and
filesystem durability still depend on their platform guarantees.

This is a recorded-revision repair. Updating to another revision, changing owner
records, local-path reimport, plugin lifecycle operations and Git worktree repair
need their corresponding maintenance workflows. This workflow refuses every directory
inside a Git working tree, including project skill copies. Unsupported schemas, source links,
submodules, invalid metadata and inaccessible sources are explicit refusals.
Missing access is not evidence that upstream was deleted.

Retaining live objects preserves metadata; it does not attest all ACLs, extended
attributes, alternate streams or external hard links, and does not isolate the
retained data from other processes with access. Another process can still edit
through an open handle or hard link. SkillView checks observed evidence before
transitions. POSIX final-name rename races retain the platform's limits. Agent
session/reload state remains unknown after filesystem verification.
