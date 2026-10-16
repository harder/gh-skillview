using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;

namespace SkillView.Inventory;

internal sealed record LinuxLinkFilesystem(ulong Device, ulong Type, ulong FileSystemId);
internal sealed record LinuxLinkMountObservation(LinuxLinkFilesystem FileSystem, uint Major, uint Minor,
    ulong MountId, SecureFileIdentity MountNamespace, string Task, string MountRecordFingerprint = "");
internal sealed record LinuxLinkMountRecord(ulong Id, ulong ParentId, uint Major, uint Minor, string FileSystem, string RawRecord);
internal sealed record LinuxLinkFdInfo(ulong MountId, ulong Inode);
internal sealed record LinuxLinkNativeMountSample(SecureFileIdentity Object, LinuxLinkFilesystem FileSystem,
    LinuxLinkFdInfo FdInfo, LinuxLinkMountRecord Mount);


/// Pins borrowed descriptor numbers for complete synchronous native observations.
internal sealed class LinuxLinkHandleReferences : IDisposable
{
    private readonly SafeHandle?[] handles;
    private int count;
    internal LinuxLinkHandleReferences(params SafeHandle[] values)
    {
        handles = new SafeHandle[values.Length];
        try
        {
            foreach (var value in values)
            {
                var added = false;
                try { value.DangerousAddRef(ref added); }
                finally { if (added) handles[count++] = value; }
            }
        }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        while (count > 0) { var handle = handles[--count]; handles[count] = null; handle!.DangerousRelease(); }
    }
}

/// Kernel proc observations are a live capability on one actual OS task, never
/// a persisted numeric mount-ID authority or an ambient process-global override.
internal abstract class LinuxLinkProcAuthority : IDisposable
{
    internal const int MaxFdInfoBytes = 16 * 1024;
    internal const int MaxMountInfoBytes = 4 * 1024 * 1024;
    internal abstract void Check(CancellationToken token);
    internal abstract LinuxLinkMountObservation ObserveHandle(SafeHandle handle, CancellationToken token);
    public abstract void Dispose();
    internal static LinuxLinkProcAuthority Open(CancellationToken token) => UnixSecureRemovalBackend.OpenLinuxLinkProcAuthority(token);

    internal static string ParseTask(string text)
    {
        var parts = text.Split('/');
        if (parts.Length != 3 || parts[1] != "task" || Number(parts[0]) > int.MaxValue || Number(parts[2]) > int.MaxValue)
            throw new IOException("Kernel thread-self task address is malformed.");
        return text;
    }

