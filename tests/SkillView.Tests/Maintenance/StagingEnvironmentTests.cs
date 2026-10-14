using SkillView.Inventory;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class StagingEnvironmentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthenticationConfigResolutionMatchesGhPrecedence(bool windows)
    {
        var root = Path.GetTempPath();
        var home = Path.Combine(root, "home");
        var explicitPath = Path.Combine(root, "explicit");
        var xdg = Path.Combine(root, "xdg");
        var appData = Path.Combine(root, "appdata");
        Assert.Equal(explicitPath, SkillUpdateCoordinator.ResolveGhConfigDirectory(home, explicitPath, xdg, appData, windows));
        Assert.Equal(Path.Combine(xdg, "gh"), SkillUpdateCoordinator.ResolveGhConfigDirectory(home, "", xdg, appData, windows));
        Assert.Equal(windows ? Path.Combine(appData, "GitHub CLI") : Path.Combine(home, ".config", "gh"),
            SkillUpdateCoordinator.ResolveGhConfigDirectory(home, null, null, appData, windows));
        Assert.Equal(Path.Combine(home, ".config", "gh"), SkillUpdateCoordinator.ResolveGhConfigDirectory(home, null, null, null, windows));
    }

    [Fact]
    public void StagingStateIsPrivateAndParentHomeIsUnchanged()
    {
        var root = Directory.CreateTempSubdirectory("skillview-stage-env-");
        var parentHome = System.Environment.GetEnvironmentVariable("HOME");
        var parentProfile = System.Environment.GetEnvironmentVariable("USERPROFILE");
        try
        {
            var environment = SkillUpdateCoordinator.CreateStagingEnvironment(root.FullName);
            foreach (var key in new[] { "HOME", "USERPROFILE", "XDG_STATE_HOME", "XDG_CACHE_HOME", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "APPDATA", "LOCALAPPDATA" })
                Assert.True(PathIdentity.IsInside(environment[key], root.FullName), key);
            Assert.False(PathIdentity.IsInside(environment["GH_CONFIG_DIR"], root.FullName));
            Assert.Equal(parentHome, System.Environment.GetEnvironmentVariable("HOME"));
            Assert.Equal(parentProfile, System.Environment.GetEnvironmentVariable("USERPROFILE"));
        }
        finally { root.Delete(true); }
    }
}
