using System.Collections.Immutable;
using System.Text.Json;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Inventory;

public sealed class LocalCatalogSourceTests : IDisposable
{
    private readonly DirectoryInfo temp = Directory.CreateTempSubdirectory("skillview-local-catalog-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public void Dispose() => temp.Delete(true);

    private (InstalledSkill Skill, ScanRoot Root, string Source) Fixture()
    {
        var source = Directory.CreateDirectory(Path.Combine(temp.FullName, "source")).FullName;
        var root = Directory.CreateDirectory(Path.Combine(temp.FullName, "skills")).FullName;
        var payload = Directory.CreateDirectory(Path.Combine(root, "demo")).FullName;
        File.WriteAllText(Path.Combine(source, "SKILL.md"), "---\nname: demo\ndescription: Source body\n---\nNew C\n");
        File.WriteAllText(Path.Combine(payload, "SKILL.md"), "---\nname: demo\ndescription: Authored I\nmetadata:\n  local-path: " + JsonSerializer.Serialize(source) + "\n---\nOld I\n");
        ScanRoot scan = new(root, Scope.Custom, null);
        return (Assert.Single(new LocalSkillScanner(new Logger()).Scan([scan])), scan, source);
    }

    private static GhSkillListRecord ActualShape(string installed, string source) => Assert.Single(GhSkillListAdapter.Parse(
        JsonSerializer.Serialize(new[] { new { skillName = "demo", agentHosts = (string[]?)null, scope = "custom", sourceURL = source, version = "", pinned = false, path = installed } })));

    [Theory]
    [InlineData("2.97.0")]
    [InlineData("2.102.0")]
    public void ActualMinimumAndCurrentLocalShapeKeepsExactScannedTracking(string contractVersion)
    {
        // Both independently captured gh versions emit this same shape. The
        // version label documents the fixture, not execution of a gh binary.
        Assert.NotEmpty(contractVersion);
        var (skill, _, source) = Fixture();
        var record = ActualShape(skill.ResolvedPath, source);
        var result = Assert.Single(LocalInventoryService.Merge([skill], [record]));
        Assert.Equal(source, record.SourceUrl);
        Assert.Equal(skill.FrontMatter.LocalPath, result.FrontMatter.LocalPath);
        Assert.Null(result.FrontMatter.Upstream); Assert.Null(result.FrontMatter.GithubRef);
        Assert.False(result.Pinned); Assert.Empty(result.MetadataConflicts);
        Assert.Equal("Authored I", result.FrontMatter.Description);
        Assert.Equal(Provenance.Both, result.Provenance);
    }

    [Theory]
    [InlineData("different-local")]
    [InlineData("different-local-spelling")]
    [InlineData("remote")]
    [InlineData("legacy-remote")]
    [InlineData("pinned")]
    [InlineData("ref")]
    [InlineData("tree")]
    public async Task ContradictoryCatalogCannotAuthorizeReimportOrPrivateWrites(string kind)
    {
        var (skill, root, source) = Fixture();
        var record = ActualShape(skill.ResolvedPath, source);
        record = kind switch
        {
            "different-local" => record with { SourceUrl = source + "-other" },
            "different-local-spelling" => record with { SourceUrl = source + Path.DirectorySeparatorChar },
            "remote" => record with { SourceUrl = "https://github.com/owner/repo" },
            "legacy-remote" => record with { Repo = "owner/repo" },
            "ref" => record with { Version = "v1" },
            "tree" => record with { GithubTreeSha = new('a', 40) },
            _ => record with { Pinned = true }
        };
        var merged = Assert.Single(LocalInventoryService.Merge([skill], [record]));
        Assert.Equal(source, merged.FrontMatter.LocalPath);
        if (kind == "pinned") Assert.True(merged.Pinned);
        else if (kind == "ref") Assert.Equal("v1", merged.FrontMatter.GithubRef);
        else if (kind == "tree") Assert.Equal(new string('a', 40), merged.TreeSha);
        else Assert.NotEmpty(merged.MetadataConflicts);
        var state = Path.Combine(temp.FullName, "private", "plans");
        var logger = new Logger(LogLevel.Debug);
        var runner = new ProcessRunner(logger, environment: PrivateEnvironment(), defaultWorkingDirectory: temp.FullName);
        var snapshot = InventorySnapshot.Empty with { Skills = [merged], ConfiguredRoots = [root], ScannedRoots = [root] };
        var before = File.ReadAllBytes(Path.Combine(skill.ResolvedPath, "SKILL.md"));
        await Assert.ThrowsAnyAsync<IOException>(() => new ReplacementPlanStore(state, logger, runner: runner)
            .PlanLocalReimportAsync(Path.Combine(temp.FullName, "must-not-run"), merged, snapshot, Token));
        Assert.False(Directory.Exists(state)); Assert.False(Directory.Exists(state + "-local-previews"));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(skill.ResolvedPath, "SKILL.md")));
        Assert.DoesNotContain(logger.Snapshot(), entry => entry.Category == "subprocess");
    }

    [Fact]
    public void ScannedRemoteAndCatalogLocalAreBothRetainedAsConflictingEvidence()
    {
        var (skill, _, source) = Fixture();
        skill = skill with { FrontMatter = skill.FrontMatter with { LocalPath = null, Upstream = "https://github.com/owner/repo" } };
        var result = Assert.Single(LocalInventoryService.Merge([skill], [ActualShape(skill.ResolvedPath, source)]));
        Assert.Equal(source, result.FrontMatter.LocalPath);
        Assert.Equal(skill.FrontMatter.Upstream, result.FrontMatter.Upstream);
        Assert.NotEmpty(result.MetadataConflicts);
    }

    [Fact]
    public void UnknownModernSourceDoesNotHideRealLegacyRepositoryEvidence()
    {
        var (skill, _, _) = Fixture();
        var result = Assert.Single(LocalInventoryService.Merge([], [ActualShape(skill.ResolvedPath, "relative/unknown") with { Repo = "owner/repo" }]));
        Assert.Null(result.FrontMatter.LocalPath); Assert.Equal("owner/repo", result.FrontMatter.Upstream);
        Assert.Contains(result.MetadataConflicts, conflict => conflict.Contains("relative/unknown", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("relative/source")]
    [InlineData("file:///source/demo")]
    [InlineData("data:text/plain,unknown")]
    public void UnknownCatalogSourceRemainsConflictInsteadOfFabricatedTracking(string raw)
    {
        var (skill, _, source) = Fixture();
        var result = Assert.Single(LocalInventoryService.Merge([skill], [ActualShape(skill.ResolvedPath, raw)]));
        Assert.Equal(source, result.FrontMatter.LocalPath); Assert.Null(result.FrontMatter.Upstream);
        Assert.Contains(result.MetadataConflicts, conflict => conflict.Contains(raw, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(@"C:\source\demo")]
    [InlineData(@"\\server\share\source\demo")]
    public void DriveAndUncPathsNeverBecomeRemoteUriTracking(string raw)
    {
        var (skill, _, _) = Fixture();
        var result = Assert.Single(LocalInventoryService.Merge([], [ActualShape(skill.ResolvedPath, raw)]));
        Assert.Null(result.FrontMatter.Upstream);
        if (OperatingSystem.IsWindows()) { Assert.Equal(raw, result.FrontMatter.LocalPath); Assert.Empty(result.MetadataConflicts); }
        else { Assert.Null(result.FrontMatter.LocalPath); Assert.NotEmpty(result.MetadataConflicts); }
    }

    [Theory]
    [InlineData("owner/repo", false, false)]
    [InlineData("other/repo", true, false)]
    [InlineData("owner/repo", false, true)]
    [InlineData("other/repo", true, true)]
    public void LegacyRepoRemoteEquivalenceAndDisagreementArePreserved(string repo, bool conflict, bool modernSource)
    {
        var (skill, _, _) = Fixture();
        skill = skill with { FrontMatter = skill.FrontMatter with { LocalPath = null, Upstream = "https://github.com/owner/repo" } };
        var result = Assert.Single(LocalInventoryService.Merge([skill], [ActualShape(skill.ResolvedPath, modernSource ? "https://github.com/owner/repo" : "") with { Repo = repo }]));
        Assert.Equal(skill.FrontMatter.Upstream, result.FrontMatter.Upstream);
        Assert.Equal(conflict, !result.MetadataConflicts.IsEmpty);
    }

    [Fact]
    public void EmptyModernSourceUsesLegacyPathOrRepositoryWithoutDiscardingIt()
    {
        var (skill, _, source) = Fixture();
        foreach (var raw in new[] { source, "owner/repo" })
        {
            var result = Assert.Single(LocalInventoryService.Merge([], [ActualShape(skill.ResolvedPath, "") with { Repo = raw }]));
            Assert.Equal(raw == source ? source : null, result.FrontMatter.LocalPath);
            Assert.Equal(raw == source ? null : "owner/repo", result.FrontMatter.Upstream);
            Assert.Empty(result.MetadataConflicts);
        }
    }

    [Fact]
    public async Task OrdinaryLinuxMergedLocalRecordReachesUnchangedMetadataCoverageRefusal()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (skill, root, source) = Fixture();
        var merged = Assert.Single(LocalInventoryService.Merge([skill], [ActualShape(skill.ResolvedPath, source)]));
        Assert.Null(merged.FrontMatter.Upstream); Assert.Empty(merged.MetadataConflicts);
        // Qualify the ordinary-host assertion from actual held metadata, not
        // capability bits. A privileged or differently unsupported host still
        // verifies the merge above, without claiming this CAP refusal contract.
        using (var probe = NativeStateDirectory.OpenOrCreate(Path.Combine(temp.FullName, "metadata-probe"), [skill.ResolvedPath, source], Token))
        using (var writer = probe.OpenFile("writer.lock", true, Token))
        using (var held = NativeDirectoryMoveLease.Open(root.Path, skill.ResolvedPath, Token))
        {
            var observed = CapturedPayloadArchive.Capture(probe, "installed", held, Token);
            if (!observed.RootMetadata!.Refusals.Concat(observed.Entries.SelectMany(entry => entry.Metadata!.Refusals))
                .Any(refusal => refusal.Contains("CAP_SYS_ADMIN", StringComparison.Ordinal))) return;
        }
        var inventory = InventorySnapshot.Empty with { Skills = [merged], ConfiguredRoots = [root], ScannedRoots = [root] };
        var logger = new Logger(LogLevel.Debug);
        var runner = new ProcessRunner(logger, environment: PrivateEnvironment(), defaultWorkingDirectory: temp.FullName);
        var before = File.ReadAllBytes(Path.Combine(skill.ResolvedPath, "SKILL.md"));
        var sourceBefore = File.ReadAllBytes(Path.Combine(source, "SKILL.md"));
        var error = await Assert.ThrowsAnyAsync<IOException>(() => new ReplacementPlanStore(Path.Combine(temp.FullName, "private", "plans"), logger, runner: runner)
            .PlanLocalReimportAsync(Path.Combine(temp.FullName, "must-not-run"), merged, inventory, Token));
        Assert.Contains("CAP_SYS_ADMIN", error.Message);
        Assert.DoesNotContain("Mixed remote/source", error.Message);
        Assert.DoesNotContain(logger.Snapshot(), entry => entry.Category == "subprocess");
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(skill.ResolvedPath, "SKILL.md")));
        Assert.Equal(sourceBefore, File.ReadAllBytes(Path.Combine(source, "SKILL.md")));
        // An ordinary Linux coverage refusal is not a successful publication.
    }

    private Dictionary<string, string> PrivateEnvironment() => new()
    {
        ["HOME"] = temp.FullName,
        ["USERPROFILE"] = temp.FullName,
        ["GIT_DIR"] = Path.Combine(temp.FullName, "private-git"),
        ["GIT_WORK_TREE"] = temp.FullName,
        ["GIT_COMMON_DIR"] = Path.Combine(temp.FullName, "private-git"),
        ["GIT_CONFIG_COUNT"] = "0"
    };
}
