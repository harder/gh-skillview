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

    private sealed class UnixStateSession(List<SafeUnixHandle> held, SafeUnixHandle root, string canonical,
        UnixStateExclusions excluded) : NativeStateDirectory
    {
        private SafeFileHandle? _barrier;
        internal override string CanonicalPath => canonical;
        internal override void ValidateLocation(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!PathIdentity.Equals(ReadFinalPath(root), canonical)) throw new IOException("State directory moved; retained evidence needs review.");
            CheckStateAncestors(root, excluded, token);
            using var buffer = new StatBuffer();
            if ((ReadStat(root, buffer.Pointer).Mode & 0xFFF) != 0x1C0) throw new IOException("State privacy changed; review its permissions.");
            CheckStateOwner(buffer.Pointer);
            if (OperatingSystem.IsMacOS()) CheckPrivateStateAcl(root);
        }
        internal override FileStream OpenFile(string name, bool writerLock, CancellationToken token) => OpenStateFile(name, writerLock, token, create: true);
        internal override FileStream OpenExistingFile(string name, bool writerLock, CancellationToken token) => OpenStateFile(name, writerLock, token, create: false);
        private FileStream OpenStateFile(string name, bool writerLock, CancellationToken token, bool create)
        {
            ValidateLocation(token); NativeStateDirectory.CheckName(name);
            var flags = OperatingSystem.IsMacOS() ? (create ? 2 | 0x200 | (writerLock ? 0 : 0x800) : 0) | 0x100 | 0x1000000 | 4
                : (create ? 2 | 0x40 | (writerLock ? 0 : 0x80) : 0) | 0x20000 | 0x80000 | 0x800;
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
                var stream = new FileStream(file, create ? FileAccess.ReadWrite : FileAccess.Read);
                // Borrow the still-owned writer handle; dup would clear CLOEXEC
                // and could leak its advisory lock into an unrelated child.
                if (writerLock) _barrier = file;
                return stream;
            }
            catch { file.Dispose(); throw; }
        }
        internal override IEnumerable<(string Name, long Bytes, string? Error)> EnumerateEntries(CancellationToken token)
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
                { yield return (entry.Value.Name, 0, statError ?? "State entry disappeared."); continue; }
                if (stat.FileType != 0x8000 || entry.Value.Inode != 0 && entry.Value.Inode != stat.Inode)
                { yield return (entry.Value.Name, 0, "Unexpected state entry type or identity change."); continue; }
                // Both verified Linux stat layouts put st_size at 48; native
                // ARM64 Darwin puts it at 96 (SDK sys/stat.h).
                var length = Marshal.ReadInt64(buffer.Pointer, OperatingSystem.IsMacOS() ? 96 : 48);
                if (length < 0) { yield return (entry.Value.Name, 0, "Invalid state entry length."); continue; }
                yield return (entry.Value.Name, length, null);
            }
        }
        internal override void FlushPublication(CancellationToken token)
        {
            ValidateLocation(token);
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
        public override void Dispose() { foreach (var directory in held.AsEnumerable().Reverse()) directory.Dispose(); }
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
