using System.Text;
using SkillView.Inventory;
using Xunit;

namespace SkillView.Tests.Inventory;

public sealed class LinuxLinkProcAuthorityTests
{
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);
    [Theory]
    [InlineData("1/task/2", true)]
    [InlineData("001/task/2", false)]
    [InlineData("1/task/0", false)]
    [InlineData("1/../2", false)]
    [InlineData("1/task/2147483648", false)]
    public void TaskAddressRequiresExactPositiveKernelTaskForm(string value, bool valid)
    {
        if (valid) Assert.Equal(value, LinuxLinkProcAuthority.ParseTask(value));
        else Assert.Throws<IOException>(() => LinuxLinkProcAuthority.ParseTask(value));
    }
    [Theory]
    [InlineData("mnt_id:\t7\nino:\t8\n", true)]
    [InlineData("mnt_id:\t7\nino:\t8", false)]
    [InlineData("mnt_id:\t7\nmnt_id:\t7\nino:\t8\n", false)]
    [InlineData("mnt_id:\t7\n", false)]
    [InlineData("mnt_id:\t18446744073709551616\nino:\t8\n", false)]
    [InlineData("mnt_id:\t7\nino:\t8\n\n", false)]
    public void FdInfoRequiresCompleteUniqueBoundedRecords(string value, bool valid)
    {
        if (valid) Assert.Equal(new LinuxLinkFdInfo(7, 8), LinuxLinkProcAuthority.ParseFdInfo(Bytes(value), Token));
        else Assert.Throws<IOException>(() => LinuxLinkProcAuthority.ParseFdInfo(Bytes(value), Token));
    }
    [Theory]
    [InlineData("7 1 8:2 / /rw rw - ext4 /dev/root rw\n", true)]
    [InlineData("7 1 8:2 / /rw\\040path rw - ext4 /dev/root rw\n", true)]
    [InlineData("7 1 8:2 / /rw\\041path rw - ext4 /dev/root rw\n", false)]
    [InlineData("7 1 4294967296:2 / /rw rw - ext4 /dev/root rw\n", false)]
    [InlineData("7 1 8:2 / /rw rw - ext4 /dev/root rw\n7 1 8:2 / /rw rw - ext4 /dev/root rw\n", false)]
    [InlineData("7 1 8:2 / /rw rw - ext4 /dev/root\n", false)]
    public void MountInfoRejectsMalformedEscapesDuplicateIdsOverflowAndTruncation(string value, bool valid)
    {
        if (valid) Assert.Equal("ext4", Assert.Single(LinuxLinkProcAuthority.ParseMountInfo(Bytes(value), Token)).Value.FileSystem);
        else Assert.Throws<IOException>(() => LinuxLinkProcAuthority.ParseMountInfo(Bytes(value), Token));
    }
    [Theory]
    [InlineData("ext4", 0xef53UL, 7UL, true)]
    [InlineData("ext3", 0xef53UL, 7UL, false)]
    [InlineData("overlay", 0xef53UL, 7UL, false)]
    [InlineData("ext4", 0xef53UL, 9UL, false)]
    [InlineData("ext4", 0x1021994UL, 7UL, false)]
    public void MagicAloneNeverGrantsFilesystemPreservation(string fs, ulong magic, ulong mount, bool valid)
    {
        var table = LinuxLinkProcAuthority.ParseMountInfo(Bytes($"7 1 8:2 / /rw rw - {fs} /dev/root rw\n"), Token);
        if (valid) Assert.Equal("ext4", LinuxLinkProcAuthority.RequireExt4(new(0x802, magic, 10), new(mount, 8), table).FileSystem);
        else Assert.Throws<IOException>(() => LinuxLinkProcAuthority.RequireExt4(new(0x802, magic, 10), new(mount, 8), table));
    }
    [Theory]
    [InlineData(0UL, 0U, 0U)]
    [InlineData(0x802UL, 8U, 2U)]
    [InlineData(ulong.MaxValue, uint.MaxValue, uint.MaxValue)]
    public void DeviceMajorMinorUseTheVerifiedFullWidthLinuxMacros(ulong device, uint major, uint minor)
    {
        Assert.Equal(major, LinuxLinkProcAuthority.DeviceMajor(device));
        Assert.Equal(minor, LinuxLinkProcAuthority.DeviceMinor(device));
    }
    [Fact]
    public void ProcByteAndRecordBoundsAndCancellationRefuse()
    {
        Assert.Throws<IOException>(() => LinuxLinkProcAuthority.ParseFdInfo(new byte[LinuxLinkProcAuthority.MaxFdInfoBytes + 1], Token));
        Assert.Throws<IOException>(() => LinuxLinkProcAuthority.ParseMountInfo(new byte[LinuxLinkProcAuthority.MaxMountInfoBytes + 1], Token));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => LinuxLinkProcAuthority.ParseFdInfo(Bytes("mnt_id:\t7\nino:\t8\n"), canceled.Token));
    }
    [Theory]
    [InlineData("device")]
    [InlineData("fsid")]
    [InlineData("mount")]
    [InlineData("namespace")]
    [InlineData("task")]
    public void EqualMagicAndNativeIdsCannotHideDifferentMountNamespaceTaskOrFileSystem(string field)
    {
        var expected = new LinuxLinkMountObservation(new(0x802, 0xef53, 9), 8, 2, 7,
            new(4, 5, 0, "", false, false, 1, 1), "1/task/2");
        var changed = field switch
        {
            "device" => expected with { FileSystem = expected.FileSystem with { Device = 0x803 } },
            "fsid" => expected with { FileSystem = expected.FileSystem with { FileSystemId = 10 } },
            "mount" => expected with { MountId = 8 },
            "namespace" => expected with { MountNamespace = expected.MountNamespace with { FileIdLow = 6 } },
            _ => expected with { Task = "1/task/3" }
        };
        LinuxLinkProcAuthority.RequireSameMount(expected, expected);
        Assert.Throws<IOException>(() => LinuxLinkProcAuthority.RequireSameMount(expected, changed));
    }

    [Theory]
    [InlineData("mount-record")]
    [InlineData("fdinfo")]
    [InlineData("fsid")]
    [InlineData("object-generation")]
    public void SecondFreshSampleChangesRefuseEvenThoughTheFirstSampleWasAdmissible(string change)
    {
        var reads = 0;
        var identity = new SecureFileIdentity(0x802, 8, 0, "", true, false, 1, 1);
        LinuxLinkNativeMountSample Read()
        {
            var second = ++reads == 2;
            var mounts = LinuxLinkProcAuthority.ParseMountInfo(Bytes("7 1 8:2 / /rw "
                + (second && change == "mount-record" ? "ro" : "rw") + " - ext4 /dev/root rw\n"), Token);
            var fd = LinuxLinkProcAuthority.ParseFdInfo(Bytes($"mnt_id:\t7\nino:\t{(second && change == "fdinfo" ? 9 : 8)}\n"), Token);
            var fs = new LinuxLinkFilesystem(0x802, 0xef53, second && change == "fsid" ? 11UL : 10UL);
            return new(second && change == "object-generation" ? identity with { ChangeTimeNanoseconds = 2 } : identity,
                fs, fd, LinuxLinkProcAuthority.RequireExt4(fs, fd, mounts));
        }
        // The former single-read admission sees a valid ext4 observation.
        Assert.Equal(7UL, Read().Mount.Id);
        reads = 0;
        Assert.Throws<IOException>(() => LinuxLinkProcAuthority.ReadStableSample(Read, Token));
        Assert.Equal(2, reads);
    }

    [Fact]
    public void CancellationBetweenFreshSamplesStopsBeforeASecondRead()
    {
        using var canceled = new CancellationTokenSource(); var reads = 0;
        LinuxLinkNativeMountSample Read()
        {
            reads++; canceled.Cancel();
            return new(new(0x802, 8, 0, "", true, false, 1, 1), new(0x802, 0xef53, 10), new(7, 8),
                new(7, 1, 8, 2, "ext4", "7 1 8:2 / / rw - ext4 /dev/root rw"));
        }
        var failure = Assert.Throws<OperationCanceledException>(() => LinuxLinkProcAuthority.ReadStableSample(Read, canceled.Token));
        Assert.Equal(canceled.Token, failure.CancellationToken); Assert.Equal(1, reads);
    }

    [Fact]
    public void ASecondFreshMountTableCanChangeOnlyAnUnrelatedRecord()
    {
        var reads = 0;
        LinuxLinkNativeMountSample Read()
        {
            var mounts = LinuxLinkProcAuthority.ParseMountInfo(Bytes("7 1 8:2 / /rw rw - ext4 /dev/root rw\n"
                + $"9 1 0:4 / /unrelated rw - tmpfs tmpfs {(++reads == 1 ? "rw" : "ro")}\n"), Token);
            var fs = new LinuxLinkFilesystem(0x802, 0xef53, 10); var fd = new LinuxLinkFdInfo(7, 8);
            return new(new(0x802, 8, 0, "", true, false, 1, 1), fs, fd, LinuxLinkProcAuthority.RequireExt4(fs, fd, mounts));
        }
        Assert.Equal(7UL, LinuxLinkProcAuthority.ReadStableSample(Read, Token).Mount.Id);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void CurrentThreadDescriptorTableRemainsAuthoritativeAfterSafeUnshareWhenPermitted()
    {
        if (!OperatingSystem.IsLinux()) return;
        var token = Token;
        var temporary = Directory.CreateTempSubdirectory("skillview-thread-fd-");
        var path = Path.Combine(temporary.FullName, "only-this-thread"); File.WriteAllText(path, "private descriptor");
        var requiredExt4 = System.Environment.GetEnvironmentVariable("SKILLVIEW_LINUX_EXPOSURE_PRESERVATION_PROOF") == "1";
        Exception? failure = null; var denied = 0; var positive = false;
        var worker = new Thread(() =>
        {
            try
            {
                if (ProcNative.unshare(0x400) != 0)
                {
                    denied = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                    if (denied != 1) throw new IOException("Unexpected CLONE_FILES refusal: " + denied);
                    return;
                }
                using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var fd = handle.DangerousGetHandle().ToInt32();
                Assert.Equal(path, new FileInfo($"/proc/thread-self/fd/{fd}").LinkTarget);
                // A descriptor opened after unshare is absent/different in the
                // leader's table; never use that leader path as authority.
                Assert.NotEqual(path, new FileInfo($"/proc/self/fd/{fd}").LinkTarget);
                using var authority = LinuxLinkProcAuthority.Open(token);
                authority.Check(token);
                // Descriptor-table authority is filesystem-independent. The
                // required preservation proof separately demands actual ext4;
                // ordinary tmpfs/non-ext4 temporary roots are not test failures.
                if (requiredExt4)
                {
                    var observed = authority.ObserveHandle(handle, token);
                    Assert.Equal(0xef53UL, observed.FileSystem.Type);
                    Assert.True(observed.MountId > 0);
                }
                authority.Check(token);
                positive = true;
            }
            catch (Exception ex) { failure = ex; }
        });
        try
        {
            // Required native proof must refuse a non-ext4 fixture even when
            // CLONE_FILES is later unavailable. Unknown proc/identity failures
            // propagate; ordinary descriptor-table testing has no ext4 gate.
            if (requiredExt4)
            {
                using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var authority = LinuxLinkProcAuthority.Open(token);
                var observed = authority.ObserveHandle(handle, token);
                Assert.Equal(0xef53UL, observed.FileSystem.Type);
                Assert.True(observed.MountId > 0);
            }
            worker.Start(); worker.Join();
            Assert.Null(failure);
            if (denied != 0) TestContext.Current.TestOutputHelper!.WriteLine("CLONE_FILES fixture unavailable: unshare returned EPERM; no descriptor-divergence positive claim.");
            else
            {
                Assert.True(positive);
                TestContext.Current.TestOutputHelper!.WriteLine(requiredExt4
                    ? "CLONE_FILES fixture positive: current-thread descriptor differs from leader; held proc authority and required ext4 observation passed."
                    : "CLONE_FILES fixture positive: current-thread descriptor differs from leader and held proc authority passed; no ext4 admission claimed.");
            }
        }
        finally { temporary.Delete(true); }
    }
    private static class ProcNative
    {
        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        internal static extern int unshare(int flags);
    }
}
