using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SkillView.Maintenance;

namespace SkillView.Inventory;

internal enum ExposureLinkRetentionMode { FullMetadataVerified, LinuxSameOriginalObjectPreservation }
internal sealed record LinkPreservationObservation(int UnixMode, uint UserId, uint GroupId, ulong LinkCount,
    ImmutableArray<PayloadExtendedAttribute> VisibleAttributes, string VisibleAttributesFingerprint, LinuxLinkMountObservation Mount)
{
    internal string Coverage => "caller-visible-all-attributes; hidden-namespaces-and-inode-flags-unknown";
    internal string Continuity => "live-original-link-pin";
}
internal sealed record LinuxLinkPinObservation(SecureFileIdentity Identity, ImmutableArray<byte> RawTarget,
    int UnixMode, uint UserId, uint GroupId, ulong LinkCount, LinuxLinkMountObservation Mount);

internal abstract class NativeExposureLinkPin : IDisposable
{
    internal virtual LinuxLinkPinObservation ObserveLinux(LinuxLinkProcAuthority authority, CancellationToken token) =>
        throw new IOException("This link pin does not support Linux same-original-object observation.");
    public abstract void Dispose();
    internal static NativeExposureLinkPin Wrap(IDisposable handle) => new StrictPin(handle);
    private sealed class StrictPin(IDisposable handle) : NativeExposureLinkPin
    { public override void Dispose() => handle.Dispose(); }
}

internal sealed partial class UnixSecureRemovalBackend
{
    private sealed class UnixExposurePin(SafeUnixHandle handle) : NativeExposureLinkPin
    {
        internal override LinuxLinkPinObservation ObserveLinux(LinuxLinkProcAuthority authority, CancellationToken token)
        {
            var added = false;
            try
            {
                handle.DangerousAddRef(ref added); authority.Check(token);
                using var stat = new StatBuffer(); var before = LinuxStat(handle, stat.Pointer, token);
                var mode = (int)(before.Mode & 0xfff);
                var x64 = RuntimeInformation.ProcessArchitecture == Architecture.X64;
                var uid = unchecked((uint)Marshal.ReadInt32(stat.Pointer, x64 ? 28 : 24));
                var gid = unchecked((uint)Marshal.ReadInt32(stat.Pointer, x64 ? 32 : 28));
                var links = x64 ? unchecked((ulong)Marshal.ReadInt64(stat.Pointer, 16)) : unchecked((uint)Marshal.ReadInt32(stat.Pointer, 20));
                if (!before.IsSymlink || mode is < 0 or > 511 || uid != StateNative.geteuid() || links != 1)
                    throw new IOException("Linux original-link preservation requires an effective-user-owned ordinary mode symlink with exactly one native link.");
                var raw = LinuxRawLink(handle, "", 32768, token);
                if (raw.Contains((byte)0)) throw new IOException("Original link raw target contains a zero byte.");
                _ = new UTF8Encoding(false, true).GetString(raw);
                var mount = authority.ObserveHandle(handle, token);
                if (!Matches(before, LinuxStat(handle, stat.Pointer, token))) throw new IOException("Original pinned link changed during observation.");
                authority.Check(token);
                return new(ToSecureIdentity(before, ""), raw.ToImmutableArray(), mode, uid, gid, links, mount);
            }
            catch (ObjectDisposedException ex) { throw new IOException("The original Linux link pin is no longer live.", ex); }
            finally { if (added) handle.DangerousRelease(); }
        }
        public override void Dispose() => handle.Dispose();
    }

