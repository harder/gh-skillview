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
