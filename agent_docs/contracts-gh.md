# GitHub CLI and ownership contracts

Read these contracts before changing this subsystem. See [the guide index](README.md)
for related workflows and [AGENTS.md](../AGENTS.md) for repository-wide rules.

- `install --force` is admitted only for positively missing or empty native
  destination roots. Current source selectors do not prove native placement
  names, so even an unrelated occupied root requires a qualified refusal. Keep
  the initial and final vacancy checks around discovery/ownership checks;
  existing payloads need an evidence-backed saved disposition and retention.
  Vacancy grants ordinary install authority only: remove `--force` from the
  effective child argv after admission, retaining requested intent separately.
  Unknown provenance never implies that a saved repair is available. See
  `agent_docs/maintenance-force-install-disposition.md`.

- `gh skill list` uses `sourceURL` for both repository URLs and local paths.
  Classify native absolute paths before URI parsing and preserve the exact
  scanned `local-path`; retain conflicting/unknown catalog and legacy evidence.
  Never turn a local catalog source into GitHub tracking. Native payload-entry
  digests and saved-plan fingerprints use separate encodings; keep their hash
  domains unchanged when comparing a materialized candidate.

- `GhPlacementCatalog` is the authority for gh placement paths. Filesystem
  discovery and UI/Doctor detection derive from it, retaining explicitly
  documented native/legacy paths separately. Shared `.agents` placements are
  not evidence that every compatible agent is installed or enabled.

- SkillView requires `gh` 2.97.0 or newer (`GhBinaryLocator.MinimumVersion`);
  that version guarantees the full `gh skill` surface SkillView relies on,
  including its current Devin and Grok agent selectors, so
  there is no per-flag capability probe — only a single `gh skill --help`
  smoke check.

- `GhSkillInstallService.BuildArgs` and `BuildListArgs` must place trusted
  flags before `--`, with repository and skill selectors after it. Search
  results can supply those selectors; GitHub CLI 2.102.0 fixed an option
  injection in its own interactive `gh skill search` install path. `--pin`
  takes a ref value, so emit `--pin=<ref>` and require a version when pinning;
  a bare `--pin` could consume the option separator. Keep SkillView's boundary
  even while supporting older `gh` releases.
  `--upstream` is a boolean flag (use a checkbox in the TUI); it does not
  accept a URL value, which would become an unintended positional argument.
  A custom `--dir` overrides scope. Do not emit `--scope` when `Options.Path`
  is set: SkillView's "custom" UI scope is not a valid gh scope value;
  gh accepts only `project` and `user`.
  A version suffix belongs on a skill selector, never the repository name;
  use `--pin=<ref>` for an entire-repository ref, including discovery listing.
  Preview also uses `repo skill@ref`, with trusted flags before `--` and both
  selectors after it. A versioned preview requires a skill selector; there is
  no repository-wide preview ref flag. Keep the two-revision live preview
  contract so argument-builder fixtures cannot bless an invalid gh command.

- When `gh` 2.101.0+ launches SkillView as an extension, prefer its `GH_PATH`
  environment value over PATH discovery so subprocesses use the same CLI host.
  Require `GH_EXTENSION=1` and an existing absolute path; older hosts and the
  standalone app continue to resolve through PATH.

- GitHub CLI 2.103.0 can refresh expiring credentials during `auth status` and
  API requests. Keep authentication delegated to gh; never cache an exported
  access token in production or describe `auth status` as zero-write. The
  compatibility review and complete commit ledger are in
  `agent_docs/gh-2.103.0-compatibility.md`.
  The seven-commit 2.103.1 patch review and executed minimum/latest checks are
  in `agent_docs/gh-2.103.1-compatibility.md`; install review output changes do
  not waive SkillView source/retention approval.

