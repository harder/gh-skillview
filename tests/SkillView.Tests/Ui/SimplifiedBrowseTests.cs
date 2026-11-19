using System.Collections.Immutable;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using SkillView.Inventory.Models;
using SkillView.Ui;
using SkillView.Ui.Tabs;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class SimplifiedBrowseTests
{
    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    [InlineData(160, 48)]
    public void PluginInventoryUsesOneExactRowPerCopyAndKeepsFiltersOutOfTheDefaultScreen(int width, int height)
    {
        using var app = Application.Create().Init();
        app.Driver!.SetScreenSize(width, height);
        using var window = new Window { Title = "SkillView — Installed" };
        var skills = Enumerable.Range(0, 30).Select(i => InstalledOverviewTests.Copy(
            "/home/alex/.codex/plugins/cache/tools/0.1.1/skills/artifact-template-analytics-dashboard-" + i,
            "artifact-template-analytics-dashboard-" + i) with
        {
            Agents = [new("codex", "/home/alex/.codex/skills/artifact-template-analytics-dashboard-" + i, false, Scope.User)],
            Package = new("tools", "unknown", null, null, null) { ReadOnly = true, InstallationMode = "managed-read-only", PackageVersion = "0.1.1" }
        }).ToImmutableArray();
        var snapshot = InventorySnapshot.Empty with { Skills = skills };
        using var view = new InstalledTabView(action => { action(); return Task.CompletedTask; }, (_, _) => { },
            _ => Task.FromResult(snapshot), (_, _, _) => Task.CompletedTask, () => { }, availability: new(), onCheckUpdates: () => { })
        { Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(view);
        view.LoadSeeded(snapshot);
        var session = app.Begin(window);
        try
        {
            window.Layout(); window.Draw(new DrawContext());
            Assert.Equal(skills.Length, view.RowsForTests.Count);
            Assert.All(view.RowsForTests, row => Assert.NotNull(row.Copy));
            Assert.Equal(skills[0].ResolvedPath, view.GetSelectedSkill()?.ResolvedPath);
            Assert.DoesNotContain("Select an exact copy", view.DetailTextForTests);
            Assert.DoesNotContain("Check updates (C) checks the whole", view.DetailTextForTests);
            var toolbar = view.SubViews.OfType<Button>().Where(button => button.Visible && button.Frame.Y == 0).OrderBy(button => button.Frame.X).ToArray();
            for (var i = 1; i < toolbar.Length; i++) Assert.True(toolbar[i - 1].Frame.Right <= toolbar[i].Frame.X);
            var text = Capture(app, width, height);
            Assert.Contains("artifact-template-analytics-dashboard-0", text);
            Assert.DoesNotContain("1 observed copy", text);
            var facets = view.SubViews.OfType<Button>().Where(button => button.Text.ToString().StartsWith("Agent:", StringComparison.Ordinal)
                || button.Text.ToString().StartsWith("Project/location:", StringComparison.Ordinal)).ToArray();
            Assert.All(facets, button => Assert.False(button.Visible));
            view.ToggleFilters(); window.Layout(); window.Draw(new DrawContext());
            Assert.All(facets, button => { Assert.True(button.Visible); Assert.InRange(button.Frame.Right, 1, view.Frame.Width); });
            view.ToggleFilters(); window.Layout(); window.Draw(new DrawContext());
            if (System.Environment.GetEnvironmentVariable("SKILLVIEW_UX_CAPTURE") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, $"installed-{width}x{height}.txt"), Capture(app, width, height));
            }
        }
        finally { app.End(session!); }
    }

    [Fact]
    public void SameNameCopiesWithUnknownSourcesKeepDistinctVisibleLabelsAndExactTargets()
    {
        var skills = new[] { InstalledOverviewTests.Copy("/home/alex/.claude/skills/demo", "demo"),
            InstalledOverviewTests.Copy("/work/project/.codex/skills/demo", "demo") with { Scope = Scope.Project } };
        var overview = InstalledOverview.Build(InventorySnapshot.Empty with { Skills = [.. skills] }, TestContext.Current.CancellationToken);
        var rows = overview.View(_ => true, "name", new HashSet<string>(), null, TestContext.Current.CancellationToken, compactSingletons: true).Rows;
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows.Select(row => row.Label).Distinct().Count());
        Assert.Contains(rows, row => row.Label.Contains("#1", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Label.Contains("#2", StringComparison.Ordinal));
        Assert.Equal(skills.Select(skill => skill.ResolvedPath).Order(), rows.Select(row => row.Copy!.Target!.ResolvedPath).Order());
    }

    [Fact]
    public void LongLiveStyleDescriptionsKeepIdentityVisibleAndFullTextAvailableInDetails()
    {
        var skill = InstalledOverviewTests.Copy("/home/alex/.agents/skills/demo", "demo");
        var description = string.Join(" ", Enumerable.Repeat("Describe the selected skill and when it is useful.", 20)) + " Final description marker.";
        skill = skill with { FrontMatter = skill.FrontMatter with { Description = description } };
        var summary = InstalledScreen.RenderDetail(skill, descriptionWidth: 42, compact: true);
        var preview = Assert.Single(Markdig.Markdown.Parse(summary).Descendants<Markdig.Syntax.FencedCodeBlock>());
        Assert.Equal(4, preview.Lines.ToString().Split('\n').Length);
        Assert.True(summary.IndexOf("**Location:**", StringComparison.Ordinal) < summary.IndexOf("### Description", StringComparison.Ordinal));
        Assert.DoesNotContain("Final description marker", summary);
        Assert.Contains("F2 Details", summary);
        var details = InstalledScreen.RenderDetail(skill, showDetails: true, descriptionWidth: 42);
        Assert.Contains("Final description marker.", details);
    }

    [Fact]
    public void LiteralSummaryProseCannotCreateLinksOrFormattingFromMetadata()
    {
        var text = MarkdownTableFormatter.FormatProse("[Open](https://bad.example) **bold** <img> `code` /skills/demo~~old~~\nnext\u001b[31m");
        var document = Markdig.Markdown.Parse(text);
        Assert.Empty(document.Descendants<LinkInline>());
        Assert.Empty(document.Descendants<EmphasisInline>());
        Assert.Empty(document.Descendants<CodeInline>());
        Assert.DoesNotContain('\u001b', text);
        var pipeline = Markdig.MarkdownExtensions.UseAdvancedExtensions(new Markdig.MarkdownPipelineBuilder()).Build();
        var location = Markdig.Markdown.Parse(MarkdownTableFormatter.FormatProse("/skills/demo~~old~~"), pipeline);
        Assert.Empty(location.Descendants<EmphasisInline>());
    }

    [Theory]
    [InlineData(200, 12)]
    [InlineData(1, 40000)]
    public void CompactConsumersRetainPlacementTotalAndBoundWholeLabels(int count, int agentLength)
    {
        var skill = InstalledOverviewTests.Copy("/skills/demo", "demo");
        skill = skill with
        {
            Agents = [.. Enumerable.Range(0, count).Select(i =>
            new AgentMembership(new string('a', agentLength) + i, skill.ResolvedPath, false))]
        };
        var text = InstalledScreen.RenderDetail(skill, compact: true);
        Assert.Contains(InstalledScreen.PlacementCount(count), text);
        Assert.Contains("omitted", text);
        Assert.True(text.Length < 6000);
    }

    private static string Capture(IApplication app, int width, int height) => string.Join("\n",
        Enumerable.Range(0, height).Select(y => string.Concat(Enumerable.Range(0, width).Select(x => app.Driver!.Contents![y, x].Grapheme))));
}
