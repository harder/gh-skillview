using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Gh;
using SkillView.Subprocess;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class SkillComparisonTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("skillview-compare-");
    public void Dispose() => _root.Delete(true);

    private InstalledSkill Install(string context, string repository = "owner/repo", string script = "echo original")
    {
        var root = Directory.CreateDirectory(Path.Combine(_root.FullName, context));
        var directory = Directory.CreateDirectory(Path.Combine(root.FullName, "demo"));
        File.WriteAllText(Path.Combine(directory.FullName, "SKILL.md"),
            "---\nname: demo\ndescription: Demo\nmetadata:\n  github-repo: https://github.com/" + repository
            + "\n  github-ref: refs/heads/main\n  github-tree-sha: " + new string('a', 40)
            + "\n  github-path: skills/demo\n---\nInstructions\n");
        File.WriteAllText(Path.Combine(directory.FullName, "script.sh"), script);
        return Assert.Single(new LocalSkillScanner(new Logger()).Scan([new(root.FullName, Scope.Custom, null)]));
    }

    [Fact]
    public async Task SameMarkdownButDifferentScriptRemainsDivergent()
    {
        var result = await new SkillComparisonService().CompareAsync(Install("one"), Install("two", script: "echo changed"),
            TestContext.Current.CancellationToken);
        Assert.True(result.Complete);
        Assert.Equal("same-source", result.SourceRelationship);
        Assert.Equal("different-payloads", result.PayloadRelationship);
        Assert.Equal(["script.sh"], result.DifferingFiles);
        Assert.False(result.RequiresExposureMigration);
    }

    [Fact]
    public async Task DifferentOriginsNeverBecomeConsolidationCandidates()
    {
        var result = await new SkillComparisonService().CompareAsync(Install("one", "author/first"), Install("two", "author/second"),
            TestContext.Current.CancellationToken);
        Assert.True(result.Complete);
        Assert.Equal("different-origins", result.SourceRelationship);
        Assert.Equal("installer-metadata-only", result.PayloadRelationship);
        Assert.False(result.RequiresExposureMigration);
    }

    [Fact]
    public async Task ChangedInventoryTrackingRequiresRefresh()
    {
        var left = Install("one");
        var right = Install("two");
        var file = Path.Combine(right.ResolvedPath, "SKILL.md");
        File.WriteAllText(file, File.ReadAllText(file).Replace("owner/repo", "different/origin", StringComparison.Ordinal));
        var result = await new SkillComparisonService().CompareAsync(left, right, TestContext.Current.CancellationToken);
        Assert.False(result.Complete);
        Assert.False(result.RequiresExposureMigration);
        Assert.Equal("different-origins", result.SourceRelationship);
        Assert.Contains(result.Diagnostics, message => message.Contains("refresh", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InaccessiblePayloadNeverReportsEquality()
    {
        var left = Install("one");
        var missing = left with { ResolvedPath = Path.Combine(_root.FullName, "missing") };
        var result = await new SkillComparisonService().CompareAsync(left, missing, TestContext.Current.CancellationToken);
        Assert.False(result.Complete);
        Assert.Equal("incomplete", result.PayloadRelationship);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Theory]
    [InlineData("owner/repo")]
    [InlineData("different/repo")]
    public async Task SharedClaimCannotHideExclusiveOwnerFromUpdate(string exclusiveSource)
    {
        var skill = Install("one");
        var shared = new SkillPackage("owner/repo", "github", "https://github.com/owner/repo", null, null)
        {
            Provider = "gh-vercel-global",
            IsExclusiveOwner = false,
            ContentHash = skill.TreeSha,
            HashKind = "recorded-git-tree",
            SkillPath = "skills/demo/SKILL.md"
        };
        var exclusive = shared with { Provider = "vercel-project", IsExclusiveOwner = true, Source = exclusiveSource };
        skill = skill with { Package = shared, OwnershipClaims = [shared, exclusive] };
        var logger = new Logger();
        var runner = new ProcessRunner(logger);
        var result = await new SkillUpdateCoordinator(new(runner, logger), new(runner, logger), new(runner, logger), logger)
            .UpdateAsync(Path.Combine(_root.FullName, "does-not-exist"), new(Targets: [skill]), TestContext.Current.CancellationToken);
        Assert.False(result.MutationAttempted);
        Assert.Contains("package owner", Assert.Single(result.Outcomes).Message, StringComparison.Ordinal);
        Assert.Equal("refused", Assert.Single(result.Outcomes).Status);
    }

    private InstalledSkill Owned(InstalledSkill skill, string name, string revision, string hash)
    {
        var path = Path.Combine(_root.FullName, name + "-lock.json");
        File.WriteAllText(path, "{\"revision\":\"" + revision + "\",\"hash\":\"" + hash + "\"}");
        var claim = new SkillPackage("owner/repo", "github", null, null, null)
        {
            Provider = "vercel-project",
            RepositoryRef = revision,
            ContentHash = hash,
            HashKind = "vercel-project-sha256",
            LockFilePath = path,
            SkillPath = "skills/demo/SKILL.md",
            RecordFingerprint = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))
        };
        return skill with { Package = claim, OwnershipClaims = [claim] };
    }

    [Fact]
    public async Task ContextualRevisionDifferencesRemainVisibleDespiteIdenticalBytes()
    {
        var left = Owned(Install("one"), "one", "v1", "hash-one");
        var right = Owned(Install("two"), "two", "v2", "hash-two");
        var result = await new SkillComparisonService().CompareAsync(left, right, TestContext.Current.CancellationToken);
        Assert.True(result.Complete);
        Assert.Equal("byte-identical", result.PayloadRelationship);
        Assert.Equal("different-recorded-revisions", result.RevisionRelationship);
        Assert.False(result.RequiresExposureMigration);
    }

    [Fact]
    public void EquivalentGitTreeEvidenceIsNotDuplicatedAndContradictoryOwnerIsConflict()
    {
        var left = Install("one");
        var claim = new SkillPackage("owner/repo", "github", "https://github.com/owner/repo.git", null, null)
        {
            Provider = "gh-vercel-global",
            IsExclusiveOwner = false,
            ContentHash = left.TreeSha,
            HashKind = "recorded-git-tree",
            SkillPath = "skills/demo/SKILL.md",
            InstallationRoot = left.ScanRoot,
            Selector = "demo"
        };
        var owned = left with { Package = claim, OwnershipClaims = [claim] };
        Assert.Equal("same-recorded-revision", SkillComparisonService.RevisionRelationship(left, owned));
        var wrong = claim with { Provider = "vercel-project", IsExclusiveOwner = true, Source = "other/repo", SourceUrl = "https://github.com/other/repo" };
        var enriched = Assert.Single(LocalInventoryService.EnrichOwnership([left], [new("demo", wrong)], TestContext.Current.CancellationToken));
        Assert.NotEmpty(enriched.MetadataConflicts);
        Assert.Equal("conflicting-provenance", SkillComparisonService.SourceRelationship(enriched, left));
    }

    [Fact]
    public async Task ChangedOwnerEvidenceRequiresRefresh()
    {
        var left = Owned(Install("one"), "one", "v1", "hash-one");
        var right = Install("two");
        File.AppendAllText(left.Package!.LockFilePath!, " ");
        var result = await new SkillComparisonService().CompareAsync(left, right, TestContext.Current.CancellationToken);
        Assert.False(result.Complete);
        Assert.Contains(result.Diagnostics, error => error.Contains("Owner record changed", StringComparison.Ordinal));
        Assert.False(result.RequiresExposureMigration);
    }

    [Fact]
    public async Task MissingRecordedRevisionsAndPartialCoverageNeverAuthorizeMigration()
    {
        var left = Install("one");
        var right = Install("two");
        var untracked = left with { FrontMatter = SkillFrontMatter.Empty };
        Assert.Equal("unknown-recorded-revision", SkillComparisonService.RevisionRelationship(untracked, untracked));
        var result = await new SkillComparisonService().CompareAsync(left, right,
            TestContext.Current.CancellationToken, inventoryComplete: false);
        Assert.True(result.Complete);
        Assert.False(result.RequiresExposureMigration);
        Assert.Contains(result.Diagnostics, error => error.Contains("coverage is partial", StringComparison.Ordinal));
        var a = Owned(left, "a", "v1", "hash");
        var b = Owned(right, "b", "v1", "hash");
        b = b with
        {
            Package = b.Package! with { HashKind = "different-domain" },
            OwnershipClaims = [b.Package! with { HashKind = "different-domain" }]
        };
        Assert.Equal("incomparable-recorded-revisions", SkillComparisonService.RevisionRelationship(a, b));
    }

    [Fact]
    public void IdentitySeparatesUnknownOriginsAndRecordedRevisionFromAuthoredVersion()
    {
        var skill = Install("one");
        Assert.NotNull(skill.SourceIdentity.Key);
        Assert.Equal("refs/heads/main", skill.Revision.RepositoryRef);
        Assert.Null(skill.Revision.AuthoredVersion);
        var unknown = skill with { FrontMatter = SkillFrontMatter.Empty };
        Assert.Equal("unknown", unknown.SourceIdentity.Provider);
        Assert.Null(unknown.SourceIdentity.Key);
        Assert.Null((unknown with { FrontMatter = SkillFrontMatter.Empty with { LocalPath = "relative/source" } }).SourceIdentity.Origin);
        Assert.Equal("invalid-recorded-local-path",
            (unknown with { FrontMatter = SkillFrontMatter.Empty with { LocalPath = "/invalid\0path" } }).SourceIdentity.Evidence);
    }
}
