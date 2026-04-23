using System.Collections.Immutable;
using System.IO;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using Xunit;

namespace SkillView.Tests.Inventory;

public class CleanupClassifierTests : IDisposable
{
    private readonly string _tempRoot;

    public CleanupClassifierTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "skillview-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
    }

    private InstalledSkill Skill(
        string name,
        string? path = null,
        ValidityState validity = ValidityState.Valid,
        Provenance prov = Provenance.FsScan,
        bool ignored = false,
        bool symlinked = false,
        string? treeSha = null)
    {
        var resolved = path ?? Path.Combine(_tempRoot, name);
        return new InstalledSkill
        {
            Name = name,
            ResolvedPath = resolved,
            ScanRoot = _tempRoot,
            Scope = Scope.User,
            Agents = ImmutableArray<AgentMembership>.Empty,
            FrontMatter = new SkillFrontMatter { Name = name, GithubTreeSha = treeSha },
            Validity = validity,
            Provenance = prov,
            Ignored = ignored,
            IsSymlinked = symlinked,
            InstalledAt = null,
        };
    }

    private InventorySnapshot Snapshot(bool usedGhList, params InstalledSkill[] skills) => new()
    {
        Skills = skills.ToImmutableArray(),
        ScannedRoots = ImmutableArray.Create(new ScanRoot(_tempRoot, Scope.User, null)),
        UsedGhSkillList = usedGhList,
        CapturedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void Classify_Malformed_Detected()
    {
        var snap = Snapshot(false, Skill("m1", validity: ValidityState.MissingSkillMd));
        var roots = new[] { new ScanRoot(_tempRoot, Scope.User, null) };
        var result = CleanupClassifier.Classify(snap, roots);
        Assert.Contains(result, c => c.Kind == CleanupClassifier.CandidateKind.Malformed);
    }

    [Fact]
    public void Classify_BrokenSymlink_Detected()
    {
        var snap = Snapshot(false, Skill("bs", validity: ValidityState.BrokenSymlink));
        var roots = new[] { new ScanRoot(_tempRoot, Scope.User, null) };
        var result = CleanupClassifier.Classify(snap, roots);
        Assert.Contains(result, c => c.Kind == CleanupClassifier.CandidateKind.BrokenSymlink);
    }

    [Fact]
    public void Classify_SourceOrphaned_WhenGhListUsedAndFsScanOnly()
    {
        var snap = Snapshot(true, Skill("orph", prov: Provenance.FsScan));
        var roots = new[] { new ScanRoot(_tempRoot, Scope.User, null) };
        var result = CleanupClassifier.Classify(snap, roots);
        Assert.Contains(result, c => c.Kind == CleanupClassifier.CandidateKind.SourceOrphaned);
    }

    [Fact]
    public void Classify_SourceOrphaned_NotReportedWhenGhListAbsent()
    {
        var snap = Snapshot(false, Skill("orph", prov: Provenance.FsScan));
        var roots = new[] { new ScanRoot(_tempRoot, Scope.User, null) };
        var result = CleanupClassifier.Classify(snap, roots);
        Assert.DoesNotContain(result, c => c.Kind == CleanupClassifier.CandidateKind.SourceOrphaned);
    }

    [Fact]
    public void Classify_Duplicate_DetectedByName()
    {
        var a = Skill("dup", path: Path.Combine(_tempRoot, "a"), prov: Provenance.Both);
        var b = Skill("dup", path: Path.Combine(_tempRoot, "b"), prov: Provenance.FsScan);
        var snap = Snapshot(false, a, b);
        var roots = new[] { new ScanRoot(_tempRoot, Scope.User, null) };
        var result = CleanupClassifier.Classify(snap, roots);
        Assert.Contains(result, c => c.Kind == CleanupClassifier.CandidateKind.Duplicate);
    }

    [Fact]
    public void Classify_Ignored_FilteredOutByDefault()
    {
        var snap = Snapshot(false, Skill("m", validity: ValidityState.MissingSkillMd, ignored: true));
        var roots = new[] { new ScanRoot(_tempRoot, Scope.User, null) };
        var result = CleanupClassifier.Classify(snap, roots);
        Assert.Empty(result);

        var withIgnored = CleanupClassifier.Classify(snap, roots, new CleanupClassifier.Options(IncludeIgnored: true));
        Assert.NotEmpty(withIgnored);
    }

    [Fact]
    public void Classify_EmptyDirectory_DetectedFromScanRoot()
    {
        var emptyDir = Path.Combine(_tempRoot, "empty-one");
        Directory.CreateDirectory(emptyDir);
        var snap = Snapshot(false);
        var roots = new[] { new ScanRoot(_tempRoot, Scope.User, null) };
        var result = CleanupClassifier.Classify(snap, roots);
        Assert.Contains(result, c => c.Kind == CleanupClassifier.CandidateKind.EmptyDirectory);
    }
}
