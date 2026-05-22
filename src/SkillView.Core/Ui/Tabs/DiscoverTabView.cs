using SkillView.Gh;
using SkillView.Ui.Theming;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace SkillView.Ui.Tabs;

/// Discover workspace — owns the search shell (query fields, results table,
/// detail pane) extracted from SkillViewApp.  Exposes all inner controls as
/// internal properties so SkillViewApp can subscribe events and read values;
/// this adapter pattern mirrors SkillDetailPaneView and lets the call sites in
/// SkillViewApp stabilise before a later phase migrates more behaviour here.
internal sealed class DiscoverTabView : FrameView
{
    // Left pane — search controls + results table.
    internal FrameView LeftFrame { get; }
    internal TextField QueryField { get; }
    internal TextField OwnerField { get; }
    internal TextField AgentField { get; }
    internal NumericUpDown<int> LimitUpDown { get; }
    internal CheckBox HiddenDirsBox { get; }
    internal Label FilterSummaryLabel { get; }
    internal TableView ResultsTable { get; }

    // Right pane — persistent skill detail panel.
    internal SkillDetailPaneView DetailPane { get; }

    internal DiscoverTabView(string actionsText, string welcomeText)
    {
        BorderStyle = LineStyle.None;
        SchemeName = SchemeNames.Base;

        // ── Left frame: search controls ──────────────────────────────────
        LeftFrame = new FrameView
        {
            Title = "Search Results",
            X = 0,
            Y = 0,
            Width = Dim.Percent(60),
            Height = Dim.Fill(),
        };

        var queryLabel = new Label { Text = "Search:", X = 0, Y = 0 };
        QueryField = new TextField
        {
            X = 8, Y = 0, Width = Dim.Fill(), Text = string.Empty,
        };
        TuiHelpers.ConfigureTextInput(QueryField, SkillViewStyling.BaseSchemeName);

        var ownerLabel = new Label { Text = "Owner:", X = 0, Y = 1 };
        OwnerField = new TextField { X = 8, Y = 1, Width = 22, Text = string.Empty };
        TuiHelpers.ConfigureTextInput(OwnerField, SkillViewStyling.BaseSchemeName);

        var limitLabel = new Label { Text = "Limit:", X = 32, Y = 1 };
        LimitUpDown = new NumericUpDown<int>
        {
            X = 39,
            Y = 1,
            Value = GhSkillSearchService.DefaultLimit,
            Increment = 10,
        };

        var agentLabel = new Label { Text = "Agent:", X = 0, Y = 2 };
        AgentField = new TextField { X = 8, Y = 2, Width = 22, Text = string.Empty };
        TuiHelpers.ConfigureTextInput(AgentField, SkillViewStyling.BaseSchemeName);

        HiddenDirsBox = new CheckBox
        {
            X = 0,
            Y = 3,
            Text = "_show hidden dirs",
        };

        FilterSummaryLabel = new Label
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Text = BuildFilterSummaryForTests(
                owner: string.Empty,
                agent: string.Empty,
                limit: GhSkillSearchService.DefaultLimit,
                hiddenDirs: false),
        };

        ResultsTable = new TableView
        {
            X = 0,
            Y = 4,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            FullRowSelect = true,
        };
        TuiHelpers.ConfigureTableKeyBindings(ResultsTable);
        TuiHelpers.ConfigureTableScheme(ResultsTable);
        TuiHelpers.ConfigureTableChrome(ResultsTable);

        LeftFrame.Add(
            queryLabel, QueryField,
            FilterSummaryLabel,
            ResultsTable);

        // ── Right pane: persistent detail panel ───────────────────────────
        DetailPane = new SkillDetailPaneView(actionsText, welcomeText)
        {
            X = Pos.Right(LeftFrame),
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
        };

        // Apply scheme to all owned controls so the workspace is self-contained.
        TuiHelpers.ApplyScheme(SkillViewStyling.BaseSchemeName,
            LeftFrame,
            queryLabel, QueryField,
            FilterSummaryLabel,
            ownerLabel, OwnerField,
            limitLabel, LimitUpDown,
            agentLabel, AgentField,
            HiddenDirsBox,
            ResultsTable,
            DetailPane,
            DetailPane.MetadataPane, DetailPane.PreviewPane,
            DetailPane.PreviewRawPane, DetailPane.LogPane);

        Add(LeftFrame, DetailPane);
    }

    internal static string BuildFilterSummaryForTests(
        string owner,
        string agent,
        int limit,
        bool hiddenDirs)
    {
        var trimmedOwner = owner.Trim();
        var trimmedAgent = agent.Trim();
        var ownerText = string.IsNullOrWhiteSpace(owner) ? "all owners" : $"owner {owner.Trim()}";
        var agentText = string.IsNullOrWhiteSpace(agent) ? "any agent" : $"agent {agent.Trim()}";
        var safeLimit = Math.Clamp(limit, 1, 200);
        var hidden = hiddenDirs ? "hidden dirs on" : "hidden dirs off";

        if (trimmedOwner.Length == 0
            && trimmedAgent.Length == 0
            && safeLimit == GhSkillSearchService.DefaultLimit
            && !hiddenDirs)
        {
            return string.Empty;
        }

        return $"Filters: {ownerText} · {agentText} · limit {safeLimit} · {hidden}";
    }

    /// Returns a human-readable summary of the current Discover filter state,
    /// using "Locations:" wording (not "roots") so scripts and tests can
    /// assert on the exact vocabulary.  Used by tests to verify workspace
    /// labelling without constructing a full TUI.
    internal static string BuildFacetSummaryForTests(
        string agent,
        string location,
        string provenance,
        bool hiddenDirs)
    {
        var sb = new System.Text.StringBuilder();

        if (!string.IsNullOrEmpty(agent))
        {
            if (sb.Length > 0) sb.Append("  ");
            sb.Append("Agent: ").Append(agent);
        }

        if (!string.IsNullOrEmpty(location))
        {
            if (sb.Length > 0) sb.Append("  ");
            sb.Append("Locations: ").Append(location);
        }

        if (!string.IsNullOrEmpty(provenance))
        {
            if (sb.Length > 0) sb.Append("  ");
            sb.Append("Provenance: ").Append(provenance);
        }

        if (sb.Length > 0) sb.Append("  ");
        sb.Append("Hidden dirs: ").Append(hiddenDirs ? "on" : "off");

        return sb.ToString();
    }
}
