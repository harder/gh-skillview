# Managed and built-in skill discovery (M10, in progress)

This implementation batch follows PR #58. Managed discovery is implemented in
the working branch and remains under review and final verification; this
document does not claim M10 acceptance or a merge yet.

## Reproduction and source evidence

A disposable native fixture at the reviewed PR #58 source contains an ordinary
user skill, a Codex built-in, and Codex/Claude plugin-cache skills. Inventory
reports the control and a gh-only `.system/builtin-fixture` row, with no owner
records. Both packaged skills are absent, and the hidden system directory makes
filesystem coverage partial. Therefore the gap is classification/protection as
well as discovery; the built-in is not completely invisible.

Current primary references, checked 2026-10-15:

- [OpenAI plugin packaging](https://developers.openai.com/plugins/build/plugins):
  root `plugin.json`, compatibility `.codex-plugin/plugin.json`, contained skill
  resources and versioned plugin caches. Cache presence does not prove enabled
  state; configuration and workspace policy remain separate.
- [Codex skill locations](https://learn.chatgpt.com/docs/build-skills): repository
  ancestors, user/admin locations, bundled system resources and symlinked skills.
  The physical `.system` convention was observed locally, separately from docs.
- [Claude plugin loading](https://code.claude.com/docs/en/plugins/loading):
  declared, fetched and session-loaded states differ. Some synced plugins have
  no install record, so an absent record cannot authorize cache deletion.
- [Claude manifest reference](https://code.claude.com/docs/en/plugins-reference):
  default skills plus declared contained paths, including a single skill folder;
  a manifest is optional for standard-layout Claude plugins.
- [Claude environment variables](https://code.claude.com/docs/en/env-vars):
  `CLAUDE_CONFIG_DIR` includes plugins; `CLAUDE_CODE_PLUGIN_CACHE_DIR` overrides
  the entire plugins root, rather than its `cache` child. Explicit plugin and
  read-only seed directories also need bounded observation.

## Implementation requirements from separate-model design review

Use one bounded managed-owner probe for local inventory, install admission and
`ReplacementPlanStore.ReadOwners`. Preserve a managed-region/exclusion witness
when metadata is malformed, unsupported or omitted by a budget. Discovery must
never turn an unparsed managed region into ownerless writable space.

Distinguish a wholly managed region from one managed payload under an ordinary
skills root. Use canonical containment/overlap for regions and exact physical
addresses for payloads. Protect aliases and missing descendants without blocking
unrelated sibling installs. `GhInstallOwnershipGuard` currently reads only
lockfiles and refuses read-only claims before selector matching; inventory-only
integration would leave direct installs unprotected.

Managed providers need their own revalidation contract. Do not manufacture a
lockfile path: existing validation assumes it can reread one. Capture the child
runner's CWD and environment, retain evidence across source discovery, and probe
again before mutation. The existing install guard's fallback to process CWD
should become `runner.EffectiveWorkingDirectory`.

Scan explicit single-skill folders as candidates, rather than visiting their
resource children. Scan only recognized/default or declared skill structures;
manifest-only resources and absent declarations remain location diagnostics.
Keep entry/root/depth/metadata bounds, cancellation and regular-file inspection.
No arbitrary home crawl or agent/plugin code execution.

Suppress a parent's hidden exclusion only when that exact subtree was completely
observed. Preserve partial coverage for other hidden siblings, linked containers,
inaccessible paths and exhausted budgets. Include entire managed containers in
maintenance-state exclusions, even when they contain no parsed skill.

Keep authored source/ref, distributor identity/version, installation context,
cache presence and active loading separate. Enabled and active state stay unknown
without evidence. Unsupported external install paths require an explicit root
rather than unrestricted traversal.

Adding discovery roots must not strand previously saved schema-2 recovery plans.
The existing replacement context hash includes configured roots. Preserve its
historical contract while independently revalidating newly observed owners,
consumers and managed-region/state exclusions. Cover this with a persisted plan
created before managed discovery is enabled.

## Acceptance coverage

- Built-ins, default/alternate agent homes, cache plugins and two versions with
  one authored name retain physical identity and exact ownership context.
- Default/declared skill containers, namespaces and direct skill folders avoid
  malformed resource rows; unsupported metadata stays visible and partial.
- Root and payload aliases, path escapes, metadata replacement, omitted budgets
  and cancellation cannot remove protection or manufacture orphan conclusions.
- CLI/detail/Doctor show managed provenance without claiming active loading.
- Remove, cleanup, update, install overwrite, quarantine and saved-plan recovery
  respect managed ownership. Unrelated ordinary skills remain operable.
- Disposable native fixtures on all four RIDs prove observable managed resources,
  zero mutation on refusal, and compatibility of pre-existing saved plans.

M10 remains incomplete until implementation, separate review, final verification
and merge. Plugin lifecycle mutations and effective-loading analysis remain their
own original findings.

## Review corrections implemented

The shared probe retains canonical protection boundaries independently of payload
coverage, parses supported Claude v2 registry contexts, limits external reads to
explicit physical subtrees, and preserves both agent-specific manifest conventions.
The v2 registry shape was checked against the local installed registry; unknown
schemas stay partial and cannot authorize writes. Registry presence is not evidence
of enabled or session-loaded state.

Install admission checks the original and latest observations, canonical ancestor
aliases, exact selected children and namespaces containing managed plugins.
`--all` is refused beside managed resources because its final mutable set cannot
be authorized by a separate listing; unrelated exact named local imports remain
available. Removal carries frozen agent context through native execution and
rechecks external adoption. Backup, quarantine and replacement check live state
ownership immediately before writable opens, as well as retaining prior exclusions.
Saved replacement context hashes keep their historical configured-root contract.

Disposable proof environments and C# staging explicitly isolate every newly read
managed-directory override. Doctor can include inventory with `--inventory`;
the TUI uses an already captured Installed snapshot without adding UI-thread I/O.
Final tests, native RID proofs, persisted-plan compatibility and merge evidence
are still required before updating the original acceptance ledger.


## Native verification commands

Publish both product hosts sequentially. Run `prove-managed-discovery.py` with
`--app /absolute/native/host`, repeat `--gh /absolute/gh` for minimum/current,
and supply `--output /tmp/evidence.json`. The private offline fixture verifies
built-ins, two package versions, managed ownership and nonempty exposures,
Doctor inventory, unchanged protected native objects/bytes/modes on refusals,
and a positive unrelated exact named import. CI runs both hosts on all four RIDs.

For historical compatibility, run `prove-legacy-owner-compatibility.py` with an
actual prior native host through `--legacy`, the new `--app`,
`--managed-discovery-fixtures` and an output path. It saves the plan before adding
managed roots, then inspects, interrupts old Apply, recovers and undoes using the
new host. This network fixture requires gh authentication captured before HOME
isolation; the skill is never executed. Both local native hosts pass.
