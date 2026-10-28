# Native filesystem contracts

Read these contracts before changing this subsystem. See [the guide index](README.md)
for related workflows and [AGENTS.md](../AGENTS.md) for repository-wide rules.

- Removal traversal must never use recursive filesystem enumeration. Treat
  every `FileAttributes.ReparsePoint` child (Unix symlink, Windows junction,
  mount point, or broken link) as a leaf, revalidate containment immediately
  before deletion, and keep cancellation/depth bounds explicit. Keep traversal
  lazy with per-depth enumerator frames so cancellation runs between entries
  and retained state stays O(depth). TUI removal must use `RemoveAsync` /
  `RemoveManyAsync`, whose progress is throttled to 10 updates/second; keep the
  modal alive until cancellation quiesces and rescan when cancellation made
  any partial filesystem change. The advanced remove confirmation is bound to
  the exact captured directory/link identities and execution mode the user
  reviewed; finish-time validation must return to Review if any of those
  values changed, even when paths and policy messages are identical. Runtime
  reports retain at most 128 individual
  errors plus an omission summary while preserving the exact error count. Do
  not overwrite a mid-target cancellation snapshot with completed-target-only
  totals: the batch progress adapter owns the latest aggregate state and keeps
  processed-target and successfully-deleted-target counts distinct. Retryable
  remove dialogs accumulate filesystem mutation totals across attempts and
  compact-to-wizard escalation so later failures cannot hide a required rescan.
  Every async removal entry point must publish a terminal canceled update even
  when its token was already canceled; synthetic cancellation reports retain
  the exact observed runtime-error count and mark cancellation explicitly.
  All non-canceled return paths, including validation refusals, must publish a
  terminal completed progress update with exact processed/deleted/error counts.
  CLI JSON must not duplicate validation refusals or unaccepted warnings into
  `runtimeErrors`. Real removals on supported platforms must stay on
  `SecureRemovalBackend`: validation pins the target's native volume/device and
  full file/inode identity, then re-runs containment and all other policy checks
  against that captured canonical deletion address. Root containment is one
  native authority boundary: open and pin the matched scan root first, open the
  selected directory or link parent relative to that held root, and derive both
  canonical addresses before verifying the root generation. Never canonicalize
  target and root through separate opens. Canonical addresses must
  come from the opened object (`GetFinalPathNameByHandleW` on Windows,
  `fgetattrlist(ATTR_CMN_FULLPATH)` on macOS, and `/proc/thread-self/fd` on Linux),
  never only from lexical normalization; canonicalize scan roots through the
  same handle-based path before comparing them. Linux does not escape its
  ` (deleted)` `/proc/thread-self/fd` annotation, so a live name can legitimately end
  with that text; disambiguate it by comparing the named path's full native
  identity and generation with the still-open descriptor, never by suffix
  rejection alone. Non-destructive Unix scan-root canonicalization must open
  the `realpath` result directly so filesystem root `/` remains valid;
  destructive helpers must continue refusing filesystem-root targets. Windows
  uses `FileIdInfo`
  and `FileIdExtdDirectoryInfo` so ReFS's full 128-bit IDs are compared, and
  pairs them with `FILE_BASIC_INFO.CreationTime`/`ChangeTime` for selected
  directories plus validated parents and links so immediate ID reuse is
  refused. It enumerates opened directory handles, opens every enumerated child and link
  relative to its held parent with `NtOpenFile`/`OBJECT_ATTRIBUTES.RootDirectory`,
  and deletes opened objects with
  `FileDispositionInfoEx` (falling back for `ERROR_NOT_SUPPORTED` as well as
  invalid-function/parameter). Treat that legacy fallback as a separate
  destructive boundary and recheck cancellation immediately before it;
  `FILE_DISPOSITION_INFO.DeleteFile` is a one-byte Win32 `BOOLEAN`, not the
  four-byte `BOOL` used by the API return value. Unix walks canonicalized paths
  through `openat`, enumerates opened directory descriptors, compares
  device/inode identities, uses Linux `openat2(RESOLVE_NO_XDEV)` to reject bind
  and filesystem mounts, refuses device changes on macOS, and deletes with
  `unlinkat`. Apply that mount boundary while validating the selected directory
  and every link-parent component relative to the held scan root, not only
  while descending during deletion. Probe the exact Linux `openat2` flags
  before enabling the backend
  relative to an opened `/` descriptor, never process CWD, so an old kernel or
  seccomp denial fails before validation or mutation without a deleted working
  directory producing a false negative. Retry interruptible Unix open/stat/
  enumeration/delete calls on `EINTR` with a bound; never retry `close`, and
  recheck cancellation before every retried `unlinkat` attempt.
  Linux native `stat` parsing must select the verified layout for
  the current process architecture: little-endian x64 reads `st_mode` at byte
  24, little-endian ARM64 at byte 16, and both read `st_ctim` at bytes 104/112;
  unverified architectures or endianness disable secure removal rather than
  guessing. Linux open flags also differ by architecture: use `LinuxOpenFlags`
  for directory/no-follow/temporary-file opens. On ARM64, x64's directory bit
  requests direct I/O instead. Recheck with `tests/native/probe-linux-abi.c`
  when adding an ABI; do not infer open flags from the `stat` layout.
  The unsuffixed macOS libc symbols expose the verified 64-bit-inode
  ABI only to native ARM64 processes; fail closed on Intel/Rosetta rather than
  parsing the ARM64 layout. Unix captured identity includes change-time seconds/nanoseconds as
  well as device/inode for selected directories, parents, links, and observed
  entries because filesystems may immediately recycle an inode. A selected
  directory whose generation or contents changed after validation must be
  refused and revalidated, not accepted for compatibility with stale behavior. Windows
  also pins a deterministic immediate-entry fingerprint (4,096-entry bound),
  including full opened-child identities and raw reparse data. Compare snapshots
  around policy inspection and before any mutation; cancellation must precede
  each native enumeration/read. This is not an atomic recursive content snapshot.
  Do not fall back to
  path-recursive deletion on Windows, macOS, or Linux. Keep cancellation
  checks immediately before every native destructive call and dispose handles
  even when identity inspection, callbacks, or those checks throw. There is no
  unsupported-platform path deletion fallback: real removal is an environment
  refusal when the secure backend is unavailable. `remove` without `--yes`
  remains a non-mutating managed preview and cannot produce an execution
  identity. Do not
  overstate the Unix guarantee: POSIX has no general
  unlink-by-descriptor operation, so the final `fstatat` → `unlinkat` name
  interval remains non-atomic for every entry, including directories and the
  selected root, against a process with the same UID. It cannot redirect
  recursive traversal through an ancestor or replacement directory, but an
  empty replacement at that final name can itself be unlinked.
  Every real directory removal requires a captured `ExecutionIdentity`. Every
  real link-only removal requires an `ExecutionLinkIdentity` that pins the
  canonical parent identity, final name, and native link identity; reverify the
  parent and link immediately before deleting the opened/relative entry. Empty-
  directory cleanup must use `RemoveValidator.ValidateEmptyDirectory`, which
  captures identity, canonicalizes containment, refuses scan roots, and sets
  `RequiresEmptyDirectory`. Both native backends must fail without deleting any
  children if such a directory is populated after validation—never downgrade
  that path into ordinary recursive removal. Broken-link cleanup similarly uses
  `RemoveValidator.ValidateBrokenSymlink` and its identity-pinned link-only
  contract; agent unlink actions use `ValidateSymlink` with the inventory roots.
  Cleanup batches must enumerate validations lazily so each candidate is pinned
  immediately before its own removal; deleting one sibling link changes the
  parent generation and intentionally invalidates any earlier sibling capture.
  Deduplicate all selected cleanup path keys before yielding the first
  validation in both TUI and CLI apply flows, then carry that pre-validation
  skip count into successful and canceled result accounting; otherwise the
  first deletion can make a later duplicate look like a failed validation or
  environment refusal, and cancellation can make a known skip look like a
  failure.
  Destructive directory identity capture may resolve ancestor components, but
  must open the final candidate name relative to the canonical parent with
  no-follow and directory-only flags. Only non-destructive canonicalization may
  follow the final component. When reviewing this boundary, inventory every
  observe → reopen → compare → delete step: a child path reconstructed after a
  parent handle was acquired is not an authority address. Require both the
  platform's full native object ID and its generation/change-time signal at
  every validation-to-execution policy boundary; never assume ID reuse is
  Unix-only. Transient traversal children still compare their full IDs and
  types, but are made authoritative by opening relative to the held parent;
  directory-enumeration timestamps are not a stable validation contract.
  Object-local removal policy must be captured through that same held directory:
  inspect `SKILL.md`, `.git`, and empty-directory state relative to the opened
  handle/descriptor, then verify the directory generation did not change during
  inspection. Windows filesystems can either preserve or change `ChangeTime`
  across a rename, so ABA tests must accept only the two safe outcomes: a
  handle-bound snapshot when generation remains stable, or an explicit
  generation-change refusal. Never capture identity and perform those checks later through its
  canonical pathname. Broken-link eligibility likewise belongs to the captured
  parent/name/link observation: determine whether the target resolves relative
  to the held parent, then recheck parent and link identities before approving
  cleanup. Adversarial coverage must combine ancestor rename/replacement with hard links,
  final-component symlink replacement, and immediate native-identifier reuse
  on every supported OS.
  `GetFinalPathNameByHandleW` normalization may strip `\\?\` only from DOS
  drive-letter paths and convert `\\?\UNC\` to ordinary UNC form. Preserve
  other extended namespaces such as `\\?\Volume{GUID}\...`; stripping their
  prefix turns them into unsafe relative paths.

- Unix directory enumeration must open a fresh cursor relative to the held
  directory (`openat` of `.`), not `dup` the directory descriptor: duplicated
  descriptors share the enumeration offset and make repeated policy/payload
  inspections incorrectly see EOF. Preserve this in both emptiness and traversal.

- Payload snapshots read entries relative to held native roots, treating links as
  leaves and rejecting special files. Recheck all observed entries/ancestors at
  completion, including raw content rehashes: Windows can retain a file's native
  generation through a same-length rewrite while a read handle is held. Windows
  also needs every directory's immediate-entry manifest because
  its ChangeTime alone may miss a later sibling edit. Staging cleanup retains the
  originally opened directory lease; never recapture a replacement by path.

- Maintenance backup evidence uses native held directories for every state-file
  create/enumeration. Pin existing excluded payload/root identities for the full
  session so a rename cannot turn one into state. Ancestor policy inspects held
  objects, brackets metadata checks with native generations, and revalidates
  canonical addresses and parent/child relationships. Refuse inherited/shared
  permissions; never rewrite a reopened directory's ACL based on earlier mkdir
  success. On native macOS ARM64, variadic openat's mode belongs on the stack;
  keep the padded P/Invoke and exact 0700/0600 regression. NULL/ENOENT from
  acl_get_fd_np means absent FILESEC_ACL in Darwin libc, not a generic permission
  bypass. File Flush(true) alone does not publish directory entries durably:
  Unix state uses held-directory fsync and macOS F_FULLFSYNC, while Windows
  creates entries with native FILE_WRITE_THROUGH and flushes file handles.
  The schema-1 backup is raw payload evidence, not ACL/xattr/owner restoration
  or authority to replace an installation. See docs/usage.md and the batch-4
  evidence before extending it into a reviewed recovery plan.
  Retain the writer FileStream's CLOEXEC handle for the macOS device barrier;
  never dup it without CLOEXEC. Such a duplicate can leak an advisory writer
  lock into concurrently spawned processes after Capture returns. Keep the
  child-outlives-capture regression and borrower AddRef/Release discipline.

- Backup exclusions use inventory ConfiguredRoots, including expected absent
  default/override/workspace/profile placements, not just ScannedRoots. Root
  configuration completeness is separate from provider/scan completeness;
  bounded omissions or unresolved contexts refuse backup admission. Keep the
  prospective canonical state exclusion before mkdir and held native checks.
  Raw backup evidence never parses or normalizes SKILL.md: it uses aggregate
  payload limits even for malformed UTF-8, flow metadata or large markdown.
  Windows payload walking needs read/enumeration rights only; keep DELETE access
  for removal callers and the readable/non-deletable ACL regression.

- Windows ACL fixtures must restore saved DACL binary forms through fresh
  DirectorySecurity/FileSecurity objects with AccessControlSections.Access.
  GetAccessControl returns descriptors without modified flags; passing an
  unchanged saved object back to SetAccessControl can write nothing. Restore
  parent then payload then file after inheritance propagation, and avoid changing
  the source owner when testing read-only backup permissions.
  Fixture owner normalization must inspect the no-follow object and skip an
  already-current-user owner. Redundant owner-only `SetSecurityInfo` can strip
  inherited OWNER RIGHTS access; retain file/directory/link regression coverage.

- Every Unix descriptor duplication must atomically retain close-on-exec through
  `F_DUPFD_CLOEXEC`; never use plain `dup` followed by `F_SETFD`. Darwin ARM64
  `fcntl` has two fixed arguments, so its minimum-fd variadic value belongs on
  the stack after six padded integer argument slots. Verify flags before stream
  or fdopendir admission and child-exec behavior, not just directory stream flags.

- Read-only maintenance history must use native existing-only opens; never
  create a missing state root, lock or archive to inspect it. Retain strict quota
  enumeration for capture, but report unsafe history leaves individually without
  following them or hiding verified peers. ZIP parsing must consume the same
  bounded immutable bytes whose comment-free end record and stored central
  entries were validated. Capture a file length once, validate it, and allocate
  only from that value. Keep raw evidence verification separate from restore
  authority; archive schema1 omits ACL/xattr/root-mode/owner restoration.

- Protect every `.git` metadata entry at a selected root, including worktree/
  submodule gitfiles and broken links; do not test only Directory.Exists or
  follow a Git marker to decide protection. Native policy must observe it
  relative to the held directory and accept only true absence. Keep the legacy
  ContainsGitDirectory wire label while describing repository/worktree metadata
  in UI text. Parent-repository warnings accepted with --yes are insufficient
  protection for the repository/worktree root itself.

- Native state History enumeration adds immediate directory-type hints through
  the existing held native cursor/stat path. It does not follow links, descend
  payloads, capture identities for execution or relax file-only EnumerateEntries
  errors. Unsafe entries remain individual protected observations. Use native
  existing-only opens and existing locks for coherent History reads; contention,
  missing locks, access failures and unknown state remain unverified.

- Explicit retirement removes only exact held private operation/slot identities,
  under root→operation writers, after full admission and fresh inspection. Keep
  strict `retiring.json` native evidence and independent surviving-archive
  validation; whole missing approved slots may resume only after new approval.
  Partial trees, unknown children or changed native generations remain protected.
  See [retirement](maintenance-operation-retirement.md). Regular-file captures
  may borrow the already-held writer FileStream: Windows byte locks require
  same-handle reads. Never use directory identity capture for file evidence.
  Retirement-only admission captures the CURRENT live tree independently of
  historical replacement context/registry state. Reject overlaps with all private
  roots, descendants and small files; hold/revalidate the live root and parent at
  destructive calls. Missing live data permits only a provably never-started
  review's abandonment. Version-2 markers retain that current evidence; supported
  version-1 markers keep their exact legacy resume evidence.

- Registry parent and owned-directory fingerprints have different Windows capture
  domains. A registry lease validates its namespace-only witness; an owned lease
  validates its full policy witness before/after inspection. Compare all native
  identity/generation/address fields across those leases, but do not compare the
  two fingerprint hashes to each other. Retain both independent repeated witness
  checks, same-file raw reads and the real registry data-file lock.

- Only dedicated registry-recorded restore may open a missing-SKILL original via
  `OpenRegistryDamaged`. Its caller binds the complete raw damaged original and
  one already-attached compatible record; ordinary relocation still requires
  SKILL.md. Preserve every held native directory/Git/identity/generation/rename
  guard. Raw bound capture is separate from historical parsed capture. Require
  supported full native metadata and revalidate archive-bound live metadata on
  each transition; no capability refusal may become review-only approval.
