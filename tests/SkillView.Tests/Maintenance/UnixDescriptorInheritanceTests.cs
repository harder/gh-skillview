using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SkillView.Inventory;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class UnixDescriptorInheritanceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("skillview-descriptors-").FullName;

    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Duplicate_HasCloseOnExecBeforeStreamOrDirectoryAdmission(bool directory)
    {
        if (!UnixSecureRemovalBackend.IsRegularFileInspectionSupported) return;
        var path = directory ? _root : Path.Combine(_root, "private.txt");
        if (!directory) File.WriteAllText(path, "private fixture");
        var original = open(path, 0);
        Assert.True(original >= 0);
        using var originalHandle = new SafeFileHandle((nint)original, true);
        var duplicate = UnixSecureRemovalBackend.DupWithRetry(original);
        Assert.True(duplicate >= 0);
        using var duplicateHandle = new SafeFileHandle((nint)duplicate, true);
        var duplicateFlags = fcntl_getfd(duplicate, 1);
        var originalFlags = fcntl_getfd(original, 1);
        Assert.True(duplicateFlags >= 0);
        Assert.True(originalFlags >= 0);
        Assert.Equal(1, duplicateFlags & 1);
        Assert.Equal(0, originalFlags & 1);
    }

    [Fact]
    public void Duplicate_InvalidDescriptorFailsWithoutFallback()
    {
        if (!UnixSecureRemovalBackend.IsRegularFileInspectionSupported) return;
        Assert.Equal(-1, UnixSecureRemovalBackend.DupWithRetry(-1));
        Assert.Equal(9, Marshal.GetLastPInvokeError()); // EBADF on Linux and Darwin.
    }

    [Fact]
    public void Snapshot_ChildExecCannotReadHeldPayloadButParentStillCan()
    {
        if (!UnixSecureRemovalBackend.IsSupportedOnCurrentPlatform) return;
        const string marker = "private-fixture-payload-7c620a";
        File.WriteAllText(Path.Combine(_root, "private.txt"), marker);
        var observed = false;
        var snapshot = PayloadSnapshot.CaptureWithEntrySink(_root, (item, _) =>
        {
            if (item.RelativePath != "private.txt") return;
            observed = true;
            var stream = Assert.IsType<FileStream>(item.Content);
            var descriptor = stream.SafeFileHandle.DangerousGetHandle().ToInt32();
            var flags = fcntl_getfd(descriptor, 1);
            Assert.True(flags >= 0);
            var start = new ProcessStartInfo("/bin/sh")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("if /bin/cat \"/dev/fd/$1\" 2>/dev/null; then exit 9; else exit 0; fi");
            start.ArgumentList.Add("skillview-descriptor-proof");
            start.ArgumentList.Add(descriptor.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var child = Process.Start(start) ?? throw new IOException("Could not launch descriptor proof child.");
            try
            {
                Assert.True(child.WaitForExit(10_000), "Descriptor proof child did not exit.");
                Assert.Equal(0, child.ExitCode);
                Assert.Equal("", child.StandardOutput.ReadToEnd());
                Assert.Equal("", child.StandardError.ReadToEnd());
            }
            finally { if (!child.HasExited) { child.Kill(true); child.WaitForExit(10_000); } }
            Assert.Equal(1, flags & 1);
            stream.Position = 0;
            using var reader = new StreamReader(stream, leaveOpen: true);
            Assert.Equal(marker, reader.ReadToEnd());
        }, TestContext.Current.CancellationToken);
        Assert.True(observed);
        Assert.Single(snapshot.Entries);
        Assert.Equal(marker, File.ReadAllText(Path.Combine(_root, "private.txt")));
        Assert.Equal(snapshot.Fingerprint, PayloadSnapshot.CaptureEvidence(_root, TestContext.Current.CancellationToken).Fingerprint);
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int fcntl_getfd(int descriptor, int command);
}
