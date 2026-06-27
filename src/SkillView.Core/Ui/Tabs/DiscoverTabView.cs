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

        // Owner, Agent, Limit, and HiddenDirs are filter state holders surfaced
        // through the [f] dialog rather than rendered inline. They are not added
        // to LeftFrame so they don't appear in the UI, but SkillViewApp reads and
        // writes them directly, and their events (ValueChanged, HasFocusChanged,
        // KeyDown) are wired for interaction-tracking and post-dialog refresh.
        OwnerField = new TextField { Text = string.Empty };
        LimitUpDown = new NumericUpDown<int>
        {
            Value = GhSkillSearchService.DefaultLimit,
            Increment = 10,
        };
        AgentField = new TextField { Text = string.Empty };
        HiddenDirsBox = new CheckBox();

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
