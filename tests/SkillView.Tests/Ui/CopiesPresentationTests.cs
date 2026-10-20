using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class CopiesPresentationTests
{
    internal static InstalledSkill Copy(string path, string name = "demo", string source = "https://github.com/test/repo", string reference = "refs/tags/v1") => new()
    {
        Name = name,
        ResolvedPath = path,
        ScanRoot = "/skills",
        Scope = Scope.User,
        Agents = [new("claude", path, false)],
        FrontMatter = new() { Name = name, Upstream = source, GithubPath = "skills/demo", GithubRef = reference },
        Validity = ValidityState.Valid,
        Provenance = Provenance.FsScan,
        Ignored = false,
        IsSymlinked = false,
        InstalledAt = null
    };

    internal static SkillComparisonResult Compared(InstalledSkill left, InstalledSkill right) =>
        new(left.InstallationId, right.InstallationId, "same-source", "different-recorded-revisions", "different-contexts", "different-payloads",
            "left", "right", ["SKILL.md"], 1, true, false, "Review differences", []);

    [Fact]
    public void RelatedCopiesRetainSeparateOriginsAndGroupKnownSourceWithoutTreatingSharedConsumersAsCopies()
    {
        var selected = Copy("/skills/shared") with { Agents = [new("claude", "/claude/demo", true), new("codex", "/codex/demo", true)] };
        var renamed = Copy("/skills/renamed", "renamed");
        var otherOrigin = Copy("/other/demo", source: "https://github.com/other/repo");
        var unrelated = Copy("/other/unrelated", "different", "https://github.com/other/repo");
        var group = CopiesPresentation.Build(InventorySnapshot.Empty with { Skills = [renamed, otherOrigin, unrelated, selected] }, selected, TestContext.Current.CancellationToken);
        Assert.Equal(3, group.Copies.Length); Assert.Same(selected, group.Copies[0]);
        Assert.Contains(renamed, group.Copies); Assert.Contains(otherOrigin, group.Copies); Assert.DoesNotContain(unrelated, group.Copies);
        var shared = CopiesPresentation.Render(group, selected, selected);
        Assert.Contains("claude, codex", shared); Assert.Contains("share this installation's files", shared);
        Assert.Contains("Different sources", CopiesPresentation.Render(group, selected, otherOrigin));
    }

    [Fact]
    public void DifferentVersionsAndPinsStaySeparateWithoutGuessingWhichIsNewer()
    {
        var reference = Copy("/one"); var selected = Copy("/two", reference: "refs/tags/v2");
        var group = new CopiesGroup([reference, selected], 0, 0, false);
        Assert.Contains("Different recorded revisions; neither is assumed newer", CopiesPresentation.Render(group, reference, selected));
        selected = selected with { FrontMatter = selected.FrontMatter with { Pinned = true, PinnedRef = "v2" } };
        var text = CopiesPresentation.Render(group, reference, selected);
        Assert.Contains("Different pin policies", text); Assert.Contains("Pin: v2", text);
    }

    [Fact]
    public void PresentationBoundsAndAmbiguityNeverChooseAnArbitraryPhysicalRow()
    {
        var selected = Copy("/selected"); var ambiguous = Copy("/ambiguous");
        var group = CopiesPresentation.Build(InventorySnapshot.Empty with
        { Skills = [selected, ambiguous, ambiguous, .. Enumerable.Range(0, 140).Select(index => Copy("/copy/" + index))] }, selected, TestContext.Current.CancellationToken);
        Assert.Equal(CopiesPresentation.MaxCopies, group.Copies.Length);
        Assert.Same(selected, group.Copies[0]); Assert.Equal(13, group.Omitted); Assert.Equal(1, group.Ambiguous);
        Assert.DoesNotContain(group.Copies, skill => skill.InstallationId == ambiguous.InstallationId);
        Assert.Contains("Additional copies not shown: 13", CopiesPresentation.Render(group, selected, selected));
        Assert.Throws<OperationCanceledException>(() => CopiesPresentation.Build(InventorySnapshot.Empty, selected, new(true)));
    }

    [Fact]
    public void UnknownProvenanceIncompleteComparisonAndUntrustedFieldsRemainExplicitAndBounded()
    {
        var left = Copy("/left") with { FrontMatter = SkillFrontMatter.Empty };
        var right = Copy("/right") with { FrontMatter = SkillFrontMatter.Empty };
        var group = new CopiesGroup([left, right], 0, 0, true);
        Assert.Contains("Source relationship unknown", CopiesPresentation.Render(group, left, right));
        var compared = Compared(left, right) with { Complete = false, PayloadRelationship = "incomplete", Diagnostics = ["Unknown owner record"] };
        var text = CopiesPresentation.Render(group, left, right, compared);
        Assert.Contains("Comparison incomplete", text); Assert.Contains("Partial discovery", text); Assert.Contains("Unknown owner record", text);
        right = right with { Name = "spoof\nline\u001b[31m", ResolvedPath = new('x', 20_000) };
        text = CopiesPresentation.Render(group, left, right);
        Assert.Contains("spoof\\nline", text); Assert.DoesNotContain('\u001b', text);
        Assert.Contains("long fields omitted", text); Assert.True(text.Length < 17 * 1024);
    }

    [Fact]
    public void CleanupHandoffFiltersByExactPhysicalIdAndPathEvenForSameName()
    {
        var selected = Copy("/one"); var other = Copy("/two");
        CleanupClassifier.Candidate Candidate(InstalledSkill skill) => new(CleanupClassifier.CandidateKind.NameCollision, skill.ResolvedPath, "copies", skill, true)
        { Impact = OperationImpact.Payload(OperationSubject.From(skill), "cleanup") };
        var candidate = Candidate(selected);
        var result = SkillViewWorkflowCoordinator.FilterCleanupForInstallation([Candidate(other), candidate], OperationSubject.From(selected));
        Assert.Same(candidate, Assert.Single(result));
        Assert.Empty(SkillViewWorkflowCoordinator.FilterCleanupForInstallation([candidate], OperationSubject.From(selected) with { Path = "/replacement" }));
    }
}
