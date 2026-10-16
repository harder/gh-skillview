using System.Collections.Immutable;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class ThreeWayPayloadReviewTests
{
    private static PayloadEntry File(string path, string hash, int mode = 420) => new(path, "file", hash, hash, 1, mode, null);
    private static PayloadEntry Directory(string path, int mode = 493) => new(path, "directory", "", "", 0, mode, null);
    private static PayloadEntry Link(string path, string bytes) => new(path, "link", bytes, bytes, 0, 511, bytes);
    private static PayloadSnapshot Snapshot(params PayloadEntry[] entries) => new("/fixture", "capture", "comparison", entries.ToImmutableArray()) { RootUnixMode = 493 };

    [Fact]
    public void IndependentLocalAndSourceChangesAreCombinedWithExtrasAndDeletions()
    {
        var b = Snapshot(File("local", "base"), File("incoming", "base"), File("removed", "base"));
        var i = Snapshot(File("local", "edit"), File("incoming", "base"), File("extra", "keep"));
        var c = Snapshot(File("local", "base"), File("incoming", "new"), File("removed", "base"));
        var review = ThreeWayPayloadReview.Analyze(b, i, c);
        Assert.True(review.CanMaterialize);
        Assert.Equal(new[] { "extra", "incoming", "local" }, review.Paths.Where(row => row.Selected is not null).Select(row => row.Path));
        Assert.Equal("edit", review.Paths.Single(row => row.Path == "local").Selected!.ContentHash);
        Assert.Equal("new", review.Paths.Single(row => row.Path == "incoming").Selected!.ContentHash);
        Assert.Null(review.Paths.Single(row => row.Path == "removed").Selected);
    }

    [Fact]
    public void ConcurrentSamePathEditsRemainUnresolvedUntilExplicitlyChosen()
    {
        var b = Snapshot(File("SKILL.md", "base")); var i = Snapshot(File("SKILL.md", "local")); var c = Snapshot(File("SKILL.md", "remote"));
        Assert.False(ThreeWayPayloadReview.Analyze(b, i, c).CanMaterialize);
        Assert.True(Assert.Single(ThreeWayPayloadReview.Analyze(b, i, c).Paths).Unresolved);
        var review = ThreeWayPayloadReview.Analyze(b, i, c, [new("SKILL.md", "installed")]);
        Assert.True(review.CanMaterialize); Assert.Equal("local", Assert.Single(review.Paths).Selected!.ContentHash);
    }

    [Fact]
    public void EqualAuthoredContentRetainsInstalledPresentationButStillRequiresTrackingBinding()
    {
        var authored = File("SKILL.md", "installed-bytes") with { ComparisonHash = "same-authored-content" };
        var source = authored with { ContentHash = "source-with-new-tracking" };
        var review = ThreeWayPayloadReview.Analyze(Snapshot(authored), Snapshot(authored), Snapshot(source));
        Assert.Equal("installed", Assert.Single(review.Paths).Selection);
        Assert.Equal("installed-bytes", Assert.Single(review.Paths).Selected!.ContentHash);
        // Analysis does not rewrite or claim to approve the installer tracking.
        Assert.Equal("source-with-new-tracking", Assert.Single(review.Paths).Source!.ContentHash);
    }

    [Fact]
    public void DeleteVersusEditNeedsAnExplicitChoiceAndCanSelectAbsence()
    {
        var b = Snapshot(File("script", "base")); var c = Snapshot(File("script", "changed"));
        Assert.False(ThreeWayPayloadReview.Analyze(b, Snapshot(), c).CanMaterialize);
        var review = ThreeWayPayloadReview.Analyze(b, Snapshot(), c, [new("script", "installed")]);
        Assert.True(review.CanMaterialize); Assert.Null(Assert.Single(review.Paths).Selected);
    }

    [Fact]
    public void IndependentEditsCanProduceAParentTypeConflictThatMustBeResolved()
    {
        var b = Snapshot(Directory("scripts"), File("scripts/old", "base"));
        var i = Snapshot(File("scripts", "local-file"));
        var c = Snapshot(Directory("scripts"), File("scripts/new", "new"));
        var review = ThreeWayPayloadReview.Analyze(b, i, c);
        Assert.False(review.CanMaterialize); Assert.Contains(review.TopologyConflicts, item => item.Contains("scripts/new", StringComparison.Ordinal));
        var resolved = ThreeWayPayloadReview.Analyze(b, i, c, [new("scripts", "source")]);
        Assert.True(resolved.CanMaterialize);
    }

    [Fact]
    public void DeletedDirectoryCannotHideAnIndependentlyAddedDescendant()
    {
        var b = Snapshot(Directory("empty")); var i = Snapshot(); var c = Snapshot(Directory("empty"), File("empty/new", "new"));
        var review = ThreeWayPayloadReview.Analyze(b, i, c);
        Assert.False(review.CanMaterialize); Assert.NotEmpty(review.TopologyConflicts);
        Assert.True(ThreeWayPayloadReview.Analyze(b, i, c, [new("empty", "source")]).CanMaterialize);
    }

    [Fact]
    public void LinkBytesAndModeChangesParticipateInWholeEntryDecisions()
    {
        var b = Snapshot(Link("link", "first"), File("run", "body"));
        var i = Snapshot(Link("link", "local"), File("run", "body", 493));
        var c = Snapshot(Link("link", "remote"), File("run", "body"));
        var review = ThreeWayPayloadReview.Analyze(b, i, c);
        Assert.True(review.Paths.Single(row => row.Path == "link").Unresolved);
        Assert.Equal(493, review.Paths.Single(row => row.Path == "run").Selected!.UnixMode);
    }

    [Fact]
    public void RootPrivacyIsPreservedUnlessReviewExplicitlySelectsSourceMode()
    {
        var b = Snapshot(); var i = b with { RootUnixMode = 448 }; var c = b with { RootUnixMode = 511 };
        Assert.Equal(448, ThreeWayPayloadReview.Analyze(b, i, c).RootMode.Selected);
        var review = ThreeWayPayloadReview.Analyze(b, i, c, [new("/", "source")]);
        Assert.Equal(511, review.RootMode.Selected); Assert.Equal("explicit-choice", review.RootMode.Reason);
    }

    [Fact]
    public void AddressPolicyDetectsCaseCollisionsWithoutAssumingGlobalIgnoreCase()
    {
        var b = Snapshot(); var i = Snapshot(File("A", "local")); var c = Snapshot(File("a", "source"));
        Assert.True(ThreeWayPayloadReview.Analyze(b, i, c).CanMaterialize);
        Assert.False(ThreeWayPayloadReview.Analyze(b, i, c, addressKey: path => path.ToUpperInvariant()).CanMaterialize);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("a/../escape")]
    [InlineData("/absolute")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    public void InvalidCapturedPathsAreRefused(string path) =>
        Assert.Throws<IOException>(() => ThreeWayPayloadReview.Analyze(Snapshot(), Snapshot(File(path, "local")), Snapshot()));

    [Fact]
    public void DuplicateAndUnknownChoicesCannotSilentlyChangeTheReview()
    {
        var snapshot = Snapshot(File("one", "content"));
        Assert.Throws<IOException>(() => ThreeWayPayloadReview.Analyze(snapshot, snapshot, snapshot, [new("one", "installed"), new("one", "source")]));
        Assert.Throws<IOException>(() => ThreeWayPayloadReview.Analyze(snapshot, snapshot, snapshot, [new("unknown", "source")]));
        Assert.Throws<IOException>(() => ThreeWayPayloadReview.Analyze(snapshot, snapshot, snapshot, [new("one", "resolved")]));
    }
    [Fact]
    public void ResolutionRequiresCapturedFileEvidenceAndSelectsItsExactResult()
    {
        var b = Snapshot(File("SKILL.md", "base")); var i = Snapshot(File("SKILL.md", "installed")); var c = Snapshot(File("SKILL.md", "incoming"));
        var resolution = File("SKILL.md", "reviewed-resolution", 384);
        var review = ThreeWayPayloadReview.Analyze(b, i, c, [new("SKILL.md", "resolved")], resolvedFiles: [resolution]);
        Assert.True(review.CanMaterialize); Assert.Same(resolution, Assert.Single(review.Paths).Selected);
        Assert.Throws<IOException>(() => ThreeWayPayloadReview.Analyze(b, i, c, resolvedFiles: [resolution]));
        Assert.Throws<IOException>(() => ThreeWayPayloadReview.Analyze(b, i, c, [new("SKILL.md", "resolved")], resolvedFiles: [Link("SKILL.md", "target")]));
    }

    [Fact]
    public void IndividuallyBoundedCapturesCannotCombineIntoAnOversizedPayload()
    {
        var local = File("local", "local") with { Bytes = PayloadSnapshot.MaxBytes };
        var review = ThreeWayPayloadReview.Analyze(Snapshot(), Snapshot(local), Snapshot(File("incoming", "source")));
        Assert.False(review.CanMaterialize); Assert.Contains(review.TopologyConflicts, message => message.Contains("byte budget", StringComparison.Ordinal));
    }

}
