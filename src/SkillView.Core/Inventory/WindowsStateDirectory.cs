using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace SkillView.Inventory;

internal sealed partial class WindowsSecureRemovalBackend
{
    [SupportedOSPlatform("windows")]
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
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new IOException("Current Windows user SID is unavailable.");
        var security = new DirectorySecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var held = new List<SafeFileHandle>();
        try
        {
            var pinned = new List<NativeIdentity>();
            foreach (var excludedPath in excluded)
            {
                token.ThrowIfCancellationRequested();
                if (!Directory.Exists(excludedPath)) continue;
                if (pinned.Count >= 4096) throw new IOException("State exclusion identity limit exceeded.");
                var captured = OpenCanonicalDirectory(excludedPath);
                held.Add(captured); pinned.Add(ReadIdentity(captured));
            }
            var exclusions = new WindowsStateExclusions(excluded, pinned);
            var canonical = OwnershipPath.CanonicalCandidate(cursor) ?? throw new IOException("Cannot canonicalize state ancestor.");
            var parent = StateNative.CreateFileW(ToExtendedPath(canonical), allowCreate ? 0xC0000000u : 0x80000000u,
                ShareRead | ShareWrite | ShareDelete, IntPtr.Zero, OpenExisting,
                BackupSemantics | OpenReparsePoint | 0x80000000, IntPtr.Zero);
            if (parent.IsInvalid) { parent.Dispose(); throw new IOException("Cannot open writable state ancestor: " + new Win32Exception(Marshal.GetLastPInvokeError()).Message); }
            held.Add(parent);
            CheckWindowsStateAncestors(parent, exclusions, token, beforeAncestorRefresh);
            while (missing.TryPop(out var name))
            {
                token.ThrowIfCancellationRequested(); NativeStateDirectory.CheckName(name);
                NativeStateDirectory.CheckExcluded(Path.Combine(ReadFinalPath(parent), name), exclusions.Paths);
                CheckWindowsStateAncestors(parent, exclusions, token, beforeAncestorRefresh);
                var destination = Path.Combine(ReadFinalPath(parent), name);
                var child = CreateStateEntryAt(parent, name, directory: true, openIf: true, descriptor, exclusive: false, token);
                held.Add(child);
                afterCreate?.Invoke(ReadFinalPath(child));
                if (!PathIdentity.Equals(ReadFinalPath(child), destination)) throw new IOException("New state directory moved before admission.");
                using var namedChild = OpenEntryAt(parent, name, true, false, requestDeleteAccess: false);
                VerifyStateRelationship(ReadIdentity(child), ReadIdentity(namedChild));
                parent = child;
            }
            CheckWindowsStateAncestors(parent, exclusions, token, beforeAncestorRefresh);
            CheckWindowsPrivateState(parent, user);
            return new WindowsStateSession(held, parent, ReadFinalPath(parent), exclusions, user, descriptor);
        }
        catch { foreach (var handle in held.AsEnumerable().Reverse()) handle.Dispose(); throw; }
    }

    private static SafeFileHandle CreateStateEntryAt(SafeFileHandle parent, string name, bool directory,
        bool openIf, byte[] descriptor, bool exclusive, CancellationToken token, bool existingOnly = false)
    {
        NativeStateDirectory.CheckName(name);
        var text = Marshal.StringToHGlobalUni(name);
        var unicodeAddress = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var securityAddress = Marshal.AllocHGlobal(descriptor.Length);
        IntPtr rawHandle = IntPtr.Zero;
        try
        {
            Marshal.Copy(descriptor, 0, securityAddress, descriptor.Length);
            var unicode = new UnicodeString
            {
                Length = checked((ushort)(name.Length * 2)),
                MaximumLength = checked((ushort)((name.Length + 1) * 2)),
                Buffer = text
            };
            Marshal.StructureToPtr(unicode, unicodeAddress, false);
            var attributes = new ObjectAttributes
            {
                Length = (uint)Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicodeAddress,
                SecurityDescriptor = securityAddress
            };
            // Write-through also applies to directory metadata creation; every
            // published file additionally uses FlushFileBuffers via Flush(true).
            var options = FileOpenForBackupIntent | FileSynchronousIoNonAlert | FileOpenReparsePoint | 2u
                | (directory ? FileDirectoryFile : FileNonDirectoryFile);
            token.ThrowIfCancellationRequested();
            var status = StateNative.NtCreateFile(out rawHandle, existingOnly ? 0x00120089u : 0x001201BFu, ref attributes, out _, IntPtr.Zero,
                0x80, exclusive ? 0u : ShareRead | ShareWrite | ShareDelete,
                existingOnly ? 1u : openIf ? 3u : 2u, options, IntPtr.Zero, 0);
            if (status < 0) throw new IOException("Create/open relative state entry failed: "
                + new Win32Exception((int)WindowsNative.RtlNtStatusToDosError(status)).Message);
            var handle = new SafeFileHandle(rawHandle, ownsHandle: true); rawHandle = IntPtr.Zero;
            try
            {
                var actual = ReadIdentity(handle);
                if (actual.IsReparsePoint || actual.IsDirectory != directory || WindowsNative.GetFileType(handle) != 1)
                    throw new IOException("State entry has an unsupported type.");
                return handle;
            }
            catch { handle.Dispose(); throw; }
        }
        finally
        {
            if (rawHandle != IntPtr.Zero) StateNative.CloseHandle(rawHandle);
            Marshal.FreeHGlobal(securityAddress); Marshal.FreeHGlobal(unicodeAddress); Marshal.FreeHGlobal(text);
        }
    }

    private sealed record WindowsStateExclusions(IReadOnlyList<string> Paths, IReadOnlyList<NativeIdentity> Pinned);

    private sealed class StatePolicyRefreshException() : IOException("State ancestor remained unstable during bounded policy refresh.");

    private static void CheckWindowsStateAncestors(SafeFileHandle root, WindowsStateExclusions excluded, CancellationToken token, Action? beforeRefresh = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { CheckWindowsStateAncestorPass(root, excluded, token, beforeRefresh); return; }
            catch (StatePolicyRefreshException) when (attempt < 15) { }
        }
    }

    private static void CheckWindowsStateAncestorPass(SafeFileHandle root, WindowsStateExclusions excluded, CancellationToken token, Action? beforeRefresh)
    {
        var parents = new List<SafeFileHandle>();
        var chain = new List<(SafeFileHandle Handle, NativeIdentity Identity, string Path)>();
        try
        {
            var current = root;
            for (var depth = 0; depth < 128; depth++)
            {
                token.ThrowIfCancellationRequested();
                var before = ReadIdentity(current);
                if (!before.IsDirectory || before.IsReparsePoint) throw new IOException("State ancestor is not an ordinary directory.");
                var canonical = ReadFinalPath(current);
                NativeStateDirectory.CheckExcluded(canonical, excluded.Paths);
                before = InspectWindowsStateAncestor(current, excluded, token);
                chain.Add((current, before, canonical));
                var parentPath = Path.GetDirectoryName(canonical);
                if (parentPath is null) break;
                // A pathname only locates a candidate parent. Opening its child
                // relative to that held parent must prove the full ID/generation
                // matches the already-held child before it joins the authority chain.
                var parent = OpenCanonicalDirectory(parentPath);
                parents.Add(parent);
                using var child = OpenEntryAt(parent, Path.GetFileName(canonical), true, false, requestDeleteAccess: false);
                VerifyStateRelationship(ReadIdentity(current), ReadIdentity(child));
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
                var refreshed = InspectWindowsStateAncestor(item.Handle, excluded, token);
                if (index + 1 < chain.Count)
                {
                    using var child = OpenEntryAt(chain[index + 1].Handle, Path.GetFileName(item.Path), true, false, requestDeleteAccess: false);
                    VerifyStateRelationship(refreshed, ReadIdentity(child));
                }
            }
        }
        finally { foreach (var parent in parents.AsEnumerable().Reverse()) parent.Dispose(); }
    }

    private static NativeIdentity InspectWindowsStateAncestor(SafeFileHandle directory, WindowsStateExclusions excluded, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var before = ReadIdentity(directory);
        foreach (var payload in excluded.Pinned)
        {
            token.ThrowIfCancellationRequested();
            if (before.Volume == payload.Volume && before.FileIdLow == payload.FileIdLow && before.FileIdHigh == payload.FileIdHigh)
                throw new IOException("State lies inside a pinned installed payload.");
        }
        try
        {
            using var skill = OpenEntryAt(directory, "SKILL.md", directory: null, enumerateDirectory: false, requestDeleteAccess: false);
            throw new IOException("State lies inside a skill-bearing ancestor.");
        }
        catch (WindowsOpenException ex) when (ex.NativeError is ErrorFileNotFound or ErrorPathNotFound) { }
        VerifyStateRelationship(before, ReadIdentity(directory));
        return before;
    }

    private static void VerifyStateRelationship(NativeIdentity expected, NativeIdentity actual)
    {
        if (expected.Volume != actual.Volume || expected.FileIdLow != actual.FileIdLow || expected.FileIdHigh != actual.FileIdHigh
            || expected.IsDirectory != actual.IsDirectory || expected.IsReparsePoint != actual.IsReparsePoint
            || expected.CreationTime != actual.CreationTime)
            throw new IOException("State ancestor relationship changed.");
        if (!Matches(expected, actual)) throw new StatePolicyRefreshException();
    }

    [SupportedOSPlatform("windows")]
    private static void CheckWindowsPrivateState(SafeFileHandle directory, SecurityIdentifier user)
    {
        var result = StateNative.GetSecurityInfo(directory, 1, 1 | 4, out _, out _, out _, out _, out var security);
        if (result != 0) throw new IOException("Cannot inspect private state ACL: " + new Win32Exception((int)result).Message);
        try
        {
            var length = StateNative.GetSecurityDescriptorLength(security);
            if (length == 0 || length > 64 * 1024) throw new IOException("State security descriptor exceeds its bound.");
            var bytes = new byte[length]; Marshal.Copy(security, bytes, 0, bytes.Length);
            var descriptor = new RawSecurityDescriptor(bytes, 0);
            if (descriptor.Owner != user || descriptor.DiscretionaryAcl is null)
                throw new IOException("State must have a current-user-owned private ACL.");
            foreach (GenericAce ace in descriptor.DiscretionaryAcl)
            {
                if (ace is not CommonAce common) throw new IOException("State has an unsupported ACL entry.");
                if (common.AceQualifier == AceQualifier.AccessAllowed && common.AccessMask != 0
                    && common.SecurityIdentifier != user && !common.SecurityIdentifier.IsWellKnown(WellKnownSidType.LocalSystemSid))
                    throw new IOException("State ACL grants another principal access; choose a private state destination.");
            }
        }
        finally { StateNative.LocalFree(security); }
    }

    [SupportedOSPlatform("windows")]
    private sealed class WindowsStateSession(List<SafeFileHandle> held, SafeFileHandle root, string canonical,
        WindowsStateExclusions excluded, SecurityIdentifier user, byte[] descriptor) : NativeStateDirectory
    {
        internal override string CanonicalPath => canonical;
        internal override void ReceiveDirectory(NativeDirectoryMoveLease source, string name, CancellationToken token)
        {
            ValidateLocation(token);
            NativeStateDirectory.CheckName(name);
            if (source is not WindowsDirectoryMove move) throw new IOException("Directory relocation platform mismatch.");
            move.Move(root, name, canonical, token);
            ValidateLocation(token);
        }
        internal override void ValidateLocation(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!PathIdentity.Equals(ReadFinalPath(root), canonical)) throw new IOException("State directory moved; retained evidence needs review.");
            CheckWindowsStateAncestors(root, excluded, token); CheckWindowsPrivateState(root, user);
        }
        internal override FileStream OpenFile(string name, bool writerLock, CancellationToken token)
        {
            ValidateLocation(token);
            var handle = CreateStateEntryAt(root, name, false, writerLock, descriptor, exclusive: true, token);
            try { CheckWindowsPrivateState(handle, user); return new FileStream(handle, FileAccess.ReadWrite); }
            catch { handle.Dispose(); throw; }
        }
        internal override FileStream OpenExistingFile(string name, bool writerLock, CancellationToken token)
        {
            ValidateLocation(token);
            var handle = CreateStateEntryAt(root, name, false, false, descriptor, exclusive: true, token, existingOnly: true);
            try { CheckWindowsPrivateState(handle, user); return new FileStream(handle, FileAccess.Read); }
            catch { handle.Dispose(); throw; }
        }
        internal override IEnumerable<(string Name, long Bytes, string? Error)> EnumerateEntries(CancellationToken token)
        {
            ValidateLocation(token);
            using var frame = new DirectoryFrame(root, canonical, ReadIdentity(root), 0, ownsDirectory: false);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!frame.TryReadNext(out var entry, out var error))
                { if (error is not null) throw new IOException(error); yield break; }
                if (entry is null) continue;
                if ((entry.Value.Attributes & (FileAttributeDirectory | FileAttributeReparsePoint)) != 0)
                { yield return (entry.Value.Name, 0, "Unexpected directory/link in state."); continue; }
                // The exclusive writer lock is already verified as zero length.
                if (entry.Value.Name == "writer.lock") { yield return (entry.Value.Name, 0, null); continue; }
                yield return ObserveEntry(entry.Value);
            }
        }
        private (string Name, long Bytes, string? Error) ObserveEntry(DirectoryEntry entry)
        {
            try
            {
                using var child = OpenEntryAt(root, entry.Name, null, false, requestDeleteAccess: false, requestReadAccess: true);
                var identity = ReadIdentity(child);
                if (identity.IsDirectory || identity.IsReparsePoint || identity.FileIdLow != entry.FileIdLow
                    || identity.FileIdHigh != entry.FileIdHigh || WindowsNative.GetFileType(child) != 1)
                    return (entry.Name, 0, "State entry identity/type changed.");
                using var stream = new FileStream(child, FileAccess.Read);
                return (entry.Name, stream.Length, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return (entry.Name, 0, ex.Message); }
        }
        internal override void FlushPublication(CancellationToken token)
        {
            ValidateLocation(token);
            // Each newly-created directory and generated file was opened with
            // native FILE_WRITE_THROUGH; generated files are also Flush(true)'d.
        }
        public override void Dispose() { foreach (var directory in held.AsEnumerable().Reverse()) directory.Dispose(); }
    }

    private static class StateNative
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("ntdll.dll")]
        internal static extern int NtCreateFile(out IntPtr handle, uint access, ref ObjectAttributes attributes,
            out IoStatusBlock status, IntPtr allocationSize, uint fileAttributes, uint share, uint disposition,
            uint options, IntPtr ea, uint eaLength);
        [DllImport("advapi32.dll")]
        internal static extern uint GetSecurityInfo(SafeFileHandle handle, int type, uint information,
            out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr security);
        [DllImport("advapi32.dll")]
        internal static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
        [DllImport("kernel32.dll")]
        internal static extern IntPtr LocalFree(IntPtr memory);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(IntPtr handle);
    }
}
