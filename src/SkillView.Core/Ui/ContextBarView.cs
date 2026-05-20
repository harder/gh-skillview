using System.Text;
using Terminal.Gui.ViewBase;

namespace SkillView.Ui;

/// Snapshot of context to render in the one-line bar below the tab strip.
/// Null/empty fields are omitted from the rendered output.
internal readonly record struct ContextBarState(
    string? Workspace,
    string? AgentLabel,
    string? LocationLabel,
    string? ProvenanceLabel,
    string? HealthLabel,
    string? FilterLabel);

/// One-line bar rendered directly below the tab strip that surfaces the active
/// agent, Locations, provenance, health, and quick-filter state as compact
/// labelled chips.
///
/// Rendering uses "Locations" / "Install locations" wording (never "roots").
/// "roots" is reserved for doctor-grade diagnostics only (§ spec §50).
internal sealed class ContextBarView : View
{
    private ContextBarState _state;

    internal ContextBarView()
    {
        CanFocus = false;
        Height = 1;
        Width = Dim.Fill();
    }

    internal void Update(ContextBarState state)
    {
        _state = state;
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var text = FormatForTests(_state);
        Move(0, 0);
        AddStr(text);
        return true;
    }

    /// Pure formatting helper — no TG2 drawing calls — usable from tests
    /// without a running Application instance.
    internal static string FormatForTests(ContextBarState state)
    {
        var sb = new StringBuilder();

        AppendChip(sb, state.AgentLabel);
        AppendLocations(sb, state.LocationLabel);
        AppendChip(sb, state.ProvenanceLabel);

        if (!string.IsNullOrWhiteSpace(state.HealthLabel))
        {
            AppendSep(sb);
            sb.Append(state.HealthLabel);
        }

        if (!string.IsNullOrWhiteSpace(state.FilterLabel))
        {
            AppendSep(sb);
            sb.Append(state.FilterLabel);
        }

        return sb.ToString();
    }

    private static void AppendLocations(StringBuilder sb, string? locationLabel)
    {
        if (string.IsNullOrWhiteSpace(locationLabel)) return;
        AppendSep(sb);
        sb.Append("Locations: ");
        sb.Append(locationLabel);
    }

    private static void AppendChip(StringBuilder sb, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        AppendSep(sb);
        sb.Append(value);
    }

    private static void AppendSep(StringBuilder sb)
    {
        if (sb.Length > 0) sb.Append("  ");
    }
}
