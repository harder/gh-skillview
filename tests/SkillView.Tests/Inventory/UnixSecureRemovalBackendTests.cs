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

    [Theory]
    [InlineData(Architecture.Arm64, true, true)]
    [InlineData(Architecture.X64, true, false)]
    [InlineData(Architecture.Arm64, false, false)]
    [InlineData(Architecture.X86, true, false)]
    public void IsMacPlatformSupported_RequiresLittleEndianArm64(
        Architecture architecture,
        bool isLittleEndian,
        bool expected)
    {
        Assert.Equal(expected, UnixSecureRemovalBackend.IsMacPlatformSupported(
            architecture,
            isLittleEndian));
    }

    [Fact]
    public void IsSameDevice_RejectsCrossDeviceChild()
    {
        Assert.True(UnixSecureRemovalBackend.IsSameDevice(17, 17));
        Assert.False(UnixSecureRemovalBackend.IsSameDevice(17, 18));
    }

    [Fact]
    public void RetryOnInterrupted_RetriesEintrAndReturnsLaterResult()
    {
        var attempts = 0;

        var result = UnixSecureRemovalBackend.RetryOnInterrupted(() =>
        {
            attempts++;
            if (attempts < 4)
            {
                Marshal.SetLastPInvokeError(4);
                return -1;
            }
            return 17;
        });

        Assert.Equal(17, result);
        Assert.Equal(4, attempts);
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
    public void TryCanonicalizePath_FilesystemRoot_Succeeds()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        if (!UnixSecureRemovalBackend.IsSupportedOnCurrentPlatform) return;

        var filesystemRoot = Path.GetPathRoot(_tempRoot)!;
        var canonicalized = new UnixSecureRemovalBackend().TryCanonicalizePath(
            filesystemRoot,
            out var canonicalPath,
            out var error);

        Assert.True(canonicalized, error);
        Assert.Equal(Path.GetFullPath(filesystemRoot), canonicalPath);
    }

    [Fact]
    public void TryCaptureDirectoryValidationWithinRoot_RootLinkRetargeted_UsesHeldRoot()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        if (!UnixSecureRemovalBackend.IsSupportedOnCurrentPlatform) return;

        var originalRoot = Path.Combine(_tempRoot, "root-original");
        var replacementRoot = Path.Combine(_tempRoot, "root-replacement");
        var linkedRoot = Path.Combine(_tempRoot, "root-link");
        var originalSkill = Path.Combine(originalRoot, "skill");
        var replacementSkill = Path.Combine(replacementRoot, "skill");
        Directory.CreateDirectory(Path.Combine(originalSkill, ".git"));
        Directory.CreateDirectory(replacementSkill);
        File.WriteAllText(Path.Combine(originalSkill, "SKILL.md"), "original");
        File.WriteAllText(Path.Combine(replacementSkill, "SKILL.md"), "replacement");
        try
        {
            Directory.CreateSymbolicLink(linkedRoot, originalRoot);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            return;
        }

        var backend = new UnixSecureRemovalBackend(
            rootIdentityCapturedForTests: () =>
            {
                Directory.Delete(linkedRoot);
                Directory.CreateSymbolicLink(linkedRoot, replacementRoot);
            });
        var captured = backend.TryCaptureDirectoryValidationWithinRoot(
            linkedRoot,
            Path.Combine(linkedRoot, "skill"),
            out var snapshot,
            out var error);

        Assert.True(captured, error);
        Assert.True(snapshot.Directory.HasGitDirectory);
        Assert.True(backend.TryCanonicalizePath(
            originalRoot,
            out var canonicalOriginalRoot,
            out var rootError), rootError);
        Assert.True(backend.TryCanonicalizePath(
            originalSkill,
            out var canonicalOriginalSkill,
            out var skillError), skillError);
        Assert.True(PathIdentity.Equals(
            canonicalOriginalRoot,
            snapshot.RootIdentity.CanonicalPath));
        Assert.True(PathIdentity.Equals(
            canonicalOriginalSkill,
            snapshot.Directory.Identity.CanonicalPath));
    }

    [Fact]
    public void TryCaptureDirectoryValidationWithinRoot_LinuxProcMount_IsRefused()
    {
        if (!OperatingSystem.IsLinux()) return;
        if (!UnixSecureRemovalBackend.IsSupportedOnCurrentPlatform) return;
        if (!Directory.Exists("/proc")) return;

        var captured = new UnixSecureRemovalBackend()
            .TryCaptureDirectoryValidationWithinRoot(
                Path.DirectorySeparatorChar.ToString(),
                "/proc",
                out _,
                out var error);

        Assert.False(captured);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryCaptureLinkValidationWithinRoot_RootLinkRetargeted_UsesHeldRoot()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        if (!UnixSecureRemovalBackend.IsSupportedOnCurrentPlatform) return;

        var originalRoot = Path.Combine(_tempRoot, "link-root-original");
        var replacementRoot = Path.Combine(_tempRoot, "link-root-replacement");
        var linkedRoot = Path.Combine(_tempRoot, "link-root-link");
        var originalLink = Path.Combine(originalRoot, "broken-link");
        var replacementLink = Path.Combine(replacementRoot, "broken-link");
        var replacementTarget = Path.Combine(replacementRoot, "valid-target");
        Directory.CreateDirectory(originalRoot);
        Directory.CreateDirectory(replacementTarget);
        try
        {
            Directory.CreateSymbolicLink(originalLink, Path.Combine(originalRoot, "missing"));
            Directory.CreateSymbolicLink(replacementLink, replacementTarget);
            Directory.CreateSymbolicLink(linkedRoot, originalRoot);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            return;
        }

        var backend = new UnixSecureRemovalBackend(
            rootIdentityCapturedForTests: () =>
            {
                Directory.Delete(linkedRoot);
                Directory.CreateSymbolicLink(linkedRoot, replacementRoot);
            });
        var captured = backend.TryCaptureLinkValidationWithinRoot(
            linkedRoot,
            Path.Combine(linkedRoot, "broken-link"),
            out var snapshot,
            out var error);

        Assert.True(captured, error);
        Assert.True(snapshot.Link.IsBroken);
        Assert.True(backend.TryCanonicalizePath(
            originalRoot,
            out var canonicalOriginalRoot,
            out var rootError), rootError);
        Assert.True(PathIdentity.Equals(
            canonicalOriginalRoot,
            snapshot.RootIdentity.CanonicalPath));
        Assert.True(PathIdentity.Equals(
            Path.Combine(canonicalOriginalRoot, "broken-link"),
            snapshot.Link.Identity.CanonicalPath));
    }

    [Fact]
    public void TryCaptureLinkValidationWithinRoot_LinuxProcParentMount_IsRefused()
    {
        if (!OperatingSystem.IsLinux()) return;
        if (!UnixSecureRemovalBackend.IsSupportedOnCurrentPlatform) return;
        if (!Directory.Exists("/proc/self/fd")) return;

        var captured = new UnixSecureRemovalBackend()
            .TryCaptureLinkValidationWithinRoot(
                Path.DirectorySeparatorChar.ToString(),
                "/proc/self/fd/0",
                out _,
                out var error);

        Assert.False(captured);
        Assert.False(string.IsNullOrWhiteSpace(error));
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
    public void TryCaptureIdentity_LiveDirectoryNamedDeletedSuffix_Succeeds()
    {
        if (!OperatingSystem.IsLinux()) return;
        if (!UnixSecureRemovalBackend.IsSupportedOnCurrentPlatform) return;

        var directory = Path.Combine(_tempRoot, "live-directory (deleted)");
        Directory.CreateDirectory(directory);

        var captured = new UnixSecureRemovalBackend().TryCaptureIdentity(
            directory,
            out var identity,
            out var error);

        Assert.True(captured, error);
        Assert.Equal(Path.GetFullPath(directory), identity.CanonicalPath);
    }

    [Fact]
    public void TryCaptureDirectoryValidation_UnlinkedDirectoryWithAnnotatedReplacement_FailsClosed()
    {
        if (!OperatingSystem.IsLinux()) return;
        if (!UnixSecureRemovalBackend.IsSupportedOnCurrentPlatform) return;

        var directory = Path.Combine(_tempRoot, "unlinked-directory");
        var annotatedReplacement = directory + " (deleted)";
        Directory.CreateDirectory(directory);
        var backend = new UnixSecureRemovalBackend(() =>
        {
            Directory.Delete(directory);
            Directory.CreateDirectory(annotatedReplacement);
        });

        var captured = backend.TryCaptureDirectoryValidation(
            directory,
            out _,
            out var error);

        Assert.False(captured);
        Assert.Contains("unlinked or replaced", error);
        Assert.True(Directory.Exists(annotatedReplacement));
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
