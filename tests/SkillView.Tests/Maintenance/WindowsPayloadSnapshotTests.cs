using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SkillView.Inventory;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class WindowsPayloadSnapshotTests
{
    [Fact]
    public void SameLengthRewriteWithStableIdentity_RefusesContentChange()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("skillview-win-content-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "SKILL.md"), "---\nname: demo\ndescription: Demo\n---\nBody\n");
            var file = Path.Combine(root.FullName, "script.txt");
            File.WriteAllText(file, "original");
            var changed = false;
            var error = Assert.Throws<IOException>(() => PayloadSnapshot.CaptureWithObservationForTests(root.FullName, (path, kind) =>
            {
                if (path != "script.txt" || kind != "file") return;
                var backend = new WindowsSecureRemovalBackend();
                Assert.True(backend.TryCaptureIdentity(file, out var before, out var message), message);
                File.WriteAllText(file, "modified");
                using var handle = Native.CreateFileW(file, 0x100, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                Assert.False(handle.IsInvalid);
                var info = new Native.BasicInfo { ChangeTime = before.WindowsChangeTime };
                Assert.True(Native.SetFileInformationByHandle(handle, 0, ref info, Marshal.SizeOf<Native.BasicInfo>()));
                Assert.True(backend.TryCaptureIdentity(file, out var after, out message), message);
                Assert.Equal(before, after);
                changed = true;
            }, TestContext.Current.CancellationToken));
            Assert.True(changed);
            Assert.Contains("content changed", error.Message);
            Assert.Equal("modified", File.ReadAllText(file));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void LaterSiblingAddsEntryToCompletedDirectoryWithStableIdentity_Refuses()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("skillview-win-late-entry-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "SKILL.md"), "---\nname: demo\ndescription: Demo\n---\nBody\n");
            foreach (var name in new[] { "one", "two" })
            {
                var directory = Directory.CreateDirectory(Path.Combine(root.FullName, name)).FullName;
                File.WriteAllText(Path.Combine(directory, "file.txt"), name);
            }
            string? firstDirectory = null;
            var firstComplete = false;
            Assert.Throws<IOException>(() => PayloadSnapshot.CaptureWithObservationForTests(root.FullName, (path, kind) =>
            {
                if (kind == "directory" && firstDirectory is null) firstDirectory = path;
                else if (kind == "file" && firstDirectory is not null && path.StartsWith(firstDirectory + "/", StringComparison.Ordinal))
                    firstComplete = true;
                else if (kind == "directory" && firstComplete && firstDirectory is not null)
                {
                    var previous = Path.Combine(root.FullName, firstDirectory);
                    var backend = new WindowsSecureRemovalBackend();
                    Assert.True(backend.TryCaptureIdentity(previous, out var before, out var error), error);
                    File.WriteAllText(Path.Combine(previous, "extra.txt"), "late extra");
                    using var handle = Native.CreateFileW(previous, 0x100, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                    Assert.False(handle.IsInvalid);
                    var info = new Native.BasicInfo { ChangeTime = before.WindowsChangeTime };
                    Assert.True(Native.SetFileInformationByHandle(handle, 0, ref info, Marshal.SizeOf<Native.BasicInfo>()),
                        $"restore generation error {Marshal.GetLastPInvokeError()}");
                    Assert.True(backend.TryCaptureIdentity(previous, out var after, out error), error);
                    Assert.Equal(before, after);
                }
            }, TestContext.Current.CancellationToken));
            Assert.True(firstComplete);
            Assert.NotNull(firstDirectory);
            Assert.True(File.Exists(Path.Combine(root.FullName, firstDirectory, "extra.txt")));
        }
        finally { root.Delete(true); }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct BasicInfo
        {
            internal long CreationTime;
            internal long LastAccessTime;
            internal long LastWriteTime;
            internal long ChangeTime;
            internal uint Attributes;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string name, uint access, uint sharing,
            IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFileInformationByHandle(SafeFileHandle handle,
            int informationClass, ref BasicInfo information, int size);
    }
}