- `InstallAgentCatalog` tracks the full `gh skill install --help` `--agent`
  list verified through `gh` 2.103.1 (48 entries; gh 2.97.0 replaced `windsurf`
  with `devin` and added `grok`, cli/cli#13987 and cli/cli#13864; gh 2.99.0
  makes Pi's `PI_CODING_AGENT_DIR` override authoritative).
  `HomeRelativePath` is best-effort and
  nullable — only set when the on-disk config-dir convention was
  independently verified; a null path just means that agent is never
  pre-checked or shown in the Doctor "detected agents" table (cosmetic only,
  never gates the actual install call). Re-diff against
  `gh skill install --help` and update the catalog when bumping the `gh`
  minimum again. The agent checkboxes in `InstallScreen`, `InstallConfirmModal`,
  and `RepoSkillPickerModal` render via the shared `AgentCheckboxGrid` helper
  inside a scrollable `View` (`ViewportSettingsFlags.HasVerticalScrollBar`) —
  don't revert to a fixed-row/fixed-height layout, the catalog is too long to
  fit on screen unscrolled.

- Install flows default to `--agent universal` (not blank) when the user
  hasn't explicitly checked any agent checkboxes — `InstallConfirmModal.
  DefaultAgents`, shared by `InstallScreen` and `RepoSkillPickerModal` (via
  `BuildOptionsFromSelection`). Verified against live `gh` 2.96.0: a blank
  `--agent` at user scope installs to gh's default (`~/.copilot/skills`),
  while `--agent universal --scope user` installs to the shared, agent-
  agnostic `~/.agents/skills`. Skipped when a custom `--dir` path is set
  (`--dir` overrides `--agent` entirely). `ScanRootResolver.UserSeeds` has a
  matching `.agents/skills` entry so the inventory scan actually discovers
  skills installed there — keep both in sync if this default ever changes.

- Explicit `StdOutTruncated` inventory captures cannot establish coverage or
  enter the cache, even when their retained text parses as JSON. Keep full-capture
  failure/empty/root-coverage tests and the native POSIX denied-root proof.

- A successful `gh skill list` process result is cacheable only when its JSON
  parses as the expected inventory schema. Preserve the distinction between a
  valid empty `[]` payload and malformed, truncated, or schema-incompatible
  output, which must remain retryable. The canonical/legacy skill-name field
  is required to be a nonblank JSON string; do not coerce numbers into names.

- gh `skill install --dir` also writes the global interoperability lockfile.
  Comparison staging and mutating gh contract fixtures must isolate child
  HOME/USERPROFILE plus XDG/Windows state. Preserve original gh authentication
  config resolution explicitly; Windows defaults to `APPDATA/GitHub CLI`, and
  XDG_CONFIG_HOME has precedence there too. Never mutate the parent environment.
  Shared gh/Vercel schema-3 lock entries are source evidence, not exclusive owners.

- Owner mutation policy must inspect every `AllOwnershipClaims` entry and
  `OwnershipDiagnostics`, never only the compatibility `Package` projection.
  Diagnostic, entry, root and file bounds must retain global incomplete coverage
  when evidence is omitted; exhausting a reporting budget cannot authorize work.

- gh's global registry is keyed by `InstallName` (possibly `namespace/name`),
  while its installer materializes flat `skill.Name` directories. Resolve actual
  remote selectors before collision checks; unresolved paths, ambiguous names,
  truncated capture or incomplete tabular discovery cannot authorize a write.
  Keep explicit truncation facts on `ProcessResult` instead of parsing a marker.

- Project/custom updates with explicit `--dir` isolate incidental global writes
  in a separate private mutation home. Shared-directory updates must keep their
  original registry context, including redirected roots and namespaced records,
  and match the current source/subpath/tree/pin before launching gh. Every native
  registry writer needs a schema/extension guard immediately before launch.
  This guard is preflight protection; coordinated journals in the maintenance
  plan layer must provide the stronger multi-writer preservation contract.

- Install admission must retain the original destination owner records across
  discovery awaits, revalidate them after the final placement probe, and inspect
  canonical contexts alongside lexical aliases. Normalize relative child HOME,
  XDG and agent config paths against the captured child working directory.
  Directory canonicalization cannot canonicalize a regular lockfile: deduplicate
  those through its canonical parent plus final name. Broken exposure links also
  need parent/name matching so aliases cannot hide contextual ownership.

- Shared update admission uses entry fingerprints so an earlier target's update
  does not invalidate an unrelated target. gh's typed lock writer adds required
  empty strings and removes optional empty strings; normalize only those known
  equivalent representations. Keep whole-file fingerprints for discovery-time
  install admission and migration review. Repository descriptions can contain
  unescaped newlines: stage a recorded source path into an empty private root,
  inspect exactly one materialized skill, and verify its path/tree instead of
  relaxing the listing's write-authority checks.

- Local inventory/comparison must suppress gh's update notifiers and telemetry
  through child-only `GH_NO_UPDATE_NOTIFIER`, `GH_NO_EXTENSION_UPDATE_NOTIFIER`
  and `GH_TELEMETRY=false`. A successful local command can otherwise spawn
  `send-telemetry`; include that child in offline trace assertions.

- Raw tracking compatibility must be tested before gh catalog enrichment: merged
  rows can acquire source/ref fields from gh list even if the parser regresses.
  Use `GhMetadataContractTests` for actual remote/pinned/local installs, including
  Installed details, and `prove-tracking-roundtrip.py` for native CLI parity and
  unchanged payload/registry evidence. Default-suite early returns are not proof
  that opt-in gh contracts executed.

- To test updates from deliberately fragmented same-selector installs, construct
  the starting copies with raw gh inside a private fixture. Product install
  admission correctly refuses a second global-selector overwrite. Capture gh
  credentials before changing fixture HOME; keychain-backed lookup afterward
  can become unauthenticated. Preserve all update/local-change assertions.

- Named Discover installs carry the explicit repository and search-result source
  path separately from gh selectors. Compact success navigation retains only
  transient source/placement/context observations from final admission, excluded
  from JSON. Never infer source subpaths or materialized leaves from authored
  names, `RepoSkill`, `ResolveWriteSelectors`, or install stdout. Conservative
  legacy/override destination alternatives do not verify which folder gh used.
  After installation, the coordinator derives each requested agent's folder set
  with single-agent GhPlacementCatalog calls under the captured context. Require
  their union to equal the admitted flat folders and recheck context/folders
  around capture. These lookups never enter the final-admission-to-launch gap;
  User-scope lookups launch no children. This evidence grants browsing only;
  later mutations require fresh admission.

- Install outcomes retain typed attempt accounting independently of success and
  exit code. Only preflight refusal, canceled admission and a proven start
  failure establish no mutating child. Old result constructors default to
  unknown; transport/lifecycle faults never imply unchanged files. Retain both
  streams on failure/cancellation. Install execution retains the bounded
  parent-exit grace and independent bounded output retention. Cancel both initial
  pipe readers on cancellation or retained transport failure before observing
  them. A started child can return ChildDrained=false when exit is unconfirmed;
  never extend cancellation/shutdown with an unlimited wait or a reaper.
  Invalidate observations without inventory capture and require manual refresh
  after that child stops. Ordinary RunAsync exception semantics stay compatible. This is observation only;
  all placement, ownership, native and final-launch admission checks remain.
- Subset install accounting retains each child result and the exact unsubmitted
  suffix. Stop after the first failed/canceled child; never retry previous
  successes or widen to --all. One --all result is a repository-wide child
  outcome, not per-skill success evidence. The low-level update coordinator
  retains its private staging install attempts separately from mutation of the
  user's installation. Staging cleanup still uses its owned native lease, but
  an unverified staging writer retains the private directory and reports its
  location rather than cleaning up while that writer may still run.
- CLI install avoids an eager before-inventory capture. After a possible write,
  invalidate and, only after verified child exit, take one uncached capture under
  an independent bounded deadline even after cancellation/failure. JSON exposes attempt, streams, refresh status
  and observed entries. Without a before snapshot, encode added:null and
  addedKnown:false, including on success; an empty array would falsely claim no
  additions. JSON clients must migrate to that explicit unknown representation.
  Never call every observed installation newly added. Text clean success keeps
  the selected repo/skill/version confirmation and complete observation count;
  warning/retry guidance belongs to failed, canceled or unverified outcomes.

- Install placement review is read-only guidance under the install service's frozen
  child context. Use `GhPlacementCatalog` and the existing non-interactive repository
  listing for flat materialization names; those names never establish source
  subpaths. Preserve conservative legacy/override alternatives and unknown ALL or
  upstream target sets. Review's optional typed folder/context binding only rejects
  stale admission; all existing shared-selector, managed and owner checks still run.
- Discover paging uses `GhSkillSearchService.Options.Page`. Previous/Next reuse the
  submitted query/owner/agent/hidden/cap tuple, never current unsubmitted text. An
  empty raw page disables Next, not Previous; a client cap or agent-filtered count
  cannot establish a total or a last page. The JSON `path` field is the only search
  source-subpath evidence; a missing, empty or untrustworthy path stays unknown (explicit `SKILL.md` or `.` can identify a root skill). The tabular
  install listing contains selectors/descriptions, not source paths, and cannot
  fill this adapter evidence gap from an authored name.
