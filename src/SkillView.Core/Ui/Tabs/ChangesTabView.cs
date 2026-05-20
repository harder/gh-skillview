using SkillView.Ui.Theming;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui.Tabs;

/// Skeleton for the Changes tab — placeholder reserved for Task 3, which
/// will extract the updates shell (skill list, dry-run / update controls,
/// result preview) out of UpdatesTabView and SkillViewApp into this view.
/// Until then the UpdatesTabView adapter hosts the actual content; this view
/// is created and stored but kept hidden so the shell wiring can be verified
/// by tests without disrupting the existing update UX.
internal sealed class ChangesTabView : FrameView
{
    internal ChangesTabView()
    {
        BorderStyle = LineStyle.None;
        SchemeName = SchemeNames.Base;
        Visible = false;
    }
}