    private sealed partial class UnixExposureDirectory
    {
        internal override IDisposable HoldLinuxLinkAuthorities() => new LinuxLinkHandleReferences(root, parent);
        internal override ImmutableArray<LinuxLinkMountObservation> CaptureLinuxLinkAuthorities(LinuxLinkProcAuthority authority, CancellationToken token)
        {
            using var references = HoldLinuxLinkAuthorities();
            CheckAddress(token); authority.Check(token);
            return [authority.ObserveHandle(root, token), authority.ObserveHandle(parent, token)];
        }
        internal override (LinuxLinkPinObservation Pin, LinkPreservationObservation Observation) CaptureLinuxPreservation(
            string name, NativeExposureLinkPin pin, LinuxLinkProcAuthority authority, CancellationToken token)
        {
            using var references = HoldLinuxLinkAuthorities();
            NativeStateDirectory.CheckName(name); CheckAddress(token); authority.Check(token);
            var before = pin.ObserveLinux(authority, token);
            using var stat = new StatBuffer(); var parentBefore = LinuxStat(parent, stat.Pointer, token);
            RequireNamedPin(before, name, stat.Pointer, token);
            _ = authority.ObserveHandle(parent, token);
            var path = $"/proc/thread-self/fd/{parent.FileDescriptor}/{name}";
            var visible = CaptureAllVisibleLinkAttributes(path, token);
            var after = pin.ObserveLinux(authority, token);
            if (before != after && (before.Identity != after.Identity || !before.RawTarget.AsSpan().SequenceEqual(after.RawTarget.AsSpan())
                || before.UnixMode != after.UnixMode || before.UserId != after.UserId || before.GroupId != after.GroupId
                || before.LinkCount != after.LinkCount || before.Mount != after.Mount))
                throw new IOException("Original pinned link changed while reading visible attributes.");
            RequireNamedPin(after, name, stat.Pointer, token);
            if (!Matches(parentBefore, LinuxStat(parent, stat.Pointer, token))) throw new IOException("Original link parent changed during preservation capture.");
            authority.Check(token); CheckAddress(token);
            return (after, new(after.UnixMode, after.UserId, after.GroupId, after.LinkCount,
                visible, VisibleAttributesHash(visible), after.Mount));
        }
        private void RequireNamedPin(LinuxLinkPinObservation pin, string name, IntPtr stat, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!TryReadStatAt(parent, name, stat, out var named, out var error) || !Matches(pin.Identity, named)
                || !pin.RawTarget.AsSpan().SequenceEqual(RawLink(parent, name, token)))
                throw new IOException(error ?? "The named link no longer equals the continuously held original pin.");
        }
    }

    private static ImmutableArray<PayloadExtendedAttribute> CaptureAllVisibleLinkAttributes(string path, CancellationToken token)
    {
        long List(byte[] bytes)
        {
            long count = -1;
            if (RetryOnInterrupted(() => { token.ThrowIfCancellationRequested(); count = (long)MetadataNative.llistxattr(path, bytes, (nuint)bytes.Length); return count < 0 ? -1 : 0; }) != 0
                || count > PayloadNativeMetadata.MaxAttributeBytes)
                throw new IOException(LastError("Complete caller-visible link attribute names could not be read within their bound"));
            return count;
        }
        byte[] Names()
        {
            var size = List([]); var bytes = new byte[checked((int)size)];
            if (List(bytes) != size) throw new IOException("Caller-visible link attribute names changed during capture.");
            return bytes;
        }
        var names = Names(); var attributes = ImmutableArray.CreateBuilder<PayloadExtendedAttribute>();
        var seen = new HashSet<string>(StringComparer.Ordinal); var offset = 0; long total = names.Length;
        while (offset < names.Length)
        {
            token.ThrowIfCancellationRequested();
            var end = Array.IndexOf(names, (byte)0, offset);
            if (end <= offset || attributes.Count >= PayloadNativeMetadata.MaxAttributes) throw new IOException("Visible attribute names are malformed or exceed their bound.");
            var name = new UTF8Encoding(false, true).GetString(names, offset, end - offset); offset = end + 1;
            if (!seen.Add(name)) throw new IOException("Visible attribute name is duplicated.");
            long Read(byte[] bytes)
            {
                long length = -1;
                if (RetryOnInterrupted(() => { token.ThrowIfCancellationRequested(); length = (long)MetadataNative.lgetxattr(path, name, bytes, (nuint)bytes.Length); return length < 0 ? -1 : 0; }) != 0
                    || length > PayloadNativeMetadata.MaxAttributeBytes)
                    throw new IOException(LastError("A caller-visible attribute value could not be read within its bound"));
                return length;
            }
            var size = Read([]); total += size;
            if (total > PayloadNativeMetadata.MaxEntryMetadataBytes) throw new IOException("Caller-visible link attributes exceed their total byte bound.");
            var value = new byte[checked((int)size)];
            if (Read(value) != size) throw new IOException("Caller-visible attribute value changed during capture.");
            var repeated = new byte[value.Length];
            if (Read(repeated) != size || !value.AsSpan().SequenceEqual(repeated)) throw new IOException("Caller-visible attribute value is unstable.");
            attributes.Add(new(name, value));
        }
        if (!names.AsSpan().SequenceEqual(Names())) throw new IOException("Caller-visible link attribute names are unstable.");
        return attributes.OrderBy(a => a.Name, StringComparer.Ordinal).ToImmutableArray();
    }
    private static string VisibleAttributesHash(ImmutableArray<PayloadExtendedAttribute> attributes)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
            foreach (var attribute in attributes) { writer.Write(attribute.Name); writer.Write(attribute.Value.Length); writer.Write(attribute.Value); }
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }
}
