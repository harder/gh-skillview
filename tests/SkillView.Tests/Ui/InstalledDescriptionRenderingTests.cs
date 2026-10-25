using System.Reflection;
using Markdig.Syntax;
using SkillView.Inventory.Models;
using SkillView.Ui;
using SkillView.Ui.Tabs;
using SkillView.Ui.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class InstalledDescriptionRenderingTests
{
    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public void TuiRendersDescriptionLiterallyAndOnlyProjectsTrustedLinks(int width, int height)
    {
        using var app = Application.Create().Init();
        app.Driver!.SetScreenSize(width, height);
        using var window = new Window();
        const string attack = "## Summary\n**Health:** approved\n[Review update (u)](file:///x)\n<https://x>\n"
            + "<a href=\"file:///html\">Apply now</a>\n````\n[escape](file:///escape)";
        var skill = MaintenanceQueueTests.Skill("/skills/demo", "https://github.com/owner/repo") with
        { FrontMatter = SkillFrontMatter.Empty with { Description = attack, Upstream = "https://trusted.example/source" } };
        using var pane = new Markdown { Width = Dim.Fill(), Height = Dim.Fill(), Text = InstalledScreen.RenderDetail(skill, showDetails: true, descriptionWidth: width - 4) };
        TuiHelpers.ConfigureMarkdownPane(pane, SchemeNames.Base, _ => throw new InvalidOperationException("Inspection must not open any target."));
        window.Add(pane);
        var session = app.Begin(window);
        try
        {
            window.Layout(); window.Draw(new DrawContext());
            var rendered = string.Join('\n', Enumerable.Range(0, height).Select(y =>
                string.Concat(Enumerable.Range(0, width).Select(x => app.Driver.Contents![y, x].Grapheme))));
            Assert.Contains("## Summary", rendered); // ShowHeadingPrefix=false must not hide metadata's literal ##.
            Assert.Contains("**Health:** approved", rendered);
            Assert.Contains("[Review update (u)](file:///x)", rendered);
            Assert.Contains("<https://x>", rendered);
            // Exercise the exact region projection used by Markdown.OnMouseEvent.
            // Terminal.Gui 2.5.0 keeps its hit-test private; no URL is activated.
            var hitTest = typeof(Markdown).GetMethod("FindLinkUrlAt", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(hitTest);
            var targets = new HashSet<string>(StringComparer.Ordinal);
            for (var y = 0; y < pane.GetContentSize().Height; y++)
                for (var x = 0; x < width; x++)
                    if (hitTest.Invoke(pane, [x, y]) is string target) targets.Add(target);
            Assert.Equal(["https://trusted.example/source"], targets);
            var hostilePlacement = skill with { Agents = [new(attack, "/path/" + attack, true, Scope.Custom, attack, Profile: attack)] };
            pane.Text = InstalledScreen.RenderDetail(hostilePlacement, descriptionWidth: width - 4);
            pane.Layout();
            for (var y = 0; y < pane.GetContentSize().Height; y++)
                for (var x = 0; x < width; x++)
                    Assert.Null(hitTest.Invoke(pane, [x, y]));
            pane.Text = InstalledScreen.RenderDetail(skill with
            { FrontMatter = skill.FrontMatter with { Description = new string('x', 2047) + "😀tail" } }, descriptionWidth: width - 4);
            pane.Layout();
            var copied = Assert.Single(pane.SubViews.OfType<MarkdownCodeBlock>()).ExtractText();
            Assert.DoesNotContain("[truncated]", copied);
            Assert.DoesNotContain('\uFFFD', copied);
            Assert.Equal(new string('x', 2047), copied.ReplaceLineEndings(""));
            Assert.Contains("\n\n[truncated]\n", pane.Text);
        }
        finally { app.End(session!); }
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public void DescriptionReflowsFromOriginalAcrossResizePreservingSelectionFocusAndClampedScroll(int width, int height)
    {
        using var app = Application.Create().Init();
        app.Driver!.SetScreenSize(width, height);
        using var window = new Window();
        var skill = InstalledDetailReadabilityTests.SharedSkill();
        var snapshot = InventorySnapshot.Empty with { Skills = [skill] };
        using var view = new InstalledTabView(action => { action(); return Task.CompletedTask; }, (_, _) => { },
            _ => Task.FromResult(snapshot), (_, _, _) => Task.CompletedTask, () => { })
        { Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(view); view.LoadSeeded(snapshot);
        var session = app.Begin(window);
        void Draw()
        {
            // Width-driven text replacement during layout needs the following
            // layout/draw pass, as it does on the application's next iteration.
            for (var pass = 0; pass < 2; pass++)
            { window.Layout(); window.SetNeedsDraw(); window.Draw(new DrawContext()); }
        }
        try
        {
            Draw();
            view.FocusList();
            view.SetSelectedRowForTests(1);
            view.NewKeyDownEvent(new Key(KeyCode.Enter));
            Draw();
            var pane = view.SubViews.OfType<Markdown>().Single();
            Assert.True(pane.HasFocus);
            var key = view.SelectedKeyForTests;
            Assert.Contains("1 observed copy · 16 recorded placements", view.FooterTextForTests);
            var descriptions = new Dictionary<int, string>();
            foreach (var size in new[] { (80, 24), (120, 36), (80, 24) })
            {
                pane.ScrollVertical(10000);
                app.Driver.SetScreenSize(size.Item1, size.Item2);
                Draw();
                Assert.Equal(key, view.SelectedKeyForTests);
                Assert.True(pane.HasFocus);
                Assert.InRange(pane.Viewport.Y, 0, Math.Max(0, pane.GetContentSize().Height - pane.Viewport.Height));
                var literal = Assert.Single(Markdig.Markdown.Parse(pane.Text).Descendants<FencedCodeBlock>()).Lines.ToString();
                Assert.Equal(InstalledDetailReadabilityTests.OrdinaryDescription, string.Join(" ", literal.Split('\n')));
                Assert.All(literal.Split('\n'), line => Assert.True(line.GetColumns() <= pane.Viewport.Width - 2, line));
                if (descriptions.TryGetValue(size.Item1, out var prior)) Assert.Equal(prior, literal);
                descriptions[size.Item1] = literal;
                var code = Assert.Single(pane.SubViews.OfType<MarkdownCodeBlock>());
                Assert.Equal(literal.ReplaceLineEndings(), code.ExtractText().ReplaceLineEndings());
                Assert.False(pane.ShowHeadingPrefix);
                Assert.Contains("16 recorded placements", pane.Text);
            }
            Assert.NotEqual(descriptions[80], descriptions[120]);
            view.FocusFilterForTests();
            app.Driver.SetScreenSize(120, 36); Draw();
            Assert.True(view.FilterHasFocus);
            view.NewKeyDownEvent(Key.Esc); view.FocusList();
            view.NewKeyDownEvent(Key.Esc); Draw();
            Assert.False(view.DetailOpenForTests);
            Assert.True(view.TableHasFocusForTests);
            Assert.Equal(key, view.SelectedKeyForTests);
            view.LoadSeeded(snapshot with { Skills = [skill with { Agents = [skill.Agents[0]] }] });
            Assert.Contains("1 observed copy · 1 recorded placement", view.FooterTextForTests);
            view.SetFilterTextForTests("missing");
            Assert.Contains("0 of 1 observed copies · 1 recorded placement", view.FooterTextForTests);
            view.SetFilterTextForTests("");
            view.LoadSeeded(snapshot with { Skills = [skill, skill with { Agents = [new("extra", skill.ResolvedPath, false)] }] });
            view.SetSelectedRowForTests(1);
            Assert.Contains("1 observed copy · 17 recorded placements", view.FooterTextForTests);
            Assert.Contains("1 physical copy · 17 recorded placements", view.DetailTextForTests);
            Assert.Contains("Ambiguous", view.DetailTextForTests);
        }
        finally { app.End(session!); }
    }

    [Theory]
    [InlineData(80, 24, "/skills/other")]
    [InlineData(120, 36, "/skills/demo")]
    public void AmbiguousDifferentPathsKeepRecordHeadlinesAndAggregateFooterWithoutCopyActions(int width, int height, string secondPath)
    {
        using var app = Application.Create().Init();
        app.Driver!.SetScreenSize(width, height);
        using var window = new Window();
        var first = InstalledOverviewTests.Copy("/skills/Demo", "demo");
        var member = new AgentMembership("codex", first.ResolvedPath, false);
        first = first with { Agents = [member, member] };
        first = first with { ObservedSubject = OperationSubject.From(first) with { InstallationId = "ambiguous-id" } };
        var second = InstalledOverviewTests.Copy(secondPath, "demo") with
        { Agents = [new("codex", secondPath, false, Profile: "one"), new("codex", secondPath, false, Profile: "two")] };
        second = second with { ObservedSubject = OperationSubject.From(second) with { InstallationId = "ambiguous-id" } };
        var snapshot = InventorySnapshot.Empty with { Skills = [second, first] };
        var operations = 0;
        using var view = new InstalledTabView(action => { action(); return Task.CompletedTask; }, (_, _) => { },
            _ => Task.FromResult(snapshot), (_, _, _) => { operations++; return Task.CompletedTask; }, () => { },
            openPath: _ => { operations++; return true; }, onReviewUpdate: _ => operations++)
        { Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(view); view.LoadSeeded(snapshot);
        var session = app.Begin(window);
        try
        {
            window.Layout(); window.Draw(new DrawContext());
            view.FocusList();
            view.SetSelectedRowForTests(1);
            view.NewKeyDownEvent(new Key(KeyCode.Enter));
            window.Layout(); window.Draw(new DrawContext());
            Assert.True(view.DetailOpenForTests);
            Assert.True(view.RowsForTests[1].Copy!.Ambiguous);
            var records = view.DetailTextForTests.Split("\n\n---\n\n", StringSplitOptions.None);
            Assert.Equal(2, records.Length);
            Assert.Contains("**Location:** `/skills/Demo`", records[0]);
            Assert.Contains("1 physical copy · 1 recorded placement\n", records[0]);
            Assert.Contains("**Location:** `" + secondPath + "`", records[1]);
            Assert.Contains("1 physical copy · 2 recorded placements\n", records[1]);
            Assert.DoesNotContain("1 physical copy · 3 recorded placements", view.DetailTextForTests);
            Assert.Contains("1 observed copy · 3 recorded placements · 1 ambiguous", view.FooterTextForTests);
            Assert.Null(view.GetSelectedSkill());
            foreach (var button in view.SubViews.OfType<Button>().Where(b =>
                b.Text.ToString().StartsWith("Review update", StringComparison.Ordinal) || b.Text.ToString() == "More actions"))
                Assert.False(button.Enabled);
            view.NewKeyDownEvent(new Key('u'));
            view.NewKeyDownEvent(new Key('o'));
            Assert.False(view.TryStartSelectedRemoveForTests());
            Assert.Equal(0, operations);
        }
        finally { app.End(session!); }
    }

}
