using System.Text;
using SkillView.Maintenance;
using SkillView.Gh;
using SkillView.Logging;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class GhSharedLockGuardTests
{
    [Theory]
    [InlineData("{\"version\":99,\"skills\":{}}")]
    [InlineData("{\"version\":3,\"skills\":{},\"lastSelectedAgents\":[\"codex\"]}")]
    [InlineData("{\"version\":3,\"skills\":{\"demo\":{\"source\":\"o/r\",\"ref\":\"main\"}}}")]
    [InlineData("{\"version\":3,\"skills\":{\"demo\":{\"source\":42}}}")]
    [InlineData("{\"version\":3,\"skills\":{},\"dismissed\":{\"prompt\":\"yes\"}}")]
    [InlineData("{\"version\":3,\"skills\":{\"demo\":{},\"demo\":{}}}")]
    public void NativeGhWouldDiscardOrResetData_Refuses(string payload) =>
        Assert.Throws<IOException>(() => GhSharedLockGuard.ValidatePayload(Encoding.UTF8.GetBytes(payload), TestContext.Current.CancellationToken));

    [Fact]
    public async Task EveryWriterRefusesUnsupportedLockBeforeLaunchingChildAndHonorsEffectiveHome()
    {
        var root = Directory.CreateTempSubdirectory("skillview-lock-writer-");
        try
        {
            var goodHome = Directory.CreateDirectory(Path.Combine(root.FullName, "good")).FullName;
            var badHome = Directory.CreateDirectory(Path.Combine(root.FullName, "bad")).FullName;
            var lockDirectory = Directory.CreateDirectory(Path.Combine(badHome, ".agents"));
            var path = Path.Combine(lockDirectory.FullName, ".skill-lock.json");
            const string bytes = "{\"version\":3,\"skills\":{},\"lastSelectedAgents\":[\"codex\"]}";
            File.WriteAllText(path, bytes);
            var homeKey = OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME";
            var logger = new Logger();
            var runner = new ProcessRunner(logger, environment: new Dictionary<string, string> { [homeKey] = goodHome });
            var install = await new GhSkillInstallService(runner, logger).InstallAsync(
                Path.Combine(root.FullName, "does-not-exist"), "team/repo", "demo",
                new(ProcessEnvironment: new Dictionary<string, string> { [homeKey] = badHome }), TestContext.Current.CancellationToken);
            Assert.False(install.Succeeded);
            Assert.Contains("would discard", install.ErrorMessage, StringComparison.Ordinal);
            var update = await new GhSkillUpdateService(new ProcessRunner(logger,
                environment: new Dictionary<string, string> { [homeKey] = badHome }), logger).UpdateAsync(
                Path.Combine(root.FullName, "does-not-exist"), cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(update.MutationAttempted);
            Assert.Contains("would discard", update.ErrorMessage, StringComparison.Ordinal);
            Assert.Equal(bytes, File.ReadAllText(path));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public async Task SupportedSameSelectorRecordCannotBeReplacedByCustomInstall()
    {
        var home = Directory.CreateTempSubdirectory("skillview-record-collision-");
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(home.FullName, ".agents"));
            var path = Path.Combine(directory.FullName, ".skill-lock.json");
            const string bytes = "{\"version\":3,\"skills\":{\"demo\":{\"source\":\"original/repo\",\"sourceType\":\"github\",\"skillPath\":\"skills/demo/SKILL.md\",\"skillFolderHash\":\"recorded-tree\"}}}";
            File.WriteAllText(path, bytes);
            var homeKey = OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME";
            var logger = new Logger();
            var runner = new ProcessRunner(logger, environment: new Dictionary<string, string> { [homeKey] = home.FullName });
            var result = await new GhSkillInstallService(runner, logger).InstallAsync(
                Path.Combine(home.FullName, "does-not-exist"), "different/repo", "demo",
                new(Path: Path.Combine(home.FullName, "custom"), Overwrite: true), TestContext.Current.CancellationToken);
            Assert.False(result.Succeeded);
            Assert.Contains("existing shared selector", result.ErrorMessage, StringComparison.Ordinal);
            Assert.Equal(bytes, File.ReadAllText(path));
        }
        finally { home.Delete(true); }
    }

    [Theory]
    [InlineData("demo", false)]
    [InlineData("demo@v1", false)]
    [InlineData("other/path/SKILL.md", false)]
    [InlineData(null, true)]
    public async Task NamespacedAliasesAndTruncatedListingCannotBypassCollisionGuard(string? requested, bool truncate)
    {
        if (OperatingSystem.IsWindows()) return; // Native shell fixture; selector logic also has portable coverage below.
        var home = Directory.CreateTempSubdirectory("skillview-discovery-bound-");
        try
        {
            Directory.CreateDirectory(Path.Combine(home.FullName, ".agents"));
            File.WriteAllText(Path.Combine(home.FullName, ".agents", ".skill-lock.json"),
                "{\"version\":3,\"skills\":{\"ns/demo\":{\"source\":\"original/repo\"}}}");
            var fake = Path.Combine(home.FullName, "gh");
            File.WriteAllText(fake, "#!/bin/sh\nfor arg do\n case \"$arg\" in --all|demo|demo@v1) touch \"$SKILLVIEW_TEST_MUTATION\"; exit 0;; esac\ndone\n"
                + (truncate ? "printf '[root] first\\t" + new string('x', 120) + "\\n'\n" : "")
                + "printf '[ns] demo\\tdescription\\n'\n");
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var marker = Path.Combine(home.FullName, "mutated");
            var logger = new Logger();
            var runner = new ProcessRunner(logger, maxCapturedCharsPerStream: truncate ? 64 : 4096,
                environment: new Dictionary<string, string> { ["HOME"] = home.FullName, ["SKILLVIEW_TEST_MUTATION"] = marker });
            var result = await new GhSkillInstallService(runner, logger).InstallAsync(fake, "different/repo", requested,
                new(All: requested is null), TestContext.Current.CancellationToken);
            Assert.False(result.Succeeded);
            Assert.Contains(truncate ? "truncated" : "existing shared selector", result.ErrorMessage, StringComparison.Ordinal);
            Assert.False(File.Exists(marker));
        }
        finally { home.Delete(true); }
    }

    [Fact]
    public void BareRemoteNameResolvesItsActualNamespacedRegistryKey() =>
        Assert.Equal(["ns/demo"], GhSkillInstallService.ResolveWriteSelectors([new("ns/demo", "description")], "demo", false));

    [Fact]
    public void SupportedInteropSchemaIsAdmitted() => GhSharedLockGuard.ValidatePayload(
        Encoding.UTF8.GetBytes("{\"version\":3,\"skills\":{\"demo\":{\"source\":\"o/r\",\"pinnedRef\":\"v1\"}},\"dismissed\":{\"prompt\":true}}"), TestContext.Current.CancellationToken);
}
