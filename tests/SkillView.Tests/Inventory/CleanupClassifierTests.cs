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

    // --- Phase 9 edge cases ---------------------------------------------

    [Fact]
    public void Classify_HiddenNestedResidue_InsideDotDir()
    {
        var hidden = Path.Combine(_tempRoot, ".hidden-agent", "residue");
        Directory.CreateDirectory(hidden);
        File.WriteAllText(Path.Combine(hidden, "SKILL.md"), "---\nname: residue\n---\nbody");
        var skill = Skill("residue", path: hidden) with
        {
            Validity = ValidityState.Valid,
        };
        var snap = Snapshot(false, skill);
        var roots = new[] { new ScanRoot(_tempRoot, Scope.User, null) };
        var result = CleanupClassifier.Classify(snap, roots);
        Assert.Contains(result, c => c.Kind == CleanupClassifier.CandidateKind.HiddenNestedResidue);
    }

    [Fact]
    public void Classify_HiddenNestedResidue_NotTriggeredForTopLevel()
    {
        var normal = Skill("normal") with { Validity = ValidityState.Valid };
        var snap = Snapshot(false, normal);
        var roots = new[] { new ScanRoot(_tempRoot, Scope.User, null) };
        var result = CleanupClassifier.Classify(snap, roots);
        Assert.DoesNotContain(result, c => c.Kind == CleanupClassifier.CandidateKind.HiddenNestedResidue);
    }

    [Fact]
    public void Classify_BrokenSharedMapping_ConflictingNames()
    {
        var sharedPath = Path.Combine(_tempRoot, "shared");
        Directory.CreateDirectory(sharedPath);
        File.WriteAllText(Path.Combine(sharedPath, "SKILL.md"), "---\nname: shared\n---\nbody");
        var skill1 = Skill("name-a", path: sharedPath) with
        {
            Agents = ImmutableArray.Create(new AgentMembership("claude", sharedPath, false)),
        };
        var skill2 = Skill("name-b", path: sharedPath) with
        {
            Agents = ImmutableArray.Create(new AgentMembership("copilot", sharedPath, false)),
        };
        var snap = Snapshot(false, skill1, skill2);
        var roots = new[] { new ScanRoot(_tempRoot, Scope.User, null) };
        var result = CleanupClassifier.Classify(snap, roots);
        Assert.Contains(result, c => c.Kind == CleanupClassifier.CandidateKind.BrokenSharedMapping);
    }

    [Fact]
    public void Classify_BrokenSharedMapping_NotTriggeredWhenNamesMatch()
    {
        var sharedPath = Path.Combine(_tempRoot, "shared2");
        Directory.CreateDirectory(sharedPath);
        var skill1 = Skill("same-name", path: sharedPath);
        var skill2 = Skill("same-name", path: sharedPath);
        var snap = Snapshot(false, skill1, skill2);
        var roots = new[] { new ScanRoot(_tempRoot, Scope.User, null) };
        var result = CleanupClassifier.Classify(snap, roots);
        Assert.DoesNotContain(result, c => c.Kind == CleanupClassifier.CandidateKind.BrokenSharedMapping);
    }

    [Fact]
    public void Classify_DuplicateWithSameTreeSha_StillDetected()
    {
        var a = Skill("dup-sha", path: Path.Combine(_tempRoot, "a"), prov: Provenance.Both, treeSha: "abc123");
        var b = Skill("dup-sha", path: Path.Combine(_tempRoot, "b"), prov: Provenance.FsScan, treeSha: "abc123");
        var snap = Snapshot(false, a, b);
        var roots = new[] { new ScanRoot(_tempRoot, Scope.User, null) };
        var result = CleanupClassifier.Classify(snap, roots);
        Assert.Contains(result, c => c.Kind == CleanupClassifier.CandidateKind.Duplicate);
    }

    [Fact]
    public void Classify_MultipleEmptyDirectories()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, "empty-a"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "empty-b"));
        var snap = Snapshot(false);
        var roots = new[] { new ScanRoot(_tempRoot, Scope.User, null) };
        var result = CleanupClassifier.Classify(snap, roots);
        var emptyDirs = result.Where(c => c.Kind == CleanupClassifier.CandidateKind.EmptyDirectory).ToList();
        Assert.Equal(2, emptyDirs.Count);
    }

    [Fact]
    public void Classify_MalformedTakesPrecedenceOverOtherClassifications()
    {
        // A malformed skill that also happens to be in a hidden dir — Malformed
        // should be emitted, not HiddenNestedResidue (malformed triggers first
        // due to the early `continue` in the classifier loop).
        var hidden = Path.Combine(_tempRoot, ".dotdir", "bad");
        Directory.CreateDirectory(hidden);
        var skill = Skill("bad", path: hidden, validity: ValidityState.MissingSkillMd);
        var snap = Snapshot(false, skill);
        var roots = new[] { new ScanRoot(_tempRoot, Scope.User, null) };
        var result = CleanupClassifier.Classify(snap, roots);
        Assert.Contains(result, c => c.Kind == CleanupClassifier.CandidateKind.Malformed);
        Assert.DoesNotContain(result, c => c.Kind == CleanupClassifier.CandidateKind.HiddenNestedResidue);
    }
}
