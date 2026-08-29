using System.Runtime.InteropServices;
using SkillView.Inventory;
using Xunit;

namespace SkillView.Tests.Inventory;

public class UnixSecureRemovalBackendTests
{
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
}
