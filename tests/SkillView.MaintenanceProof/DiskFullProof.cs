using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SkillView.Inventory;

// Proof-only real ENOSPC. Never mount, resize, fill an ordinary filesystem, or
// remove anything. The parent owns cleanup of this one exclusively created file.
internal sealed class DiskFullProof : IDisposable
{
    internal const string FillerName = "disk-full-proof-filler";
    private const long MaxBytes = 128L * 1024 * 1024;
    private const long TmpfsMagic = 0x01021994;
    private const string DedicatedMount = "/proof-fixtures";
    private readonly Descriptor root;
    private readonly string canonicalRoot;

    private DiskFullProof(Descriptor root, string canonicalRoot)
    { this.root = root; this.canonicalRoot = canonicalRoot; }

    internal static DiskFullProof Admit(string canonicalRoot)
    {
        if (!OperatingSystem.IsLinux() || !BitConverter.IsLittleEndian
            || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new IOException("Disk-full proof requires verified little-endian Linux x64/ARM64.");
        if (!Path.GetFileName(canonicalRoot).StartsWith("skillview-local-merge-proof-", StringComparison.Ordinal))
            throw new IOException("Disk-full proof requires its private local-merge fixture root.");
        var descriptor = Native.open(canonicalRoot, LinuxOpenFlags.Directory | LinuxOpenFlags.NoFollow | LinuxOpenFlags.CloseOnExec);
        if (descriptor < 0) throw Failure("Open disk-full proof root");
        var root = new Descriptor(descriptor);
        try
        {
            var proof = new DiskFullProof(root, canonicalRoot);
            proof.Validate();
            var markerFd = Native.openat(root.Value, "disposable-proof-marker",
                LinuxOpenFlags.NoFollow | LinuxOpenFlags.CloseOnExec | LinuxOpenFlags.NonBlocking, 0);
            if (markerFd < 0) throw Failure("Open held disk-full marker");
            using var marker = new Descriptor(markerFd);
            var expected = Encoding.UTF8.GetBytes("SkillView disposable process proof\n");
            var bytes = new byte[expected.Length + 1];
            var count = Native.read(marker.Value, bytes, (nuint)bytes.Length);
            if (count != expected.Length || !bytes.AsSpan(0, expected.Length).SequenceEqual(expected))
                throw new IOException("Disk-full proof marker differs from its required exact contents.");
            return proof;
        }
        catch { root.Dispose(); throw; }
    }

    internal DiskFullWitness Fill(CancellationToken token)
    {
        var initial = Validate();
        var fd = Native.openat(root.Value, FillerName, 1 | LinuxOpenFlags.Create | LinuxOpenFlags.Exclusive
            | LinuxOpenFlags.NoFollow | LinuxOpenFlags.CloseOnExec, 0x180);
        if (fd < 0) throw Failure("Create exclusive disk-full filler");
        using var filler = new Descriptor(fd);
        var identity = new byte[512];
        if (Native.fstat(filler.Value, identity) != 0) throw Failure("Inspect disk-full filler");
        var device = unchecked((ulong)BitConverter.ToInt64(identity, 0));
        var inode = unchecked((ulong)BitConverter.ToInt64(identity, 8));
        Console.WriteLine("DISK_FULL_FILLER " + JsonSerializer.Serialize(new DiskFullFillerIdentity(FillerName, device, inode)));
        Console.Out.Flush();
        var buffer = new byte[64 * 1024];
        long written = 0;
        for (var attempts = 0; attempts < 8192; attempts++)
        {
            token.ThrowIfCancellationRequested();
            // One final byte past the bounded filesystem capacity is sufficient
            // to observe ENOSPC; never write more than 128 MiB in total.
            var remaining = MaxBytes - written;
            if (remaining <= 0) throw new IOException("Bounded filler reached its limit without ENOSPC.");
            var count = Native.write(filler.Value, buffer, (nuint)Math.Min(buffer.Length, remaining));
            if (count > 0) { written = checked(written + (long)count); continue; }
            var error = Marshal.GetLastPInvokeError();
            if (count < 0 && error == 4) continue;
            if (count < 0 && error == 28)
            {
                var full = Validate();
                if (full.AvailableBytes != 0 || written == 0)
                    throw new IOException("ENOSPC did not coincide with a nonempty filler and exhausted bounded tmpfs.");
                return new(FillerName, initial.TotalBytes, written, full.AvailableBytes, 28,
                    device, inode);
            }
            throw new IOException($"Fill bounded tmpfs failed: errno={error}, write={count}.");
        }
        throw new IOException("Bounded filler syscall limit exceeded without ENOSPC.");
    }

    private (long TotalBytes, long AvailableBytes) Validate()
    {
        // Linux x64/ARM64 statfs has 64-bit words and block counts. The buffer
        // deliberately exceeds both verified native structures; other ABIs are
        // refused before any native call. fstatfs checks the held root itself.
        var bytes = new byte[512];
        if (Native.fstatfs(root.Value, bytes) != 0) throw Failure("Inspect held proof filesystem");
        var fragment = BitConverter.ToInt64(bytes, 72);
        var blockSize = BitConverter.ToInt64(bytes, 8);
        var blocks = BitConverter.ToInt64(bytes, 16);
        var available = BitConverter.ToInt64(bytes, 32);
        if (BitConverter.ToInt64(bytes, 0) != TmpfsMagic || fragment <= 0 || blockSize != fragment
            || blocks <= 0 || blocks > MaxBytes / fragment || available < 0 || available > blocks)
            throw new IOException("Disk-full proof refuses any filesystem other than a bounded tmpfs of at most 128 MiB.");
        ValidateDedicatedMount();
        var address = new byte[32 * 1024];
        var length = Native.readlink($"/proc/self/fd/{root.Value}", address, (nuint)address.Length);
        if (length <= 0 || length >= address.Length || Encoding.UTF8.GetString(address, 0, (int)length) != canonicalRoot)
            throw new IOException("Held disk-full proof root moved or differs from its canonical marked address.");
        return (checked(blocks * fragment), checked(available * fragment));
    }

    private void ValidateDedicatedMount()
    {
        if (Path.GetDirectoryName(canonicalRoot) != DedicatedMount)
            throw new IOException("Disk-full proof requires a dedicated /proof-fixtures mount and a direct marked fixture child; shared tmpfs is refused.");
        var parentFd = Native.open(DedicatedMount, LinuxOpenFlags.Directory | LinuxOpenFlags.NoFollow | LinuxOpenFlags.CloseOnExec);
        if (parentFd < 0) throw Failure("Open dedicated proof mount");
        using var parent = new Descriptor(parentFd);
        var rootStat = ReadPrivateDirectoryIdentity(root);
        var parentStat = ReadPrivateDirectoryIdentity(parent);
        if (rootStat != parentStat)
            throw new IOException("The dedicated mount and marked fixture must be on the same native filesystem.");
        using var mountFile = File.OpenRead("/proc/self/mountinfo");
        using var mountBytes = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = mountFile.Read(buffer);
            if (count == 0) break;
            if (mountBytes.Length + count > 256 * 1024)
                throw new IOException("Proof mount table exceeds its bounded admission budget.");
            mountBytes.Write(buffer, 0, count);
        }
        var device = $"{((rootStat >> 8) & 0xFFF) | ((rootStat >> 32) & 0xFFFFF000)}:{(rootStat & 0xFF) | ((rootStat >> 12) & 0xFFFFFF00)}";
        var entries = Encoding.UTF8.GetString(mountBytes.GetBuffer(), 0, (int)mountBytes.Length).Split('\n')
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(fields => fields.Length >= 10 && fields[2] == device).ToArray();
        if (entries.Length != 1) throw new IOException("Proof tmpfs must have one mount identity with no visible aliases.");
        var entry = entries[0];
        var separator = Array.IndexOf(entry, "-");
        if (separator < 6 || separator + 3 >= entry.Length || entry[3] != "/" || entry[4] != DedicatedMount
            || entry[separator + 1] != "tmpfs" || !entry[5].Split(',').Contains("rw", StringComparer.Ordinal)
            || entry.Skip(6).Take(separator - 6).Any(field => field.StartsWith("shared:", StringComparison.Ordinal)
                || field.StartsWith("master:", StringComparison.Ordinal) || field.StartsWith("propagate_from:", StringComparison.Ordinal)))
            throw new IOException("Proof requires a dedicated private tmpfs mount with no shared propagation.");
        var children = Directory.EnumerateFileSystemEntries(DedicatedMount).Take(2).ToArray();
        if (children.Length != 1 || children[0] != canonicalRoot)
            throw new IOException("Dedicated proof mount must contain only this marked fixture; unrelated tmpfs users are refused.");
    }

