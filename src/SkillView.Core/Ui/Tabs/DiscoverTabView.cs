using SkillView.Ui.Theming;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui.Tabs;

/// Skeleton for the Discover tab — placeholder reserved for Task 3, which
/// will extract the search shell (query fields, results table, detail pane)
/// out of SkillViewApp into this view.  Until then SkillViewApp continues to
/// host the search panes directly; this view is created and stored but kept
/// hidden so the shell wiring can be verified by tests without disrupting the
/// existing search UX.
internal sealed class DiscoverTabView : FrameView
{
    internal DiscoverTabView()
    {
        BorderStyle = LineStyle.None;
        SchemeName = SchemeNames.Base;
        Visible = false;
    }
}
