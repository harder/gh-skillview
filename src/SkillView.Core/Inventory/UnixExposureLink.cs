using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;

namespace SkillView.Inventory;

internal sealed partial class UnixSecureRemovalBackend
{
    internal static NativeExposureDirectory OpenExposureDirectory(string rootPath, string entry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var parts = GetRelativeComponents(rootPath, entry);
        if (parts.Length > SkillView.Maintenance.PayloadSnapshot.MaxDepth) throw new IOException("Exposure authority depth exceeded.");
        SafeUnixHandle? root = null, parent = null;
        try
        {
            root = OpenDirectory(RealPath(rootPath));
            using var stat = new StatBuffer();
            var before = ReadStat(root, stat.Pointer);
            parent = OpenRelativeParent(root, parts, before.Device, stat.Pointer);
            var heldParent = parent ?? root;
            var rootIdentity = CaptureStableRootIdentity(root, before, stat);
            var parentPath = ReadFinalPath(heldParent);
            if (!PathIdentity.Equals(parentPath, rootIdentity.CanonicalPath) && !PathIdentity.IsInside(parentPath, rootIdentity.CanonicalPath))
                throw new IOException("Exposure parent escapes the held root.");
            var authority = new UnixExposureDirectory(root, heldParent, rootIdentity.CanonicalPath, parentPath, ownsHandles: true);
            root = parent = null;
            return authority;
        }
        finally { parent?.Dispose(); root?.Dispose(); }
    }

    internal static NativeExposureDirectory BindExposureSlot(NativeStateDirectory state, CancellationToken token) =>
        state is UnixStateSession session ? session.BindExposureSlot(token) : throw new IOException("Retained-link state platform mismatch.");

    private sealed partial class UnixStateSession
    {
        internal NativeExposureDirectory BindExposureSlot(CancellationToken token)
        {
            ValidateLocation(token);
            return new UnixExposureDirectory(root, root, canonical, canonical, false, ValidateLocation);
        }
    }

