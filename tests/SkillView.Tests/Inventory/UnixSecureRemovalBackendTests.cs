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
        Assert.Equal(104, layout.ChangeTimeSecondsOffset);
        Assert.Equal(112, layout.ChangeTimeNanosecondsOffset);
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

    [Fact]
    public void TryCaptureIdentity_FinalSymlinkToDirectory_FailsClosed()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        if (!UnixSecureRemovalBackend.IsSupportedOnCurrentPlatform) return;

        var target = Path.Combine(_tempRoot, "final-link-target");
        var link = Path.Combine(_tempRoot, "final-link");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            return;
        }

        var captured = new UnixSecureRemovalBackend().TryCaptureIdentity(
            link,
            out _,
            out var error);

        Assert.False(captured);
        Assert.NotNull(error);
        Assert.True(Directory.Exists(target));
        Assert.True(PathResolver.IsSymlink(link));
    }

    [Fact]
    public void TryCaptureDirectoryValidation_AncestorAba_InspectsOpenedDirectory()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        if (!UnixSecureRemovalBackend.IsSupportedOnCurrentPlatform) return;

        var parent = Path.Combine(_tempRoot, "policy-parent");
        var movedParent = Path.Combine(_tempRoot, "policy-parent-original");
        var skill = Path.Combine(parent, "skill");
        Directory.CreateDirectory(Path.Combine(skill, ".git"));
        File.WriteAllText(Path.Combine(skill, "SKILL.md"), "body");
        var backend = new UnixSecureRemovalBackend(() =>
        {
            Directory.Move(parent, movedParent);
            Directory.CreateDirectory(Path.Combine(parent, "skill"));
            File.WriteAllText(Path.Combine(parent, "skill", "SKILL.md"), "clean");
        });

        var captured = backend.TryCaptureDirectoryValidation(
            skill,
            out var snapshot,
            out var error);

        Assert.True(captured, error);
        Assert.True(snapshot.HasSkillFile);
        Assert.True(snapshot.HasGitDirectory);
        Assert.Contains("policy-parent-original", snapshot.Identity.CanonicalPath);
    }

    [Fact]
    public void TryCaptureLinkValidation_BrokenLinkReplacedWithValidLink_FailsClosed()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        if (!UnixSecureRemovalBackend.IsSupportedOnCurrentPlatform) return;

        var target = Path.Combine(_tempRoot, "valid-target");
        var link = Path.Combine(_tempRoot, "changing-broken-link");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(link, Path.Combine(_tempRoot, "missing"));
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            return;
        }
        var backend = new UnixSecureRemovalBackend(linkTargetObservedForTests: () =>
        {
            Directory.Delete(link);
            Directory.CreateSymbolicLink(link, target);
        });

        var captured = backend.TryCaptureLinkValidation(link, out _, out var error);

        Assert.False(captured);
        Assert.NotNull(error);
        Assert.True(Directory.Exists(target));
        Assert.True(PathResolver.IsSymlink(link));
    }
}
