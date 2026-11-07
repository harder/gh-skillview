using SkillView.Ui;
using SkillView.Ui.Theming;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Editor;
using Terminal.Gui.Editor.Document;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class DependencyRenderingTests
{
    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public void MarkdownPartialInvalidationPreservesStyledUnicodeAndTableCells(int width, int height)
    {
        using var app = Application.Create().Init();
        app.Driver!.SetScreenSize(width, height);
        using var window = new Window();
        using var pane = new Markdown
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Text = "# Dependency preview\n\n**Styled** 日本語 😀\n\n| Name | Value |\n| --- | --- |\n| alpha | `code` |\n| beta | ready |\n\n```csharp\nvar answer = 42;\n```",
        };
        TuiHelpers.ConfigureMarkdownPane(pane, SchemeNames.Base, _ => false);
        window.Add(pane);
        var session = app.Begin(window);
        try
        {
            app.LayoutAndDraw();
            // Complete the next ordinary paint after initial Markdown child layout.
            app.LayoutAndDraw();
            Assert.False(app.ClearScreenNextIteration);
            var initial = Grid(app, width, height);
            var text = string.Concat(initial.Select(cell => cell.Text));
            Assert.Contains("Dependency preview", text);
            Assert.Contains("Styled", text);
            Assert.Contains("alpha", text);
            Assert.Contains("ready", text);
            var japanese = Array.FindIndex(initial, cell => cell.Text == "日");
            Assert.True(japanese >= 0);
            Assert.InRange(japanese % width, 0, width - 5);
            Assert.Equal("本", initial[japanese + 2].Text);
            Assert.Equal("語", initial[japanese + 4].Text);
            Assert.Contains("😀", text);
            Assert.True(initial.Select(cell => cell.Style).Distinct().Count() > 1);
            Assert.Contains("var answer = 42;", Assert.Single(pane.SubViews.OfType<MarkdownCodeBlock>()).ExtractText());

            // Repainting just the Markdown viewport must retain its styled content.
            pane.SetNeedsDraw();
            Assert.False(app.ClearScreenNextIteration);
            app.LayoutAndDraw();
            Assert.False(app.ClearScreenNextIteration);
            var partial = Grid(app, width, height);
            Assert.Equal(initial, partial);
            app.LayoutAndDraw(true);
            Assert.Equal(partial, Grid(app, width, height));

            pane.Text = "# Replaced\n\n**Short** 日本語 😀";
            app.LayoutAndDraw();
            var replaced = Grid(app, width, height);
            Assert.Contains("Replaced", string.Concat(replaced.Select(cell => cell.Text)));
            Assert.DoesNotContain("alpha", string.Concat(replaced.Select(cell => cell.Text)));
            Assert.DoesNotContain("answer", string.Concat(replaced.Select(cell => cell.Text)));
            app.LayoutAndDraw(true);
            Assert.Equal(replaced, Grid(app, width, height));
        }
        finally { app.End(session!); }
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public void EditorRawTextStaysInDocumentThroughBothPublicTextSettersAndReadOnlyInput(int width, int height)
    {
        using var app = Application.Create().Init();
        app.Driver!.SetScreenSize(width, height);
        using var window = new Window();
        using var editor = new Editor { Width = Dim.Fill(), Height = Dim.Fill() };
        TuiHelpers.ConfigureReadOnlyPane(editor, SchemeNames.Base);
        string DocumentText() => Assert.IsType<TextDocument>(editor.Document).Text;
        const string first = "raw **literal** 日本語 😀\nsecond line";
        editor.Text = first;
        Assert.Equal(first, DocumentText());
        Assert.Equal(first, editor.Text);
        const string second = "replacement [literal](file:///not-opened)\ncombining e\u0301 😀";
        View polymorphic = editor;
        polymorphic.Text = second;
        Assert.Equal(second, DocumentText());
        Assert.Equal(second, editor.Text);
        Assert.Equal(second, polymorphic.Text);
        window.Add(editor);
        var session = app.Begin(window);
        try
        {
            editor.SetFocus();
            app.LayoutAndDraw();
            editor.SelectAll();
            Assert.Equal(second, editor.SelectedText);
            editor.NewKeyDownEvent(new Key('x'));
            Assert.True(editor.ReadOnly);
            Assert.True(editor.WordWrap);
            Assert.Equal(second, DocumentText());
            Assert.Equal(second, editor.Text);
            app.LayoutAndDraw();
            Assert.DoesNotContain("\uFFFD", string.Concat(Grid(app, width, height).Select(cell => cell.Text)));
        }
        finally { app.End(session!); }
    }

    private static (string Text, Attribute? Style)[] Grid(IApplication app, int width, int height) =>
        Enumerable.Range(0, height).SelectMany(y => Enumerable.Range(0, width).Select(x =>
            (app.Driver!.Contents![y, x].Grapheme, app.Driver.Contents[y, x].Attribute))).ToArray();
}
