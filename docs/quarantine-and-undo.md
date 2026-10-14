# Quarantine and undo

SkillView can temporarily relocate one standalone skill directory, then return
that same directory to its original location. This first workflow is CLI-only.
Use it for an independently installed copy under an explicit custom scan root.

Preview with global discovery flags before the command:

```sh
skillview --scan-root /path/to/custom-skills quarantine --path /path/to/custom-skills/demo --json
```

Review the original path and `reviewFingerprint`. Apply the reviewed observation:

```sh
skillview --scan-root /path/to/custom-skills quarantine --path /path/to/custom-skills/demo \
  --yes --standalone --expect <reviewFingerprint> --json
```

`--standalone` confirms this copy has no additional installation manager. A changed
payload, root, parent or configured discovery context requires a new preview.
Known ownership records, tracking/pins, shared consumers, repository metadata,
agent-managed placements and incomplete inventory are refused. A directory named
`skills` under the current project is a project placement; it is not a standalone
custom placement merely because `--scan-root` also names it.

The successful result contains an operation `id`. Keep the same discovery flags
when previewing and performing undo:

```sh
skillview --scan-root /path/to/custom-skills undo --id <operation-id> --json
skillview --scan-root /path/to/custom-skills undo --id <operation-id> --yes --json
skillview --scan-root /path/to/custom-skills quarantine-history --json
```

Undo checks the retained native directory and payload, original root/parent
identity and generation, discovery context, and vacancy of the original path.
It refuses a replacement at that path. Discovered installations and exposures
that overlap either subtree are refused, including dangling agent links whose
relative or aliased targets would become active when the original path returns.
A link component followed by `..` is treated as uncertain and requires inspection. An already completed undo verifies the
restored object and returns success without another move.

State is stored under `SkillView/quarantine` in the platform state directory, or
under `SKILLVIEW_STATE_HOME` when set. State must be outside Git working trees,
all configured skill roots and all known installed payloads. Source and state
must share a filesystem/volume. At most 64 operation directories and a bounded
aggregate payload are admitted; there is no automatic purge in this release.

Each relocation uses an exclusive native directory rename. It preserves the
live directory, file bytes, permissions, links, empty directories and attached
filesystem metadata; it does not extract an archive or copy/delete the tree.
This is **not security isolation**. Existing handles/hard links and existing
agent sessions can still use content. On Windows, retained descendant ACLs can
permit access through a private parent. SkillView does not observe agent session
state or claim that an already loaded skill has been unloaded.

The prepared journal is durable before the move; completion is journaled after
object/payload verification. On Unix, both changed parent directories are flushed.
Windows namespace durability is not separately attested. Interrupted or failed
operations retain their data and explicit journals for inspection. An
`undo-prepared` operation is not automatically retried, including cancellation
immediately after preparing but before moving. SkillView cannot infer whether an
interrupted rename ran. Do not delete the retained data or journals to clear an
error. History reports recorded states; undo preview checks live evidence.

Broader owner-aware updates, consolidation, manager-record repair, generic
plan/apply and automated recovery of ambiguous operations remain follow-up work.
