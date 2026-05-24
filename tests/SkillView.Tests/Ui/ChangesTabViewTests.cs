using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Ui.Tabs;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class ChangesTabViewTests
{
    [Fact]
    public void Load_ShowsDetailForTheFirstPendingItemWithoutNeedingEnter()
    {
        var view = new ChangesTabView(
            runOnUi: action =>
            {
                action();
                return Task.CompletedTask;
            },
            snapshotLoader: static () => Task.FromResult(InventorySnapshot.Empty),
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
            snapshotLoader: static () => Task.FromResult(InventorySnapshot.Empty),
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
            snapshotLoader: static () => Task.FromResult(SnapshotWithMalformedSkill()),
            onActivateUpdates: static () => { },
            onActivateCleanup: static () => { },
            onActivateDoctor: static () => { },
            onLeaveTab: static () => { });

        await view.LoadAsync();

        var detail = Assert.IsType<Markdown>(view.SubViews.Single(child => child is Markdown));
        var rendered = detail.Text.ToString();

        Assert.Contains("## Selected", rendered);
        Assert.Contains("/skills/codex-primary-runtime", rendered);
        Assert.Contains("validity=MissingSkillMd", rendered);
    }

    [Fact]
    public async Task LoadAsync_CleanupOnlyQueue_UsesCleanupCandidateCopyInsteadOfGenericPendingCopy()
    {
        var view = new ChangesTabView(
            runOnUi: action =>
            {
                action();
                return Task.CompletedTask;
            },
            snapshotLoader: static () => Task.FromResult(SnapshotWithMalformedSkill()),
            onActivateUpdates: static () => { },
            onActivateCleanup: static () => { },
            onActivateDoctor: static () => { },
            onLeaveTab: static () => { });

        await view.LoadAsync();

        Assert.Contains("cleanup candidate", view.StatusTextForTests, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pending", view.StatusTextForTests, StringComparison.OrdinalIgnoreCase);
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