    private sealed partial class UnixExposureDirectory(SafeUnixHandle root, SafeUnixHandle parent, string rootPath, string parentPath,
        bool ownsHandles, Action<CancellationToken>? guard = null) : NativeExposureDirectory
    {
        private SafeUnixHandle ParentHandle => parent;
        internal override ExposureDirectoryEvidence Capture(CancellationToken token)
        {
            CheckAddress(token);
            using var buffer = new StatBuffer();
            var rootBefore = ReadStat(root, buffer.Pointer); var before = ReadStat(parent, buffer.Pointer);
            var entries = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            using var frame = new DirectoryFrame(parent, parent, "", parentPath, 0, false, false);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!frame.TryReadNext(out var item, out var error))
                { if (error is not null) throw new IOException(error); break; }
                if (item is null) continue;
                if (entries.Count >= 4096) throw new IOException("Exposure namespace exceeds4096 entries.");
                if (!TryReadStatAt(parent, item.Value.Name, buffer.Pointer, out var observed, out var failure)
                    || item.Value.Inode != 0 && item.Value.Inode != observed.Inode)
                    throw new IOException(failure ?? "Exposure namespace changed.");
                var raw = observed.IsSymlink ? RawLink(parent, item.Value.Name, token) : null;
                if (!TryReadStatAt(parent, item.Value.Name, buffer.Pointer, out var after, out failure) || !Matches(observed, after))
                    throw new IOException(failure ?? "Exposure entry changed during namespace capture.");
                entries.Add(item.Value.Name, EntryHash(ToSecureIdentity(observed, ""), raw));
            }
            if (!Matches(before, ReadStat(parent, buffer.Pointer)) || !Matches(rootBefore, ReadStat(root, buffer.Pointer)))
                throw new IOException("Exposure parent/root changed during namespace capture.");
            CheckAddress(token);
            return new(ToSecureIdentity(rootBefore, rootPath), ToSecureIdentity(before, parentPath), entries.ToImmutable());
        }

        internal override ExposureLinkEvidence ReadLink(string name, CancellationToken token)
        {
            NativeStateDirectory.CheckName(name); CheckAddress(token);
            using var stat = new StatBuffer();
            var beforeParent = ReadStat(parent, stat.Pointer);
            if (!TryReadStatAt(parent, name, stat.Pointer, out var before, out var error) || !before.IsSymlink)
                throw new IOException(error ?? "The selected exposure is not an ordinary symlink.");
            var raw = RawLink(parent, name, token);
            // Strict decoding makes later raw-target interpretation explicit;
            // never normalize invalid native bytes into a different pathname.
            _ = new UTF8Encoding(false, true).GetString(raw);
            var identity = ToSecureIdentity(before, Path.Combine(parentPath, name));
            var metadata = SkillView.Maintenance.PayloadNativeMetadata.Capture(new(name, "link", null, (int)(before.Mode & 0xFFF), raw,
                null, identity, NativeParentHandle: parent, NativeName: name), token);
            metadata.Validate();
            if (!TryReadStatAt(parent, name, stat.Pointer, out var after, out error) || !Matches(before, after)
                || !Matches(beforeParent, ReadStat(parent, stat.Pointer)) || !raw.AsSpan().SequenceEqual(RawLink(parent, name, token)))
                throw new IOException(error ?? "Link or parent changed during metadata capture.");
            return new(identity, raw.ToImmutableArray(), metadata, MetadataHash(metadata));
        }

        internal override NativeExposureLinkPin PinLink(string name, ExposureLinkEvidence expected, CancellationToken token)
        {
            NativeStateDirectory.CheckName(name); CheckAddress(token);
            var flags = OperatingSystem.IsMacOS() ? 0x00200000 | 0x01000000 : LinuxOpenFlags.Path | LinuxOpenFlags.NoFollow | LinuxOpenFlags.CloseOnExec;
            var handle = CreateHandle(OpenAtWithRetry(parent.FileDescriptor, name, flags), "pin exposure link without following target");
            try
            {
                using var stat = new StatBuffer();
                if (!Matches(expected.Identity, ReadStat(handle, stat.Pointer))) throw new IOException("Link changed before its native object was pinned.");
                return new UnixExposurePin(handle);
            }
            catch { handle.Dispose(); throw; }
        }

        internal override SecureFileIdentity ResolveLink(string name, CancellationToken token)
        { NativeStateDirectory.CheckName(name); return Resolve(name, token); }
        internal override SecureFileIdentity ResolveRawTarget(ReadOnlySpan<byte> raw, CancellationToken token)
        {
            if (raw.IsEmpty || raw.Length > 32768 || raw.Contains((byte)0)) throw new IOException("Unsupported raw Unix link target.");
            var path = new UTF8Encoding(false, true).GetString(raw);
            // A final slash makes Unix open follow a final symlink despite
            // O_NOFOLLOW. Strip separators only from this no-follow candidate;
            // preserve root-only addresses, dot components and stored raw bytes.
            var candidate = path.TrimEnd('/');
            return Resolve(candidate.Length == 0 ? path : candidate, token, followFinal: false);
        }
        private SecureFileIdentity Resolve(string path, CancellationToken token, bool followFinal = true)
        {
            CheckAddress(token); using var stat = new StatBuffer();
            var before = ReadStat(parent, stat.Pointer);
            token.ThrowIfCancellationRequested();
            using var target = CreateHandle(OpenAtWithRetry(parent.FileDescriptor, path,
                followFinal ? UnixConstants.DirectoryFollowOpenFlags : UnixConstants.DirectoryOpenFlags), "open exposure target");
            var targetStat = ReadStat(target, stat.Pointer);
            if (!targetStat.IsDirectory || targetStat.IsSymlink || targetStat.Device != before.Device)
                throw new IOException("Exposure target must be an ordinary same-filesystem directory.");
            var identity = new UnixSecureRemovalBackend().CaptureDirectoryValidation(target, path, stat).Identity;
            if (!Matches(before, ReadStat(parent, stat.Pointer))) throw new IOException("Exposure parent changed during target resolution.");
            CheckAddress(token); return identity;
        }

        internal override void MoveExclusive(string name, ExposureLinkEvidence expected, ExposureDirectoryEvidence sourceBefore,
            NativeExposureDirectory destination, string destinationName, ExposureDirectoryEvidence destinationBefore, Action<CancellationToken>? beforeNativeMove, CancellationToken token)
        {
            if (destination is not UnixExposureDirectory other) throw new IOException("Link relocation platform mismatch.");
            NativeStateDirectory.CheckName(name); NativeStateDirectory.CheckName(destinationName);
            CheckAddress(token); other.CheckAddress(token);
            NativeExposureLinkLease.SameLink(expected with { Preservation = null }, ReadLink(name, token), true);
            using var stat = new StatBuffer();
            if (ReadStat(parent, stat.Pointer).Device != ReadStat(other.ParentHandle, stat.Pointer).Device) throw new IOException("Cross-filesystem link relocation refused.");
            beforeNativeMove?.Invoke(token);
            Validate(sourceBefore, token); other.Validate(destinationBefore, token);
            token.ThrowIfCancellationRequested();
            // The final checked-name interval is not atomic against same-UID
            // writers. Post-rename identity checks stop all further transitions.
            var result = OperatingSystem.IsMacOS()
                ? MoveNative.renameatx_np(parent.FileDescriptor, name, other.ParentHandle.FileDescriptor, destinationName, 4)
                : MoveNative.renameat2(parent.FileDescriptor, name, other.ParentHandle.FileDescriptor, destinationName, 1);
            if (result != 0) throw new IOException(LastError("Exclusive link relocation failed"));
        }

        private void CheckAddress(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); guard?.Invoke(token);
            if (!PathIdentity.Equals(ReadFinalPath(root), rootPath) || !PathIdentity.Equals(ReadFinalPath(parent), parentPath))
                throw new IOException("Held exposure parent/root moved.");
        }
        internal override void Flush(CancellationToken token) { CheckAddress(token); FlushStateDirectory(parent, token); }
        public override void Dispose() { if (ownsHandles) { if (!ReferenceEquals(root, parent)) parent.Dispose(); root.Dispose(); } }
    }

    private static byte[] RawLink(SafeUnixHandle parent, string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var bytes = new byte[32768];
        var length = ReadLinkAtWithRetry(parent.FileDescriptor, name, bytes);
        if (length <= 0 || length >= bytes.Length) throw new IOException("Link target unavailable or exceeds its byte limit.");
        return bytes.AsSpan(0, (int)length).ToArray();
    }
}
