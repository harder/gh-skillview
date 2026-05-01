using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class CleanupScreenTests
{
    [Fact]
    public void BuildRemoveConfirmationText_SummarizesKindsAndPaths()
    {
        var candidates = ImmutableArray.Create(
            new CleanupClassifier.Candidate(
                CleanupClassifier.CandidateKind.Duplicate,
                "/tmp/a",
                "duplicate install",
                Skill: null),
            new CleanupClassifier.Candidate(
                CleanupClassifier.CandidateKind.Duplicate,
                "/tmp/b",
                "duplicate install",
                Skill: null),
            new CleanupClassifier.Candidate(
                CleanupClassifier.CandidateKind.BrokenSymlink,
                "/tmp/c",
                "broken link",
                Skill: null));

        var text = CleanupScreen.BuildRemoveConfirmationText(candidates);

        Assert.Contains("Remove 3 cleanup candidate(s)?", text);
        Assert.Contains("- duplicate: 2", text);
        Assert.Contains("- broken-link: 1", text);
        Assert.Contains("/tmp/a", text);
        Assert.Contains("/tmp/c", text);
    }

    [Fact]
    public void BuildRemoveConfirmationText_ShowsFirstFewPathsOnly()
    {
        var candidates = Enumerable.Range(0, 5)
            .Select(i => new CleanupClassifier.Candidate(
                CleanupClassifier.CandidateKind.EmptyDirectory,
                $"/tmp/{i}",
                "empty",
                Skill: null))
            .ToImmutableArray();

        var text = CleanupScreen.BuildRemoveConfirmationText(candidates);

        Assert.Contains("/tmp/0", text);
        Assert.Contains("/tmp/1", text);
        Assert.Contains("/tmp/2", text);
        Assert.DoesNotContain("/tmp/3", text);
        Assert.Contains("…and 2 more", text);
    }
}
