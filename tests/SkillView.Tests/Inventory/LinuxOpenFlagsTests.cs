using System.Runtime.InteropServices;
using SkillView.Inventory;
using Xunit;

namespace SkillView.Tests.Inventory;

public sealed class LinuxOpenFlagsTests
{
    [Theory]
    [InlineData(Architecture.X64, 0x10000, 0x20000)]
    [InlineData(Architecture.Arm64, 0x4000, 0x8000)]
    public void UsesVerifiedArchitectureSpecificDirectoryAndNoFollowBits(Architecture architecture, int directory, int noFollow)
    {
        var flags = LinuxOpenFlags.ForArchitecture(architecture, true);
        Assert.Equal(directory, flags.Directory);
        Assert.Equal(noFollow, flags.NoFollow);
    }

    [Theory]
    [InlineData(Architecture.Arm64, false)]
    [InlineData(Architecture.X64, false)]
    [InlineData(Architecture.X86, true)]
    [InlineData(Architecture.Arm, true)]
    public void UnverifiedAbiIsRefusedBeforeNativeOpen(Architecture architecture, bool littleEndian) =>
        Assert.Throws<IOException>(() => LinuxOpenFlags.ForArchitecture(architecture, littleEndian));

    [Fact]
    public void NativeLinuxDirectoryCanonicalizationAndFileNoFollowUseTheSameVerifiedAbi()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "skillview-linux-open-flags-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.True(SecureRemovalBackend.TryCanonicalizePath("/", out var filesystemRoot, out var rootError), rootError);
            Assert.Equal("/", filesystemRoot);
            Assert.True(SecureRemovalBackend.TryCanonicalizePath(root, out var canonical, out var error), error);
            Assert.True(Path.IsPathFullyQualified(canonical));
            Assert.True(Directory.Exists(canonical));
            var file = Path.Combine(root, "regular"); var link = Path.Combine(root, "link");
            File.WriteAllText(file, "unchanged");
            File.CreateSymbolicLink(link, file);
            Assert.Throws<IOException>(() => { using var rejected = RegularFileInspection.Open(link); });
            using (var regular = RegularFileInspection.Open(file)) Assert.Equal(9, regular.Length);
            Assert.Equal("unchanged", File.ReadAllText(file));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
