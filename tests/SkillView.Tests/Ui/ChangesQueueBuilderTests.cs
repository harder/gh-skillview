using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class ChangesQueueBuilderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(Provenance.Both)]
    [InlineData(Provenance.FsScan)]
    public void RoutineInstallIsNotAProblemOrAnUpdateEvenWhenUntracked(Provenance provenance)
    {
        var skill = MaintenanceQueueTests.Skill("/skills/manual", "https://github.com/one/repo") with { Provenance = provenance };
        var queue = MaintenanceQueueBuilder.Build(InventorySnapshot.Empty with { Skills = [skill], UsedGhSkillList = true }, Token);
        Assert.Contains(queue.Entries, entry => entry.Issue == "installation-review");
        if (provenance == Provenance.FsScan) Assert.Contains(queue.Entries, entry => entry.Issue == "Untracked");
        var projection = ChangesQueueBuilder.Build(queue, Token);
        Assert.Empty(projection.Rows);
        Assert.Equal(0, projection.Problems);
        Assert.Equal(0, projection.ReviewSuggestions);
        Assert.Equal(0, projection.OmittedRows);
        Assert.Contains("Updates not checked", projection.Counts);
    }

    [Theory]
    [InlineData(ValidityState.BrokenSymlink, "BrokenSymlink")]
    [InlineData(ValidityState.MissingSkillMd, "Malformed")]
    [InlineData(ValidityState.UnparsableFrontMatter, "Malformed")]
    [InlineData(ValidityState.InvalidMetadata, "Malformed")]
    public void PositiveFindingsRemainProblemsEvenWhenOnlyReviewIsSupported(ValidityState validity, string issue)
    {
        var skill = MaintenanceQueueTests.Skill("/skills/broken", "https://github.com/one/repo") with { Validity = validity };
        var queue = MaintenanceQueueBuilder.Build(InventorySnapshot.Empty with { Skills = [skill] }, Token);
        var projection = ChangesQueueBuilder.Build(queue, Token);
        var row = Assert.Single(projection.Rows);
        Assert.Equal("Problem", row.Kind);
        Assert.Equal(issue, row.Entry.Issue);
        Assert.Equal(1, projection.Problems);
        Assert.Equal(0, projection.ReviewSuggestions);
        Assert.Same(queue.Entries.Single(entry => entry.Issue == issue), row.Entry);
    }

    [Fact]
    public void EmptyResidueRemainsAProblemWithTheCapturedExactSubject()
    {
        var root = Directory.CreateTempSubdirectory("skillview-issues-empty-");
        try
        {
            var empty = Directory.CreateDirectory(Path.Combine(root.FullName, "leftover"));
            var queue = MaintenanceQueueBuilder.Build(InventorySnapshot.Empty with { ScannedRoots = [new(root.FullName, Scope.Custom, null)] }, Token);
            var projection = ChangesQueueBuilder.Build(queue, Token);
            var row = Assert.Single(projection.Rows);
            Assert.Equal("EmptyDirectory", row.Entry.Issue);
            Assert.Equal("Problem", row.Kind);
            Assert.Equal(empty.FullName, row.Entry.Subject.Path);
            Assert.Same(Assert.Single(queue.Entries), row.Entry);
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void SameNameIntentionalOverridesFormOneReviewFamilyWithEveryFrozenEntry()
    {
        var first = Entry("/projects/alpha/.agents/skills/demo");
        var second = Entry("/projects/beta/.agents/skills/demo") with { Issue = "Duplicate" };
        var queue = Queue([second, first]);
        var projection = ChangesQueueBuilder.Build(queue, Token);
        var row = Assert.Single(projection.Rows);
        Assert.Equal("Review", row.Kind);
        Assert.Equal("demo · 2 captured copies", row.Title);
        Assert.Equal(0, projection.Problems);
        Assert.Equal(1, projection.ReviewSuggestions);
        Assert.Equal(0, projection.OmittedRows);
        Assert.Same(first, row.Entry); // Stable routing anchor, never a preferred survivor.
        Assert.Equal(new[] { first, second }, row.Entries);
        var summary = ChangesQueueBuilder.Render(row, Token, false, null);
        Assert.Contains("alpha", summary);
        Assert.Contains("beta", summary);
        Assert.Contains("intentional project overrides", summary);
        Assert.DoesNotContain("Family fields omitted:", summary);
        var evidence = ChangesQueueBuilder.Render(row, Token, true, null);
        Assert.Contains(first.Subject.Path, evidence);
        Assert.Contains(second.Subject.Path, evidence);
        Assert.DoesNotContain("Family fields omitted:", evidence);
        Assert.Contains(first.Id, evidence);
        Assert.Contains(second.Id, evidence);
        var reversed = Assert.Single(ChangesQueueBuilder.Build(Queue([first, second]), Token).Rows);
        Assert.Same(row.Entry, reversed.Entry);
    }

    [Fact]
    public void CaseVariantNamesUseOneFamilyAndDistinctProjectLocationSuffixes()
    {
        var first = Entry(Path.Combine("projects", "alpha", ".agents", "skills", "demo"));
        var second = Entry(Path.Combine("projects", "beta", ".agents", "skills", "DEMO")) with { Title = "DEMO" };
        var row = Assert.Single(ChangesQueueBuilder.Build(Queue([first, second]), Token).Rows);
        Assert.Equal(2, row.Entries.Count);
        Assert.Equal(Path.Combine("alpha", ".agents", "skills"), row.LocationLabels[0]);
        Assert.Equal(Path.Combine("beta", ".agents", "skills"), row.LocationLabels[1]);
        Assert.Same(first, row.Entry);
    }

    [Fact]
    public void ProblemsAtCopyLocationsRemainSeparateFromTheSingleFamily()
    {
        var first = Entry("/skills/one");
        var second = Entry("/skills/two");
        var malformed = first with { Id = "malformed", Issue = "Malformed" };
        var projection = ChangesQueueBuilder.Build(Queue([first, second, malformed]), Token);
        Assert.Equal(2, projection.Rows.Count);
        Assert.Equal(1, projection.Problems);
        Assert.Equal(1, projection.ReviewSuggestions);
        Assert.Same(malformed, projection.Rows[0].Entry);
        Assert.Equal(2, projection.Rows[1].Entries.Count);
    }

    [Fact]
    public void IncompleteCoverageIsRetainedSeparatelyFromFindings()
    {
        var queue = MaintenanceQueueBuilder.Build(InventorySnapshot.Empty with { ConfiguredRootsComplete = false }, Token);
        var projection = ChangesQueueBuilder.Build(queue, Token);
        Assert.Empty(projection.Rows);
        Assert.Equal(0, projection.Problems);
        Assert.Contains("incomplete", projection.CoverageDetail);
        Assert.Equal(0, projection.OmittedRows);
    }

    [Fact]
    public void LimitsCountFindingsAfterRoutineFilteringAndFamilyGrouping()
    {
        var routine = Entry("/routine") with { Issue = "installation-review" };
        var findings = Enumerable.Range(0, ChangesQueueBuilder.MaxRows + 1)
            .Select(index => Entry("/skills/" + index) with { Issue = "Malformed" });
        var projection = ChangesQueueBuilder.Build(Queue(Enumerable.Repeat(routine, 10000).Concat(findings).ToImmutableArray()), Token);
        Assert.Equal(ChangesQueueBuilder.MaxRows, projection.Rows.Count);
        Assert.Equal(1, projection.OmittedRows);
        Assert.Equal(ChangesQueueBuilder.MaxRows + 1, projection.Problems);
        Assert.Equal(0, projection.ReviewSuggestions);
    }

    [Fact]
    public void SanitizedAndBoundedLabelsNeverReplaceExactRoutingOrMergeDistinctRawNames()
    {
        var name = new string('n', 300) + "\n\u001b[31m";
        var first = Entry("/projects/\u001b[31malpha/demo") with { Title = name, Issue = "Malformed" };
        var second = Entry("/projects/alpha/demo") with { Title = name, Issue = "Malformed" };
        var rows = ChangesQueueBuilder.Build(Queue([first, second]), Token).Rows;
        Assert.NotEqual(rows[0].Title, rows[1].Title);
        Assert.All(rows, row =>
        {
            Assert.InRange(row.Title.Length, 1, ChangesQueueBuilder.MaxTitleCharacters);
            Assert.DoesNotContain('\u001b', row.Title);
            Assert.DoesNotContain('\n', row.Title);
        });
        Assert.Same(first, rows[0].Entry);
        Assert.Same(second, rows[1].Entry);
        var separateNames = ChangesQueueBuilder.Build(Queue([first with { Issue = "NameCollision" },
            second with { Issue = "NameCollision", Title = name + "other" }]), Token);
        Assert.Equal(2, separateNames.ReviewSuggestions);
    }

    [Fact]
    public void LongNamesAndLocationsKeepDistinctMarkersAfterTheWholeTitleIsBounded()
    {
        var common = new string('x', 200);
        var first = Entry(Path.Combine("projects", common + "alpha" + common, "demo")) with
        { Title = new string('n', 300), Issue = "Malformed" };
        var second = Entry(Path.Combine("projects", common + "beta" + common, "demo")) with
        { Title = first.Title, Issue = first.Issue };
        var rows = ChangesQueueBuilder.Build(Queue([first, second]), Token).Rows;
        Assert.NotEqual(rows[0].Title, rows[1].Title);
        Assert.Contains("#1", rows[0].Title);
        Assert.Contains("#2", rows[1].Title);
        Assert.All(rows, row => Assert.InRange(row.Title.Length, 1, ChangesQueueBuilder.MaxTitleCharacters));
        Assert.Same(first, rows[0].Entry);
        Assert.Same(second, rows[1].Entry);
    }

    [Fact]
    public void LargeFamilyRetainsMembersAndReportsWholeFieldOmissions()
    {
        var entries = Enumerable.Range(0, 300).Select(index => Entry("/skills/" + index + new string('p', 1000))).ToImmutableArray();
        var projection = ChangesQueueBuilder.Build(Queue(entries), Token);
        var row = Assert.Single(projection.Rows);
        Assert.Equal(300, row.Entries.Count);
        Assert.Equal(0, projection.OmittedRows);
        foreach (var technical in new[] { false, true })
        {
            var text = ChangesQueueBuilder.Render(row, Token, technical, null);
            Assert.InRange(text.Length, 1, technical ? MaintenanceQueueFormatter.MaxDetailCharacters : MaintenanceQueueFormatter.MaxSummaryCharacters);
            Assert.Contains("Family fields omitted:", text);
            Assert.DoesNotContain("Family fields omitted: 0", text);
        }
    }

    [Fact]
    public void CancellationDuringEntryCaptureDoesNotPublishRows()
    {
        using var cancellation = new CancellationTokenSource();
        IEnumerable<MaintenanceQueueEntry> Entries()
        {
            yield return Entry("one") with { Issue = "installation-review" };
            cancellation.Cancel();
            yield return Entry("two");
        }
        Assert.Throws<OperationCanceledException>(() => ChangesQueueBuilder.ProjectRows(Entries(), cancellation.Token));
    }

    private static MaintenanceQueue Queue(ImmutableArray<MaintenanceQueueEntry> entries) => new(DateTimeOffset.UnixEpoch, false, [], entries);
    private static MaintenanceQueueEntry Entry(string path)
    {
        var subject = OperationSubject.From(CopiesPresentationTests.Copy(Path.GetFullPath(path)));
        return new("copy:" + subject.InstallationId, "NameCollision", "demo", subject, [subject.Path],
            "review-required", "same name", "review-cleanup", false, false);
    }
}
