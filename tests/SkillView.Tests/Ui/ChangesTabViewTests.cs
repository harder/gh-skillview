using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Ui.Tabs;
using SkillView.Maintenance;
using Terminal.Gui.Input;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class ChangesTabViewTests
{
    [Fact]
    public async Task Enter_RoutesExactSelectedIssueAndPhysicalInstallation()
    {
        var first = MaintenanceQueueTests.Skill("/skills/one", "https://github.com/one/repo");
        var second = MaintenanceQueueTests.Skill("/skills/two", "https://github.com/two/repo");
        MaintenanceQueueEntry? activated = null;
        using var view = new ChangesTabView(action => { action(); return Task.CompletedTask; },
            _ => Task.FromResult(InventorySnapshot.Empty with { Skills = [first, second] }),
            static () => throw new InvalidOperationException("Untargeted updates must not be used."),
            static () => throw new InvalidOperationException("Untargeted cleanup must not be used."),
            static () => { }, static () => { }, onActivateEntry: entry => activated = entry);
        await view.LoadAsync();
        var table = Assert.IsType<TableView>(view.SubViews.Single(child => child is TableView));
        table.SetSelection(0, 1, false); // NameCollision at the second physical address.
        table.NewKeyDownEvent(Key.Enter);
        Assert.NotNull(activated);
        Assert.Equal("NameCollision", activated.Issue);
        Assert.Equal(second.InstallationId, activated.Subject.InstallationId);
        Assert.Equal(second.ResolvedPath, activated.Cleanup!.Path);
    }

    [Fact]
    public async Task CancelBeforeQueuedUiDispatch_DoesNotPublishRows()
    {
        Action? queued = null;
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var view = new ChangesTabView(action => { queued = action; return dispatched.Task; },
            _ => Task.FromResult(SnapshotWithMalformedSkill()), static () => { }, static () => { },
            static () => { }, static () => { });
        var loading = view.LoadAsync();
        Assert.NotNull(queued);
        view.CancelPendingLoad();
        queued();
        dispatched.SetResult();
        await loading;
        Assert.Equal(0, view.RowCountForTests);
    }

    [Fact]
    public void Load_ShowsDetailForTheFirstPendingItemWithoutNeedingEnter()
    {
        var view = new ChangesTabView(
            runOnUi: action =>
            {
                action();
                return Task.CompletedTask;
            },
            snapshotLoader: static _ => Task.FromResult(InventorySnapshot.Empty),
            onActivateUpdates: static () => { },
            onActivateCleanup: static () => { },
            onActivateDoctor: static () => { },
            onLeaveTab: static () => { });

        view.Load(
            updates: [],
            cleanup: ["SYM  stale-link"],
            diagnostics: [],
            summary: "Needs review");

        var detail = Assert.IsType<Markdown>(view.SubViews.Single(child => child is Markdown));

        Assert.Contains("## Cleanup", detail.Text.ToString());
        Assert.Contains("SYM  stale-link", detail.Text.ToString());
    }

    [Fact]
    public void Load_AssignsExplicitWidthToTheTitleColumn()
    {
        var view = new ChangesTabView(
            runOnUi: action =>
            {
                action();
                return Task.CompletedTask;
            },
            snapshotLoader: static _ => Task.FromResult(InventorySnapshot.Empty),
            onActivateUpdates: static () => { },
            onActivateCleanup: static () => { },
            onActivateDoctor: static () => { },
            onLeaveTab: static () => { });

        view.Load(
            updates: [],
            cleanup: ["malformed  codex-primary-runtime"],
            diagnostics: [],
            summary: "Needs review");

        var table = Assert.IsType<TableView>(view.SubViews.Single(child => child is TableView));
        var kindStyle = table.Style.GetOrCreateColumnStyle(0);
        var titleStyle = table.Style.GetOrCreateColumnStyle(1);

        Assert.False(table.Style.ExpandLastColumn);
        Assert.True(titleStyle.MinWidth > kindStyle.MinWidth);
        Assert.Equal(titleStyle.MinWidth, titleStyle.MaxWidth);
    }

    [Fact]
    public async Task LoadAsync_ShowsCleanupCandidateDetailOnSelectionWithoutOpeningCleanup()
    {
        var view = new ChangesTabView(
            runOnUi: action =>
            {
                action();
                return Task.CompletedTask;
            },
            snapshotLoader: static _ => Task.FromResult(SnapshotWithMalformedSkill()),
            onActivateUpdates: static () => { },
            onActivateCleanup: static () => { },
            onActivateDoctor: static () => { },
            onLeaveTab: static () => { });

        await view.LoadAsync();

        var detail = Assert.IsType<Markdown>(view.SubViews.Single(child => child is Markdown));
        var rendered = detail.Text.ToString();

        Assert.Contains("## Selected", rendered);
        Assert.Contains("/skills/codex-primary-runtime", rendered);
        Assert.Contains("This directory has no SKILL.md file", rendered);
        Assert.DoesNotContain("validity=MissingSkillMd", rendered);
    }

    [Fact]
    public async Task LoadAsync_LabelsUnknownUpdateRowsAsObservations()
    {
        var view = new ChangesTabView(
            runOnUi: action =>
            {
                action();
                return Task.CompletedTask;
            },
            snapshotLoader: static _ => Task.FromResult(SnapshotWithMalformedSkill()),
            onActivateUpdates: static () => { },
            onActivateCleanup: static () => { },
            onActivateDoctor: static () => { },
            onLeaveTab: static () => { });

        await view.LoadAsync();

        Assert.Contains("observations", view.StatusTextForTests, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("availability and sessions unknown", view.StatusTextForTests, StringComparison.OrdinalIgnoreCase);
    }

    private static InventorySnapshot SnapshotWithMalformedSkill() => InventorySnapshot.Empty with
    {
        ScannedRoots = ImmutableArray.Create(new ScanRoot("/skills", Scope.User, "copilot")),
        Skills = ImmutableArray.Create(new InstalledSkill
        {
            Name = "codex-primary-runtime",
            ResolvedPath = "/skills/codex-primary-runtime",
            ScanRoot = "/skills",
            Scope = Scope.User,
            Agents = ImmutableArray.Create(new AgentMembership("copilot", "/skills/codex-primary-runtime", false)),
            FrontMatter = SkillFrontMatter.Empty,
            Validity = ValidityState.MissingSkillMd,
            Provenance = Provenance.FsScan,
            Ignored = false,
            IsSymlinked = false,
            InstalledAt = null,
            Package = new SkillPackage(
                Source: "TCGplayer/guild-ai-tools-and-notes",
                SourceType: "github",
                SourceUrl: "https://github.com/TCGplayer/guild-ai-tools-and-notes.git",
                InstalledAt: null,
                UpdatedAt: null),
        }),
    };
}