    private static ulong ReadPrivateDirectoryIdentity(Descriptor directory)
    {
        var bytes = new byte[512];
        if (Native.fstat(directory.Value, bytes) != 0) throw Failure("Inspect private proof directory");
        var modeOffset = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 24 : 16;
        var uidOffset = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 28 : 24;
        if ((BitConverter.ToUInt32(bytes, modeOffset) & 0xFFFF) != 0x41C0
            || BitConverter.ToUInt32(bytes, uidOffset) != Native.geteuid())
            throw new IOException("Dedicated proof mount and fixture must be current-user-owned private 0700 directories.");
        return BitConverter.ToUInt64(bytes, 0);
    }

    public void Dispose() => root.Dispose();
    private static IOException Failure(string operation) => new(operation + ": " + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
    private sealed class Descriptor : SafeHandleMinusOneIsInvalid
    {
        internal Descriptor(int value) : base(true) => SetHandle(new IntPtr(value));
        internal int Value => handle.ToInt32();
        protected override bool ReleaseHandle() => Native.close(handle.ToInt32()) == 0;
    }
    private static class Native
    {
        [DllImport("libc", SetLastError = true)] internal static extern int open(string path, int flags);
        [DllImport("libc", SetLastError = true)] internal static extern int openat(int directory, string name, int flags, uint mode);
        [DllImport("libc", SetLastError = true)] internal static extern int fstatfs(int descriptor, [Out] byte[] buffer);
        [DllImport("libc", SetLastError = true)] internal static extern int fstat(int descriptor, [Out] byte[] buffer);
        [DllImport("libc", SetLastError = true)] internal static extern nint read(int descriptor, [Out] byte[] buffer, nuint count);
        [DllImport("libc", SetLastError = true)] internal static extern nint write(int descriptor, byte[] buffer, nuint count);
        [DllImport("libc", SetLastError = true)] internal static extern nint readlink(string path, [Out] byte[] buffer, nuint count);
        [DllImport("libc", SetLastError = true)] internal static extern int close(int descriptor);
        [DllImport("libc")] internal static extern uint geteuid();
    }
}

internal sealed record DiskFullWitness(string Name, long FilesystemBytes, long FillerBytes, long AvailableBytes,
    int Errno, ulong Device, ulong Inode);
internal sealed record DiskFullFillerIdentity(string Name, ulong Device, ulong Inode);
