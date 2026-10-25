using System.Reflection;
using SkillView.Inventory.Models;
using SkillView.Ui;
using SkillView.Ui.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
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
        using var pane = new Markdown { Width = Dim.Fill(), Height = Dim.Fill(), Text = InstalledScreen.RenderDetail(skill, showDetails: true) };
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
        }
        finally { app.End(session!); }
    }
}
