namespace SkillView.Ui;

internal enum ActionScreen { Discover, Installed, Issues, UpdateReview, Doctor }

/// Display metadata only. Existing handlers and their admission gates own behavior.
internal sealed record ActionLabel(string Key, string Label, string Explanation, params string[] Aliases)
{
    internal StatusHint Hint => new(Key, Label);
    internal string ButtonText => Key.Length == 0 ? Label : $"{Label} ({Key})";
    internal string Legend => $"[{Key}] {Label}";
    internal string Markdown => $"- **{(Key.Length == 0 ? Label : string.Join(" / ", new[] { Key }.Concat(Aliases)))}** — "
        + (Key.Length == 0 ? Explanation : Label + ": " + Explanation);
}

/// Small, pure vocabulary shared by Help, shell hints and corresponding controls.
/// No key matching or command dispatch belongs here.
internal static class ActionLabels
{
    internal static readonly ActionLabel Help = new("?", "Help", "show keys for the current screen", "F1");
    internal static readonly ActionLabel Quit = new("Ctrl+Q", "Quit", "quit from anywhere, including text fields (also Ctrl+Alt+Q)");
    internal static readonly ActionLabel ListQuit = new("q", "Quit", "quit outside text fields", "Q");
    internal static readonly ActionLabel Tabs = new("1/2/3", "Tabs", "open Discover / Installed / Issues; outside Installed, left/right also cycle when the focused list or pane does not use them");
    internal static readonly ActionLabel Locations = new("F5", "Locations", "select workspaces, scan roots and agent profiles");
    internal static readonly ActionLabel History = new("F4", "History", "inspect retained operations and review eligible Abandon or Discard Undo data actions; each requires separate confirmation");
    internal static readonly ActionLabel Back = new("Esc", "Back", "leave the field, return from detail, or leave this screen; at a primary list use q to quit");
    internal static readonly ActionLabel Focus = new("Tab", "Move focus", "reach visible controls; Shift+Tab moves back");
    internal static readonly ActionLabel Selection = new("↑ / ↓", "Selection", "move through the focused list");
    internal static readonly ActionLabel Details = new("F2", "Details", "toggle summary / technical evidence for the same selection");
    internal static readonly ActionLabel Refresh = new("r", "Refresh", "refresh local inventory; does not check upstream updates");
    internal static readonly ActionLabel CheckUpdates = new("C", "Check updates", "check all eligible copies; browsing stays available; C cancels a running check");
    internal static readonly ActionLabel CancelCheck = CheckUpdates with { Label = "Cancel check" };
    internal static readonly ActionLabel Cleanup = new("c", "Cleanup", "open cleanup review; no deletion without confirmation");
    internal static readonly ActionLabel Doctor = new("d", "Doctor", "show environment diagnostics", "D");
    internal static readonly ActionLabel Filters = new("f", "Filters", "open Discover filters (owner, agent, limit, hidden directories)", "F");
    internal static readonly ActionLabel Search = new("/", "Search", "focus the Installed search field; Esc leaves it", "f", "F");
    internal static readonly ActionLabel DiscoverSearch = new("/", "Search", "focus the Discover query field; Enter or Ctrl+J submits the query");
    internal static readonly ActionLabel Preview = new("Enter", "Preview", "load the selected search result while the results list has focus", "p", "P", "v", "V", "→", "Ctrl+J");
    internal static readonly ActionLabel Install = new("i", "Install", "review the selected result in compact confirmation before installing");
    internal static readonly ActionLabel AdvancedInstall = new("I", "Advanced install", "review the selected result in the advanced wizard");
    internal static readonly ActionLabel PickSkills = new("A", "Pick skills", "list skills from the selected repository and choose a subset; listing failure offers Retry or Cancel");
    internal static readonly ActionLabel OpenRepo = new("o", "Open", "open the selected repository in the browser", "O");
    internal static readonly ActionLabel OpenPath = new("o", "Open", "open the selected installed path", "O");
    internal static readonly ActionLabel Previous = new("", "Previous", "use the button to load the previous submitted search page");
    internal static readonly ActionLabel Next = new("", "Next", "use the button to load the next page when offered; paging uses the submitted query and filters");
    internal static readonly ActionLabel Logs = new("l", "Logs", "show the Discover log pane", "L");
    internal static readonly ActionLabel ReturnPreview = Logs with { Label = "Preview", Explanation = "return from logs to the Discover preview when focus is outside the read-only log editor" };
    internal static readonly ActionLabel PreviewMode = new("e", "Raw/rendered", "toggle raw / rendered Discover preview while logs are hidden", "E");
    internal static readonly ActionLabel Sort = new("s", "Sort", "cycle sort order", "S");
    internal static readonly ActionLabel Browse = new("Enter", "Details/Expand", "expand a source group or browse the selected exact copy");
    internal static readonly ActionLabel Expand = new("← / →", "Collapse/Expand", "collapse / expand the selected Installed source group");
    internal static readonly ActionLabel ReviewUpdate = new("u", "Review update", "review an update for the selected exact copy", "U");
    internal static readonly ActionLabel ReviewIssue = new("Enter", "Open", "expand a family, open an exact child's comparison, inspect registry guidance, or open an exact item's review (narrow layouts show details first); never applies a change");
    internal static readonly ActionLabel ReviewExactIssue = new("u", "Review", "open the existing review for the selected eligible exact copy", "U");
    internal static readonly ActionLabel Guided = new("g", "Guided", "start a sequence only with eligible exact items and no active sequence, review or preference work");
    internal static readonly ActionLabel ReviewNext = new("n", "Review next", "review one next exact item after prior reconciliation; active sequence only");
    internal static readonly ActionLabel SkipGuided = new("s", "Skip", "skip the unstarted guided item; active sequence only");
    internal static readonly ActionLabel StopGuided = new("x", "Stop", "stop only the guided request; active sequence only");
    internal static readonly ActionLabel KeepBoth = new("k", "Keep both", "mark the selected observed copy difference intentional; an intentional difference offers Restore suggestions instead");
    internal static readonly ActionLabel RestoreSuggestions = KeepBoth with { Label = "Restore" };
    internal static readonly ActionLabel Choices = new("K", "Choices", "review saved intentional differences; resetting them requires separate confirmation");
    internal static readonly ActionLabel MoreActions = new("", "More actions", "open, remove, package check and contextual local-source reviews; unavailable actions explain their limits");
    internal static readonly ActionLabel Remove = new("x", "Remove", "review removal of the selected installed copy", "X");
    internal static readonly ActionLabel PackageCheck = new("K", "Package check", "check the selected copy without launching a client");
    internal static readonly ActionLabel Reimport = new("R", "Reimport local", "review a local reimport when applicable");
    internal static readonly ActionLabel SwitchSource = new("T", "Switch local source", "review a local source switch when applicable");
    internal static readonly ActionLabel PinFilter = new("P", "Pin filter", "cycle the Installed pin filter");
    internal static readonly ActionLabel ScopeFilter = new("G", "Scope filter", "cycle all / user / project / custom; retain every shared consumer");
    internal static readonly ActionLabel PrepareUpdate = ReviewUpdate with { Explanation = "prepare a candidate for this exact copy when available; approval is separate", Aliases = ["U", "c"] };
    internal static readonly ActionLabel SourceDiff = new("D", "View source changes", "when a saved candidate is shown, open frozen saved source changes; opening Diff grants no source or retention approval", "d");
    internal static readonly ActionLabel PlanUnpin = new("p", "Plan unpin only", "prepare a separate pin-only review for this exact copy when available");

