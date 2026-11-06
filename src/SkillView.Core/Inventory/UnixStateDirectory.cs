using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SkillView.Inventory;

internal sealed partial class UnixSecureRemovalBackend
{
    internal static NativeStateDirectory OpenStateDirectory(string path, IReadOnlyList<string> excluded, CancellationToken token, Action<string>? afterCreate = null, Action? beforeAncestorRefresh = null,
        bool allowCreate = true)
    {
        var full = Path.GetFullPath(path);
        if (PathIdentity.Equals(full, Path.GetPathRoot(full)!)) throw new IOException("State cannot be a filesystem root.");
        if (PathResolver.IsSymlink(full)) throw new IOException("State root cannot be a link.");
        var missing = new Stack<string>();
        var cursor = full;
        while (!Directory.Exists(cursor))
        {
            token.ThrowIfCancellationRequested();
            if (!allowCreate) throw new DirectoryNotFoundException("Maintenance state does not exist; history creates no files.");
            if (missing.Count >= 32) throw new IOException("State directory creation depth exceeded.");
            missing.Push(Path.GetFileName(cursor));
            cursor = Path.GetDirectoryName(cursor) ?? throw new IOException("State has no existing ancestor.");
        }
        var held = new List<SafeUnixHandle>();
        try
        {
            var pinned = new List<NativeStat>();
            using var exclusionBuffer = new StatBuffer();
            foreach (var excludedPath in excluded)
            {
                token.ThrowIfCancellationRequested();
                if (!Directory.Exists(excludedPath)) continue;
                if (pinned.Count >= 4096) throw new IOException("State exclusion identity limit exceeded.");
                var captured = CreateHandle(OpenWithRetry(excludedPath, UnixConstants.DirectoryOpenFlags), "pin excluded installation failed");
                held.Add(captured); pinned.Add(ReadStat(captured, exclusionBuffer.Pointer));
            }
            var exclusions = new UnixStateExclusions(excluded, pinned);
            var canonical = RealPath(cursor);
            var parent = CreateHandle(OpenWithRetry(canonical, UnixConstants.DirectoryFollowOpenFlags), "open state ancestor failed");
            held.Add(parent);
            CheckStateAncestors(parent, exclusions, token, beforeRefresh: beforeAncestorRefresh);
            while (missing.TryPop(out var name))
            {
                token.ThrowIfCancellationRequested();
                NativeStateDirectory.CheckName(name);
                var destination = Path.Combine(ReadFinalPath(parent), name);
                NativeStateDirectory.CheckExcluded(destination, exclusions.Paths);
                CheckStateAncestors(parent, exclusions, token, beforeRefresh: beforeAncestorRefresh);
                var created = RetryOnInterrupted(() =>
                {
                    token.ThrowIfCancellationRequested();
                    return StateNative.mkdirat(parent.FileDescriptor, name, 0x1C0);
                }) == 0;
                if (!created && Marshal.GetLastPInvokeError() != 17) throw new IOException(LastError("create state directory failed"));
                if (created) afterCreate?.Invoke(destination);
                var child = OpenDirectoryAt(parent, name, refuseNestedMount: true);
                held.Add(child);
                if (!PathIdentity.Equals(ReadFinalPath(child), destination)) throw new IOException("New state directory moved before admission.");
                if (OperatingSystem.IsMacOS()) CheckPrivateStateAcl(child);
                FlushStateDirectory(parent, token);
                parent = child;
            }
            var root = parent;
            var address = ReadFinalPath(root);
            CheckStateAncestors(root, exclusions, token, beforeRefresh: beforeAncestorRefresh);
            using var buffer = new StatBuffer();
            var identity = ReadStat(root, buffer.Pointer);
            if ((identity.Mode & 0xFFF) != 0x1C0) throw new IOException("State directory must be private (no group/other permissions).");
            CheckStateOwner(buffer.Pointer);
            if (OperatingSystem.IsMacOS()) CheckPrivateStateAcl(root);
            return new UnixStateSession(held, root, address, exclusions);
        }
        catch { foreach (var handle in held.AsEnumerable().Reverse()) handle.Dispose(); throw; }
    }

    private sealed record UnixStateExclusions(IReadOnlyList<string> Paths, IReadOnlyList<NativeStat> Pinned);

