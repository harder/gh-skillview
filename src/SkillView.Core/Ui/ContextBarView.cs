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

/// One-line bar rendered directly below the tab strip. It starts with the
/// active workspace title, then surfaces agent, location, provenance, health,
/// and quick-filter state as compact labelled chips.
///
/// Rendering uses "Location" / "Install location" wording (never "roots").
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

    internal ContextBarState CurrentStateForTests => _state;

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

        AppendChip(sb, state.Workspace);
        AppendChip(sb, state.AgentLabel);
        AppendLabeledValue(sb, "Location", state.LocationLabel);
        AppendLabeledValue(sb, "Source", state.ProvenanceLabel);
        AppendLabeledValue(sb, "Health", state.HealthLabel);

        if (!string.IsNullOrWhiteSpace(state.FilterLabel))
        {
            AppendSep(sb);
            sb.Append(state.FilterLabel);
        }

        return sb.ToString();
    }

    internal static bool ShouldShowForTests(ContextBarState state) =>
        !string.IsNullOrWhiteSpace(FormatForTests(state));

    private static void AppendLabeledValue(StringBuilder sb, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        AppendSep(sb);
        sb.Append(label);
        sb.Append(": ");
        sb.Append(value);
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
