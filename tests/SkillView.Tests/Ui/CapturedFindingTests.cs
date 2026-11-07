using System.Collections.Immutable;
using System.Text.Json;
using SkillView.Cli;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Preferences;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class CapturedFindingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    internal static InstalledSkill Copy(string path, string? reference = null, string? tree = null) =>
        InstalledOverviewTests.Copy(path, "demo", "fixture/source", reference) with
        { FrontMatter = InstalledOverviewTests.Copy(path, "demo", "fixture/source", reference).FrontMatter with { GithubTreeSha = tree } };
    private static InventorySnapshot Snapshot(params InstalledSkill[] skills) => InventorySnapshot.Empty with { Skills = [.. skills] };
    private static CapturedFinding[] Families(InventorySnapshot snapshot) => MaintenanceQueueBuilder.Build(snapshot, Token).Findings
        .Where(finding => finding.Navigation.Contains(FindingNavigation.InspectCopies)).ToArray();

    [Fact]
    public void ComparableRevisionsGroupOnceRetainAllExposuresAndNeverClaimAnUpgrade()
    {
        var first = Copy("/fixtures/alpha/demo", "refs/tags/v1") with
        { Agents = [new("codex", "/fixtures/links/demo", true, Scope.User), new("claude", "/fixtures/alpha/demo", false, Scope.Project, "/fixtures/alpha")] };
        var second = Copy("/fixtures/beta/demo", "refs/tags/v2");
        var snapshot = Snapshot(first, second);
        var queue = MaintenanceQueueBuilder.Build(snapshot, Token);
        var family = Assert.Single(Families(snapshot));
        Assert.Equal("recorded-revisions", family.Kind);
        Assert.Equal(FindingClass.Review, family.Classification);
        Assert.Equal(2, Assert.IsType<FindingCopies>(family.Subject).Copies.Length);
        Assert.Equal(2, ((FindingCopies)family.Subject).Copies[0].Exposures.Length);
        var row = Assert.Single(ChangesQueueBuilder.Build(queue, Token).Rows);
        var text = ChangesQueueBuilder.Render(row, Token, true, null);
        Assert.Contains("**Enter / Right:** expand the exact captured members here.", text);
        Assert.Contains("Select a child for inline **Compare files**", text);
        Assert.Contains("use **u** for its exact saved update review. Each copy is reviewed separately.", text);
        Assert.Contains("Compare files", text);
        Assert.Contains("neither is assumed newer", text); Assert.Contains("/fixtures/links/demo", text);
        Assert.Contains("Updates not checked", ChangesQueueBuilder.Build(queue, Token).Counts);
        Assert.DoesNotContain(queue.Findings, finding => finding.Classification == FindingClass.Update);
        var evidence = InstalledIssueEvidence.Capture(snapshot, 7, queue);
        Assert.All(snapshot.Skills, skill => Assert.Contains("recorded-revisions", evidence.Findings[(skill.InstallationId, skill.ResolvedPath)]));
        Assert.False(evidence.IsCurrent(snapshot with { }, 7)); Assert.False(evidence.IsCurrent(snapshot, 8));
    }

    [Theory]
    [InlineData("authored")]
    [InlineData("missing")]
    [InlineData("tree-versus-ref")]
    [InlineData("conflicting")]
    public void UnknownOrSeparateRevisionDomainsNeverCreateAlignmentFinding(string mode)
    {
        var first = Copy("/fixtures/one/demo"); var second = Copy("/fixtures/two/demo");
        if (mode == "authored")
        { first = first with { FrontMatter = first.FrontMatter with { Version = "v1" } }; second = second with { FrontMatter = second.FrontMatter with { GithubRef = "v2" } }; }
        if (mode == "tree-versus-ref")
        { first = first with { FrontMatter = first.FrontMatter with { GithubTreeSha = "v1" } }; second = second with { FrontMatter = second.FrontMatter with { GithubRef = "v2" } }; }
        if (mode == "conflicting")
        { first = first with { FrontMatter = first.FrontMatter with { GithubRef = "v1" }, MetadataConflicts = ["conflicting source/ref"] }; second = Copy(second.ResolvedPath, "v2"); }
        Assert.DoesNotContain(Families(Snapshot(first, second)), finding => finding.Kind == "recorded-revisions");
    }

    [Theory]
    [InlineData(null, "Review", "unknown or incomplete")]
    [InlineData("refs/tags/v1", "Information", "matching comparable")]
    public void SameSourceIndependentCopiesAlwaysRemainVisibleWithoutInventingDuplicates(string? reference, string classification, string wording)
    {
        var snapshot = Snapshot(Copy("/fixtures/one/demo", reference), Copy("/fixtures/two/demo", reference));
        var family = Assert.Single(Families(snapshot));
        Assert.Equal("same-source-copies", family.Kind); Assert.Equal(classification, family.Classification.ToString());
        Assert.Contains(wording, family.Explanation); Assert.Equal(2, ((FindingCopies)family.Subject).Copies.Length);
        var rows = ChangesQueueBuilder.Build(MaintenanceQueueBuilder.Build(snapshot, Token), Token).Rows;
        var rendered = ChangesQueueBuilder.Render(Assert.Single(rows), Token, false, null);
        Assert.Contains("Compare files", rendered); Assert.Contains("same source", rows[0].Title);
        Assert.DoesNotContain("exact duplicate", rendered, StringComparison.OrdinalIgnoreCase);
        var evidence = InstalledIssueEvidence.Capture(snapshot, 1, MaintenanceQueueBuilder.Build(snapshot, Token));
        Assert.All(snapshot.Skills, skill => Assert.Contains("same-source-copies", evidence.Findings[(skill.InstallationId, skill.ResolvedPath)]));
    }

    [Fact]
    public void EquivalentTreeDomainsRemainComparableWithAnAdditionalOwnerClaim()
    {
        var first = Copy("/fixtures/one/demo", tree: new('a', 40));
        var second = Copy("/fixtures/two/demo", tree: new('b', 40)) with
        { OwnershipClaims = [new("fixture/source", "github", null, null, null) { HashKind = "recorded-git-tree", ContentHash = new('b', 40), SkillPath = "skills/demo" }] };
        Assert.Equal("recorded-revisions", Assert.Single(Families(Snapshot(first, second))).Kind);
    }

    [Fact]
    public void InvalidTreesStayUnknownAndHexCaseDoesNotInventDifferentRevisions()
    {
        var unknown = Assert.Single(Families(Snapshot(Copy("/fixtures/one/demo", tree: "not-a-tree"), Copy("/fixtures/two/demo", tree: "also-not-a-tree"))));
        Assert.Equal("same-source-copies", unknown.Kind); Assert.Equal(FindingClass.Review, unknown.Classification);
        var matching = Assert.Single(Families(Snapshot(Copy("/fixtures/one/demo", tree: new('A', 40)), Copy("/fixtures/two/demo", tree: new('a', 40)))));
        Assert.Equal("same-source-copies", matching.Kind); Assert.Equal(FindingClass.Information, matching.Classification);
    }

    [Fact]
    public void InstalledTreeDifferenceDoesNotInventARefDifference()
    {
        var snapshot = Snapshot(Copy("/fixtures/one/demo", tree: new('a', 40)), Copy("/fixtures/two/demo", tree: new('b', 40)));
        var overview = InstalledOverview.Build(snapshot, Token);
        var row = Assert.Single(overview.View(_ => true, "name", new HashSet<string>(), null, Token).Rows);
        Assert.Contains("recorded revisions differ", row.Summary);
        Assert.DoesNotContain("refs differ", row.Summary);
    }

    [Fact]
    public void UnsupportedGhSourceTypeIsProtectedCoverageNotAnUnmatchedReview()
    {
        using var fixture = new RegistryCounterexample();
        var finding = Assert.Single(MaintenanceQueueBuilder.Build(InventorySnapshot.Empty with
        { OwnershipRecords = [fixture.Record with { SourceType = "unknown" }] }, Token).Findings);
        Assert.Equal(FindingClass.Coverage, finding.Classification);
        Assert.Equal("unknown-gh-record", finding.Kind);
        Assert.Equal([FindingNavigation.InspectRegistry], finding.Navigation);
    }

    [Fact]
    public void InstallationMethodsAreInformationAndSharedExposuresAreOnePhysicalCopy()
    {
        var first = Copy("/fixtures/shared/demo", "v1") with { Agents = [new("codex", "/fixtures/codex/demo", true), new("claude", "/fixtures/claude/demo", true)] };
        Assert.Empty(Families(Snapshot(first)));
        var second = Copy("/fixtures/standalone/demo", "v1");
        var family = Assert.Single(Families(Snapshot(first, second)));
        Assert.Equal("installation-methods", family.Kind); Assert.Equal(FindingClass.Information, family.Classification);
        Assert.Equal(2, ((FindingCopies)family.Subject).Copies.Length);
        Assert.Contains("not an error", family.Explanation);
    }

    [Fact]
    public void DifferentAndUnknownSourcesStaySeparateWithConflictsAndCoverageRetained()
    {
        var first = Copy("/fixtures/one/demo", "v1");
        var second = Copy("/fixtures/two/demo", "v2") with { MetadataConflicts = ["source mismatch"], OwnershipDiagnostics = ["owner read incomplete"] };
        var snapshot = Snapshot(first, second) with { ConfiguredRootsComplete = false };
        var queue = MaintenanceQueueBuilder.Build(snapshot, Token);
        var family = Assert.Single(Families(snapshot));
        Assert.Equal("source-differences", family.Kind); Assert.True(family.Partial);
        Assert.Contains(queue.Findings, finding => finding.Kind == "MetadataConflict" && finding.Classification == FindingClass.Problem);
        Assert.Equal(2, ((FindingCopies)family.Subject).Copies.Length);
    }

    [Fact]
    public void UnmatchedRecordCounterexampleDoesNotInventAPathOrAStaleConclusion()
    {
        // Two real independent custom copies can exist outside configured roots.
        using var fixture = new RegistryCounterexample();
        var snapshot = InventorySnapshot.Empty with { OwnershipRecords = [fixture.Record] };
        var queue = MaintenanceQueueBuilder.Build(snapshot, Token);
        var finding = Assert.Single(queue.Findings);
        Assert.Equal("unmatched-gh-record", finding.Kind); Assert.Equal(FindingClass.Review, finding.Classification);
        Assert.IsType<FindingRegistry>(finding.Subject); Assert.Empty(finding.Entries);
        Assert.Equal([FindingNavigation.InspectRegistry], finding.Navigation);
        Assert.Empty(queue.Entries); Assert.Empty(snapshot.Skills); Assert.False(snapshot.IsPartial);
        var row = Assert.Single(ChangesQueueBuilder.Build(queue, Token).Rows);
        var detail = ChangesQueueBuilder.Render(row, Token, true, null);
        Assert.Contains("Not matched to any observed copy in known locations", detail);
        Assert.Contains("custom-folder copies may exist", detail); Assert.Contains("--scan-root ROOT", detail);
        Assert.Contains(fixture.Record.Selector!, detail); Assert.Contains(fixture.Record.EntryFingerprint!, detail);
        Assert.DoesNotContain("review-cleanup", detail); Assert.True(Directory.Exists(fixture.CustomA)); Assert.True(Directory.Exists(fixture.CustomB));
        using var json = JsonDocument.Parse(CliDispatcher.RenderQueueJson(queue));
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("entries").GetArrayLength());
        Assert.False(json.RootElement.TryGetProperty("findings", out _));
        var scans = snapshot with { Skills = [Copy(fixture.CustomA), Copy(fixture.CustomB)] };
        Assert.Contains(MaintenanceQueueBuilder.Build(scans, Token).Findings, f => f.Kind == "unmatched-gh-record");
    }

    [Fact]
    public void AttachedMissingSkillSubjectStaysPhysicalWhileUnsupportedRecordsStayUnknown()
    {
        using var fixture = new RegistryCounterexample();
        var skill = Copy(fixture.CustomA) with { Validity = ValidityState.MissingSkillMd, OwnershipClaims = [fixture.Record] };
        var queue = MaintenanceQueueBuilder.Build(Snapshot(skill) with { OwnershipRecords = [fixture.Record] }, Token);
        Assert.DoesNotContain(queue.Findings, finding => finding.Subject is FindingRegistry);
        Assert.Contains(queue.Findings, finding => finding.Kind == "Malformed" && finding.Subject is FindingCopies);
        var unsupported = MaintenanceQueueBuilder.Build(InventorySnapshot.Empty with { OwnershipRecords = [fixture.Record with { SchemaVersion = 99, ReadOnly = true }] }, Token);
        var unknown = Assert.Single(unsupported.Findings);
        Assert.Equal(FindingClass.Coverage, unknown.Classification); Assert.True(unknown.Partial);
        Assert.Equal([FindingNavigation.InspectRegistry], unknown.Navigation);
    }

    [Fact]
    public void MaliciousRegistryAndLargeFamilyRenderBoundedLiteralEvidenceWithoutLosingSubjects()
    {
        using var fixture = new RegistryCounterexample();
        var malicious = fixture.Record with { Selector = "bad\u001b[31m\n[open](https://bad.example)`", Source = new('s', 40000) };
        var queue = MaintenanceQueueBuilder.Build(InventorySnapshot.Empty with { OwnershipRecords = [malicious] }, Token);
        var row = Assert.Single(ChangesQueueBuilder.Build(queue, Token).Rows);
        Assert.DoesNotContain('\u001b', row.Title); Assert.DoesNotContain('\n', row.Title);
        foreach (var technical in new[] { false, true })
        {
            var text = ChangesQueueBuilder.Render(row, Token, technical, null);
            Assert.DoesNotContain('\u001b', text); Assert.Contains("whole evidence fields omitted", text);
            Assert.InRange(text.Length, 1, technical ? MaintenanceQueueFormatter.MaxDetailCharacters : MaintenanceQueueFormatter.MaxSummaryCharacters);
            Assert.Contains(MarkdownTableFormatter.FormatCodeSpan(malicious.Selector!), text);
        }
        var copies = Enumerable.Range(0, 300).Select(i => Copy("/fixtures/" + i + new string('x', 2000) + "/demo", i % 2 == 0 ? "v1" : "v2")).ToArray();
        var familyRow = Assert.Single(ChangesQueueBuilder.Build(MaintenanceQueueBuilder.Build(Snapshot(copies), Token), Token).Rows);
        Assert.Equal(300, ((FindingCopies)familyRow.Finding!.Subject).Copies.Length);
        Assert.InRange(ChangesQueueBuilder.Render(familyRow, Token, true, null).Length, 1, MaintenanceQueueFormatter.MaxDetailCharacters);
    }

    [Fact]
    public void CancellationPreventsFindingPublication()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => MaintenanceQueueBuilder.Build(Snapshot(Copy("/fixtures/one"), Copy("/fixtures/two")), canceled.Token));
    }

    private sealed class RegistryCounterexample : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("skillview-record-counterexample-");
        internal string CustomA => Path.Combine(_root.FullName, "work-a", "custom", "demo");
        internal string CustomB => Path.Combine(_root.FullName, "work-b", "custom", "demo");
        internal SkillPackage Record => new("fixture/source", "github", "https://github.com/fixture/source", null, null)
        {
            Provider = "gh-vercel-global",
            SchemaVersion = 3,
            Selector = "demo",
            ContextRoot = _root.FullName,
            LockFilePath = Path.Combine(_root.FullName, ".agents", ".skill-lock.json"),
            EntryFingerprint = new('a', 64),
            RecordFingerprint = new('b', 64),
            IsExclusiveOwner = false
        };
        internal RegistryCounterexample()
        { foreach (var path in new[] { CustomA, CustomB }) { Directory.CreateDirectory(path); File.WriteAllText(Path.Combine(path, "SKILL.md"), "---\nname: demo\ndescription: Fixture\n---\n"); } }
        public void Dispose() => _root.Delete(true);
    }
}