    internal static LinuxLinkFdInfo ParseFdInfo(ReadOnlySpan<byte> bytes, CancellationToken token)
    {
        var lines = Lines(bytes, MaxFdInfoBytes, 128, 1024);
        ulong? mount = null, inode = null;
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            token.ThrowIfCancellationRequested();
            var colon = line.IndexOf(':');
            if (colon <= 0) throw new IOException("Kernel fdinfo field is malformed.");
            var key = line[..colon];
            if (!fields.Add(key)) throw new IOException("Kernel fdinfo field is duplicated.");
            var value = line[(colon + 1)..].Trim(' ', '\t');
            if (key == "mnt_id") mount = Number(value);
            else if (key == "ino") inode = Number(value);
        }
        return new(mount ?? throw new IOException("Kernel fdinfo has no mount ID."),
            inode ?? throw new IOException("Kernel fdinfo has no inode."));
    }

    internal static ImmutableDictionary<ulong, LinuxLinkMountRecord> ParseMountInfo(ReadOnlySpan<byte> bytes, CancellationToken token)
    {
        var records = ImmutableDictionary.CreateBuilder<ulong, LinuxLinkMountRecord>();
        foreach (var line in Lines(bytes, MaxMountInfoBytes, 16384, 16 * 1024))
        {
            token.ThrowIfCancellationRequested();
            var fields = line.Split(' ');
            var separator = Array.IndexOf(fields, "-");
            if (fields.Any(string.IsNullOrEmpty) || separator < 6 || separator + 4 != fields.Length
                || fields.Count(field => field == "-") != 1)
                throw new IOException("Kernel mountinfo record is malformed.");
            var id = Number(fields[0]); var parent = Number(fields[1]);
            var device = fields[2].Split(':');
            if (device.Length != 2) throw new IOException("Kernel mountinfo device is malformed.");
            var majorValue = Number(device[0], allowZero: true); var minorValue = Number(device[1], allowZero: true);
            if (majorValue > uint.MaxValue || minorValue > uint.MaxValue) throw new IOException("Kernel mountinfo device overflows.");
            var major = (uint)majorValue; var minor = (uint)minorValue;
            _ = DecodeMountPath(fields[3]); _ = DecodeMountPath(fields[4]);
            if (!records.TryAdd(id, new(id, parent, major, minor, fields[separator + 1], line)))
                throw new IOException("Kernel mountinfo mount ID is duplicated.");
        }
        if (records.Count == 0) throw new IOException("Kernel mountinfo is empty.");
        return records.ToImmutable();
    }

    internal static LinuxLinkMountRecord RequireExt4(LinuxLinkFilesystem filesystem, LinuxLinkFdInfo fd,
        ImmutableDictionary<ulong, LinuxLinkMountRecord> mounts)
    {
        var major = DeviceMajor(filesystem.Device); var minor = DeviceMinor(filesystem.Device);
        if (filesystem.Type != 0xef53 || !mounts.TryGetValue(fd.MountId, out var mount)
            || mount.FileSystem != "ext4" || mount.Major != major || mount.Minor != minor)
            throw new IOException("Same-original-object preservation requires a held, currently listed ext4 mount; filesystem magic alone is insufficient.");
        return mount;
    }

    // The production reader performs fresh bounded native/proc I/O each time.
    // Keeping this coordinator separate permits a changing-second-read regression
    // without replacing the live capability or exposing raw descriptors.
    internal static LinuxLinkNativeMountSample ReadStableSample(Func<LinuxLinkNativeMountSample> read, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var before = read();
        token.ThrowIfCancellationRequested(); var after = read();
        if (before != after) throw new IOException("Held fdinfo, relevant mount record or native filesystem/object changed between fresh observations.");
        token.ThrowIfCancellationRequested(); return after;
    }

    internal static void RequireSameMount(LinuxLinkMountObservation expected, LinuxLinkMountObservation actual)
    {
        if (expected != actual) throw new IOException("Linux preservation requires the same verified ext4 device/fsid/mount, task and active namespace for every authority.");
    }

    internal static uint DeviceMajor(ulong device) => (uint)(((device >> 8) & 0xfff) | ((device >> 32) & 0xfffff000));
    internal static uint DeviceMinor(ulong device) => (uint)((device & 0xff) | ((device >> 12) & 0xffffff00));

    private static ulong Number(string text, bool allowZero = false)
    {
        if (text.Length == 0 || text.Any(c => c is < '0' or > '9') || text.Length > 1 && text[0] == '0'
            || !ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || !allowZero && value == 0)
            throw new IOException("Kernel proc numeric field is malformed or overflows.");
        return value;
    }
    private static string[] Lines(ReadOnlySpan<byte> bytes, int maxBytes, int maxLines, int maxLineBytes)
    {
        if (bytes.Length == 0 || bytes.Length > maxBytes || bytes[^1] != '\n' || bytes.Contains((byte)0) || bytes.Contains((byte)'\r'))
            throw new IOException("Kernel proc stream is empty, truncated, malformed or exceeds its bound.");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException ex) { throw new IOException("Kernel proc stream is not valid UTF-8.", ex); }
        var lines = text[..^1].Split('\n');
        if (lines.Length > maxLines || lines.Any(line => line.Length == 0 || Encoding.UTF8.GetByteCount(line) > maxLineBytes))
            throw new IOException("Kernel proc record exceeds its line/count bound.");
        return lines;
    }
    private static string DecodeMountPath(string text)
    {
        var result = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\') { result.Append(text[i]); continue; }
            if (i + 3 >= text.Length) throw new IOException("Kernel mountinfo path escape is truncated.");
            result.Append(text.Substring(i + 1, 3) switch
            { "040" => ' ', "011" => '\t', "012" => '\n', "134" => '\\', _ => throw new IOException("Kernel mountinfo path escape is malformed.") });
            i += 3;
        }
        if (result.Length == 0 || result[0] != '/') throw new IOException("Kernel mountinfo path is not absolute.");
        return result.ToString();
    }
}

