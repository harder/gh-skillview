using System.Buffers.Binary;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SkillView.Inventory;

internal sealed partial class WindowsSecureRemovalBackend
{
    [SupportedOSPlatform("windows")]
    internal static NativeExposureDirectory OpenExposureDirectory(string rootPath, string entry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var parts = GetRelativeComponents(rootPath, entry);
        if (parts.Length > SkillView.Maintenance.PayloadSnapshot.MaxDepth) throw new IOException("Exposure authority depth exceeded.");
        SafeFileHandle? root = null, parent = null;
        try
        {
            root = OpenCanonicalDirectory(rootPath, enumerateDirectory: true);
            var before = ReadIdentity(root); parent = OpenRelativeParent(root, parts);
            var heldParent = parent ?? root;
            // Enumerating the immediate namespace requires LIST_DIRECTORY on
            // the already root-bound parent, not an independently opened path.
            if (parent is not null)
            {
                var parentParts = parts[..^1]; SafeFileHandle cursor = root; SafeFileHandle? owned = null;
                try
                {
                    foreach (var part in parentParts)
                    {
                        token.ThrowIfCancellationRequested();
                        SafeFileHandle? next = OpenEntryAt(cursor, part, true, true, requestDeleteAccess: false);
                        try
                        {
                            var observed = ReadIdentity(next);
                            if (observed.IsReparsePoint || observed.Volume != before.Volume)
                                throw new IOException("Exposure parent crosses a reparse/volume boundary.");
                            owned?.Dispose(); owned = next; cursor = next; next = null;
                        }
                        finally { next?.Dispose(); }
                    }
                    if (!Matches(ReadIdentity(parent), ReadIdentity(cursor))) throw new IOException("Exposure parent changed while acquiring enumeration authority.");
                    parent.Dispose(); parent = owned; owned = null; heldParent = parent!;
                }
                finally { owned?.Dispose(); }
            }
            var identity = CaptureStableRootIdentity(root, before); var parentPath = ReadFinalPath(heldParent);
            if (!PathIdentity.Equals(parentPath, identity.CanonicalPath) && !PathIdentity.IsInside(parentPath, identity.CanonicalPath))
                throw new IOException("Exposure parent escapes its held root.");
            var result = new WindowsExposureDirectory(root, heldParent, identity.CanonicalPath, parentPath, true);
            root = parent = null; return result;
        }
        finally { parent?.Dispose(); root?.Dispose(); }
    }

    [SupportedOSPlatform("windows")]
    internal static NativeExposureDirectory BindExposureSlot(NativeStateDirectory state, CancellationToken token) =>
        state is WindowsStateSession session ? session.BindExposureSlot(token) : throw new IOException("Retained-link state platform mismatch.");

