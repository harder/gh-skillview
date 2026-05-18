using SkillView.Ui.Theming;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// Terminal.Gui 2.2 marks TextView [Obsolete] in favor of gui-cs/Editor, which
// is an editor-grade widget intended for full document editing. SkillView uses
// TextView only as a read-only scrollable text surface for the raw SKILL.md
// preview and the log pane — neither needs an editing model, and pulling in a
// second NuGet for two read-only panes isn't justified. Suppress narrowly so
// the rest of the file is still checked.
#pragma warning disable CS0618 // TextView obsolete — see note above.

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

    internal Label ItemActionsLabel { get; }
    internal FrameView MetadataFrame { get; }
    internal Markdown MetadataPane { get; }
    internal FrameView PreviewFrame { get; }
    internal Markdown PreviewPane { get; }
    internal TextView PreviewRawPane { get; }
    internal TextView LogPane { get; }

    /// `actionsText` is the one-line hint strip rendered at the top of the
    /// pane. `welcomeText` seeds both preview views before any selection is
    /// made. `actionsScheme` is applied to the actions label so it reads as a
    /// status-bar strip rather than blending into the panel.
    internal SkillDetailPaneView(string actionsText, string welcomeText)
    {
        // No frame title — the inner Details / SKILL.md frames carry their own.
        BorderStyle = LineStyle.None;
        // Vertical stack: actions strip · auto-sized metadata · preview body.
        // Metadata-on-top keeps item context visible while the preview scrolls,
        // and the actions bar advertises i/o/e at point of use rather than
        // burying them in the bottom status bar.
        ItemActionsLabel = new Label
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1,
            Text = actionsText,
        };

        MetadataFrame = new FrameView
        {
            Title = "Details",
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = MinMetadataHeight + 2,
            BorderStyle = LineStyle.Single,
        };
        MetadataPane = new Markdown
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            Text = "_(no selection)_",
        };
        TuiHelpers.ConfigureMarkdownPane(MetadataPane, SchemeNames.Base);
        MetadataFrame.Add(MetadataPane);

        PreviewFrame = new FrameView
        {
            Title = "SKILL.md",
            X = 0,
            Y = Pos.Bottom(MetadataFrame),
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            BorderStyle = LineStyle.Single,
        };
        PreviewPane = new Markdown
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            Text = welcomeText,
        };
        TuiHelpers.ConfigureMarkdownPane(PreviewPane, SchemeNames.Base);

        PreviewRawPane = new TextView
        {
            X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(),
            Text = welcomeText,
            Visible = false,
        };
        TuiHelpers.ConfigureReadOnlyPane(PreviewRawPane, SchemeNames.Base);
        PreviewFrame.Add(PreviewPane, PreviewRawPane);

        // Logs overlay the rest of the pane when surfaced via `l`. They sit
        // outside MetadataFrame/PreviewFrame so the toggle is a single
        // visibility flip rather than a layout rebuild.
        LogPane = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Visible = false,
        };
        TuiHelpers.ConfigureReadOnlyPane(LogPane, SchemeNames.Base);

        Add(ItemActionsLabel, MetadataFrame, PreviewFrame, LogPane);

        SchemeName = SchemeNames.Base;
        ItemActionsLabel.SetScheme(TuiHelpers.CreateStatusScheme(TuiHelpers.NotificationLevel.Info));
    }
}
