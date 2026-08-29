using System.IO;
using SkillView.Inventory;
using Xunit;

namespace SkillView.Tests.Inventory;

public sealed class WindowsSecureRemovalBackendTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "skillview-win-native-" + Guid.NewGuid().ToString("N"));

    public WindowsSecureRemovalBackendTests() => Directory.CreateDirectory(_tempRoot);

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(@"\\?\C:\skills\demo", @"C:\skills\demo")]
    [InlineData(@"\\?\UNC\server\share\demo", @"\\server\share\demo")]
    [InlineData(@"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}\skills\demo", @"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}\skills\demo")]
    [InlineData(@"C:\skills\demo", @"C:\skills\demo")]
    public void NormalizeFinalPath_ConvertsExtendedDosPath(string path, string expected)
    {
        Assert.Equal(expected, WindowsSecureRemovalBackend.NormalizeFinalPath(path));
    }

    [Fact]
    public void TryCaptureIdentity_UsesFinalPathFromOpenedHandle()
    {
        if (!OperatingSystem.IsWindows()) return;

        var realRoot = Path.Combine(_tempRoot, "real-root");
        var linkedRoot = Path.Combine(_tempRoot, "linked-root");
        var skill = Path.Combine(realRoot, "skill");
        Directory.CreateDirectory(skill);
        if (!TryCreateDirectoryLink(linkedRoot, realRoot)) return;

        var backend = new WindowsSecureRemovalBackend();
        var captured = backend.TryCaptureIdentity(
            Path.Combine(linkedRoot, "skill"),
            out var identity,
            out var error);

        Assert.True(captured, error);
        Assert.True(PathIdentity.Equals(skill, identity.CanonicalPath));
    }

    [Fact]
    public void TryCanonicalizePath_UsesFinalPathFromOpenedHandle()
    {
        if (!OperatingSystem.IsWindows()) return;

        var realRoot = Path.Combine(_tempRoot, "canonical-real");
        var linkedRoot = Path.Combine(_tempRoot, "canonical-link");
        Directory.CreateDirectory(realRoot);
        if (!TryCreateDirectoryLink(linkedRoot, realRoot)) return;

        var canonicalized = new WindowsSecureRemovalBackend().TryCanonicalizePath(
            linkedRoot,
            out var canonicalPath,
            out var error);

        Assert.True(canonicalized, error);
        Assert.True(PathIdentity.Equals(realRoot, canonicalPath));
    }

    [Fact]
    public void TryCaptureDirectoryValidationWithinRoot_RootLinkRetargeted_UsesHeldRoot()
    {
        if (!OperatingSystem.IsWindows()) return;

        var originalRoot = Path.Combine(_tempRoot, "root-original");
        var replacementRoot = Path.Combine(_tempRoot, "root-replacement");
        var linkedRoot = Path.Combine(_tempRoot, "root-link");
        var originalSkill = Path.Combine(originalRoot, "skill");
        var replacementSkill = Path.Combine(replacementRoot, "skill");
        Directory.CreateDirectory(Path.Combine(originalSkill, ".git"));
        Directory.CreateDirectory(replacementSkill);
        File.WriteAllText(Path.Combine(originalSkill, "SKILL.md"), "original");
        File.WriteAllText(Path.Combine(replacementSkill, "SKILL.md"), "replacement");
        if (!TryCreateDirectoryLink(linkedRoot, originalRoot)) return;

        var backend = new WindowsSecureRemovalBackend(
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
        Assert.True(PathIdentity.Equals(
            originalRoot,
            snapshot.RootIdentity.CanonicalPath));
        Assert.True(PathIdentity.Equals(
            originalSkill,
            snapshot.Directory.Identity.CanonicalPath));
    }

    [Fact]
    public void TryCaptureLinkValidationWithinRoot_RootLinkRetargeted_UsesHeldRoot()
    {
        if (!OperatingSystem.IsWindows()) return;

        var originalRoot = Path.Combine(_tempRoot, "link-root-original");
        var replacementRoot = Path.Combine(_tempRoot, "link-root-replacement");
        var linkedRoot = Path.Combine(_tempRoot, "link-root-link");
        var originalLink = Path.Combine(originalRoot, "broken-link");
        var replacementLink = Path.Combine(replacementRoot, "broken-link");
        var replacementTarget = Path.Combine(replacementRoot, "valid-target");
        Directory.CreateDirectory(originalRoot);
        Directory.CreateDirectory(replacementTarget);
        if (!TryCreateDirectoryLink(originalLink, Path.Combine(originalRoot, "missing"))) return;
        if (!TryCreateDirectoryLink(replacementLink, replacementTarget)) return;
        if (!TryCreateDirectoryLink(linkedRoot, originalRoot)) return;

        var backend = new WindowsSecureRemovalBackend(
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
        Assert.True(PathIdentity.Equals(
            originalRoot,
            snapshot.RootIdentity.CanonicalPath));
        Assert.True(PathIdentity.Equals(
            originalLink,
            snapshot.Link.Identity.CanonicalPath));
    }

    [Fact]
    public void TryCaptureDirectoryValidation_TargetAba_InspectsOpenedDirectoryOrFailsClosed()
    {
        if (!OperatingSystem.IsWindows()) return;

        var parent = Path.Combine(_tempRoot, "policy-parent");
        var skill = Path.Combine(parent, "skill");
        var movedSkill = Path.Combine(parent, "skill-original");
        Directory.CreateDirectory(Path.Combine(skill, ".git"));
        File.WriteAllText(Path.Combine(skill, "SKILL.md"), "body");
        var backend = new WindowsSecureRemovalBackend(() =>
        {
            Directory.Move(skill, movedSkill);
            Directory.CreateDirectory(skill);
            File.WriteAllText(Path.Combine(skill, "SKILL.md"), "clean");
        });

        var captured = backend.TryCaptureDirectoryValidation(
            skill,
            out var snapshot,
            out var error);

        Assert.True(Directory.Exists(Path.Combine(movedSkill, ".git")));
        Assert.False(Directory.Exists(Path.Combine(skill, ".git")));
        if (!captured)
        {
            Assert.Equal(
                "selected directory changed during policy inspection",
                error);
            return;
        }

        Assert.True(snapshot.HasSkillFile);
        Assert.True(snapshot.HasGitDirectory);
        Assert.True(PathIdentity.Equals(movedSkill, snapshot.Identity.CanonicalPath));
    }

    [Fact]
    public void TryCaptureLinkValidation_BrokenLinkReplacedWithValidLink_FailsClosed()
    {
        if (!OperatingSystem.IsWindows()) return;

        var target = Path.Combine(_tempRoot, "valid-target");
        var link = Path.Combine(_tempRoot, "changing-broken-link");
        Directory.CreateDirectory(target);
        if (!TryCreateDirectoryLink(link, Path.Combine(_tempRoot, "missing"))) return;
        var backend = new WindowsSecureRemovalBackend(linkTargetObservedForTests: () =>
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

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