    private sealed class StatePolicyRefreshException() : IOException("State ancestor remained unstable during bounded policy refresh.");

    private static void CheckStateAncestors(SafeUnixHandle directory, UnixStateExclusions excluded, CancellationToken token,
        bool flush = false, Action? beforeRefresh = null)
    {
        // Concurrent siblings can change an ancestor generation without moving
        // the held objects. Retry the entire bounded observation, never a write.
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { CheckStateAncestorPass(directory, excluded, token, flush, beforeRefresh); return; }
            catch (StatePolicyRefreshException) when (attempt < 15) { }
        }
    }

    private static void CheckStateAncestorPass(SafeUnixHandle directory, UnixStateExclusions excluded, CancellationToken token,
        bool flush, Action? beforeRefresh)
    {
        var parents = new List<SafeUnixHandle>();
        var chain = new List<(SafeUnixHandle Handle, NativeStat Identity, string Path)>();
        using var buffer = new StatBuffer();
        try
        {
            var current = directory;
            for (var depth = 0; depth < 128; depth++)
            {
                token.ThrowIfCancellationRequested();
                var canonical = ReadFinalPath(current);
                NativeStateDirectory.CheckExcluded(canonical, excluded.Paths);
                var before = InspectStateAncestor(current, excluded, buffer.Pointer, token);
                chain.Add((current, before, canonical));
                var parentPath = Path.GetDirectoryName(canonical);
                if (parentPath is null) break;
                // '..' is resolved relative to the held directory, including
                // read-only mount boundaries. Validate the reverse relationship.
                var parent = OpenDirectoryAt(current, "..", refuseNestedMount: false);
                parents.Add(parent);
                if (!PathIdentity.Equals(ReadFinalPath(parent), parentPath))
                    throw new IOException("State ancestor relationship changed.");
                using var child = OpenDirectoryAt(parent, Path.GetFileName(canonical), refuseNestedMount: false);
                VerifyStateRelationship(ReadStat(current, buffer.Pointer), ReadStat(child, buffer.Pointer));
                current = parent;
                if (depth == 127) throw new IOException("State ancestor inspection limit exceeded.");
            }
            beforeRefresh?.Invoke();
            for (var index = 0; index < chain.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var item = chain[index];
                if (!PathIdentity.Equals(ReadFinalPath(item.Handle), item.Path))
                    throw new IOException("State ancestor moved before admission.");
                var refreshed = InspectStateAncestor(item.Handle, excluded, buffer.Pointer, token);
                if (index + 1 < chain.Count)
                {
                    using var child = OpenDirectoryAt(chain[index + 1].Handle, Path.GetFileName(item.Path), refuseNestedMount: false);
                    VerifyStateRelationship(refreshed, ReadStat(child, buffer.Pointer));
                }
                if (flush && item.Identity.Device == chain[0].Identity.Device) FlushStateDirectory(item.Handle, token);
            }
        }
        finally { foreach (var parent in parents.AsEnumerable().Reverse()) parent.Dispose(); }
    }

    private static NativeStat InspectStateAncestor(SafeUnixHandle directory, UnixStateExclusions excluded, IntPtr buffer, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var before = ReadStat(directory, buffer);
        foreach (var payload in excluded.Pinned)
        {
            token.ThrowIfCancellationRequested();
            if (before.Device == payload.Device && before.Inode == payload.Inode)
                throw new IOException("State lies inside a pinned installed payload.");
        }
        var result = FStatAtWithRetry(directory.FileDescriptor, "SKILL.md", buffer, UnixConstants.NoFollowFlag);
        var error = Marshal.GetLastPInvokeError();
        if (result == 0) throw new IOException("State lies inside a skill-bearing ancestor.");
        if (error != 2) throw new IOException("Cannot inspect state ancestor metadata: " + new Win32Exception(error).Message);
        VerifyStateRelationship(before, ReadStat(directory, buffer));
        return before;
    }

    private static void VerifyStateRelationship(NativeStat expected, NativeStat actual)
    {
        if (expected.Device != actual.Device || expected.Inode != actual.Inode || expected.FileType != actual.FileType)
            throw new IOException("State ancestor relationship changed.");
        if (!Matches(expected, actual)) throw new StatePolicyRefreshException();
    }

    private static void FlushStateDirectory(SafeUnixHandle directory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (RetryOnInterrupted(() => StateNative.fsync(directory.FileDescriptor)) != 0)
            throw new IOException(LastError("state directory durability barrier failed"));
    }

    private static void CheckStateOwner(IntPtr stat)
    {
        var uidOffset = OperatingSystem.IsMacOS() ? 16 : RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 28 : 24;
        if (unchecked((uint)Marshal.ReadInt32(stat, uidOffset)) != StateNative.geteuid())
            throw new IOException("State must be owned by the current user.");
    }

    private static void CheckPrivateStateAcl(SafeUnixHandle directory)
    {
        var acl = StateNative.acl_get_fd_np(directory.FileDescriptor, 0x100);
        if (acl == IntPtr.Zero)
        {
            // Darwin libc's successful fstatx_np with no FILESEC_ACL property
            // returns NULL/ENOENT; other failures remain refusals.
            if (Marshal.GetLastPInvokeError() == 2) return;
            throw new IOException(LastError("inspect state ACL failed"));
        }
        try
        {
            if (StateNative.acl_valid(acl) != 0) throw new IOException("State ACL is invalid.");
            var result = StateNative.acl_get_entry(acl, 0, out _);
            // Darwin returns 0 for a found entry; an empty validated ACL
            // reports EINVAL when asked for its first entry.
            if (result == 0 || Marshal.GetLastPInvokeError() != 22)
                throw new IOException("Existing state directory has an extended ACL or ACL inspection failed.");
        }
        finally { StateNative.acl_free(acl); }
    }

    private sealed partial class UnixStateSession(List<SafeUnixHandle> held, SafeUnixHandle root, string canonical,
        UnixStateExclusions excluded, Action<CancellationToken>? stagingGuard = null,
        SafeUnixHandle? payloadParent = null, string? payloadName = null) : NativeStateDirectory
    {
        private SafeFileHandle? _barrier;
        private bool sealedStage;
        internal override NativePayloadDirectory CreatePayloadDirectory(string name, CancellationToken token)
        {
            NativeStateDirectory.CheckName(name);
            return CreatePayloadChild(name, token);
        }
        internal NativePayloadDirectory CreatePayloadChild(string name, CancellationToken token)
        {
            NativePayloadDirectory.CheckName(name);
            return new UnixPayloadDirectory(CreateChild(name, token));
        }
        internal FileStream CreatePayloadFile(string name, CancellationToken token)
        {
            NativePayloadDirectory.CheckName(name);
            return OpenStateFile(name, false, token, create: true, payloadName: true);
        }
        internal void CreatePayloadLink(string name, ReadOnlySpan<byte> rawTarget, int? unixMode, CancellationToken token,
            SkillView.Maintenance.PayloadNativeMetadata? metadata = null,
            SkillView.Maintenance.PayloadMetadataAuthority? authority = null)
        {
            ValidateLocation(token); NativePayloadDirectory.CheckName(name);
            if (rawTarget.IsEmpty || rawTarget.Length > 64 * 1024 || rawTarget.Contains((byte)0)
                || unixMode is null or < 0 or > 511 || !OperatingSystem.IsMacOS() && unixMode != 511)
                throw new IOException("Unsupported raw payload link target or mode.");
            var target = new byte[rawTarget.Length + 1]; rawTarget.CopyTo(target);
            if (RetryOnInterrupted(() => { token.ThrowIfCancellationRequested(); return PayloadNative.symlinkat(target, root.FileDescriptor, name); }) != 0)
                throw new IOException(LastError("create exclusive payload link failed"));
            if (OperatingSystem.IsMacOS())
            {
                // O_SYMLINK opens the link itself. Never chmod a reconstructed
                // path or follow the target, including dangling/external links.
                using var link = CreateHandle(OpenAtWithRetry(root.FileDescriptor, name, 0x00200000 | 0x01000000), "open newly staged link");
                using var stat = new StatBuffer();
                var observed = ReadStat(link, stat.Pointer);
                if (!observed.IsSymlink || !TryReadStatAt(root, name, stat.Pointer, out var named, out _) || !Matches(observed, named)
                    || unchecked((ushort)Marshal.ReadInt16(stat.Pointer, 6)) != 1)
                    throw new IOException("New staged link identity or hard-link topology is unsupported.");
                using var borrowed = new SafeFileHandle(new IntPtr(link.FileDescriptor), ownsHandle: false);
                if (metadata is not null) SkillView.Maintenance.PayloadNativeMetadata.Apply(borrowed, metadata, unixMode, token, authority);
                else File.SetUnixFileMode(borrowed, (UnixFileMode)unixMode.Value);
            }
            else if (metadata is not null) throw new IOException("Linux link metadata publication requires complete native flag coverage.");
            FlushStateDirectory(root, token);
            ValidateLocation(token);
        }
        internal void SealPayload(int? unixMode, CancellationToken token)
        {
            if (OperatingSystem.IsWindows()) throw new IOException("Unix payload staging is unavailable on Windows.");
            if (stagingGuard is null || unixMode is null or < 0 or > 511)
                throw new IOException("Only new payload directories with ordinary Unix permission bits may be sealed.");
            ValidateLocation(token);
            using var borrowed = new SafeFileHandle(new IntPtr(root.FileDescriptor), ownsHandle: false);
            File.SetUnixFileMode(borrowed, (UnixFileMode)unixMode.Value);
            FlushStateDirectory(root, token);
            sealedStage = true;
        }
        internal void SealPayloadWithMetadata(int? unixMode, SkillView.Maintenance.PayloadNativeMetadata metadata, CancellationToken token,
            SkillView.Maintenance.PayloadMetadataAuthority? authority = null)
        {
            if (stagingGuard is null) throw new IOException("Only a new staged payload can receive native metadata.");
            ValidateLocation(token);
            using var borrowed = new SafeFileHandle(new IntPtr(root.FileDescriptor), ownsHandle: false);
            SkillView.Maintenance.PayloadNativeMetadata.Apply(borrowed, metadata, unixMode, token, authority);
            FlushStateDirectory(root, token);
            sealedStage = true;
        }
        internal SkillView.Maintenance.PayloadSnapshot CapturePayload(CancellationToken token, Action<NativePayloadItem>? inspectMetadata = null)
        {
            token.ThrowIfCancellationRequested();
            if (!sealedStage || stagingGuard is null || payloadParent is null || payloadName is null)
                throw new IOException("Only a sealed held payload can be finalized.");
            stagingGuard(token);
            if (!PathIdentity.Equals(ReadFinalPath(root), canonical)) throw new IOException("Draft payload moved before finalization.");
            var snapshot = SkillView.Maintenance.PayloadSnapshot.CaptureBoundWithEntrySink(canonical, (item, _) => inspectMetadata?.Invoke(item),
                (depth, observe, ct) => WalkOpenedPayload(root, payloadParent, payloadName, depth, observe, ct, flushToDisk: true, observeRoot: inspectMetadata), token);
            stagingGuard(token);
            if (!PathIdentity.Equals(snapshot.Path, canonical) || !PathIdentity.Equals(ReadFinalPath(root), canonical))
                throw new IOException("Draft payload moved during finalization.");
            return snapshot;
        }
        internal override NativeStateDirectory CreateStagingDirectory(string name, CancellationToken token)
        {
            ValidateLocation(token); NativeStateDirectory.CheckName(name);
            return CreateChild(name, token);
        }
        private UnixStateSession CreateChild(string name, CancellationToken token)
        {
            ValidateLocation(token);
            if (RetryOnInterrupted(() => { token.ThrowIfCancellationRequested(); return StateNative.mkdirat(root.FileDescriptor, name, 0x1C0); }) != 0)
                throw new IOException(LastError("create exclusive staging directory failed"));
            var child = OpenDirectoryAt(root, name, refuseNestedMount: true);
            try
            {
                var address = Path.Combine(canonical, name);
                if (!PathIdentity.Equals(ReadFinalPath(child), address)) throw new IOException("Staging directory moved during creation.");
                FlushStateDirectory(root, token);
                return new UnixStateSession([child], child, address, excluded, ValidateLocation, root, name);
            }
            catch { child.Dispose(); throw; }
        }
        internal override void SealStagingDirectory(int unixMode, CancellationToken token)
        {
            if (stagingGuard is null || unixMode is not (448 or 493)) throw new IOException("Only new staging directories may be sealed.");
            ValidateLocation(token);
            using var borrowed = new SafeFileHandle(new IntPtr(root.FileDescriptor), ownsHandle: false);
            if (OperatingSystem.IsWindows()) throw new IOException("Unix staging is unavailable on Windows.");
            File.SetUnixFileMode(borrowed, (UnixFileMode)unixMode);
            FlushStateDirectory(root, token);
            sealedStage = true;
        }
        internal override OwnedDirectoryLease CaptureOwnership(CancellationToken token)
        {
            ValidateLocation(token);
            var descriptor = DupWithRetry(root.FileDescriptor);
            if (descriptor < 0) throw new IOException(LastError("duplicate owned state directory failed"));
            return CreateOwnedDirectoryLease(new SafeUnixHandle(descriptor));
        }
        internal override string CanonicalPath => canonical;
        internal override void ReceiveDirectory(NativeDirectoryMoveLease source, string name, CancellationToken token)
        {
            ValidateLocation(token);
            NativeStateDirectory.CheckName(name);
            if (source is not UnixDirectoryMove move) throw new IOException("Directory relocation platform mismatch.");
            move.Move(root, name, canonical, token);
            ValidateLocation(token);
        }
        internal override void ValidateLocation(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!PathIdentity.Equals(ReadFinalPath(root), canonical)) throw new IOException("State directory moved; retained evidence needs review.");
            if (sealedStage) throw new IOException("Staging directory is sealed; no more writes are admitted.");
            if (stagingGuard is null) CheckStateAncestors(root, excluded, token);
            else stagingGuard(token);
            using var buffer = new StatBuffer();
            if ((ReadStat(root, buffer.Pointer).Mode & 0xFFF) != 0x1C0) throw new IOException("State privacy changed; review its permissions.");
            CheckStateOwner(buffer.Pointer);
            if (OperatingSystem.IsMacOS()) CheckPrivateStateAcl(root);
        }
        internal override FileStream OpenFile(string name, bool writerLock, CancellationToken token) => OpenStateFile(name, writerLock, token, create: true);
        internal override FileStream OpenExistingFile(string name, bool writerLock, CancellationToken token) => OpenStateFile(name, writerLock, token, create: false);
        internal override FileStream CreateAnonymousMetadataWitness(CancellationToken token)
        {
            ValidateLocation(token);
            if (!OperatingSystem.IsLinux()) throw new IOException("Anonymous metadata witnesses require Linux.");
            // O_TMPFILE|O_EXCL creates an unnamed inode which Linux permanently
            // forbids linking. No retained name can redirect a later probe.
            var how = new OpenHow
            {
                Flags = (ulong)(LinuxOpenFlags.TemporaryFile | LinuxOpenFlags.Exclusive | LinuxOpenFlags.CloseOnExec | LinuxOpenFlags.ReadWrite),
                Mode = 0x180,
                Resolve = UnixConstants.ResolveNoCrossDevice | UnixConstants.ResolveBeneath | UnixConstants.ResolveNoSymlinks
            };
            var descriptor = RetryOnInterrupted(() =>
            {
                token.ThrowIfCancellationRequested();
                return checked((int)UnixNative.syscall(UnixConstants.OpenAt2SystemCall, root.FileDescriptor, ".",
                    ref how, (nuint)Marshal.SizeOf<OpenHow>()));
            });
            if (descriptor < 0) throw new IOException(LastError("create anonymous metadata witness failed"));
            var file = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
            try { return new FileStream(file, FileAccess.ReadWrite); }
            catch { file.Dispose(); throw; }
        }
        private FileStream OpenStateFile(string name, bool writerLock, CancellationToken token, bool create, bool payloadName = false)
        {
            ValidateLocation(token);
            if (payloadName) NativePayloadDirectory.CheckName(name); else NativeStateDirectory.CheckName(name);
            var flags = OperatingSystem.IsMacOS() ? (create ? 2 | 0x200 | (writerLock ? 0 : 0x800) : 0) | 0x100 | 0x1000000 | 4
                : (create ? LinuxOpenFlags.ReadWrite | LinuxOpenFlags.Create | (writerLock ? 0 : LinuxOpenFlags.Exclusive) : 0) | LinuxOpenFlags.NoFollow | LinuxOpenFlags.CloseOnExec | LinuxOpenFlags.NonBlocking;
            token.ThrowIfCancellationRequested();
            int descriptor;
            if (OperatingSystem.IsLinux())
            {
                var how = new OpenHow
                {
                    Flags = (ulong)flags,
                    Mode = create ? 0x180u : 0u,
                    Resolve = UnixConstants.ResolveNoCrossDevice | UnixConstants.ResolveBeneath | UnixConstants.ResolveNoSymlinks
                };
                descriptor = RetryOnInterrupted(() =>
                {
                    token.ThrowIfCancellationRequested();
                    return checked((int)UnixNative.syscall(UnixConstants.OpenAt2SystemCall, root.FileDescriptor, name,
                        ref how, (nuint)Marshal.SizeOf<OpenHow>()));
                });
            }
            else descriptor = RetryOnInterrupted(() =>
            {
                token.ThrowIfCancellationRequested();
                return create ? StateNative.openat_create(root.FileDescriptor, name, flags, 0, 0, 0, 0, 0, 0x180)
                    : UnixNative.openat(root.FileDescriptor, name, flags);
            });
            if (descriptor < 0) throw new IOException(LastError("open state file failed"));
            using var native = new SafeUnixHandle(descriptor);
            using var buffer = new StatBuffer();
            var fileIdentity = ReadStat(native, buffer.Pointer);
            if (fileIdentity.FileType != 0x8000 || (fileIdentity.Mode & 0xFFF) != 0x180)
                throw new IOException("State entry must be a private regular file.");
            CheckStateOwner(buffer.Pointer);
            if (OperatingSystem.IsMacOS())
            {
                CheckPrivateStateAcl(native);
            }
            if (writerLock && RetryOnInterrupted(() => StateNative.flock(descriptor, 2 | 4)) != 0)
                throw new IOException(LastError("maintenance writer is already active"));
            var file = new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
            native.SetHandleAsInvalid();
            try
            {
                FileStream stream = writerLock ? new StateWriterStream(file, create ? FileAccess.ReadWrite : FileAccess.Read)
                    : new FileStream(file, create ? FileAccess.ReadWrite : FileAccess.Read);
                // Borrow the still-owned writer handle; dup would clear CLOEXEC
                // and could leak its advisory lock into an unrelated child.
                if (writerLock) _barrier = file;
                return stream;
            }
            catch
            {
                try
                {
                    if (writerLock && !file.IsClosed)
                        RetryOnInterrupted(() => StateNative.flock(descriptor, 8));
                }
                finally { file.Dispose(); }
                throw;
            }
        }
        internal override IEnumerable<(string Name, long Bytes, string? Error)> EnumerateEntries(CancellationToken token) =>
            EnumerateHistoryEntries(token).Select(entry => (entry.Name, entry.Bytes,
                entry.Directory ? "Unexpected state entry type or identity change." : entry.Error));

        internal override IEnumerable<(string Name, long Bytes, string? Error, bool Directory)> EnumerateHistoryEntries(CancellationToken token)
        {
            ValidateLocation(token);
            using var frame = new DirectoryFrame(root, root, "state", canonical, 0, false, false);
            using var buffer = new StatBuffer();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!frame.TryReadNext(out var entry, out var error))
                { if (error is not null) throw new IOException(error); yield break; }
                if (entry is null) continue;
                if (!TryReadStatAt(root, entry.Value.Name, buffer.Pointer, out var stat, out var statError))
                { yield return (entry.Value.Name, 0, statError ?? "State entry disappeared.", false); continue; }
                if (entry.Value.Inode != 0 && entry.Value.Inode != stat.Inode || stat.FileType is not (0x8000 or 0x4000))
                { yield return (entry.Value.Name, 0, "Unexpected state entry type or identity change.", false); continue; }
                if (stat.FileType == 0x4000) { yield return (entry.Value.Name, 0, null, true); continue; }
                // Both verified Linux stat layouts put st_size at 48; native
                // ARM64 Darwin puts it at 96 (SDK sys/stat.h).
                var length = Marshal.ReadInt64(buffer.Pointer, OperatingSystem.IsMacOS() ? 96 : 48);
                if (length < 0) { yield return (entry.Value.Name, 0, "Invalid state entry length.", false); continue; }
                yield return (entry.Value.Name, length, null, false);
            }
        }
        internal override void FlushPublication(CancellationToken token)
        {
            ValidateLocation(token);
            if (stagingGuard is not null) { FlushStateDirectory(root, token); return; }
            CheckStateAncestors(root, excluded, token, flush: true);
            if (OperatingSystem.IsMacOS())
            {
                token.ThrowIfCancellationRequested();
                if (_barrier is null) throw new IOException("State writer durability handle is unavailable.");
                var retained = false;
                try
                {
                    _barrier.DangerousAddRef(ref retained);
                    if (RetryOnInterrupted(() => StateNative.fcntl_fullsync(_barrier.DangerousGetHandle().ToInt32(), 51)) != 0)
                        throw new IOException(LastError("state device durability barrier failed"));
                }
                finally { if (retained) _barrier.DangerousRelease(); }
            }
        }
        internal override void FlushNamespace(CancellationToken token)
        {
            ValidateLocation(token); FlushStateDirectory(root, token);
        }
        public override void Dispose() { foreach (var directory in held.AsEnumerable().Reverse()) directory.Dispose(); }
    }

    // flock belongs to the open file description, which can outlive a close
    // through dup or the fork-before-exec interval. End ownership explicitly,
    // independently of cancellation, before allowing the descriptor to close.
    private sealed class StateWriterStream : FileStream
    {
        private readonly SafeFileHandle handle;
        private readonly object disposeGate = new();
        private bool released;

        internal StateWriterStream(SafeFileHandle handle, FileAccess access) : base(handle, access) => this.handle = handle;

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
            {
                // FileStream's finalizer reaches this override, including after
                // partial construction. Finalizer cleanup must never throw.
                try { ReleaseWriter(flush: false); }
                catch { }
                finally { base.Dispose(false); }
                return;
            }
            try { ReleaseWriter(flush: true); }
            finally { base.Dispose(true); }
        }

        private void ReleaseWriter(bool flush)
        {
            lock (disposeGate)
            {
                if (released) return;
                released = true;
                try { if (flush) base.Flush(); }
                finally
                {
                    if (!handle.IsClosed && !handle.IsInvalid)
                    {
                        var added = false;
                        try
                        {
                            handle.DangerousAddRef(ref added);
                            if (RetryOnInterrupted(() => StateNative.flock(handle.DangerousGetHandle().ToInt32(), 8)) != 0)
                                throw new IOException(LastError("maintenance writer unlock failed"));
                        }
                        finally { if (added) handle.DangerousRelease(); }
                    }
                }
            }
        }

        public override ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static class StateNative
    {
        [DllImport("libc", SetLastError = true, EntryPoint = "openat")]
        // Apple ARM64 variadic arguments start on the stack. Padding the five
        // remaining integer argument registers places openat's mode there.
        internal static extern int openat_create(int directory, string path, int flags,
            nint pad3, nint pad4, nint pad5, nint pad6, nint pad7, uint mode);
        [DllImport("libc", SetLastError = true)] internal static extern int mkdirat(int directory, string name, uint mode);
        [DllImport("libc", SetLastError = true)] internal static extern int fsync(int descriptor);
        [DllImport("libc", SetLastError = true)] internal static extern int flock(int descriptor, int operation);
        [DllImport("libc")] internal static extern uint geteuid();
        [DllImport("libc", SetLastError = true)] internal static extern IntPtr acl_get_fd_np(int descriptor, int type);
        [DllImport("libc", SetLastError = true)] internal static extern int acl_get_entry(IntPtr acl, int entryId, out IntPtr entry);
        [DllImport("libc", SetLastError = true)] internal static extern int acl_valid(IntPtr acl);
        [DllImport("libc", SetLastError = true)] internal static extern int acl_free(IntPtr acl);
        [DllImport("libc", SetLastError = true, EntryPoint = "fcntl")] internal static extern int fcntl_fullsync(int descriptor, int command);
    }
}
