using SkillView.Maintenance;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class SourceDiffPresentationTests
{
    [Fact]
    public void DiffKeepsUnknownOmittedLiteralPathsAndEstablishedLocalDisposition()
    {
        const string hostile = "references/`[link](https://invalid)\n## forged\u001b[31m.md";
        var target = CopiesPresentationTests.Copy("/skills/demo");
        var plan = ReviewedUpdatesTabTests.Review(target);
        var before = new PayloadEntry("edited.md", "file", "old", "old", 10, 420, null);
        var after = before with { ContentHash = "new", Bytes = 12 };
        var report = new SourceReviewReport([
            new(hostile, null, after with { Path = hostile }, "+literal authored text\n", "literal hunk", 4)
        ], 5, 4, new("partial", [], 0, ["unsupported grammar"], 3), new("partial", [], 0, [], 1), [], 7, "Captured literal scope only");
        plan = plan with
        {
            Revision = plan.Revision! with { LocalChanges = ["extra", "edited.md", "unestablished"] },
            Comparison = new("old", "new", [new("extra", before with { Path = "extra" }, null), new("edited.md", before, after)]),
            SourceReview = new(1, "installed", "candidate", "i", "c", SourceUpdateReview.Hash(report), report)
        };
        var text = SourceReviewScreen.RenderDiff(plan);
        Assert.Contains("Authored version: unknown → unknown", text);
        Assert.Contains("omitted entries: 4", text);
        Assert.Contains("Omitted/truncated diff lines: 4", text);
        Assert.Contains("Omitted coverage reasons old/new: 2/1", text);
        Assert.Contains("Omitted declaration deltas: 7", text);
        Assert.Contains("references/`[link](https://invalid)\\n## forged.md", text);
        Assert.DoesNotContain("\n## forged", text); Assert.DoesNotContain('\u001b', text);
        Assert.Contains("Local: extra — absent from candidate; original only for Undo", text);
        Assert.Contains("Local: edited.md — replaced by candidate entry", text);
        Assert.Contains("Local: unestablished — no changed entry established", text);
        Assert.Contains("Whole-package replacement", text); Assert.Contains("not carried over", text);
        Assert.Contains("Opening Diff grants no approval", text);
        Assert.False(plan.AcknowledgeSourceReview);
    }

    [Fact]
    public void LegacyMissingComparisonIsUnknownAndDisplayLimitIsExplicit()
    {
        var plan = ReviewedUpdatesTabTests.Review(CopiesPresentationTests.Copy("/skills/demo"));
        var text = SourceReviewScreen.RenderDiff(plan);
        Assert.Contains("legacy evidence unavailable", text);
        Assert.Contains("Comparison unavailable; file changes unknown", text);
        var report = new SourceReviewReport([new("SKILL.md", null, null, new('x', 300_000), "bounded", 0)],
            1, 0, new("partial", [], 0, [], 0), new("partial", [], 0, [], 0), [], 0, "unknown");
        text = SourceReviewScreen.RenderDiff(plan with { SourceReview = new(1, "i", "c", "i", "c", "report", report) });
        Assert.True(text.Length <= SourceReviewScreen.MaxDiffCharacters);
        Assert.Contains("Diff display limit", text);
        Assert.Contains("Unknown and omitted evidence is not approval", text);
    }

    [Fact]
    public void SameRefAndUnknownRefDoNotInventAuthoredVersionOrCommitEvidence()
    {
        var plan = ReviewedUpdatesTabTests.Review(CopiesPresentationTests.Copy("/skills/demo"));
        Assert.DoesNotContain("Same source ref", UpdateReviewPresentation.RefChange(plan));
        plan = plan with { Subject = plan.Subject with { Metadata = plan.Subject.Metadata with { GithubRef = plan.Revision!.Source.Ref } } };
        Assert.StartsWith("Same source ref", UpdateReviewPresentation.RefChange(plan));
        Assert.Contains("authored version is separate", UpdateReviewPresentation.RefChange(plan));
        Assert.Contains("unknown → unknown", UpdateReviewPresentation.Plan(plan, true));
    }
}
