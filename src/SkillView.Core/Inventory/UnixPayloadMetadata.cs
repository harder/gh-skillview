using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SkillView.Maintenance;

namespace SkillView.Inventory;

internal sealed partial class UnixSecureRemovalBackend
{
    internal static void ApplyPayloadMetadata(SafeFileHandle target, PayloadNativeMetadata metadata, int? mode, CancellationToken token, PayloadMetadataAuthority? authority = null)
    {
        var retained = false;
        try
        {
            target.DangerousAddRef(ref retained);
            ApplyPayloadMetadataCore(target.DangerousGetHandle(), metadata, mode, token, authority);
        }
        catch (ObjectDisposedException ex) { throw new IOException("The staged metadata handle is no longer active.", ex); }
        finally { if (retained) target.DangerousRelease(); }
    }

    private static void ApplyPayloadMetadataCore(IntPtr descriptor, PayloadNativeMetadata metadata, int? mode, CancellationToken token, PayloadMetadataAuthority? authority)
    {
        if (OperatingSystem.IsWindows()) throw new IOException("Unix metadata is unavailable on Windows.");
        token.ThrowIfCancellationRequested();
        if (!metadata.Supported || metadata.Platform != (OperatingSystem.IsMacOS() ? "macos" : "linux")
            || metadata.UserId != StateNative.geteuid() || metadata.GroupId is null
            || mode is null or < 0 or > 511 || metadata.Attributes.IsDefault || metadata.Attributes.Length > PayloadNativeMetadata.MaxAttributes)
            throw new IOException("Native metadata does not match the supported owner/platform/permission policy.");
        using var borrowed = new SafeUnixHandle(descriptor.ToInt32(), ownsHandle: false);
        using var stat = new StatBuffer();
        var before = ReadStat(borrowed, stat.Pointer);
        if (metadata.Kind switch { "directory" => !before.IsDirectory, "link" => !before.IsSymlink, "file" => before.FileType != 0x8000, _ => true })
            throw new IOException("Native metadata destination kind changed.");
        var destinationLinks = OperatingSystem.IsMacOS() ? unchecked((ulong)(ushort)Marshal.ReadInt16(stat.Pointer, 6))
            : RuntimeInformation.ProcessArchitecture == Architecture.X64 ? unchecked((ulong)Marshal.ReadInt64(stat.Pointer, 16))
            : unchecked((uint)Marshal.ReadInt32(stat.Pointer, 20));
        if (metadata.Kind != "directory" && destinationLinks != 1)
            throw new IOException("The staged metadata destination has an unsupported hard-link topology.");
        var destinationUid = unchecked((uint)Marshal.ReadInt32(stat.Pointer, OperatingSystem.IsMacOS() ? 16 : RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 28 : 24));
        var destinationGid = unchecked((uint)Marshal.ReadInt32(stat.Pointer, OperatingSystem.IsMacOS() ? 20 : RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 32 : 28));
        if (destinationUid != StateNative.geteuid()) throw new IOException("The staged metadata destination is not owned by the effective user.");
        if (metadata.Kind == "link" && OperatingSystem.IsLinux() && (mode != 511 || !metadata.Attributes.IsEmpty))
            throw new IOException("Linux link publication supports fixed mode0777 and no extended attributes.");
        if ((destinationUid != metadata.UserId || destinationGid != metadata.GroupId) && RetryOnInterrupted(() =>
            {
                token.ThrowIfCancellationRequested();
                return metadata.Kind == "link" && OperatingSystem.IsLinux()
                    ? MetadataNative.fchownat(borrowed.FileDescriptor, "", metadata.UserId.Value, metadata.GroupId.Value, 0x1000 | 0x100)
                    : MetadataNative.fchown(borrowed.FileDescriptor, metadata.UserId.Value, metadata.GroupId.Value);
            }) != 0)
            throw new IOException(LastError("preserve staged payload ownership failed"));
        long bytes = 0; var names = new HashSet<string>(StringComparer.Ordinal);
        var existing = ImmutableArray.CreateBuilder<PayloadExtendedAttribute>();
        var unsupported = ImmutableArray.CreateBuilder<string>();
        if (metadata.Kind != "link" || OperatingSystem.IsMacOS())
            ReadAttributes(borrowed, null, null, false, existing, unsupported, token);
        if (unsupported.Count != 0) throw new IOException("Staged extended attributes cannot be inspected completely: " + string.Join(" ", unsupported));
        // Newly-created private nodes can acquire attributes from their state
        // parent or the OS (for example macOS provenance). They were never
        // selected from I. Remove extras through this held node before copying
        // the exact reviewed attributes; never touch a live installation here.
        foreach (var extra in existing.Where(attribute => !metadata.Attributes.Any(wanted => wanted.Name == attribute.Name)))
        {
            if (RetryOnInterrupted(() =>
                {
                    token.ThrowIfCancellationRequested();
                    return OperatingSystem.IsMacOS() ? MetadataNative.fremovexattr_mac(borrowed.FileDescriptor, extra.Name, 0)
                        : MetadataNative.fremovexattr_linux(borrowed.FileDescriptor, extra.Name);
                }) != 0) throw new IOException(LastError("remove unselected staged extended attribute failed"));
        }
        foreach (var attribute in metadata.Attributes)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(attribute.Name) || attribute.Name.Contains('\0') || !names.Add(attribute.Name)
                || attribute.Value is null || attribute.Value.Length > PayloadNativeMetadata.MaxAttributeBytes
                || (bytes += Encoding.UTF8.GetByteCount(attribute.Name) + attribute.Value.Length) > PayloadNativeMetadata.MaxEntryMetadataBytes
                || OperatingSystem.IsLinux() && !attribute.Name.StartsWith("user.", StringComparison.Ordinal)
                || OperatingSystem.IsMacOS() && attribute.Name == "com.apple.decmpfs")
                throw new IOException("Native metadata attributes are inconsistent or unsupported.");
            var result = RetryOnInterrupted(() =>
            {
                token.ThrowIfCancellationRequested();
                return OperatingSystem.IsMacOS()
                    ? MetadataNative.fsetxattr_mac(borrowed.FileDescriptor, attribute.Name, attribute.Value, (nuint)attribute.Value.Length, 0, 0)
                    : MetadataNative.fsetxattr_linux(borrowed.FileDescriptor, attribute.Name, attribute.Value, (nuint)attribute.Value.Length, 0);
            });
            if (result != 0) throw new IOException(LastError("preserve staged payload extended attribute failed"));
        }
        token.ThrowIfCancellationRequested();
        using var modeHandle = new SafeFileHandle(descriptor, ownsHandle: false);
        if (metadata.Kind != "link" || !OperatingSystem.IsLinux()) File.SetUnixFileMode(modeHandle, (UnixFileMode)mode.Value);
        if (metadata.Kind != "link")
        {
            var actual = CapturePayloadMetadata(new("", metadata.Kind, null, mode, null, null,
                ToSecureIdentity(ReadStat(borrowed, stat.Pointer), ""), borrowed), token, authority);
            if (!actual.Supported) throw new IOException("Staged native metadata is unsupported: " + string.Join(" ", actual.Refusals));
            if (actual.UserId != metadata.UserId || actual.GroupId != metadata.GroupId)
                throw new IOException($"Staged ownership differs from review ({metadata.UserId}:{metadata.GroupId} -> {actual.UserId}:{actual.GroupId}).");
            if (!SameAttributes(metadata.Attributes, actual.Attributes))
                throw new IOException("Staged extended attributes differ from review. Expected: " + string.Join(", ", metadata.Attributes.Select(item => item.Name))
                    + "; observed: " + string.Join(", ", actual.Attributes.Select(item => item.Name)) + ".");
        }
    }

    private static bool SameAttributes(ImmutableArray<PayloadExtendedAttribute> left, ImmutableArray<PayloadExtendedAttribute> right) =>
        left.Length == right.Length && left.All(expected => right.Any(actual => actual.Name == expected.Name && actual.Value.AsSpan().SequenceEqual(expected.Value)));

    internal static PayloadNativeMetadata CapturePayloadMetadata(NativePayloadItem item, CancellationToken token, PayloadMetadataAuthority? authority = null)
    {
        var attributes = ImmutableArray.CreateBuilder<PayloadExtendedAttribute>();
        var refusals = ImmutableArray.CreateBuilder<string>();
        uint? uid = null, gid = null; ulong? links = null; int? mode = item.UnixMode;
        var coverage = OperatingSystem.IsMacOS() ? "macos-native-owner" : "caller-visible-xattr-names";
        SafeUnixHandle? ownedLink = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var handle = item.NativeHandle as SafeUnixHandle;
            var parent = item.NativeParentHandle as SafeUnixHandle;
            if (item.Kind == "link")
            {
                if (parent is null || item.NativeName is null) throw new IOException("A held link parent/name is required.");
                var flags = OperatingSystem.IsMacOS() ? 0x00200000 | 0x01000000 : LinuxOpenFlags.Path | LinuxOpenFlags.NoFollow | LinuxOpenFlags.CloseOnExec;
                ownedLink = CreateHandle(OpenAtWithRetry(parent.FileDescriptor, item.NativeName, flags), "open metadata link without following target");
                handle = ownedLink;
            }
            if (handle is null || handle.IsClosed) throw new IOException("A live held metadata handle is required.");
            using var stat = new StatBuffer();
            var before = ReadStat(handle, stat.Pointer);
            if (!Matches(item.Identity, before)) throw new IOException("Payload metadata object changed before inspection.");
            mode = (int)(before.Mode & 0xFFF);
            if (mode != item.UnixMode) throw new IOException("Payload metadata mode differs from its captured object.");
            if (OperatingSystem.IsMacOS())
            {
                uid = unchecked((uint)Marshal.ReadInt32(stat.Pointer, 16));
                gid = unchecked((uint)Marshal.ReadInt32(stat.Pointer, 20));
                links = unchecked((ushort)Marshal.ReadInt16(stat.Pointer, 6));
                if (Marshal.ReadInt32(stat.Pointer, 116) != 0) refusals.Add("BSD file flags require a dedicated metadata transition.");
                // The existing ACL helper accepts only a proven empty Darwin
                // extended ACL. A nonempty ACL is an explicit refusal, not an
                // assumption that chmod can preserve its inheritance semantics.
                try { CheckPrivateStateAcl(handle); }
                catch (IOException) { refusals.Add("Extended ACLs or unavailable ACL inspection require a metadata-preserving adapter."); }
            }
            else
            {
                if (authority?.ProvesStandardLinuxNamespaces(token) == true) coverage = "linux-standard-namespaces-with-cap-sys-admin";
                else refusals.Add("Privileged extended-attribute names may be hidden; complete namespace coverage is unavailable without CAP_SYS_ADMIN. Caller-visible attributes remain captured for review.");
                var x64 = RuntimeInformation.ProcessArchitecture == Architecture.X64;
                uid = unchecked((uint)Marshal.ReadInt32(stat.Pointer, x64 ? 28 : 24));
                gid = unchecked((uint)Marshal.ReadInt32(stat.Pointer, x64 ? 32 : 28));
                links = x64 ? unchecked((ulong)Marshal.ReadInt64(stat.Pointer, 16)) : unchecked((uint)Marshal.ReadInt32(stat.Pointer, 20));
                // Non-ordinary inode flags must not be silently discarded.
                // O_PATH links cannot answer this ioctl; Linux symlink flags
                // are not supported by this adapter.
                if (item.Kind != "link")
                {
                    uint flags = 0;
                    if (RetryOnInterrupted(() => MetadataNative.ioctl(handle.FileDescriptor, 0x80086601, ref flags)) != 0)
                        refusals.Add("Filesystem inode flags could not be inspected.");
                    else if ((flags & ~(0x00080000u | 0x00001000u)) != 0) // EXTENTS/INDEX are storage implementation details.
                        refusals.Add("Nonstandard filesystem inode flags require a dedicated metadata transition.");
                }
                else refusals.Add("Linux symlink inode flags are not observable through this metadata adapter.");
            }
            if (uid != StateNative.geteuid() || gid is null)
                refusals.Add("Merge currently requires an effective-user-owned object with a captured group.");
            if (item.Kind != "directory" && links != 1)
                refusals.Add("Hard-linked payload entries require a topology-preserving transition.");
            if (item.UnixMode is null or < 0 or > 511)
                refusals.Add("Special or unavailable Unix permission bits require explicit metadata policy.");
            ReadAttributes(handle, parent, item.NativeName, item.Kind == "link", attributes, refusals, token);
            if (OperatingSystem.IsLinux() && item.Kind == "link" && attributes.Count != 0)
                refusals.Add("Linux link extended attributes require a handle-bound publication adapter.");
            if (!Matches(before, ReadStat(handle, stat.Pointer))) throw new IOException("Payload metadata changed during inspection.");
            if (item.Kind == "link" && (!TryReadStatAt(parent!, item.NativeName!, stat.Pointer, out var named, out _) || !Matches(before, named)))
                throw new IOException("Payload metadata link changed in its held parent.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        { refusals.Add(ex.Message); }
        finally { ownedLink?.Dispose(); }
        return new(OperatingSystem.IsMacOS() ? "macos" : "linux", item.Identity, item.Kind, mode,
            uid, gid, links, false, attributes.ToImmutable(), coverage, refusals.ToImmutable());
    }

    internal static bool ProbeTrustedMetadataAuthority(SafeFileHandle witness, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) return false;
        // The trusted namespace's native write check requires authority in the
        // initial user namespace. Probe only this held private witness, never an
        // installed object. No privileges are requested or enabled here.
        var retained = false;
        try
        {
            witness.DangerousAddRef(ref retained);
            using var borrowed = new SafeUnixHandle(witness.DangerousGetHandle().ToInt32(), ownsHandle: false);
            using var stat = new StatBuffer();
            var observed = ReadStat(borrowed, stat.Pointer);
            var links = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? unchecked((ulong)Marshal.ReadInt64(stat.Pointer, 16))
                : unchecked((uint)Marshal.ReadInt32(stat.Pointer, 20));
            var owner = unchecked((uint)Marshal.ReadInt32(stat.Pointer, RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 28 : 24));
            if (observed.FileType != 0x8000 || links != 0 || (observed.Mode & 0xFFF) != 0x180 || owner != StateNative.geteuid()) return false;
            var marker = new byte[] { 83, 86, 49 };
            return RetryOnInterrupted(() =>
            {
                token.ThrowIfCancellationRequested();
                return MetadataNative.fsetxattr_witness(witness, "trusted.skillview-visibility-probe", marker, (nuint)marker.Length, 0);
            }) == 0;
        }
        finally { if (retained) witness.DangerousRelease(); }
    }

    private static void ReadAttributes(SafeUnixHandle handle, SafeUnixHandle? parent, string? name, bool link,
        ImmutableArray<PayloadExtendedAttribute>.Builder attributes, ImmutableArray<string>.Builder refusals, CancellationToken token)
    {
        var path = !OperatingSystem.IsMacOS() && link ? $"/proc/thread-self/fd/{parent!.FileDescriptor}/{name}" : null;
        long List(byte[] buffer)
        {
            long count = -1;
            RetryOnInterrupted(() =>
            {
                token.ThrowIfCancellationRequested();
                count = OperatingSystem.IsMacOS() ? MetadataNative.flistxattr_mac(handle.FileDescriptor, buffer, (nuint)buffer.Length, 0x20)
                    : path is not null ? MetadataNative.llistxattr(path, buffer, (nuint)buffer.Length)
                    : MetadataNative.flistxattr_linux(handle.FileDescriptor, buffer, (nuint)buffer.Length);
                return count < 0 ? -1 : 0;
            });
            if (count < 0 || count > PayloadNativeMetadata.MaxAttributeBytes)
                throw new IOException("Extended attribute names could not be captured within their byte limit.");
            return count;
        }
        var required = List([]);
        var names = new byte[(int)required];
        var count = required == 0 ? 0 : List(names);
        if (count > names.Length) throw new IOException("Extended attribute names grew during capture.");
        var offset = 0; var attributeCount = 0; long bytes = count;
        while (offset < count)
        {
            token.ThrowIfCancellationRequested();
            var end = Array.IndexOf(names, (byte)0, offset, (int)count - offset);
            if (end <= offset || attributeCount++ >= PayloadNativeMetadata.MaxAttributes)
                throw new IOException("Extended attribute names are malformed or exceed their entry limit.");
            var attribute = new UTF8Encoding(false, true).GetString(names, offset, end - offset); offset = end + 1;
            if (OperatingSystem.IsLinux() && !attribute.StartsWith("user.", StringComparison.Ordinal)
                || OperatingSystem.IsMacOS() && attribute is "com.apple.decmpfs")
            { refusals.Add("A protected, ACL or compressed attribute requires a dedicated metadata adapter: " + attribute); continue; }
            long Read(byte[] buffer)
            {
                long length = -1;
                RetryOnInterrupted(() =>
                {
                    token.ThrowIfCancellationRequested();
                    length = OperatingSystem.IsMacOS() ? MetadataNative.fgetxattr_mac(handle.FileDescriptor, attribute, buffer, (nuint)buffer.Length, 0, 0x20)
                        : path is not null ? MetadataNative.lgetxattr(path, attribute, buffer, (nuint)buffer.Length)
                        : MetadataNative.fgetxattr_linux(handle.FileDescriptor, attribute, buffer, (nuint)buffer.Length);
                    return length < 0 ? -1 : 0;
                });
                if (length < 0 || length > PayloadNativeMetadata.MaxAttributeBytes)
                    throw new IOException("Extended attribute value could not be captured within its byte limit.");
                return length;
            }
            var length = Read([]);
            if ((bytes += length) > PayloadNativeMetadata.MaxEntryMetadataBytes)
                throw new IOException("Extended attribute values could not be captured within their byte limit.");
            var value = new byte[(int)length];
            if (length != 0 && Read(value) != length) throw new IOException("Extended attribute value changed during capture.");
            attributes.Add(new(attribute, value));
        }
    }

    private static class MetadataNative
    {
        [DllImport("libc", EntryPoint = "fsetxattr", SetLastError = true)] internal static extern int fsetxattr_witness(SafeFileHandle descriptor, string name, byte[] value, nuint bytes, int flags);
        [DllImport("libc", SetLastError = true)] internal static extern int fchown(int descriptor, uint uid, uint gid);
        [DllImport("libc", SetLastError = true)] internal static extern int fchownat(int descriptor, string name, uint uid, uint gid, int flags);
        [DllImport("libc", EntryPoint = "fsetxattr", SetLastError = true)] internal static extern int fsetxattr_mac(int descriptor, string name, byte[] value, nuint bytes, uint position, int options);
        [DllImport("libc", EntryPoint = "fsetxattr", SetLastError = true)] internal static extern int fsetxattr_linux(int descriptor, string name, byte[] value, nuint bytes, int flags);
        [DllImport("libc", EntryPoint = "fremovexattr", SetLastError = true)] internal static extern int fremovexattr_mac(int descriptor, string name, int options);
        [DllImport("libc", EntryPoint = "fremovexattr", SetLastError = true)] internal static extern int fremovexattr_linux(int descriptor, string name);
        [DllImport("libc", SetLastError = true)] internal static extern int ioctl(int descriptor, nuint request, ref uint flags);
        [DllImport("libc", EntryPoint = "flistxattr", SetLastError = true)] internal static extern nint flistxattr_mac(int descriptor, byte[] names, nuint bytes, int options);
        [DllImport("libc", EntryPoint = "flistxattr", SetLastError = true)] internal static extern nint flistxattr_linux(int descriptor, byte[] names, nuint bytes);
        [DllImport("libc", SetLastError = true)] internal static extern nint llistxattr(string path, byte[] names, nuint bytes);
        [DllImport("libc", EntryPoint = "fgetxattr", SetLastError = true)] internal static extern nint fgetxattr_mac(int descriptor, string name, byte[] value, nuint bytes, uint position, int options);
        [DllImport("libc", EntryPoint = "fgetxattr", SetLastError = true)] internal static extern nint fgetxattr_linux(int descriptor, string name, byte[] value, nuint bytes);
        [DllImport("libc", SetLastError = true)] internal static extern nint lgetxattr(string path, string name, byte[] value, nuint bytes);
    }
}
