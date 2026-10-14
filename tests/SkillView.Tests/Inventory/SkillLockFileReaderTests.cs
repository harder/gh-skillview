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
        Assert.Equal(roots, observations.Select(entry => entry.Package.InstallationRoot));
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
