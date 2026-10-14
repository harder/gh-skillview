using SkillView.Inventory;
using SkillView.Logging;
using Xunit;

namespace SkillView.Tests.Inventory;

public sealed class SkillLockFileReaderTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "skillview-lock-reader-" + Guid.NewGuid().ToString("N"));

    public SkillLockFileReaderTests() => Directory.CreateDirectory(_tempRoot);

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    [Fact]
    public void UserTwoProjectsAndXdgRecordsKeepContextsAndHashesSeparate()
    {
        var home = Path.Combine(_tempRoot, "home");
        var user = Path.Combine(home, ".agents", "skills");
        var one = Path.Combine(_tempRoot, "one", ".agents", "skills");
        var two = Path.Combine(_tempRoot, "two", ".claude", "skills");
        var state = Path.Combine(_tempRoot, "state");
        foreach (var root in new[] { user, one, two }) Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(home, ".agents", SkillLockFileReader.FileName),
            "{\"version\":3,\"skills\":{\"demo\":{\"source\":\"team/user\",\"sourceType\":\"github\",\"skillFolderHash\":\"tree\",\"skillPath\":\"skills/demo/SKILL.md\"}}}");
        foreach (var project in new[] { "one", "two" })
            File.WriteAllText(Path.Combine(_tempRoot, project, SkillLockFileReader.ProjectFileName),
                "{\"version\":1,\"skills\":{\"demo\":{\"source\":\"team/" + project
                + "\",\"sourceType\":\"github\",\"computedHash\":\"payload\",\"ref\":\"release\"}}}");
        Directory.CreateDirectory(Path.Combine(state, "skills"));
        File.WriteAllText(Path.Combine(state, "skills", SkillLockFileReader.FileName),
            "{\"version\":3,\"skills\":{\"xdg-only\":{\"source\":\"team/xdg\",\"sourceType\":\"github\"}}}");
        var records = new SkillLockFileReader(new Logger()).LoadObservations([user, one, two], TestContext.Current.CancellationToken,
            new(home, state));
        Assert.Equal(4, records.Length);
        Assert.Equal(3, records.Where(record => record.Name == "demo").Select(record => record.Package.Source).Distinct().Count());
        Assert.Contains(records, record => record.Package.Provider == "vercel-global-xdg" && record.Name == "xdg-only");
        var projectRecord = records.Single(record => record.Package.Source == "team/one").Package;
        Assert.Equal("release", projectRecord.RepositoryRef);
        Assert.Equal("vercel-project-sha256", projectRecord.HashKind);
        Assert.Equal("payload", projectRecord.ContentHash);
        Assert.Contains(one, projectRecord.CoveredRoots);
        Assert.All(projectRecord.CoveredRoots, root => Assert.True(OwnershipPath.Same(one, root)));
        Assert.True(projectRecord.IsExclusiveOwner);
        Assert.False(records.Single(record => record.Package.Source == "team/user").Package.IsExclusiveOwner);
    }

    [Fact]
    public void DiagnosticBudgetAndEntryLimitRetainGlobalIncompleteCoverage()
    {
        var first = Path.Combine(_tempRoot, "first", "skills");
        var later = Path.Combine(_tempRoot, "later", "skills");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(later);
        var invalid = string.Join(",", Enumerable.Range(0, 128).Select(i => "\"bad" + i + "\":{}"));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(first)!, SkillLockFileReader.FileName),
            "{\"skills\":{" + invalid + "}}");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(later)!, SkillLockFileReader.FileName), "malformed");
        var diagnostics = new List<LockReadDiagnostic>();
        var reader = new SkillLockFileReader(new Logger());
        reader.LoadObservations([first, later], TestContext.Current.CancellationToken, new(HomeDirectory: ""), diagnostics);
        Assert.Equal(128, diagnostics.Count);
        Assert.Contains(diagnostics, diagnostic => diagnostic.CoveredRoots.IsEmpty && diagnostic.Message.Contains("incomplete", StringComparison.Ordinal));
        invalid = string.Join(",", Enumerable.Range(0, SkillLockFileReader.MaxEntries + 1).Select(i => "\"bad" + i + "\":{}"));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(first)!, SkillLockFileReader.FileName), "{\"skills\":{" + invalid + "}}");
        diagnostics.Clear();
        reader.LoadObservations([first, later], TestContext.Current.CancellationToken, new(HomeDirectory: ""), diagnostics);
        Assert.Contains(diagnostics, diagnostic => diagnostic.CoveredRoots.IsEmpty);
        Assert.True(diagnostics.Count <= 128);
    }

    [Fact]
    public void AncestorProjectLockDoesNotClaimCustomDirectory()
    {
        var custom = Path.Combine(_tempRoot, "unrelated-custom");
        var canonical = Path.Combine(_tempRoot, ".agents", "skills");
        Directory.CreateDirectory(custom);
        Directory.CreateDirectory(canonical);
        File.WriteAllText(Path.Combine(_tempRoot, SkillLockFileReader.ProjectFileName),
            "{\"version\":1,\"skills\":{\"demo\":{\"source\":\"team/repo\",\"sourceType\":\"github\"}}}");
        var records = new SkillLockFileReader(new Logger()).LoadObservations([custom, canonical],
            TestContext.Current.CancellationToken, new(HomeDirectory: ""));
        var covered = Assert.Single(records).Package.CoveredRoots;
        Assert.Contains(canonical, covered);
        Assert.All(covered, root => Assert.True(OwnershipPath.Same(canonical, root)));
        Assert.DoesNotContain(custom, covered);
    }

    [Fact]
    public void UnknownSchemaPreservesReadOnlyEvidenceAndDiagnostic()
    {
        var root = Path.Combine(_tempRoot, "skills");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(_tempRoot, SkillLockFileReader.FileName),
            "{\"version\":99,\"skills\":{\"demo\":{\"source\":\"team/repo\"}}}");
        var diagnostics = new List<LockReadDiagnostic>();
        var records = new SkillLockFileReader(new Logger()).LoadObservations([root], TestContext.Current.CancellationToken,
            new(HomeDirectory: ""), diagnostics);
        Assert.True(Assert.Single(records).Package.ReadOnly);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("Unsupported", StringComparison.Ordinal));
    }

    [Fact]
    public void ObservationsRetainEverySameNameOwnerInItsOwnContext()
    {
        var roots = new[] { "one", "two" }.Select(name => Path.Combine(_tempRoot, name, "skills")).ToArray();
        foreach (var root in roots)
        {
            Directory.CreateDirectory(root);
            var owner = Path.GetFileName(Path.GetDirectoryName(root));
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(root)!, SkillLockFileReader.FileName),
                "{\"skills\":{\"sample\":{\"source\":\"" + owner + "/repo\",\"sourceType\":\"github\"}}}");
        }
        var observations = new SkillLockFileReader(new Logger()).LoadObservations(roots, TestContext.Current.CancellationToken)
            .Where(entry => entry.Name == "sample").ToArray();
        Assert.Equal(2, observations.Length);
        Assert.Equal(["one/repo", "two/repo"], observations.Select(entry => entry.Package.Source));
        for (var i = 0; i < roots.Length; i++)
            Assert.True(OwnershipPath.Same(roots[i], observations[i].Package.InstallationRoot!));
    }

    [Fact]
    public void LoadFromRoots_ReadsValidBoundedManifest()
    {
        var skills = Path.Combine(_tempRoot, "agent", "skills");
        Directory.CreateDirectory(skills);
        File.WriteAllText(
            Path.Combine(_tempRoot, "agent", SkillLockFileReader.FileName),
            """
            {"skills":{"sample":{"source":"owner/repo","sourceType":"github"}}}
            """);

        var packages = new SkillLockFileReader(new Logger()).LoadFromRoots([skills]);

        var package = packages["sample"];
        Assert.Equal("owner/repo", package.Source);
        Assert.Equal("github", package.SourceType);
    }

    [Fact]
    public void LoadFromRoots_IgnoresManifestOverSizeLimit()
    {
        var skills = Path.Combine(_tempRoot, "agent", "skills");
        Directory.CreateDirectory(skills);
        var lockFile = Path.Combine(_tempRoot, "agent", SkillLockFileReader.FileName);
        File.WriteAllText(
            lockFile,
            "{\"skills\":{\"oversized\":{\"source\":\"owner/repo\"}}}"
            + new string(' ', SkillLockFileReader.MaxFileBytes));
        var logger = new Logger();

        var packages = new SkillLockFileReader(logger).LoadFromRoots([skills]);

        Assert.DoesNotContain("oversized", packages.Keys);
        Assert.Contains(
            logger.Snapshot(),
            entry => entry.Category == "inventory.lockfile"
                && entry.Message.Contains("exceeds", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadFromRoots_HonorsCancellationBeforeFileIo()
    {
        var skills = Path.Combine(_tempRoot, "agent", "skills");
        Directory.CreateDirectory(skills);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new SkillLockFileReader(new Logger()).LoadFromRootsWithCancellation(
                [skills],
                cancellation.Token));
    }
}