internal sealed partial class UnixSecureRemovalBackend
{
    internal static LinuxLinkProcAuthority OpenLinuxLinkProcAuthority(CancellationToken token)
    {
        if (!OperatingSystem.IsLinux() || !BitConverter.IsLittleEndian
            || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new IOException("Linux same-original-object ABI is not verified for this process.");
        return LinuxProcSession.Create(token);
    }

    private sealed class LinuxProcSession : LinuxLinkProcAuthority
    {
        private readonly SafeUnixHandle proc, task, fdDirectory, fdInfo, nsDirectory, mountNamespace;
        private readonly string taskAddress;
        private readonly NativeStat procIdentity, taskIdentity, fdIdentity, fdInfoIdentity, nsIdentity, namespaceIdentity;
        private readonly LinuxLinkFilesystem procFilesystem;
        private bool disposed;
        private LinuxProcSession(SafeUnixHandle proc, SafeUnixHandle task, SafeUnixHandle fdDirectory,
            SafeUnixHandle fdInfo, SafeUnixHandle nsDirectory, SafeUnixHandle mountNamespace, string address, CancellationToken token)
        {
            this.proc = proc; this.task = task; this.fdDirectory = fdDirectory; this.fdInfo = fdInfo;
            this.nsDirectory = nsDirectory; this.mountNamespace = mountNamespace; taskAddress = address;
            using var stat = new StatBuffer();
            procIdentity = LinuxStat(proc, stat.Pointer, token); taskIdentity = LinuxStat(task, stat.Pointer, token);
            fdIdentity = LinuxStat(fdDirectory, stat.Pointer, token); fdInfoIdentity = LinuxStat(fdInfo, stat.Pointer, token);
            nsIdentity = LinuxStat(nsDirectory, stat.Pointer, token); namespaceIdentity = LinuxStat(mountNamespace, stat.Pointer, token);
            procFilesystem = LinuxFilesystem(proc, token);
            if (procFilesystem.Type != 0x9fa0 || LinuxFilesystem(mountNamespace, token).Type != 0x6e736673)
                throw new IOException("Kernel procfs and mount namespace authority could not be verified.");
            foreach (var handle in new[] { task, fdDirectory, fdInfo, nsDirectory }) RequireProc(handle, token);
            Check(token);
        }
        internal static LinuxProcSession Create(CancellationToken token)
        {
            var handles = new List<SafeUnixHandle>();
            try
            {
                var proc = OpenDirectory("/proc"); handles.Add(proc);
                if (LinuxFilesystem(proc, token).Type != 0x9fa0) throw new IOException("The fixed /proc anchor is not kernel procfs.");
                var address = ParseTask(new UTF8Encoding(false, true).GetString(LinuxRawLink(proc, "thread-self", 128, token)));
                SafeUnixHandle cursor = proc;
                foreach (var part in address.Split('/'))
                { cursor = LinuxOpenAt(cursor, part, directory: true, follow: false, token); handles.Add(cursor); }
                var task = cursor;
                var fd = LinuxOpenAt(task, "fd", true, false, token); handles.Add(fd);
                var info = LinuxOpenAt(task, "fdinfo", true, false, token); handles.Add(info);
                var ns = LinuxOpenAt(task, "ns", true, false, token); handles.Add(ns);
                var mnt = LinuxOpenAt(ns, "mnt", false, true, token); handles.Add(mnt);
                var result = new LinuxProcSession(proc, task, fd, info, ns, mnt, address, token);
                foreach (var handle in handles.Where(h => h != proc && h != task && h != fd && h != info && h != ns && h != mnt)) handle.Dispose();
                handles.Clear(); return result;
            }
            finally { foreach (var handle in handles) handle.Dispose(); }
        }
        internal override void Check(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(disposed, this);
            using var references = new LinuxLinkHandleReferences(proc, task, fdDirectory, fdInfo, nsDirectory, mountNamespace);
            if (new UTF8Encoding(false, true).GetString(LinuxRawLink(proc, "thread-self", 128, token)) != taskAddress)
                throw new IOException("Linux link preservation is bound to its original OS thread/task.");
            using var stat = new StatBuffer();
            foreach (var pair in new[] { (proc, procIdentity), (task, taskIdentity), (fdDirectory, fdIdentity), (fdInfo, fdInfoIdentity), (nsDirectory, nsIdentity) })
                if (!Matches(pair.Item2, LinuxStat(pair.Item1, stat.Pointer, token))) throw new IOException("Held kernel proc authority changed.");
            using var currentProc = OpenDirectory("/proc");
            if (!Matches(procIdentity, LinuxStat(currentProc, stat.Pointer, token)) || LinuxFilesystem(currentProc, token) != procFilesystem)
                throw new IOException("The active /proc address no longer names the held kernel procfs.");
            using var currentFd = OpenDirectory("/proc/thread-self/fd");
            if (!Matches(fdIdentity, LinuxStat(currentFd, stat.Pointer, token))) throw new IOException("Current-thread descriptor authority changed.");
            using var currentNamespace = LinuxOpenAt(nsDirectory, "mnt", false, true, token);
            if (!Matches(namespaceIdentity, LinuxStat(currentNamespace, stat.Pointer, token))
                || !Matches(namespaceIdentity, LinuxStat(mountNamespace, stat.Pointer, token))
                || LinuxFilesystem(currentNamespace, token).Type != 0x6e736673)
                throw new IOException("The active thread mount namespace changed.");
            RequireProc(task, token); RequireProc(fdDirectory, token); RequireProc(fdInfo, token); RequireProc(nsDirectory, token);
        }
        private void RequireProc(SafeUnixHandle handle, CancellationToken token)
        { if (LinuxFilesystem(handle, token) != procFilesystem) throw new IOException("A held proc task entry escapes its verified procfs."); }
        internal override LinuxLinkMountObservation ObserveHandle(SafeHandle handle, CancellationToken token)
        {
            var added = false;
            try
            {
                handle.DangerousAddRef(ref added);
                using var references = new LinuxLinkHandleReferences(proc, task, fdDirectory, fdInfo, nsDirectory, mountNamespace);
                using var borrowed = new SafeUnixHandle(handle.DangerousGetHandle().ToInt32(), ownsHandle: false);
                LinuxLinkNativeMountSample ReadSample()
                {
                    Check(token);
                    using var stat = new StatBuffer(); var before = LinuxStat(borrowed, stat.Pointer, token);
                    var filesystem = LinuxFilesystem(borrowed, token);
                    var info = ParseFdInfo(ReadStream(fdInfo, borrowed.FileDescriptor.ToString(CultureInfo.InvariantCulture), MaxFdInfoBytes, token), token);
                    var mounts = ParseMountInfo(ReadStream(task, "mountinfo", MaxMountInfoBytes, token), token);
                    var mount = RequireExt4(filesystem, info, mounts);
                    if (info.Inode != before.Inode || filesystem.Device != before.Device || !Matches(before, LinuxStat(borrowed, stat.Pointer, token)))
                        throw new IOException("Current-task fdinfo contradicts the held native object.");
                    Check(token);
                    return new(ToSecureIdentity(before, ""), filesystem, info, mount);
                }
                var sampled = ReadStableSample(ReadSample, token);
                Check(token);
                return new(sampled.FileSystem, sampled.Mount.Major, sampled.Mount.Minor, sampled.Mount.Id,
                    ToSecureIdentity(namespaceIdentity, ""), taskAddress,
                    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sampled.Mount.RawRecord))));
            }
            catch (ObjectDisposedException ex) { throw new IOException("Linux link authority handle is no longer live.", ex); }
            finally { if (added) handle.DangerousRelease(); }
        }
        private byte[] ReadStream(SafeUnixHandle parent, string name, int limit, CancellationToken token)
        {
            using var stream = LinuxOpenAt(parent, name, false, false, token); RequireProc(stream, token);
            using var stat = new StatBuffer(); var before = LinuxStat(stream, stat.Pointer, token);
            if (before.IsDirectory || before.IsSymlink || before.FileType != 0x8000) throw new IOException("Expected a kernel proc regular stream.");
            using var bytes = new MemoryStream(); var buffer = new byte[4096];
            while (true)
            {
                var count = 0;
                if (RetryOnInterrupted(() => { token.ThrowIfCancellationRequested(); count = checked((int)LinuxLinkNative.read(stream.FileDescriptor, buffer, (nuint)Math.Min(buffer.Length, limit + 1L - bytes.Length))); return count; }) < 0)
                    throw new IOException(LastError("Read bounded kernel proc stream"));
                if (count == 0) break;
                bytes.Write(buffer, 0, count);
                if (bytes.Length > limit) throw new IOException("Kernel proc stream exceeds its complete-read byte limit.");
            }
            if (!Matches(before, LinuxStat(stream, stat.Pointer, token))) throw new IOException("Kernel proc stream identity changed.");
            return bytes.ToArray();
        }
        public override void Dispose()
        {
            if (disposed) return; disposed = true;
            // Cleanup must never depend on the caller still being on the task.
            mountNamespace.Dispose(); nsDirectory.Dispose(); fdInfo.Dispose(); fdDirectory.Dispose(); task.Dispose(); proc.Dispose();
        }
    }

    private static SafeUnixHandle LinuxOpenAt(SafeUnixHandle parent, string name, bool directory, bool follow, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var flags = LinuxOpenFlags.CloseOnExec | LinuxOpenFlags.NonBlocking | (directory ? LinuxOpenFlags.Directory : 0) | (follow ? 0 : LinuxOpenFlags.NoFollow);
        if (follow)
        {
            // This one controlled kernel magic leaf intentionally enters nsfs.
            // A bind-mounted non-link cannot masquerade as active ns/mnt.
            if (directory || name != "mnt") throw new IOException("Unexpected proc magic-link request.");
            var raw = new UTF8Encoding(false, true).GetString(LinuxRawLink(parent, name, 128, token));
            if (!raw.StartsWith("mnt:[", StringComparison.Ordinal) || !raw.EndsWith(']')
                || !ulong.TryParse(raw.AsSpan(5, raw.Length - 6), NumberStyles.None, CultureInfo.InvariantCulture, out var inode)
                || inode == 0 || raw != $"mnt:[{inode.ToString(CultureInfo.InvariantCulture)}]")
                throw new IOException("Kernel mount-namespace link is malformed.");
            var handle = CreateHandle(RetryOnInterrupted(() => { token.ThrowIfCancellationRequested(); return UnixNative.openat(parent.FileDescriptor, name, flags); }), "Open held kernel namespace");
            try
            {
                using var stat = new StatBuffer();
                if (LinuxStat(handle, stat.Pointer, token).Inode != inode) throw new IOException("Kernel namespace link contradicts its opened object.");
                return handle;
            }
            catch { handle.Dispose(); throw; }
        }
        var how = new OpenHow
        {
            Flags = (ulong)flags,
            Resolve = UnixConstants.ResolveNoCrossDevice | UnixConstants.ResolveBeneath | UnixConstants.ResolveNoSymlinks
        };
        for (var attempt = 0; attempt < MaxInterruptedAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var descriptor = UnixNative.syscall(UnixConstants.OpenAt2SystemCall, parent.FileDescriptor, name, ref how, (nuint)Marshal.SizeOf<OpenHow>());
            if (descriptor >= 0 || Marshal.GetLastPInvokeError() != UnixConstants.Interrupted)
                return CreateHandle(checked((int)descriptor), "Open bounded root-relative kernel proc entry");
        }
        throw new IOException(LastError("Open kernel proc entry interrupted beyond its retry limit"));
    }

    private static NativeStat LinuxStat(SafeUnixHandle handle, IntPtr buffer, CancellationToken token)
    {
        if (RetryOnInterrupted(() => { token.ThrowIfCancellationRequested(); return UnixNative.fstat(handle.FileDescriptor, buffer); }) != 0)
            throw new IOException(LastError("Inspect held Linux link authority"));
        return ParseStat(buffer);
    }
    private static LinuxLinkFilesystem LinuxFilesystem(SafeUnixHandle handle, CancellationToken token)
    {
        var buffer = Marshal.AllocHGlobal(120);
        try
        {
            if (RetryOnInterrupted(() => { token.ThrowIfCancellationRequested(); return LinuxLinkNative.fstatfs(handle.FileDescriptor, buffer); }) != 0)
                throw new IOException(LastError("Inspect held filesystem ABI"));
            using var stat = new StatBuffer();
            return new(LinuxStat(handle, stat.Pointer, token).Device, unchecked((ulong)Marshal.ReadInt64(buffer, 0)), unchecked((ulong)Marshal.ReadInt64(buffer, 56)));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static byte[] LinuxRawLink(SafeUnixHandle parent, string name, int bound, CancellationToken token)
    {
        var bytes = new byte[bound]; var count = 0;
        if (RetryOnInterrupted(() => { token.ThrowIfCancellationRequested(); count = checked((int)UnixNative.readlinkat(parent.FileDescriptor, name, bytes, (nuint)bytes.Length)); return count; }) < 0
            || count == 0 || count >= bound)
            throw new IOException(LastError("Read bounded original Linux link"));
        return bytes.AsSpan(0, count).ToArray();
    }
    private static class LinuxLinkNative
    {
        [DllImport("libc", SetLastError = true)] internal static extern int fstatfs(int descriptor, IntPtr buffer);
        [DllImport("libc", SetLastError = true)] internal static extern nint read(int descriptor, [Out] byte[] buffer, nuint length);
    }
}
