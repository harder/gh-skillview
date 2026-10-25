# Native metadata and saved local merges

Merged payloads need independently captured native metadata as well as bytes,
links and Unix modes. Retained originals remain the rollback authority. A metadata
refusal permits review and preserve; it never permits silently dropping metadata
during a retained operation. New merge creation is retired; these shared
primitives remain required by schema-3 validation, unpin and local reimport.

Windows evidence independently versions complete owner/group/DACL/SACL security
descriptors, supported file attributes and bounded alternate data streams. Older
and Unix archives omit the Windows field, preserving their serialized contracts.
An ordinary supported fixture is owned by the effective user and has a recoverable
DACL; unsupported owners, restrictive ACLs, reparse kinds, storage attributes and
stream layouts require separate transitions.

Capture and publication use held native objects with verified access rights.
Complete SACL access requires an existing `SeSecurityPrivilege`; the synchronous
scope duplicates the effective token, enables that privilege on the duplicate,
and restores the exact prior thread token. It neither changes the process token
nor obtains a privilege the caller lacks. Run `AccessCheck` inside that
impersonation scope: a primary process token does not meet its documented contract.
Never carry this thread scope across an await.

`BackupRead` has a 20-byte wire header but requires a native request larger than
the 24-byte `WIN32_STREAM_ID` structure. `WindowsBackupReadStream` therefore uses
25-byte minimum requests and at most 24 bytes of fixed scratch lookahead. Validate
stream names, types and budgets before retaining stream bodies. Consume pending
lookahead before `BackupSeek`, and release the native context even after failure.
Security descriptors come from explicit complete security queries; backup-stream
enumeration alone is not evidence of SACL visibility.
Use held-handle `NtQuerySecurityObject(BACKUP_SECURITY_INFORMATION)` with bounded
sizing, an exact second read and self-relative descriptor validation. Native x64
and ARM64 probes demonstrated that `GetSecurityInfo` rejected that full-query flag
despite sufficient rights while the same object's native full query succeeded.
Individual component queries are diagnostic evidence, never a partial fallback.

Windows native tests run with `SKILLVIEW_WINDOWS_METADATA_PROOF=1` on x64 and ARM64.
That setting makes environmental refusal fail the supported fixture. The fixture
explicitly sets current-user ownership because an elevated runner's default owner
can be Administrators. The actual-gh CLI proofs likewise establish current-user
ownership on their disposable installed payload and resolution inputs before
capturing any plan. They record that preparation explicitly; they do not prove
support for Administrators-owned installations or arbitrary Windows metadata.
Portable framing tests run on every platform. Native AOT CLI and process-death
proofs complement these tests; compilation alone is not native execution evidence.
For a focused native regression run, dispatch `ci.yml` with
`verification=windows-metadata`. Skipped full jobs use distinct diagnostic check
names, so this run cannot stand in for the normal full CI gate. Full dispatches,
pushes and pull requests retain all checks and their ordinary names.

Linux open flags are architecture-specific. Use `LinuxOpenFlags` for x64/ARM64
directory/no-follow flags and for deriving `O_TMPFILE`. An anonymous held witness
must prove initial-namespace trusted-attribute authority before claiming complete
Linux metadata visibility. Unprivileged refusal and privileged publication are
separate required proofs.

API contracts: [AccessCheck](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-accesscheck),
[BackupRead](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-backupread),
[NtQuerySecurityObject](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-ntquerysecurityobject).
