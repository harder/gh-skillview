using System.Runtime.InteropServices;
using SkillView.Inventory;
using Xunit;

namespace SkillView.Tests.Inventory;

public sealed class UnixSecureRemovalBackendTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "skillview-unix-native-" + Guid.NewGuid().ToString("N"));

    public UnixSecureRemovalBackendTests() => Directory.CreateDirectory(_tempRoot);

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(Architecture.X64, 24)]
    [InlineData(Architecture.Arm64, 16)]
    public void TryGetLinuxStatLayout_KnownLittleEndianAbi_UsesExpectedModeOffset(
        Architecture architecture,
        int expectedModeOffset)
    {
        var supported = UnixSecureRemovalBackend.TryGetLinuxStatLayout(
            architecture,
            isLittleEndian: true,
            out var layout);

        Assert.True(supported);
        Assert.Equal(0, layout.DeviceOffset);
        Assert.Equal(8, layout.InodeOffset);
        Assert.Equal(expectedModeOffset, layout.ModeOffset);
    }

    [Theory]
    [InlineData(Architecture.X86)]
    [InlineData(Architecture.Arm)]
    [InlineData(Architecture.Wasm)]
    public void TryGetLinuxStatLayout_UnverifiedArchitecture_FailsClosed(
        Architecture architecture)
    {
        var supported = UnixSecureRemovalBackend.TryGetLinuxStatLayout(
            architecture,
            isLittleEndian: true,
            out var layout);

        Assert.False(supported);
        Assert.Equal(default, layout);
    }

    [Theory]
    [InlineData(Architecture.X64)]
    [InlineData(Architecture.Arm64)]
    public void TryGetLinuxStatLayout_BigEndian_FailsClosed(Architecture architecture)
    {
        var supported = UnixSecureRemovalBackend.TryGetLinuxStatLayout(
            architecture,
            isLittleEndian: false,
            out var layout);

        Assert.False(supported);
        Assert.Equal(default, layout);
    }

    [Theory]
    [InlineData(Architecture.X64)]
    [InlineData(Architecture.Arm64)]
    public void IsLinuxPlatformSupported_RequiresOpenAt2(Architecture architecture)
    {
        Assert.True(UnixSecureRemovalBackend.IsLinuxPlatformSupported(
            architecture,
            isLittleEndian: true,
            openAt2Available: true));
        Assert.False(UnixSecureRemovalBackend.IsLinuxPlatformSupported(
            architecture,
            isLittleEndian: true,
            openAt2Available: false));
    }

    [Fact]
    public void IsLinuxPlatformSupported_RejectsUnknownStatLayoutEvenWithOpenAt2()
    {
        Assert.False(UnixSecureRemovalBackend.IsLinuxPlatformSupported(
            Architecture.X86,
            isLittleEndian: true,
            openAt2Available: true));
    }

    [Fact]
    public void TryCaptureIdentity_UsesFinalPathFromOpenedHandle()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        if (!UnixSecureRemovalBackend.IsSupportedOnCurrentPlatform) return;

        var realRoot = Path.Combine(_tempRoot, "real-root");
        var linkedRoot = Path.Combine(_tempRoot, "linked-root");
        var skill = Path.Combine(realRoot, "skill");
        Directory.CreateDirectory(skill);
        try
        {
            Directory.CreateSymbolicLink(linkedRoot, realRoot);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            return;
        }

        var captured = new UnixSecureRemovalBackend().TryCaptureIdentity(
            Path.Combine(linkedRoot, "skill"),
            out var identity,
            out var error);

        if (!captured) Assert.Fail(error ?? "identity capture failed without an error");
        Assert.EndsWith(
            Path.Combine("real-root", "skill"),
            identity.CanonicalPath,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"{Path.DirectorySeparatorChar}linked-root{Path.DirectorySeparatorChar}",
            identity.CanonicalPath,
            StringComparison.Ordinal);
    }
}