    internal static ActionLabel[] ScreenActions(ActionScreen screen, bool showingLogs = false, bool sourceDiffVisible = false) => screen switch
    {
        ActionScreen.Discover => [DiscoverSearch, Preview, Filters, Install, AdvancedInstall, PickSkills, Previous, Next,
            OpenRepo, Sort, showingLogs ? ReturnPreview : Logs, PreviewMode,
            new("", "Rendered Markdown", "Ctrl+A selects all; Ctrl+C copies selected text; clicking a link opens it"),
            new("r", "Refresh", "rerun the current Discover query, if one is entered", "R")],
        ActionScreen.Installed => [Browse, Expand, Search, Details, CheckUpdates, ReviewUpdate, MoreActions, Cleanup,
            Remove, OpenPath, PackageCheck, Reimport, SwitchSource, Sort, PinFilter, ScopeFilter, Refresh],
        ActionScreen.Issues => [ReviewIssue, ReviewExactIssue, Details, CheckUpdates, KeepBoth, Choices, Guided, ReviewNext, SkipGuided, StopGuided, Refresh with { Aliases = ["R"] }, Cleanup with { Explanation = "review the selected exact cleanup finding, or filter Issues to cleanup findings; no deletion without confirmation" }, Doctor],
        ActionScreen.UpdateReview => [Details, .. sourceDiffVisible ? new[] { SourceDiff } : [], PrepareUpdate, PlanUnpin, Refresh with { Aliases = ["R"] },
            new("", "Review controls", "Choose version, source acknowledgement, retention approval and apply remain separate visible controls; F2 includes frozen source changes")],
        ActionScreen.Doctor => [Doctor, new("/", "Search", "return to Discover and focus its query field")],
        _ => [],
    };

    internal static List<StatusHint> Footer(ActionScreen screen, bool showingLogs = false, bool checkingUpdates = false)
    {
        var check = checkingUpdates ? CancelCheck : CheckUpdates;
        ActionLabel[] actions = screen switch
        {
            ActionScreen.Discover when showingLogs => [ReturnPreview, Help, Quit],
            ActionScreen.Discover => [Tabs, Filters, Logs, Help, Quit],
            // The strip retains rightmost hints first: keep primary actions near Help.
            ActionScreen.Installed => [Cleanup, Refresh, check, Browse, Search, Help, Quit],
            ActionScreen.Issues => [Cleanup, Doctor, Refresh, Details, check, ReviewIssue, Help, Quit],
            ActionScreen.UpdateReview => [Details, Back, Help, Quit],
            _ => [Back, Help, Quit],
        };
        return actions.Select(action => action.Hint).ToList();
    }

    internal static string DiscoverLegend(bool hasResults) => string.Join("    ",
        (hasResults ? new[] { Preview, Install, OpenRepo, Help } : [Filters, Help]).Select(action => action.Legend));
}