    private sealed partial class WindowsStateSession
    {
        internal NativeExposureDirectory BindExposureSlot(CancellationToken token)
        {
            ValidateLocation(token);
            return new WindowsExposureDirectory(root, root, canonical, canonical, false, ValidateLocation);
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class WindowsExposureDirectory(SafeFileHandle root, SafeFileHandle parent, string rootPath, string parentPath,
        bool ownsHandles, Action<CancellationToken>? guard = null) : NativeExposureDirectory
    {
        private SafeFileHandle ParentHandle => parent;
        internal override ExposureDirectoryEvidence Capture(CancellationToken token)
        {
            CheckAddress(token); var before = ReadIdentity(parent); var rootBefore = ReadIdentity(root);
            var entries = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            using var frame = new DirectoryFrame(parent, parentPath, before, 0, ownsDirectory: false);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!frame.TryReadNext(out var item, out var error)) { if (error is not null) throw new IOException(error); break; }
                if (item is null) continue;
                if (entries.Count >= 4096) throw new IOException("Exposure namespace exceeds4096 entries.");
                using var child = OpenEntryAt(parent, item.Value.Name, null, false, requestDeleteAccess: false);
                var observed = ReadIdentity(child);
                if (observed.FileIdLow != item.Value.FileIdLow || observed.FileIdHigh != item.Value.FileIdHigh
                    || observed.IsDirectory != ((item.Value.Attributes & FileAttributeDirectory) != 0)
                    || observed.IsReparsePoint != ((item.Value.Attributes & FileAttributeReparsePoint) != 0))
                    throw new IOException("Exposure entry changed before namespace capture.");
                var raw = observed.IsReparsePoint ? ReadReparseData(child) : null;
                if (!Matches(observed, ReadIdentity(child))) throw new IOException("Exposure entry changed during namespace capture.");
                entries.Add(item.Value.Name, EntryHash(ToSecureIdentity(observed, ""), raw));
            }
            if (!Matches(before, ReadIdentity(parent)) || !Matches(rootBefore, ReadIdentity(root)))
                throw new IOException("Exposure root/parent changed during namespace capture.");
            CheckAddress(token);
            return new(ToSecureIdentity(rootBefore, rootPath), ToSecureIdentity(before, parentPath), entries.ToImmutable());
        }
        internal override ExposureLinkEvidence ReadLink(string name, CancellationToken token)
        {
            NativeStateDirectory.CheckName(name); CheckAddress(token); var beforeParent = ReadIdentity(parent);
            using var link = OpenEntryAt(parent, name, true, false, requestDeleteAccess: false);
            var before = ReadIdentity(link);
            if (!before.IsDirectory || !before.IsReparsePoint) throw new IOException("Exposure must be a directory symbolic link.");
            var raw = ReadReparseData(link); _ = ParseExposureTarget(raw);
            var identity = ToSecureIdentity(before, Path.Combine(parentPath, name));
            var metadata = CapturePayloadMetadata(new(name, "link", null, null, raw, null, identity, link, parent, name), token);
            metadata.Validate();
            if (!Matches(before, ReadIdentity(link)) || !Matches(beforeParent, ReadIdentity(parent)) || !raw.AsSpan().SequenceEqual(ReadReparseData(link)))
                throw new IOException("Exposure link/parent changed during metadata capture.");
            return new(identity, raw.ToImmutableArray(), metadata, MetadataHash(metadata));
        }
        internal override NativeExposureLinkPin PinLink(string name, ExposureLinkEvidence expected, CancellationToken token)
        {
            NativeStateDirectory.CheckName(name); CheckAddress(token);
            var handle = OpenEntryAt(parent, name, true, false, requestDeleteAccess: true);
            try
            {
                if (!Matches(expected.Identity, ReadIdentity(handle))) throw new IOException("Link changed before its native object was pinned.");
                return NativeExposureLinkPin.Wrap(handle);
            }
            catch { handle.Dispose(); throw; }
        }
        internal override SecureFileIdentity ResolveLink(string name, CancellationToken token)
        {
            NativeStateDirectory.CheckName(name); CheckAddress(token); var before = ReadIdentity(parent);
            using var target = OpenEntryAt(parent, name, true, true, openReparsePoint: false, requestDeleteAccess: false);
            return TargetIdentity(target, before, token);
        }
        internal override SecureFileIdentity ResolveRawTarget(ReadOnlySpan<byte> raw, CancellationToken token)
        {
            CheckAddress(token); var before = ReadIdentity(parent); var address = ReadFinalPath(parent);
            var parsed = ParseExposureTarget(raw);
            using var target = OpenExposureRawTarget(parent, parsed.Path, parsed.Relative, token);
            var result = TargetIdentity(target, before, token);
            if (!PathIdentity.Equals(address, ReadFinalPath(parent)))
                throw new IOException("Original exposure parent moved during raw target resolution.");
            return result;
        }
        private SecureFileIdentity TargetIdentity(SafeFileHandle target, NativeIdentity parentBefore, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var observed = ReadIdentity(target);
            if (!observed.IsDirectory || observed.IsReparsePoint || observed.Volume != parentBefore.Volume)
                throw new IOException("Exposure target must be an ordinary same-volume directory.");
            var result = new WindowsSecureRemovalBackend().CaptureDirectoryValidation(target, ReadFinalPath(target), token).Identity;
            if (!Matches(parentBefore, ReadIdentity(parent))) throw new IOException("Exposure parent changed during target resolution.");
            CheckAddress(token); return result;
        }
        internal override void MoveExclusive(string name, ExposureLinkEvidence expected, ExposureDirectoryEvidence sourceBefore,
            NativeExposureDirectory destination, string destinationName, ExposureDirectoryEvidence destinationBefore, Action<CancellationToken>? beforeNativeMove, CancellationToken token)
        {
            if (destination is not WindowsExposureDirectory other) throw new IOException("Link relocation platform mismatch.");
            NativeStateDirectory.CheckName(name); NativeStateDirectory.CheckName(destinationName);
            CheckAddress(token); other.CheckAddress(token);
            NativeExposureLinkLease.SameLink(expected with { Preservation = null }, ReadLink(name, token), true);
            using var link = OpenEntryAt(parent, name, true, false, requestDeleteAccess: true);
            if (!Matches(expected.Identity, ReadIdentity(link))) throw new IOException("Link changed before rename handle acquisition.");
            if (ReadIdentity(parent).Volume != ReadIdentity(other.ParentHandle).Volume) throw new IOException("Cross-volume link relocation refused.");
            var filename = Encoding.Unicode.GetBytes(destinationName); var size = checked(20 + filename.Length);
            var information = Marshal.AllocHGlobal(size);
            try
            {
                // Same verified FILE_RENAME_INFORMATION ABI as directory moves;
                // ReplaceIfExists remains zero, so foreign slots are untouched.
                Marshal.Copy(new byte[size], 0, information, size);
                Marshal.WriteIntPtr(information, 8, other.ParentHandle.DangerousGetHandle());
                Marshal.WriteInt32(information, 16, filename.Length); Marshal.Copy(filename, 0, information + 20, filename.Length);
                beforeNativeMove?.Invoke(token);
                Validate(sourceBefore, token); other.Validate(destinationBefore, token);
                if (!Matches(expected.Identity, ReadIdentity(link))) throw new IOException("Pinned link generation changed immediately before rename.");
                token.ThrowIfCancellationRequested();
                var status = MoveNative.NtSetInformationFile(link, out _, information, checked((uint)size), 10);
                if (status != 0) throw new IOException("Exclusive link relocation failed: " + new Win32Exception(unchecked((int)WindowsNative.RtlNtStatusToDosError(status))).Message);
            }
            finally { Marshal.FreeHGlobal(information); }
        }
        private void CheckAddress(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); guard?.Invoke(token);
            if (!PathIdentity.Equals(ReadFinalPath(root), rootPath) || !PathIdentity.Equals(ReadFinalPath(parent), parentPath))
                throw new IOException("Held exposure root/parent moved.");
        }
        // Existing Windows native rename has no separate namespace durability
        // attestation. A future journal must reconcile both names after restart.
        internal override void Flush(CancellationToken token) => CheckAddress(token);
        public override void Dispose() { if (ownsHandles) { if (!ReferenceEquals(root, parent)) parent.Dispose(); root.Dispose(); } }
    }

    private static (string Path, bool Relative) ParseExposureTarget(ReadOnlySpan<byte> raw)
    {
        if (!IsSupportedMetadataReparse(raw)) throw new IOException("Junction/mount/unknown reparse exposure refused.");
        var offset = BinaryPrimitives.ReadUInt16LittleEndian(raw[8..]); var length = BinaryPrimitives.ReadUInt16LittleEndian(raw[10..]);
        var printOffset = BinaryPrimitives.ReadUInt16LittleEndian(raw[12..]); var printLength = BinaryPrimitives.ReadUInt16LittleEndian(raw[14..]);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(raw[16..]);
        if (flags > 1 || length == 0 || ((offset | length | printOffset | printLength) & 1) != 0
            || 20L + offset + length > raw.Length || 20L + printOffset + printLength > raw.Length)
            throw new IOException("Malformed symbolic-link substitute/print target.");
        var path = new UnicodeEncoding(false, false, true).GetString(raw.Slice(20 + offset, length));
        if (path.Contains('\0') || flags == 1 && (Path.IsPathRooted(path) || path.Contains(':'))
            || flags == 0 && !(path.StartsWith("\\??\\", StringComparison.Ordinal) && path.Length > 7
                && char.IsAsciiLetter(path[4]) && path[5] == ':' && path[6] == '\\'))
            throw new IOException("Only relative or local-drive absolute symbolic-link targets are supported.");
        return (path, flags == 1);
    }

    [SupportedOSPlatform("windows")]
    private static SafeFileHandle OpenExposureRawTarget(SafeFileHandle originalParent, string path, bool relative, CancellationToken token)
    {
        // This intentionally resolves the captured raw path from the original
        // held parent. It is read-only and its resulting full identity/address
        // must match the separately held target, before any rename is admitted.
        if (path.Split('\\', '/').Any(component => component is "." or ".."))
        {
            // Windows expands dot components in a symbolic link's substituted
            // path. NtOpenFile does not perform that DOS-path normalization.
            // This is only a READ-ONLY candidate witness, never rename/delete
            // authority: its opened full native identity/generation/address
            // still must equal the separately held target. Keep no-dot NT
            // paths (including their intermediate reparse behavior) unchanged.
            token.ThrowIfCancellationRequested();
            var originalAddress = ReadFinalPath(originalParent);
            if (!IsExposureLocalDrivePath(originalAddress))
                throw new IOException("Dot-containing raw target requires a held local-drive original parent.");
            string normalized;
            try { normalized = Path.GetFullPath(relative ? path : path[4..], originalAddress); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException)
            { throw new IOException("Raw exposure target normalization is unavailable.", error); }
            if (!IsExposureLocalDrivePath(normalized))
                throw new IOException("Normalized raw exposure target is not a supported local-drive address.");
            path = "\\??\\" + normalized;
            relative = false;
        }
        if (path.Length > (ushort.MaxValue - sizeof(char)) / sizeof(char))
            throw new IOException("Raw exposure target exceeds the bounded native path length.");
        IntPtr text = IntPtr.Zero, unicodeAddress = IntPtr.Zero, raw = IntPtr.Zero;
        try
        {
            text = Marshal.StringToHGlobalUni(path);
            unicodeAddress = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            var unicode = new UnicodeString { Length = checked((ushort)(path.Length * 2)), MaximumLength = checked((ushort)((path.Length + 1) * 2)), Buffer = text };
            Marshal.StructureToPtr(unicode, unicodeAddress, false);
            var attributes = new ObjectAttributes { Length = (uint)Marshal.SizeOf<ObjectAttributes>(), RootDirectory = relative ? originalParent.DangerousGetHandle() : IntPtr.Zero, ObjectName = unicodeAddress };
            token.ThrowIfCancellationRequested();
            var status = WindowsNative.NtOpenFile(out raw, FileReadAttributes | FileListDirectory | SynchronizeAccess, ref attributes, out _,
                ShareRead | ShareWrite | ShareDelete, FileOpenForBackupIntent | FileSynchronousIoNonAlert | FileDirectoryFile | FileOpenReparsePoint);
            if (status != 0) throw new WindowsOpenException(path, unchecked((int)WindowsNative.RtlNtStatusToDosError(status)));
            var result = new SafeFileHandle(raw, true); raw = IntPtr.Zero; return result;
        }
        finally
        {
            if (raw != IntPtr.Zero && raw != new IntPtr(-1)) new SafeFileHandle(raw, true).Dispose();
            if (unicodeAddress != IntPtr.Zero) Marshal.FreeHGlobal(unicodeAddress);
            if (text != IntPtr.Zero) Marshal.FreeHGlobal(text);
        }
    }
    private static bool IsExposureLocalDrivePath(string path) => path.Length >= 3
        && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';

}
