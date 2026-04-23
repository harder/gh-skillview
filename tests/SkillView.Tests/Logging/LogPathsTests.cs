using System.Runtime.InteropServices;
using SkillView.Logging;
using Xunit;

namespace SkillView.Tests.Logging;

public class LogPathsTests
{
    [Fact]
    public void Resolve_ends_with_SkillView_logs()
    {
        var path = LogPaths.Resolve();
        Assert.EndsWith(Path.Combine("SkillView", "logs"), path);
    }

    [Fact]
    public void FileNameForDate_is_iso_dated_and_invariant()
    {
        var name = LogPaths.FileNameForDate(new DateOnly(2026, 4, 23));
        Assert.Equal("skillview-2026-04-23.log", name);
    }

    [Fact]
    public void Resolve_is_platform_appropriate()
    {
        var root = LogPaths.ResolveCacheRoot();
        Assert.False(string.IsNullOrEmpty(root));
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Assert.Contains(Path.Combine("Library", "Caches"), root);
        }
    }
}
