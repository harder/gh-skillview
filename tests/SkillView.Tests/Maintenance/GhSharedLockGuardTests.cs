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
    public async Task RelativeUpdateHomeIsGuardedInTheActualChildWorkingDirectory()
    {
        var root = Directory.CreateTempSubdirectory("skillview-relative-update-home-");
        try
        {
            foreach (var name in new[] { "home", "override" })
            {
                var directory = Directory.CreateDirectory(Path.Combine(root.FullName, name, ".agents"));
                File.WriteAllText(Path.Combine(directory.FullName, ".skill-lock.json"), "{\"version\":99,\"skills\":{}}");
            }
            var homeKey = OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME";
            var logger = new Logger();
            var updater = new GhSkillUpdateService(new(logger, environment: new Dictionary<string, string> { [homeKey] = "home" },
                defaultWorkingDirectory: root.FullName), logger);
            Assert.Equal(Path.Combine(root.FullName, "home"), updater.EffectiveHome);
            foreach (var environment in new IReadOnlyDictionary<string, string>?[] { null, new Dictionary<string, string> { [homeKey] = "override" } })
            {
                var result = await updater.UpdateAsync(Path.Combine(root.FullName, "does-not-exist"), new(ProcessEnvironment: environment),
                    TestContext.Current.CancellationToken);
                Assert.False(result.MutationAttempted);
                Assert.Contains("unsupported", result.ErrorMessage, StringComparison.Ordinal);
            }
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ContextualDestinationOwnerBlocksRemoteAndLocalInstall(bool xdg, bool overwrite)
    {
        if (OperatingSystem.IsWindows()) return; // Shell child marker; portable owner guard policy tested separately.
        var root = Directory.CreateTempSubdirectory("skillview-destination-owner-");
        try
        {
            var home = Path.Combine(root.FullName, "home");
            var state = Path.Combine(root.FullName, "state");
            var destination = xdg ? Path.Combine(home, ".agents", "skills") : Path.Combine(root.FullName, "project", ".agents", "skills");
            Directory.CreateDirectory(Path.Combine(destination, "demo"));
            var payload = Path.Combine(destination, "demo", "SKILL.md");
            const string content = "---\nname: demo\ndescription: Demo\n---\nOriginal payload\n";
            File.WriteAllText(payload, content);
            var lockPath = xdg ? Path.Combine(state, "skills", ".skill-lock.json")
                : Path.Combine(root.FullName, "project", "skills-lock.json");
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
            var bytes = "{\"version\":" + (xdg ? "3" : "1") + ",\"skills\":{\"demo\":{\"source\":\"original/repo\",\"sourceType\":\"github\"}}}";
            File.WriteAllText(lockPath, bytes);
            var fake = Path.Combine(root.FullName, "gh");
            var marker = Path.Combine(root.FullName, "mutated");
            File.WriteAllText(fake, "#!/bin/sh\nfor arg do\n case \"$arg\" in --all|demo) touch \"$SKILLVIEW_TEST_MUTATION\"; exit 0;; esac\ndone\nprintf '[root] demo\\tdescription\\n'\n");
            File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var logger = new Logger();
            var runner = new ProcessRunner(logger, environment: new Dictionary<string, string>
            { ["HOME"] = home, ["XDG_STATE_HOME"] = state, ["SKILLVIEW_TEST_MUTATION"] = marker });
            var service = new GhSkillInstallService(runner, logger);
            foreach (var local in new[] { false, true })
            {
                var result = await service.InstallAsync(fake, "different/repo", "demo",
                    new(Path: destination, Overwrite: overwrite, FromLocal: local), TestContext.Current.CancellationToken);
                Assert.False(result.Succeeded);
                Assert.Contains(overwrite ? "Force install refused" : "contextual owner", result.ErrorMessage, StringComparison.Ordinal);
                Assert.False(File.Exists(marker));
                Assert.Equal(content, File.ReadAllText(payload));
                Assert.Equal(bytes, File.ReadAllText(lockPath));
            }
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void EntryFingerprintsIgnoreOtherRecordsButDetectSelectedEntryChanges()
    {
        var home = Directory.CreateTempSubdirectory("skillview-entry-evidence-");
        try
        {
            var root = Directory.CreateDirectory(Path.Combine(home.FullName, ".agents", "skills"));
            var path = Path.Combine(home.FullName, ".agents", ".skill-lock.json");
            const string before = "{\"version\":3,\"skills\":{\"one\":{\"source\":\"a/repo\",\"sourceType\":\"github\"},\"two\":{\"source\":\"b/repo\",\"sourceType\":\"github\"}}}";
            File.WriteAllText(path, before);
            var token = TestContext.Current.CancellationToken;
            var records = new SkillView.Inventory.SkillLockFileReader(new Logger()).LoadObservations([root.FullName], token, new(home.FullName));
            var claim = records.Single(record => record.Name == "two").Package;
            var target = new SkillView.Inventory.LocalSkillScanner(new Logger());
            var directory = Directory.CreateDirectory(Path.Combine(root.FullName, "two"));
            File.WriteAllText(Path.Combine(directory.FullName, "SKILL.md"), "---\nname: two\ndescription: Two\n---\nBody\n");
            var skill = Assert.Single(target.Scan([new(root.FullName, SkillView.Inventory.Models.Scope.User, null)]))
                with
            { OwnershipClaims = [claim] };
            File.WriteAllText(path, before.Replace("a/repo", "updated/repo", StringComparison.Ordinal));
            GhSharedLockGuard.ValidateCapturedOwners([skill], token);
            // A real gh full-file marshal adds these required empty strings
            // and drops optional empty values on unrelated entries.
            File.WriteAllText(path, before.Replace("\"b/repo\",", "\"b/repo\",\"sourceUrl\":\"\",\"installedAt\":\"\",\"updatedAt\":\"\",\"skillFolderHash\":\"\",", StringComparison.Ordinal));
            GhSharedLockGuard.ValidateCapturedOwners([skill], token);
            File.WriteAllText(path, before.Replace("b/repo", "external/repo", StringComparison.Ordinal));
            Assert.Throws<IOException>(() => GhSharedLockGuard.ValidateCapturedOwners([skill], token));
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
