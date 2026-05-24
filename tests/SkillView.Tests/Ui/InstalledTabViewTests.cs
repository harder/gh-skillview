using System.Collections.Immutable;
using SkillView.Inventory.Models;
using SkillView.Ui.Tabs;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class InstalledTabViewTests
{
    [Fact]
    public void ResolveWidthForLayout_IgnoresUnstableFrameWidthUntilViewportIsReady()
    {
        var width = InstalledTabView.ResolveWidthForLayoutForTests(
            viewportWidth: 0,
            frameWidth: 96,
            fallbackWidth: 70);

        Assert.Equal(70, width);
    }

    [Fact]
    public void ResolveWidthForLayout_FallsBackWhenNoRealWidthIsAvailable()
    {
        var width = InstalledTabView.ResolveWidthForLayoutForTests(
            viewportWidth: 0,
            frameWidth: 0,
            fallbackWidth: 70);

        Assert.Equal(70, width);
    }

    [Fact]
    public void LoadSeeded_KeepsAgentsColumnCompactWithoutExpandingTheLastColumn()
    {
        var view = CreateSubject();

        view.LoadSeeded(SnapshotWithSkill(
            name: "frontend-design",
            packageSource: "owner/repo",
            agentIds: ["claude", "gemini", "copilot"]));

        var table = Assert.IsType<TableView>(view.SubViews.Single(child => child is TableView));
        var agentsIndex = Array.IndexOf(table.Table!.ColumnNames, "Agents");
        var agentsStyle = table.Style.GetOrCreateColumnStyle(agentsIndex);

        Assert.False(table.Style.ExpandLastColumn);
        Assert.Equal(5, agentsStyle.MinWidth);
        Assert.Equal(5, agentsStyle.MaxWidth);
    }

    [Fact]
    public void LoadSeeded_ShowsARightEdgeLineForTheInstalledTable()
    {
        var view = CreateSubject();

        view.LoadSeeded(SnapshotWithSkill(
            name: "frontend-design",
            packageSource: "owner/repo",
            agentIds: ["claude", "gemini"]));

        var table = Assert.IsType<TableView>(view.SubViews.Single(child => child is TableView));

        Assert.True(table.Style.ShowVerticalCellLineForLastColumn);
    }

    [Fact]
    public void LoadSeeded_AssignsRemainingLeftPaneWidthToNameAndPackageColumns()
    {
        var view = CreateSubject();

        view.LoadSeeded(SnapshotWithSkill(
            name: "workers-best-practices",
            packageSource: "cloudflare/skills",
            agentIds: ["claude", "gemini"]));

        var table = Assert.IsType<TableView>(view.SubViews.Single(child => child is TableView));
        var nameIndex = Array.IndexOf(table.Table!.ColumnNames, "Name");
        var packageIndex = Array.IndexOf(table.Table.ColumnNames, "Package");
        var nameStyle = table.Style.GetOrCreateColumnStyle(nameIndex);
        var packageStyle = table.Style.GetOrCreateColumnStyle(packageIndex);

        Assert.Equal(nameStyle.MaxWidth, nameStyle.MinWidth);
        Assert.Equal(packageStyle.MaxWidth, packageStyle.MinWidth);
        Assert.True(nameStyle.MaxWidth > 8);
        Assert.True(packageStyle.MaxWidth > 8);
    }

    [Fact]
    public void LoadSeeded_RequestsMultipleDeferredUiPassesToStabilizeInitialColumnWidths()
    {
        var deferredPasses = 0;
        var view = new InstalledTabView(
            runOnUi: action =>
            {
                deferredPasses++;
                action();
                return Task.CompletedTask;
            },
            snapshotLoader: static () => Task.FromResult(InventorySnapshot.Empty),
            onRemove: static (_, _) => { },
            onLeaveTab: static () => { },
            onGoToSearch: static () => { });

        view.LoadSeeded(SnapshotWithSkill(
            name: "frontend-design",
            packageSource: "owner/repo",
            agentIds: ["claude", "gemini"]));

        Assert.Equal(3, deferredPasses);
    }

    [Fact]
    public void LoadSeeded_RestoresFilterFocusAfterRefresh()
    {
        var view = CreateSubject();
        view.LoadSeeded(SnapshotWithSkill(
            name: "frontend-design",
            packageSource: "owner/repo",
            agentIds: ["claude"]));

        view.FocusFilterForTests();
        view.LoadSeeded(SnapshotWithSkill(
            name: "frontend-design",
            packageSource: "owner/repo",
            agentIds: ["claude"]));

        Assert.True(view.FilterHasFocusForTests);
        Assert.False(view.TableHasFocusForTests);
    }

    [Fact]
    public void Constructor_ShowsInstalledFilterGuideAndCompactLegend()
    {
        var view = CreateSubject();

        var labels = view.SubViews
            .OfType<Label>()
            .Select(label => label.Text.ToString())
            .ToArray();

        Assert.Contains(labels, text => text.Contains("name", StringComparison.OrdinalIgnoreCase)
            && text.Contains("agent", StringComparison.OrdinalIgnoreCase)
            && text.Contains("package", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(labels, text => text.Contains("USR", StringComparison.Ordinal)
            && text.Contains("PRJ", StringComparison.Ordinal)
            && text.Contains("OK", StringComparison.Ordinal)
            && text.Contains("REV", StringComparison.Ordinal));
    }

    private static InstalledTabView CreateSubject() => new(
        runOnUi: action =>
        {
            action();
            return Task.CompletedTask;
        },
        snapshotLoader: static () => Task.FromResult(InventorySnapshot.Empty),
        onRemove: static (_, _) => { },
        onLeaveTab: static () => { },
        onGoToSearch: static () => { });

    private static InventorySnapshot SnapshotWithSkill(
        string name,
        string? packageSource,
        params string[] agentIds) => InventorySnapshot.Empty with
    {
        Skills =
        [
            new InstalledSkill
            {
                Name = name,
                ResolvedPath = $"/skills/{name}",
                ScanRoot = "/skills",
                Scope = Scope.User,
                Agents = agentIds
                    .Select(agentId => new AgentMembership(agentId, $"/skills/{name}", IsSymlink: false))
                    .ToImmutableArray(),
                FrontMatter = SkillFrontMatter.Empty,
                Validity = ValidityState.Valid,
                Provenance = Provenance.FsScan,
                Ignored = false,
                IsSymlinked = false,
                InstalledAt = null,
                Package = packageSource is null
                    ? null
                    : new SkillPackage(
                        Source: packageSource,
                        SourceType: "git",
                        SourceUrl: $"https://example.test/{packageSource}",
                        InstalledAt: null,
                        UpdatedAt: null),
            },
        ],
    };
}
