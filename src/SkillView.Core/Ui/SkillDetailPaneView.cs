using SkillView.Ui.Theming;
using Terminal.Gui.Drawing;
using Terminal.Gui.Editor;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui;

/// Right-pane container for skill details: a one-line item-actions hint, an
/// auto-sized metadata frame, a SKILL.md preview body, and a (hidden by
/// default) full-frame log viewer overlay. Mirrors the layout of winget-tui's
/// detail panel — a persistent companion to whatever list owns the cursor.
///
/// Owns construction of the inner views but does not host them; the public
/// surface area returns the inner controls so the (currently large)
/// SkillViewApp can keep operating on them directly. Later phases will migrate
/// behavioral methods (preview toggle, metadata render) into this class as
/// the call sites stabilize.
internal sealed class SkillDetailPaneView : FrameView
{
    private const int MinMetadataHeight = 3;
    private const int MaxMetadataHeight = 5;
    private string _actionsText;
    private string? _healthSummary;

    internal Label ItemActionsLabel { get; }
    internal FrameView MetadataFrame { get; }
    internal Markdown MetadataPane { get; }
    internal FrameView PreviewFrame { get; }
    internal Markdown PreviewPane { get; }
    internal Editor PreviewRawPane { get; }
    internal Editor LogPane { get; }

    /// `actionsText` is the one-line hint strip rendered at the top of the
    /// pane. `welcomeText` seeds both preview views before any selection is
    /// made. `actionsScheme` is applied to the actions label so it reads as a
    /// status-bar strip rather than blending into the panel.
    internal SkillDetailPaneView(string actionsText, string welcomeText)
    {
        _actionsText = actionsText.Trim();
        Title = "Details";
        BorderStyle = LineStyle.Single;
        // Vertical stack: auto-sized metadata · preview body · action strip.
        // Keeping actions at the bottom mirrors winget's detail panes and keeps
        // the content area visually calmer.
        ItemActionsLabel = new Label
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
            Text = string.Empty,
        };

        MetadataFrame = new FrameView
        {
            Title = string.Empty,
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = MinMetadataHeight + 2,
            BorderStyle = LineStyle.None,
        };
        MetadataPane = new Markdown
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Text = "_Select a skill to preview._",
        };
        TuiHelpers.ConfigureMarkdownPane(MetadataPane, SchemeNames.Base);
        MetadataFrame.Add(MetadataPane);

        PreviewFrame = new FrameView
        {
            Title = string.Empty,
            X = 0,
            Y = Pos.Bottom(MetadataFrame),
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            BorderStyle = LineStyle.None,
        };
        PreviewPane = new Markdown
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Text = welcomeText,
        };
        TuiHelpers.ConfigureMarkdownPane(PreviewPane, SchemeNames.Base);

        PreviewRawPane = new Editor
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = false,
            Text = welcomeText,
            Visible = false,
        };
        TuiHelpers.ConfigureReadOnlyPane(PreviewRawPane, SchemeNames.Base);
        PreviewFrame.Add(PreviewPane, PreviewRawPane);

        // Logs overlay the rest of the pane when surfaced via `l`. They sit
        // outside MetadataFrame/PreviewFrame so the toggle is a single
        // visibility flip rather than a layout rebuild.
        LogPane = new Editor
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = false,
            Visible = false,
        };
        TuiHelpers.ConfigureReadOnlyPane(LogPane, SchemeNames.Base);

        Add(ItemActionsLabel, MetadataFrame, PreviewFrame, LogPane);

        SchemeName = SchemeNames.Base;
        ItemActionsLabel.SetScheme(TuiHelpers.CreateStatusScheme(TuiHelpers.NotificationLevel.Info));
        RefreshHeader();
    }

    internal void SetHealthSummary(string? summary)
    {
        _healthSummary = summary;
        RefreshHeader();
    }

    internal void SetActionsText(string actionsText)
    {
        _actionsText = actionsText.Trim();
        RefreshHeader();
    }

    /// Update the metadata pane with a raw markdown string and auto-size the
    /// surrounding frame.  Pass null or empty to reset to the "(no selection)"
    /// placeholder.
    internal void SetMetadataContent(string? markdown)
    {
        var text = string.IsNullOrEmpty(markdown) ? "_Select a skill to preview._" : markdown;
        MetadataPane.Text = text;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var nonBlank = lines.Count(l => !string.IsNullOrWhiteSpace(l));
        MetadataFrame.Height = Math.Clamp(nonBlank, MinMetadataHeight, MaxMetadataHeight) + 2;
    }

    /// Update the metadata pane with a set of chip-style labels.  Pass no
    /// arguments (or an empty array) to reset to the "(no selection)" placeholder.
    internal void SetMetadataChips(params string[] chips)
    {
        var markdown = chips.Length == 0
            ? null
            : string.Join("  ", System.Linq.Enumerable.Select(chips, c => $"**{c}**"));
        SetMetadataContent(markdown);
    }

    private void RefreshHeader()
    {
        ItemActionsLabel.Text = string.IsNullOrWhiteSpace(_healthSummary)
            ? _actionsText
            : $"{_healthSummary}  ·  {_actionsText}";
    }
}
