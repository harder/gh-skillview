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
    Search    = 0,
    Installed = 1,
    Updates   = 2,
}

/// Top header strip showing the three primary tabs (Search / Installed /
/// Updates) as winget-tui-style "pills". The active tab is rendered with
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
        (SkillViewTab.Search,    "◇", "Search"),
        (SkillViewTab.Installed, "▣", "Installed"),
        (SkillViewTab.Updates,   "△", "Updates"),
    };

    /// One column per tab. Recomputed on every Draw so the layout reflows on
    /// terminal resize without needing an explicit FrameChanged hook.
    private readonly Dictionary<SkillViewTab, (int X, int Width)> _tabRegions = new();

    private SkillViewTab _active = SkillViewTab.Search;

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

    protected override bool OnDrawingContent (DrawContext? context)
    {
        var viewport = Viewport;
        var width = viewport.Width;
        if (width <= 0) return true;

        // Build right-anchored tabs: "  Skill View          ◇ Search   ▣ Installed   △ Updates  "
        // Logo on the left, tabs flush right with a 2-cell gap between pills.
        const string logo = "  Skill View";
        var inactiveFg = WingetTuiTheme.TextSecondary;
        var activeFg   = WingetTuiTheme.Accent;
        var background = WingetTuiTheme.Background;

        // Clear row with background fill.
        Move(0, 0);
        SetAttribute(new Attribute(WingetTuiTheme.TextPrimary, background));
        AddStr(new string(' ', width));

        Move(0, 0);
        SetAttribute(new Attribute(WingetTuiTheme.Accent, background, TextStyle.Bold));
        AddStr(logo);

        // Compute pill total width so we can right-align.
        const int gap = 3;
        var pillWidths = Tabs.Select(t => Pill(t.Icon, t.Label).Length).ToArray();
        var pillsTotal = pillWidths.Sum() + gap * (Tabs.Length - 1);

        var x = Math.Max(logo.Length + 4, width - pillsTotal - 2);
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

    protected override bool OnMouseEvent (Mouse mouseEvent)
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
