using System.Collections.Immutable;
using System.Text;
using SkillView.Ui.Theming;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace SkillView.Ui;

/// A key/label pair rendered as a shortcut hint in the status strip.
internal readonly record struct StatusHint(string Key, string Label);

/// One-line bottom strip: left-side facet badges, center status message,
/// right-side shortcut hints with graceful truncation when width is tight.
///
/// Mirrors the winget-gui-tui status-strip model: shortcut hints are right-
/// aligned, and the rightmost pair always survives truncation so the most
/// critical action key is always visible.
internal sealed class StatusStripView : View
{
    private string _statusText = string.Empty;
    private ImmutableArray<StatusHint> _hints = [];
    private string _leftBadges = string.Empty;

    internal StatusStripView()
    {
        CanFocus = false;
        Height = 1;
        Width = Dim.Fill();
        SchemeName = SchemeNames.Base;
    }

    internal void Update(string statusText, IEnumerable<StatusHint> hints, string leftBadges = "")
    {
        _statusText = statusText ?? string.Empty;
        _hints = [..hints];
        _leftBadges = leftBadges ?? string.Empty;
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        if (width <= 0) return true;

        var bg = WingetTuiTheme.Surface;
        var fg = WingetTuiTheme.TextSecondary;
        var accentFg = WingetTuiTheme.Accent;

        Move(0, 0);
        SetAttribute(new Attribute(fg, bg));
        AddStr(new string(' ', width));

        // Left badges.
        var x = 0;
        if (_leftBadges.Length > 0)
        {
            Move(x, 0);
            SetAttribute(new Attribute(fg, bg));
            AddStr(" " + _leftBadges + " ");
            x += 1 + _leftBadges.Length + 1;
        }

        // Center status — truncated to available center space.
        if (_statusText.Length > 0)
        {
            var centerX = Math.Max(x, width / 4);
            Move(centerX, 0);
            SetAttribute(new Attribute(WingetTuiTheme.TextPrimary, bg));
            AddStr(TuiHelpers.Truncate(_statusText, width / 2));
        }

        // Right hints — rendered right-aligned using TruncateHintsForTests to
        // determine which pairs fit, then drawn right-to-left.
        var visibleHints = TruncateHintsForTests(_hints, width - x);
        if (visibleHints.Count > 0)
        {
            var rightText = BuildHintsText(visibleHints);
            var rx = width - rightText.Length;
            if (rx > x)
            {
                Move(rx, 0);
                SetAttribute(new Attribute(accentFg, bg));
                AddStr(rightText);
            }
        }

        return true;
    }

    private static string BuildHintsText(IReadOnlyList<StatusHint> hints)
    {
        var sb = new StringBuilder();
        foreach (var hint in hints)
        {
            if (sb.Length > 0) sb.Append("  ");
            sb.Append(hint.Key);
            sb.Append(':');
            sb.Append(hint.Label);
        }
        return sb.ToString();
    }

    /// Pure layout helper usable from tests. Given an ordered list of hints
    /// and available pixel width, returns the subset that fits — always
    /// preserving rightmost pairs when truncation is necessary.
    internal static IReadOnlyList<StatusHint> TruncateHintsForTests(
        IReadOnlyList<StatusHint> hints, int availableWidth)
    {
        if (availableWidth <= 0 || hints.Count == 0) return [];

        // Measure each hint as "key:label" plus a 2-space separator.
        const int sep = 2;
        var widths = new int[hints.Count];
        for (var i = 0; i < hints.Count; i++)
        {
            widths[i] = hints[i].Key.Length + 1 + hints[i].Label.Length;
        }

        // Work from the right: keep adding hints until we exhaust the budget.
        var budget = availableWidth;
        var kept = new List<StatusHint>(hints.Count);
        for (var i = hints.Count - 1; i >= 0; i--)
        {
            var cost = widths[i] + (kept.Count > 0 ? sep : 0);
            if (budget < cost) break;
            kept.Insert(0, hints[i]);
            budget -= cost;
        }

        return kept;
    }
}
