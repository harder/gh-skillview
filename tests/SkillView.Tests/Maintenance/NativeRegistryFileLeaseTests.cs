using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SkillView.Inventory;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class NativeRegistryFileLeaseTests : IDisposable
{
    private readonly DirectoryInfo temporary = Directory.CreateTempSubdirectory("skillview-registry-lease-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private string Parent => Path.Combine(temporary.FullName, "agents");
    private string Registry => Path.Combine(Parent, ".skill-lock.json");
    private static readonly byte[] Original = Encoding.UTF8.GetBytes("old registry\n");
    public NativeRegistryFileLeaseTests()
    {
        Directory.CreateDirectory(Parent);
        System.IO.File.WriteAllBytes(Registry, Original);
    }
    public void Dispose() => temporary.Delete(true);

    [Fact]
    public void WritePreservesNativeFileObjectAndUnixPermissions()
    {
        if (!OperatingSystem.IsWindows()) System.IO.File.SetUnixFileMode(Registry, (UnixFileMode)416);
        using var lease = NativeRegistryFileLease.Open(Registry, Token);
        var before = lease.Read(Token);
        var desired = Encoding.UTF8.GetBytes("desired registry contents\n");
        var after = lease.Write(before, desired, Token);
        Assert.Equal(desired, after.Bytes);
        Assert.True(NativeDirectoryMoveLease.SameObject(before.Identity, after.Identity));
        Assert.Equal(NativeRegistryFileLease.Hash(desired), after.ContentHash);
        Assert.True(lease.MutationAttempted);
        if (!OperatingSystem.IsWindows()) Assert.Equal((UnixFileMode)416, System.IO.File.GetUnixFileMode(Registry));
        lease.Dispose();
        Assert.Equal(desired, System.IO.File.ReadAllBytes(Registry));
    }

    [Fact]
    public void SecondDataFileLockRefusesUntilFirstLeaseDisposes()
    {
        var first = NativeRegistryFileLease.Open(Registry, Token);
        try { Assert.Throws<IOException>(() => NativeRegistryFileLease.Open(Registry, Token)); }
        finally { first.Dispose(); }
        using var next = NativeRegistryFileLease.Open(Registry, Token);
        Assert.Equal(Original, next.Read(Token).Bytes);
    }

    [Fact]
    public void UnixDisposeUnlocksEvenWhileDuplicatedFileDescriptionRemainsOpen()
    {
        if (OperatingSystem.IsWindows()) return;
        using var first = NativeRegistryFileLease.Open(Registry, Token);
        var property = typeof(NativeRegistryFileLease).GetProperty("File", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var stream = (FileStream)property.GetValue(first)!;
        var fd = UnixSecureRemovalBackend.DupWithRetry(stream.SafeFileHandle.DangerousGetHandle().ToInt32());
        Assert.True(fd >= 0);
        using var duplicate = new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
        Assert.Throws<IOException>(() => NativeRegistryFileLease.Open(Registry, Token));
        first.Dispose();
        // This used to fail until the duplicate (or a forked child before
        // exec) closed its descriptor. Disposing the owner must end its lock.
        using var next = NativeRegistryFileLease.Open(Registry, Token);
        Assert.Equal(Original, next.Read(Token).Bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnixFailedOpenUnlocksDuplicatedDescriptionAndPreservesOriginalFailure(bool cancel)
    {
        if (OperatingSystem.IsWindows()) return;
        using var cancellation = new CancellationTokenSource();
        SafeFileHandle? duplicate = null;
        var injected = new IOException("Injected initialization failure");
        try
        {
            var error = Record.Exception(() => UnixSecureRemovalBackend.OpenRegistryFile(Registry, cancellation.Token, descriptor =>
            {
                var fd = UnixSecureRemovalBackend.DupWithRetry(descriptor);
                Assert.True(fd >= 0);
                duplicate = new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
                if (cancel) cancellation.Cancel(); else throw injected;
            }));
            if (cancel) Assert.IsType<OperationCanceledException>(error); else Assert.Same(injected, error);
            using var next = NativeRegistryFileLease.Open(Registry, Token);
            Assert.Equal(Original, next.Read(Token).Bytes);
        }
        finally { duplicate?.Dispose(); }
    }

    [Fact]
    public void FinalLinkCannotRedirectRegistryAuthority()
    {
        var external = Path.Combine(temporary.FullName, "external");
        System.IO.File.WriteAllBytes(external, Original);
        System.IO.File.Delete(Registry);
        System.IO.File.CreateSymbolicLink(Registry, external);
        Assert.Throws<IOException>(() => NativeRegistryFileLease.Open(Registry, Token));
        Assert.Equal(Original, System.IO.File.ReadAllBytes(external));
    }

    [Fact]
    public void ReplacedFileAddressRefusesBeforeTruncation()
    {
        using var lease = NativeRegistryFileLease.Open(Registry, Token);
        var before = lease.Read(Token);
        var retained = Registry + ".retained";
        try { System.IO.File.Move(Registry, retained); }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException)
        {
            Assert.False(lease.MutationAttempted);
            lease.Dispose();
            Assert.Equal(Original, System.IO.File.ReadAllBytes(Registry)); return;
        }
        System.IO.File.WriteAllBytes(Registry, Original);
        Assert.Throws<IOException>(() => lease.Write(before, Encoding.UTF8.GetBytes("new"), Token));
        lease.Dispose();
        Assert.Equal(Original, System.IO.File.ReadAllBytes(Registry));
        Assert.Equal(Original, System.IO.File.ReadAllBytes(retained));
        Assert.False(lease.MutationAttempted);
    }

    [Fact]
    public void RenamedParentCannotRedirectWriteToNewNamespace()
    {
        using var lease = NativeRegistryFileLease.Open(Registry, Token);
        var before = lease.Read(Token);
        var moved = Parent + ".moved";
        try { Directory.Move(Parent, moved); }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException)
        {
            Assert.True(Directory.Exists(Parent)); Assert.False(Directory.Exists(moved)); Assert.False(lease.MutationAttempted); return;
        }
        Directory.CreateDirectory(Parent);
        System.IO.File.WriteAllBytes(Registry, Original);
        Assert.Throws<IOException>(() => lease.Write(before, Encoding.UTF8.GetBytes("new"), Token));
        lease.Dispose();
        Assert.Equal(Original, System.IO.File.ReadAllBytes(Registry));
        Assert.Equal(Original, System.IO.File.ReadAllBytes(Path.Combine(moved, ".skill-lock.json")));
        Assert.False(lease.MutationAttempted);
    }

    [Fact]
    public void SameLengthUncooperativeChangeCannotBeOverwrittenFromStaleSnapshot()
    {
        using var lease = NativeRegistryFileLease.Open(Registry, Token);
        var before = lease.Read(Token);
        // gh locks byte zero on Windows. A noncooperating writer can still
        // change another byte; content evidence must refuse that stale intent.
        using (var other = NativeRegistryTestWriter.Open(Registry))
        {
            other.Position = 1; other.WriteByte((byte)'X'); other.Flush(true);
        }
        Assert.Throws<IOException>(() => lease.Write(before, Encoding.UTF8.GetBytes("replacement"), Token));
        lease.Dispose();
        Assert.Equal("oXd registry\n", System.IO.File.ReadAllText(Registry));
        Assert.False(lease.MutationAttempted);
    }

    [Fact]
    public void ByteLimitsAndCancellationRefuseBeforeMutation()
    {
        using var lease = NativeRegistryFileLease.Open(Registry, Token);
        var before = lease.Read(Token);
        Assert.Throws<IOException>(() => lease.Write(before, new byte[SkillLockFileReader.MaxFileBytes + 1], Token));
        Assert.Throws<OperationCanceledException>(() => lease.Write(before, [], new(true)));
        Assert.Throws<OperationCanceledException>(() => NativeRegistryFileLease.Open(Registry + ".missing", new(true)));
        Assert.False(lease.MutationAttempted);
        lease.Dispose();
        Assert.Equal(Original, System.IO.File.ReadAllBytes(Registry));
    }

    [Fact]
    public void OversizedAndMissingExistingFilesAreNotModifiedOrCreated()
    {
        var size = SkillLockFileReader.MaxFileBytes + 1;
        System.IO.File.WriteAllBytes(Registry, new byte[size]);
        using (var lease = NativeRegistryFileLease.Open(Registry, Token)) Assert.Throws<IOException>(() => lease.Read(Token));
        Assert.Equal(size, new FileInfo(Registry).Length);
        System.IO.File.Delete(Registry);
        Assert.Throws<IOException>(() => NativeRegistryFileLease.Open(Registry, Token));
        Assert.False(System.IO.File.Exists(Registry));
    }
}

internal static class NativeRegistryTestWriter
{
    internal static FileStream Open(string path)
    {
        if (OperatingSystem.IsWindows())
            return new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
        // A path-based .NET open takes flock too. Borrow an ordinary native
        // descriptor to model a writer that does not obey the manager lock.
        var fd = NativeOpen(path, 1 | (OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000));
        if (fd < 0) throw new IOException("Test native open failed: " + Marshal.GetLastPInvokeError());
        var handle = new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
        try { return new FileStream(handle, FileAccess.Write, bufferSize: 1); }
        catch { handle.Dispose(); throw; }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int NativeOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

}
