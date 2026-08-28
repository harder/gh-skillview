using SkillView.Ui.Theming;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;
using Mouse = Terminal.Gui.Input.Mouse;

namespace SkillView.Ui;

/// Identifiers for the top-level tabs surfaced in the TabBarView. Order
/// matches the visual left-to-right order and the numeric jump keys (1/2/3).
internal enum SkillViewTab
{
    Discover = 0,
    Installed = 1,
    Changes = 2,
}

/// Top header strip showing the three primary tabs (Discover / Installed /
/// Changes) as winget-tui-style "pills". The active tab is rendered with
/// the accent color and a bullet indicator; inactive tabs are dim.
///
/// Click hit-testing maps mouse-down to a tab change via the
/// <see cref="TabActivated"/> event; key-based switching is handled at the
/// SkillViewApp level and pushed down via <see cref="SetActiveTab"/>.
///
/// This view does NOT host the tab content; it's a pure header. SkillViewApp
/// hosts the content panes below it and swaps visibility on tab change.
internal sealed class TabBarView : View
{
    private static readonly (SkillViewTab Tab, string Icon, string Label)[] Tabs =
    {
        (SkillViewTab.Discover,  "◇", "Discover"),
        (SkillViewTab.Installed, "▣", "Installed"),
        (SkillViewTab.Changes,   "△", "Changes"),
    };

    /// One column per tab. Recomputed on every Draw so the layout reflows on
    /// terminal resize without needing an explicit FrameChanged hook.
    private readonly Dictionary<SkillViewTab, (int X, int Width)> _tabRegions = new();

    private SkillViewTab _active = SkillViewTab.Discover;

    internal event EventHandler<SkillViewTab>? TabActivated;

    internal SkillViewTab ActiveTab => _active;

    internal TabBarView()
    {
        CanFocus = false;
        Height = 1;
        Width = Dim.Fill();
        SchemeName = SchemeNames.Base;
    }

    internal void SetActiveTab(SkillViewTab tab)
    {
        if (_active == tab) return;
        _active = tab;
        SetNeedsDraw();
    }

    /// Returns the display labels of all tabs in visual order. Used by tests
    /// to verify the workflow-first labelling without parsing drawn output.
    internal IReadOnlyList<string> TabLabelsForTests => Tabs.Select(t => t.Label).ToArray();

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var viewport = Viewport;
        var width = viewport.Width;
        if (width <= 0) return true;

        // Branding already lives in the window title. Keep this row focused on
        // navigation and leave the line below it for the active workspace title.
        var inactiveFg = WingetTuiTheme.TextSecondary;
        var activeFg = WingetTuiTheme.Accent;
        var background = WingetTuiTheme.Background;

        // Clear row with background fill.
        Move(0, 0);
        SetAttribute(new Attribute(WingetTuiTheme.TextPrimary, background));
        AddStr(new string(' ', width));

        // Compute pill total width so we can right-align.
        const int gap = 3;
        var pillWidths = Tabs.Select(t => Pill(t.Icon, t.Label).Length).ToArray();
        var pillsTotal = pillWidths.Sum() + gap * (Tabs.Length - 1);

        var x = Math.Max(0, width - pillsTotal - 2);
        _tabRegions.Clear();
        for (var i = 0; i < Tabs.Length; i++)
        {
            var (tab, icon, label) = Tabs[i];
            var text = Pill(icon, label);
            var isActive = tab == _active;
            var attr = isActive
                ? new Attribute(activeFg, background, TextStyle.Bold)
                : new Attribute(inactiveFg, background);
            SetAttribute(attr);
            Move(x, 0);
            AddStr(text);
            _tabRegions[tab] = (x, text.Length);
            x += text.Length + gap;
        }

        return true;
    }

    protected override bool OnMouseEvent(Mouse mouseEvent)
    {
        if (!mouseEvent.Flags.HasFlag(MouseFlags.LeftButtonClicked))
        {
            return false;
        }
        if (mouseEvent.Position is not { } pos)
        {
            return false;
        }
        var x = pos.X;
        foreach (var (tab, (regionX, regionWidth)) in _tabRegions)
        {
            if (x >= regionX && x < regionX + regionWidth)
            {
                if (tab != _active)
                {
                    TabActivated?.Invoke(this, tab);
                }
                return true;
            }
        }
        return false;
    }

    private static string Pill(string icon, string label) => $" {icon} {label} ";
}
