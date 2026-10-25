# Physical discovery contracts

Read these contracts before changing this subsystem. See [the guide index](README.md)
for related workflows and [AGENTS.md](../AGENTS.md) for repository-wide rules.

- Read-only skill and lock metadata uses `RegularFileInspection`, whose verified
  native ABI check is independent of destructive traversal support. Linux hosts
  without `openat2` may inspect regular metadata while removal stays disabled.
  CI's Linux AOT job proves this with both EPERM and ENOSYS seccomp denials.

- Text retained from a second read of a held payload stream must match the
  captured entry's raw content hash before it supports a report or fingerprint.
  Discard partial path and text observations when final native capture fails.

- Hidden directory/link exclusions make inventory coverage partial independently
  of the bounded location table. Skipped regular metadata files do not omit
  skill discovery. Startup TUI capture must use the workflow coordinator so
  workspace/profile options match later rescans.

- Inventory observes physical placements, managed/built-in/plugin payloads,
  ownership and coverage; enabled state and running-session loading remain unknown.
  Do not launch AI clients for discovery. Keep source/version evidence and concise
  conditional reload guidance separate from mutation authority. Static package
  compatibility rules belong in `PackageChecking`; no agent configuration merge,
  permission, trust or invocation simulation is part of inventory.

- Namespace scanning stops at skill payloads and stays bounded to 8 levels,
  8,192 entries, 2,048 enumerated directories and 16 MiB of aggregate metadata
  (+1 overflow observation byte). Charge each successful read even when a later
  read fails. Child container links are not traversed; omitted, inaccessible or
  unsupported coverage remains partial and cannot support orphan conclusions.

- Inventory uses the same captured child CWD and effective HOME/config/state
  values as gh. Retain physical workspace observations alongside Git's actual
  redirected context. Native directory keys and broken-entry parent/name keys
  merge aliases on every supported OS; preserve original paths for policy.

- Inventory scope/agent filters select reconciled physical rows. With an agent
  selected, both predicates must match one exposure; scope alone accepts the copy's
  own scope or any exposure scope. Retain every other consumer and metadata conflict in
  the selected row. Capture the default gh catalog without those filters first.
  Legacy memberships with null scope use their installation's declared scope;
  a known contrary exposure scope never falls back to another consumer.

- Use `PathIdentity` for path normalization, keys, equality, and containment.
  Do not substitute a global Windows/macOS ignore-case rule: Windows supports
  case-sensitive directories and macOS supports case-sensitive volumes. A
  missing direct child must probe its containing directory, not the containing
  directory's name in its parent. Preserve case conservatively when that
  directory is missing, empty, or cannot be probed.

- Keep filesystem inventory aligned with `gh skill`'s configuration-directory
  overrides: Claude uses `CLAUDE_CONFIG_DIR/skills` and Pi uses
  `PI_CODING_AGENT_DIR/skills` (gh 2.99+). Scan the default user root alongside
  either override so older/manual skills remain visible, and use the same Pi
  override when pre-checking install-agent boxes and rendering Doctor.

- `ManagedSkillDiscovery` observes built-ins and plugin payloads without granting
  mutation authority. Keep protection boundaries separate from payload traversal
  scope and provider coverage: lexical/retained alias evidence can deny a write,
  but only canonical containment or an explicit bounded root can authorize reads.
  Preserve both captured canonical and reviewed boundary paths in state exclusions.
  Recheck managed ownership at execution, including removal and state placement;
  registry adoption need not change a payload's native identity. Carry captured
  HOME/CWD/agent overrides through removal validation, cleanup and execution.
  A destination containing managed children permits exact named imports but
  refuses `--all`: a preflight listing cannot bind gh's later mutable target set.
  Isolated staging and disposable proofs must override Codex/Claude plugin homes
  and clear explicit plugin/seed lists alongside HOME. See
  `agent_docs/maintenance-managed-discovery.md`.
  Read-only canonicalization must remain available on verified Unix ABIs when
  Linux `openat2` is unavailable; that never enables removal or relocation.
  Inaccessible ownership comparisons report partial inventory, and cleanup
  skips unknown coverage. An unreadable manifest path is unknown, not evidence
  that an ordinary directory is a plugin.

- Package claims are contextual observations. Do not use the legacy name-map
  lock reader in production or assign a global same-name owner to an unrelated
  installation. Retain all claims; unresolved conflicts must block mutation.

- Read-only ownership matching may index lexical/canonical/entry addresses and
  reuse case/containment observations within one capture. Conservative candidate
  buckets must still use the original contextual matching predicate; retain all
  aliases, conflicts and consumers. Never reuse those path observations for
  destructive admission. Cleanup classification uses uncached coverage/owner
  comparisons; managed discovery for install/removal admission explicitly disables
  capture-local path reuse. Cached and uncached observations use the same
  PathIdentity/OwnershipPath predicates; uncached mode must reobserve every
  lookup. Skip package address work when the index is empty or only legacy
  names can match another row; explicit selectors can describe differently named
  physical aliases and must retain that possibility. Root catalog probes are bounded to four concurrent
  children and reconciled in configured-root order. See
  [inventory performance](inventory-performance.md).

- Installed preserves legacy scope-only filtering: with Agent and Project/location
  both Any, G matches the copy's own scope or any exposure scope, like the CLI.
  Once either membership facet is constrained, Agent + Project/location + Scope
  must match one exposure. Context falls back only to its retained discovery root or
  placement; copies with no exposure offer unknown agent and declared scope/root,
  never invented consumer availability. Source/owner selects any retained claim
  while grouping remains primary source key + name. Unknown/managed/conflicting
  evidence and every other consumer remain on a matching physical copy.
- Inline coverage is frozen from configured/scanned roots and captured limits.
  Name referenced-but-unscanned contexts only from explicit retained owner/link
  evidence; never enumerate unconfigured projects. Filters preserve observed
  denominators and partial coverage, and must perform no I/O or classification.
