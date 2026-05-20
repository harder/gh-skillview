using SkillView.Inventory;
using SkillView.Ui;
using Terminal.Gui.Drivers;
using Terminal.Gui.Editor;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class TuiHelpersTests
{
    [Theory]
    [InlineData(null, 10, "")]
    [InlineData("", 10, "")]
    [InlineData("short", 10, "short")]
    [InlineData("exactly10!", 10, "exactly10!")]
    [InlineData("this is a long string", 10, "this is a…")]
    [InlineData("ab", 2, "ab")]
    [InlineData("abc", 2, "a…")]
    public void Truncate_ClipsWithEllipsis(string? input, int max, string expected)
    {
        Assert.Equal(expected, TuiHelpers.Truncate(input, max));
    }

    [Fact]
    public void Truncate_RespectsDisplayWidth_ForWideGlyphs()
    {
        var result = TuiHelpers.Truncate("当用户明确要求使用", 8);

        Assert.EndsWith("…", result);
        Assert.True(result.GetColumns() <= 8);
    }

    [Theory]
    [InlineData(null, 3, "")]
    [InlineData("", 3, "")]
    [InlineData("/a/b/c", 3, "/a/b/c")]
    [InlineData("/home/user/.config/skills/copilot/my-skill", 3, "…/skills/copilot/my-skill")]
    [InlineData("/a/b", 3, "/a/b")]
    [InlineData("/a/b/c/d/e", 2, "…/d/e")]
    public void ShortenPath_KeepsLastSegments(string? input, int segments, string expected)
    {
        Assert.Equal(expected, TuiHelpers.ShortenPath(input, segments));
    }

    [Theory]
    [InlineData(CleanupClassifier.CandidateKind.Malformed, "malformed")]
    [InlineData(CleanupClassifier.CandidateKind.SourceOrphaned, "orphan")]
    [InlineData(CleanupClassifier.CandidateKind.Duplicate, "duplicate")]
    [InlineData(CleanupClassifier.CandidateKind.EmptyDirectory, "empty-dir")]
    [InlineData(CleanupClassifier.CandidateKind.BrokenSharedMapping, "broken-map")]
    [InlineData(CleanupClassifier.CandidateKind.HiddenNestedResidue, "hidden-nest")]
    [InlineData(CleanupClassifier.CandidateKind.BrokenSymlink, "broken-link")]
    [InlineData(CleanupClassifier.CandidateKind.OrphanCanonicalCopy, "orphan-copy")]
    public void ShortKind_ReturnsCompactLabel(CleanupClassifier.CandidateKind kind, string expected)
    {
        Assert.Equal(expected, TuiHelpers.ShortKind(kind));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  \n  \n  ", "")]
    [InlineData("error: not found", "error: not found")]
    [InlineData("  \nactual error line\nmore stuff", "actual error line")]
    [InlineData("a very long error message that exceeds the default maximum length for inline status display in the TUI", "a very long error message that exceeds the default maximum …")]
    public void ErrorSnippet_ExtractsFirstLine(string? input, string expected)
    {
        Assert.Equal(expected, TuiHelpers.ErrorSnippet(input));
    }

    [Fact]
    public void ErrorSnippet_RespectsCustomMaxLen()
    {
        Assert.Equal("abcdefghi…", TuiHelpers.ErrorSnippet("abcdefghijklmnop", maxLen: 10));
    }

    [Fact]
    public void HelpText_IsNotEmpty()
    {
        Assert.True(TuiHelpers.HelpText.Length > 0);
        Assert.Contains("quit", TuiHelpers.HelpText);
    }

    [Fact]
    public void HelpText_ClarifiesEnterPreviewRequiresResultsFocus()
    {
        Assert.Contains("when results are focused", TuiHelpers.HelpText);
    }

    [Fact]
    public void HelpText_DocumentsHiddenDirToggle()
    {
        Assert.Contains("hidden-dir", TuiHelpers.HelpText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HelpText_DocumentsRenderedMarkdownCopySupport()
    {
        Assert.Contains("Ctrl+C", TuiHelpers.HelpText);
        Assert.Contains("copy", TuiHelpers.HelpText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WelcomeHint_IsNotEmpty()
    {
        Assert.True(TuiHelpers.WelcomeHint.Length > 0);
        Assert.Contains("help", TuiHelpers.WelcomeHint);
    }

    [Fact]
    public void WelcomeHint_DocumentsRenderedMarkdownCopySupport()
    {
        Assert.Contains("Ctrl+C", TuiHelpers.WelcomeHint);
        Assert.Contains("copy", TuiHelpers.WelcomeHint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreviewHint_DocumentsRenderedMarkdownCopySupport()
    {
        Assert.Contains("Ctrl+C", TuiHelpers.PreviewHint);
        Assert.Contains("copy", TuiHelpers.PreviewHint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithMarkdownShortcuts_AppendsSelectCopyAndOpenLinkHints()
    {
        var shortcuts = TuiHelpers.WithMarkdownShortcuts(
        [
            new Shortcut { Title = "x", HelpText = "Base" },
        ]);

        Assert.Collection(
            shortcuts,
            shortcut =>
            {
                Assert.Equal("x", shortcut.Title);
                Assert.Equal("Base", shortcut.HelpText);
            },
            shortcut =>
            {
                Assert.Equal("Ctrl+A", shortcut.Title);
                Assert.Equal("Select", shortcut.HelpText);
            },
            shortcut =>
            {
                Assert.Equal("Ctrl+C", shortcut.Title);
                Assert.Equal("Copy", shortcut.HelpText);
            },
            shortcut =>
            {
                Assert.Equal("Click", shortcut.Title);
                Assert.Equal("Open link", shortcut.HelpText);
            });
    }

    [Theory]
    [InlineData("copilot", "C")]
    [InlineData("github-copilot", "C")]
    [InlineData("claude-code", "⟁")]
    [InlineData("gemini-cli", "✦")]
    public void AgentIcon_SupportsCurrentAgentIds(string agentId, string expected)
    {
        Assert.Equal(expected, TuiHelpers.AgentIcon(agentId));
    }

    [Theory]
    [InlineData('v')]
    [InlineData('V')]
    [InlineData('p')]
    [InlineData('P')]
    public void IsPreviewKey_AcceptsPreviewRunes(char key)
    {
        Assert.True(TuiHelpers.IsPreviewKey(new Key(key)));
    }

    [Fact]
    public void IsPreviewKey_AcceptsEnter()
    {
        Assert.True(TuiHelpers.IsPreviewKey(new Key(KeyCode.Enter)));
    }

    [Fact]
    public void IsPreviewKey_RejectsOtherKeys()
    {
        Assert.False(TuiHelpers.IsPreviewKey(new Key('x')));
    }

    [Fact]
    public void ApplyScheme_SetsSchemeName_OnProvidedViews()
    {
        var label = new Label();
        var field = new TextField();

        TuiHelpers.ApplyScheme(SkillViewStyling.BaseSchemeName, label, field, null);

        Assert.Equal(SkillViewStyling.BaseSchemeName, label.SchemeName);
        Assert.Equal(SkillViewStyling.BaseSchemeName, field.SchemeName);
    }

    [Fact]
    public void SkillDetailPaneView_UsesEditorViews_ForRawPreviewAndLogs()
    {
        var pane = new SkillDetailPaneView("actions", "welcome");

        Assert.IsType<Editor>(pane.PreviewRawPane);
        Assert.IsType<Editor>(pane.LogPane);
    }

    [Fact]
    public void ConfigureReadOnlyPane_SetsReadableDefaults()
    {
        var view = new Editor();

        TuiHelpers.ConfigureReadOnlyPane(view, SkillViewStyling.DialogSchemeName);

        Assert.True(view.ReadOnly);
        Assert.True(view.WordWrap);
        Assert.True(view.ViewportSettings.HasFlag(ViewportSettingsFlags.HasVerticalScrollBar));
        Assert.Equal(SkillViewStyling.DialogSchemeName, view.SchemeName);
    }

    [Fact]
    public void ConfigureMarkdownPane_RoutesLinkClicksThroughProvidedOpener()
    {
        var openedTargets = new List<string>();
        var view = new TestMarkdown();

        TuiHelpers.ConfigureMarkdownPane(view, SkillViewStyling.DialogSchemeName, target =>
        {
            openedTargets.Add(target);
            return true;
        });

        var handled = view.RaiseLinkClicked("https://example.test/docs");

        Assert.True(handled);
        Assert.Equal(["https://example.test/docs"], openedTargets);
    }

    [Fact]
    public void ConfigureMarkdownPane_MarksNonAnchorLinksHandledWhenProvidedOpenerFails()
    {
        var openedTargets = new List<string>();
        var view = new TestMarkdown();

        TuiHelpers.ConfigureMarkdownPane(view, SkillViewStyling.DialogSchemeName, target =>
        {
            openedTargets.Add(target);
            return false;
        });

        var handled = view.RaiseLinkClicked("https://example.test/docs");

        Assert.True(handled);
        Assert.Equal(["https://example.test/docs"], openedTargets);
    }

    [Fact]
    public void ConfigureMarkdownPane_DoesNotInterceptAnchorLinks()
    {
        var openedTargets = new List<string>();
        var view = new TestMarkdown();

        TuiHelpers.ConfigureMarkdownPane(view, SkillViewStyling.DialogSchemeName, target =>
        {
            openedTargets.Add(target);
            return true;
        });

        var handled = view.RaiseLinkClicked("#details");

        Assert.False(handled);
        Assert.Empty(openedTargets);
    }

    [Fact]
    public void ConfigureTableChrome_HidesOnlyOuterVerticalChrome()
    {
        var table = new TableView
        {
            Style =
            {
                ShowVerticalCellLines = true,
                ShowVerticalCellLineForFirstColumn = true,
                ShowVerticalCellLineForLastColumn = true,
                ShowVerticalHeaderLines = true,
            },
        };

        TuiHelpers.ConfigureTableChrome(table);

        Assert.True(table.Style.ShowVerticalCellLines);
        Assert.False(table.Style.ShowVerticalCellLineForFirstColumn);
        Assert.False(table.Style.ShowVerticalCellLineForLastColumn);
        Assert.True(table.Style.ShowVerticalHeaderLines);
    }

    [Fact]
    public void DisableTypeToSearch_ClearsCollectionNavigator()
    {
        var table = new TableView();

        TuiHelpers.DisableTypeToSearch(table);

        Assert.Null(table.CollectionNavigator);
    }

    [Fact]
    public void ConfigureTableKeyBindings_RoutesPreviewKeyToAccepted()
    {
        var table = new TableView();
        var accepted = false;

        table.Accepted += (_, _) =>
        {
            accepted = true;
        };
        TuiHelpers.ConfigureTableKeyBindings(table);

        _ = table.NewKeyDownEvent(new Key('p'));

        Assert.True(accepted);
    }

    [Fact]
    public void ConfigureTableKeyBindings_PreservesDisabledTypeToSearch()
    {
        var table = new TableView();

        TuiHelpers.ConfigureTableKeyBindings(table);

        Assert.Null(table.CollectionNavigator);
    }

    private sealed class TestMarkdown : Markdown
    {
        public bool RaiseLinkClicked(string url)
        {
            var method = typeof(Markdown).GetMethod(
                "RaiseLinkClicked",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            Assert.NotNull(method);

            return Assert.IsType<bool>(method.Invoke(this, [url]));
        }
    }
}
