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
        Assert.Contains(result, c => c.Kind == CleanupClassifier.CandidateKind.Untracked);
    }

    [Fact]
    public void Classify_SourceOrphaned_NotReportedWhenGhListAbsent()
    {
        var snap = Snapshot(false, Skill("orph", prov: Provenance.FsScan));
        var roots = new[] { new ScanRoot(_tempRoot, Scope.User, null) };
        var result = CleanupClassifier.Classify(snap, roots);
        Assert.DoesNotContain(result, c => c.Kind == CleanupClassifier.CandidateKind.Untracked);
    }

    [Fact]
    public void Classify_NameCollision_RequiresReviewWithoutChoosingSurvivor()
    {
        var a = Skill("dup", path: Path.Combine(_tempRoot, "a"), prov: Provenance.Both);
        var b = Skill("dup", path: Path.Combine(_tempRoot, "b"), prov: Provenance.FsScan);
        var snap = Snapshot(false, a, b);
        var roots = new[] { new ScanRoot(_tempRoot, Scope.User, null) };
        var result = CleanupClassifier.Classify(snap, roots);
        Assert.Equal(2, result.Count(c => c.Kind == CleanupClassifier.CandidateKind.NameCollision));
        Assert.All(result.Where(c => c.Kind == CleanupClassifier.CandidateKind.NameCollision), c => Assert.True(c.ReviewOnly));
    }

    [Fact]
    public void DeduplicateByPath_SourceOrphanedAndDuplicateShareOneRemoval()
    {
        var primary = Skill(
            "dup",
            path: Path.Combine(_tempRoot, "primary"),
            prov: Provenance.Both);
        var duplicatePath = Path.Combine(_tempRoot, "duplicate");
        var duplicate = Skill(
            "dup",
            path: duplicatePath,
            prov: Provenance.FsScan);
        var candidates = CleanupClassifier.Classify(
            Snapshot(usedGhList: true, primary, duplicate),
            [new ScanRoot(_tempRoot, Scope.User, null)]);

        var matching = candidates
            .Where(candidate => PathIdentity.Equals(candidate.Path, duplicatePath))
            .ToImmutableArray();
        var selection = CleanupClassifier.DeduplicateByPath(
            matching,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, matching.Length);
        Assert.Contains(matching, candidate =>
            candidate.Kind == CleanupClassifier.CandidateKind.Untracked);
        Assert.Contains(matching, candidate =>
            candidate.Kind == CleanupClassifier.CandidateKind.NameCollision);
        Assert.Single(selection.Unique);
        Assert.Single(selection.Duplicates);
        Assert.Equal(duplicatePath, selection.Unique[0].Path);
        Assert.Equal(duplicatePath, selection.Duplicates[0].Path);
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

    [Fact]
    public void Classify_RealScannerEmptyObservationBecomesEmptyDirectoryWithoutDroppingSafetyLimits()
    {
        var empty = Directory.CreateDirectory(Path.Combine(_tempRoot, "empty-observed")).FullName;
        var roots = new[] { new ScanRoot(_tempRoot, Scope.User, null) };
        var scanner = new LocalSkillScanner(new SkillView.Logging.Logger());
        var observed = Assert.Single(scanner.Scan(roots));
        Assert.Equal(ValidityState.MissingSkillMd, observed.Validity);
        var snapshot = Snapshot(false, observed);
        var candidate = Assert.Single(CleanupClassifier.Classify(snapshot, roots));
        Assert.Equal(CleanupClassifier.CandidateKind.EmptyDirectory, candidate.Kind);
        Assert.False(candidate.ReviewOnly);
        Assert.Same(observed, candidate.Skill);
        Assert.Equal(empty, candidate.Path);

        var claim = new SkillPackage("owner/repo", "github", null, null, null)
        { InstallationRoot = _tempRoot, Selector = "empty-observed" };
        foreach (var protectedSnapshot in new[]
        {
            snapshot with { Diagnostics = new ScanDiagnostics { InaccessiblePaths = ["unreadable"] } },
            snapshot with { OwnershipRecords = [claim] },
            snapshot with { Skills = [observed with { OwnershipClaims = [claim] }] }
        })
        {
            var review = Assert.Single(CleanupClassifier.Classify(protectedSnapshot, roots));
            Assert.Equal(CleanupClassifier.CandidateKind.EmptyDirectory, review.Kind);
            Assert.True(review.ReviewOnly);
        }

        File.WriteAllText(Path.Combine(empty, "keep.txt"), "keep");
        Assert.Equal(CleanupClassifier.CandidateKind.Malformed,
            Assert.Single(CleanupClassifier.Classify(snapshot, roots)).Kind);
    }

    [Fact]
    public void Classify_EmptySymlinkTargetDoesNotBecomeEmptyDirectoryRemoval()
    {
        var target = Directory.CreateDirectory(Path.Combine(_tempRoot, "container", "target")).FullName;
        var link = Path.Combine(_tempRoot, "empty-link");
        if (OperatingSystem.IsWindows()) { Assert.Skip("Unix empty-link observation; Windows native link proofs run separately."); return; }
        Directory.CreateSymbolicLink(link, target);
        var observation = Skill("empty-link", path: target, validity: ValidityState.MissingSkillMd, symlinked: true);
        var candidates = CleanupClassifier.Classify(Snapshot(false, observation), [new(_tempRoot, Scope.User, null)]);
        Assert.DoesNotContain(candidates, candidate => candidate.Kind == CleanupClassifier.CandidateKind.EmptyDirectory);
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void MissingPayloadOwnerRecordsAndOwnedBrokenLinksRequireReview()
    {
        var empty = Directory.CreateDirectory(Path.Combine(_tempRoot, "demo")).FullName;
        var claim = new SkillPackage("owner/repo", "github", null, null, null)
        { InstallationRoot = _tempRoot, Selector = "demo" };
        var broken = Skill("broken", validity: ValidityState.BrokenSymlink) with { OwnershipClaims = [claim] };
        var snapshot = Snapshot(false, broken) with { OwnershipRecords = [claim] };
        var candidates = CleanupClassifier.Classify(snapshot, [new(_tempRoot, Scope.User, null)]);
        Assert.True(Assert.Single(candidates, candidate => candidate.Kind == CleanupClassifier.CandidateKind.BrokenSymlink).ReviewOnly);
        Assert.True(Assert.Single(candidates, candidate => candidate.Path == empty).ReviewOnly);
        Assert.True(Directory.Exists(empty));
        if (!OperatingSystem.IsWindows())
        {
            var alias = _tempRoot + "-alias";
            try
            {
                Directory.CreateSymbolicLink(alias, _tempRoot);
                var aliased = CleanupClassifier.Classify(snapshot, [new(alias, Scope.User, null)]);
                Assert.True(Assert.Single(aliased, candidate => candidate.Kind == CleanupClassifier.CandidateKind.EmptyDirectory).ReviewOnly);
            }
            finally { if (Directory.Exists(alias)) Directory.Delete(alias); }
        }
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
    public void Classify_SameUpstreamTreeDoesNotEstablishIdenticalLocalContent()
    {
        var a = Skill("dup-sha", path: Path.Combine(_tempRoot, "a"), prov: Provenance.Both, treeSha: "abc123");
        var b = Skill("dup-sha", path: Path.Combine(_tempRoot, "b"), prov: Provenance.FsScan, treeSha: "abc123");
        var snap = Snapshot(false, a, b);
        var roots = new[] { new ScanRoot(_tempRoot, Scope.User, null) };
        var result = CleanupClassifier.Classify(snap, roots);
        Assert.Equal(2, result.Count(c => c.Kind == CleanupClassifier.CandidateKind.NameCollision));
        Assert.All(result.Where(c => c.Kind == CleanupClassifier.CandidateKind.NameCollision), c => Assert.True(c.ReviewOnly));
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

    [Fact]
    public void ClassifyWithCancellation_StopsBeforeFilesystemEnumeration()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            CleanupClassifier.ClassifyWithCancellation(
                Snapshot(false),
                [new ScanRoot(_tempRoot, Scope.User, null)],
                options: null,
                cancellation.Token));
    }
}
