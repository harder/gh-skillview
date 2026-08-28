using System.Collections.Immutable;
using System.Text;
using SkillView.Ui.Theming;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

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
    private readonly SpinnerView _spinner;
    private bool _isBusy;

    internal StatusStripView()
    {
        CanFocus = false;
        Height = 1;
        Width = Dim.Fill();
        SchemeName = SchemeNames.Base;

        _spinner = new SpinnerView
        {
            X = 0,
            Y = 0,
            Width = 1,
            Height = 1,
            Visible = false,
            AutoSpin = false,
            Style = new SpinnerStyle.Dots(),
        };
        _spinner.SetScheme(TuiHelpers.CreateStatusScheme(TuiHelpers.NotificationLevel.Info));
        Add(_spinner);
    }

    internal void Update(string statusText, IEnumerable<StatusHint> hints, string leftBadges = "")
    {
        _statusText = statusText ?? string.Empty;
        _hints = [.. hints];
        _leftBadges = leftBadges ?? string.Empty;
        SetNeedsDraw();
    }

    internal void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        _spinner.Visible = isBusy;
        _spinner.AutoSpin = isBusy;
        SetNeedsDraw();
    }

    internal bool IsBusyForTests => _isBusy;

    internal string LeftBadgesForTests => _leftBadges;

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        if (width <= 0) return true;

        var (strip, statusText, hintAccent) = TuiHelpers.GetStatusStripAttributes();

        Move(0, 0);
        SetAttribute(strip);
        AddStr(new string(' ', width));

        // Left badges.
        var x = 0;
        if (_leftBadges.Length > 0)
        {
            Move(x, 0);
            SetAttribute(strip);
            AddStr(" " + _leftBadges + " ");
            x += 1 + _leftBadges.Length + 1;
        }

        // Center status — truncated to available center space.
        if (_statusText.Length > 0)
        {
            var centerX = Math.Max(x, width / 4);
            _spinner.X = centerX;
            var textX = centerX + (_isBusy ? 2 : 0);
            Move(textX, 0);
            SetAttribute(statusText);
            AddStr(TuiHelpers.Truncate(_statusText, Math.Max(0, width / 2 - (_isBusy ? 2 : 0))));
        }

        // Right hints — budget excludes left badges AND the center-status
        // region so hints can never overwrite the status text (Bug 1).
        var measuredStatus = _isBusy ? "  " + _statusText : _statusText;
        var leftEdge = ComputeHintLeftEdge(width, x, measuredStatus);
        var visibleHints = TruncateHintsForTests(_hints, width - leftEdge);
        if (visibleHints.Count > 0)
        {
            var rightText = BuildHintsText(visibleHints);
            var rx = width - rightText.Length;
            if (rx >= leftEdge)  // >= allows exact-fit rendering (Bug 2)
            {
                Move(rx, 0);
                SetAttribute(hintAccent);
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

    /// Compute the leftmost x-position available for right-aligned hints.
    /// Accounts for both the left-badge end and the center-status text region
    /// so that hints are never budgeted into space occupied by the status.
    internal static int ComputeHintLeftEdge(int width, int leftBadgeEnd, string statusText)
    {
        if (statusText.Length == 0)
            return leftBadgeEnd;
        var centerX = Math.Max(leftBadgeEnd, width / 4);
        var centerEnd = centerX + Math.Min(statusText.Length, width / 2);
        return Math.Max(leftBadgeEnd, centerEnd);
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
