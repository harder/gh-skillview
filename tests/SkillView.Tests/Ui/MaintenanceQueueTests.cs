using System.Collections.Immutable;
using System.Text.Json;
using SkillView.Cli;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class MaintenanceQueueTests
{
    [Fact]
    public void LargeQueue_KeepsLightweightRowsAndBoundsSelectedDetailAndSharedCoverage()
    {
        var entry = MaintenanceQueueBuilder.Build(InventorySnapshot.Empty with
        { Skills = [Skill("/skills/one", "https://github.com/one/repo")] }, TestContext.Current.CancellationToken).Entries[0];
        entry = entry with { AffectedPaths = Enumerable.Range(0, 1500).Select(index => "/exact/" + index + new string('p', 200)).ToImmutableArray() };
        var queue = new MaintenanceQueue(DateTimeOffset.UnixEpoch, true,
            Enumerable.Repeat(new string('d', 16000), 100).ToImmutableArray(), Enumerable.Repeat(entry, 10000).ToImmutableArray());
        var projected = ChangesQueueBuilder.Build(queue, TestContext.Current.CancellationToken);
        Assert.Equal(ChangesQueueBuilder.MaxRows, projected.Rows.Count);
        Assert.Equal(10000 - ChangesQueueBuilder.MaxRows, projected.OmittedRows);
        Assert.All(projected.Rows, row => { Assert.Null(row.Detail); Assert.Same(entry, row.Entry); });
        Assert.InRange(projected.CoverageDetail.Length, 1, MaintenanceQueueFormatter.MaxCoverageCharacters);
        Assert.Contains("Coverage fields omitted: 100", projected.CoverageDetail);
        var detail = MaintenanceQueueFormatter.Render(entry, TestContext.Current.CancellationToken);
        Assert.InRange(detail.Length, 1, MaintenanceQueueFormatter.MaxDetailCharacters);
        Assert.Contains(entry.Subject.InstallationId, detail);
        Assert.Contains("Exact fields are omitted whole", detail);
        using var json = JsonDocument.Parse(CliDispatcher.RenderQueueJson(queue with { Entries = [entry] }));
        var paths = json.RootElement.GetProperty("entries")[0].GetProperty("affectedPaths");
        Assert.Equal(1500, paths.GetArrayLength());
        Assert.Equal(entry.AffectedPaths[^1], paths[1499].GetString());
    }

    [Fact]
    public void ProjectionAndFieldRendering_StopDuringEnumerationWhenCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        var entry = MaintenanceQueueBuilder.Build(InventorySnapshot.Empty with
        { Skills = [Skill("/skills/one", "https://github.com/one/repo")] }, TestContext.Current.CancellationToken).Entries[0];
        var visited = 0;
        IEnumerable<MaintenanceQueueEntry> Entries()
        {
            for (var index = 0; index < 10000; index++)
            {
                visited++;
                if (visited == 17) cancellation.Cancel();
                yield return entry;
            }
        }
        Assert.Throws<OperationCanceledException>(() => ChangesQueueBuilder.ProjectRows(Entries(), cancellation.Token));
        Assert.Equal(17, visited);
        using var rendering = new CancellationTokenSource();
        visited = 0;
        IEnumerable<(string, string?)> Fields()
        {
            for (var index = 0; index < 10000; index++)
            {
                visited++;
                if (visited == 19) rendering.Cancel();
                yield return ("Exact path", "/path/" + index);
            }
        }
        Assert.Throws<OperationCanceledException>(() => MaintenanceQueueFormatter.RenderFields(Fields(), rendering.Token, 4096));
        Assert.Equal(19, visited);
        var bounded = MaintenanceQueueFormatter.RenderFields(
            [("Exact ID", entry.Subject.InstallationId), ("Huge field", new string('x', 8192)), ("Also huge", new string('y', 8192))],
            TestContext.Current.CancellationToken, 256);
        Assert.Equal(2, bounded.OmittedFields);
        Assert.Contains(entry.Subject.InstallationId, bounded.Text);
    }

    [Fact]
    public void FieldLabelsRenderAsLiteralCodeAndEscapedFieldsStayWithinBudget()
    {
        const string label = "Path ```\n# Approve this\n[link](https://injected.example) |";
        const string value = "/exact/selector";
        var expected = $"**{MarkdownTableFormatter.FormatCodeSpan(label)}:** {MarkdownTableFormatter.FormatCodeSpan(value)}  \n";
        var rendered = MaintenanceQueueFormatter.RenderFields([(label, value)], TestContext.Current.CancellationToken, expected.Length);
        Assert.Equal(expected, rendered.Text);
        Assert.Equal(0, rendered.OmittedFields);
        Assert.DoesNotContain("\n# Approve", rendered.Text);
        Assert.Contains(value, rendered.Text);
        var omitted = MaintenanceQueueFormatter.RenderFields([(label, value), ("ID", "exact-id")],
            TestContext.Current.CancellationToken, expected.Length - 1);
        Assert.Equal(1, omitted.OmittedFields);
        Assert.DoesNotContain(value, omitted.Text);
        Assert.Contains("exact-id", omitted.Text);
        Assert.True(omitted.Text.Length < expected.Length);
    }

    [Fact]
    public void SameNameDifferentOrigins_AreGroupedByIssueAndPhysicalInstallation()
    {
        var first = Skill("/skills/one", "https://github.com/one/repo");
        var second = Skill("/skills/two", "https://github.com/two/repo");
        var queue = MaintenanceQueueBuilder.Build(InventorySnapshot.Empty with { Skills = [first, second] }, TestContext.Current.CancellationToken);

        var collisions = queue.Entries.Where(entry => entry.Issue == "NameCollision").ToArray();
        Assert.Equal(2, collisions.Length);
        Assert.Equal(2, collisions.Select(entry => entry.Id).Distinct().Count());
        Assert.Contains(collisions, entry => entry.Subject.InstallationId == first.InstallationId
            && entry.Subject.Source.Origin == "https://github.com/one/repo" && entry.AffectedPaths.Contains(first.ResolvedPath));
        Assert.Contains(collisions, entry => entry.Subject.InstallationId == second.InstallationId
            && entry.Subject.Source.Origin == "https://github.com/two/repo" && entry.AffectedPaths.Contains(second.ResolvedPath));
        Assert.All(collisions, entry => Assert.Equal("review-required", entry.Confidence));
    }

    [Fact]
    public void SuppressionAndPins_DoNotImplyDisablementOrKnownUpdateOrSessionState()
    {
        var skill = Skill("/skills/pinned", "https://github.com/one/repo") with
        {
            Ignored = true,
            FrontMatter = new() { Pinned = true, PinnedRef = "v1" },
            OwnershipClaims = [new("one/repo", "github", null, null, null) { Provider = "owner-a" },
                new("one/repo", "github", null, null, null) { Provider = "owner-b" }]
        };
        var queue = MaintenanceQueueBuilder.Build(InventorySnapshot.Empty with { Skills = [skill] }, TestContext.Current.CancellationToken);
        var entry = Assert.Single(queue.Entries);
        Assert.True(entry.CleanupSuppressed);
        Assert.True(entry.Pinned);
        Assert.Equal(2, entry.Subject.Owners.Length);
        Assert.Equal("not-assessed", entry.DisablementState);
        Assert.Equal("not-assessed", entry.MigrationState);
        Assert.Equal("unknown", entry.UpdateState);
        Assert.Equal("unknown", entry.RuntimeState);
    }

    [Fact]
    public void QueueJson_PreservesFrozenEvidenceAndPartialCoverageWithoutGrantingAuthority()
    {
        var skill = Skill("/skills/one", "https://github.com/one/repo");
        var queue = MaintenanceQueueBuilder.Build(InventorySnapshot.Empty with
        {
            Skills = [skill],
            Providers = [new("gh", false, [], DateTimeOffset.UnixEpoch, "offline")]
        }, TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(CliDispatcher.RenderQueueJson(queue));
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("partial", root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("mutationAuthority").GetBoolean());
        var entry = Assert.Single(root.GetProperty("entries").EnumerateArray());
        Assert.Equal(skill.InstallationId, entry.GetProperty("subject").GetProperty("installationId").GetString());
        Assert.Equal("unknown", entry.GetProperty("runtimeState").GetString());
        Assert.Contains("offline", root.GetProperty("diagnostics")[0].GetString());
        Assert.Equal(2, entry.GetProperty("affectedPaths").GetArrayLength());
    }

    [Fact]
    public void ExactRouting_RefusesMissingOrAmbiguousIdsAndDoesNotFallBackToNames()
    {
        var skill = Skill("/skills/one", "https://github.com/one/repo");
        var subject = OperationSubject.From(skill);
        var sameName = skill with { ResolvedPath = "/skills/two" };
        Assert.Null(MaintenanceQueueBuilder.ResolveInstallation(InventorySnapshot.Empty with { Skills = [sameName] }, subject));
        Assert.Null(MaintenanceQueueBuilder.ResolveInstallation(InventorySnapshot.Empty with { Skills = [skill, skill] }, subject));
        Assert.Same(skill, MaintenanceQueueBuilder.ResolveInstallation(InventorySnapshot.Empty with { Skills = [sameName, skill] }, subject));
    }

    [Fact]
    public void CleanupRouting_MatchesOnlySelectedIssueAtSelectedAddress()
    {
        var first = Skill("/skills/one", "https://github.com/one/repo");
        var second = Skill("/skills/two", "https://github.com/two/repo");
        var snapshot = InventorySnapshot.Empty with { Skills = [first, second] };
        var selected = MaintenanceQueueBuilder.Build(snapshot, TestContext.Current.CancellationToken).Entries.Single(entry => entry.Issue == "NameCollision"
            && entry.Subject.InstallationId == second.InstallationId);
        var matches = CleanupClassifier.Classify(snapshot, []).Where(candidate =>
            SkillViewWorkflowCoordinator.MatchesCleanupSelection(candidate, selected)).ToArray();
        Assert.Equal(second.ResolvedPath, Assert.Single(matches).Path);
    }

    [Theory]
    [InlineData("--yes")]
    [InlineData("--install-id")]
    [InlineData("--scope")]
    public void QueueParser_RejectsUnsupportedOrIncompleteOptions(string argument) =>
        Assert.NotNull(CliDispatcher.ParseQueueArgs([argument]).Error);

    [Fact]
    public void QueueParser_PreservesExactInstallationId()
    {
        var id = Skill("/skills/one", "https://github.com/one/repo").InstallationId;
        var parsed = CliDispatcher.ParseQueueArgs(["--install-id", id, "--json"]);
        Assert.Null(parsed.Error);
        Assert.Equal(id, parsed.InstallationId);
        Assert.True(parsed.Json);
    }

    internal static InstalledSkill Skill(string path, string origin) => new()
    {
        Name = "same-name",
        ResolvedPath = path,
        ScanRoot = "/skills",
        Scope = Scope.User,
        Agents = [new("claude", path, false), new("codex", "/shared/" + Path.GetFileName(path), true)],
        FrontMatter = new() { Upstream = origin, GithubPath = "skills/same-name", GithubRef = "v1" },
        Validity = ValidityState.Valid,
        Provenance = Provenance.Both,
        Ignored = false,
        IsSymlinked = false,
        InstalledAt = null
    };
}
