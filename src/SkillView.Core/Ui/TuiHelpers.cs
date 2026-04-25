using System.Text;
using SkillView.Inventory;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Attribute = Terminal.Gui.Drawing.Attribute;

// ReSharper disable once CheckNamespace — keep flat SkillView.Ui namespace

namespace SkillView.Ui;

/// Shared formatting utilities for TUI screens. Keeps column rendering
/// consistent and avoids duplicating truncation / label logic.
internal static class TuiHelpers
{
    /// Detect whether we're running inside Warp terminal, which has known
    /// issues with Enter key delivery to TUI apps on macOS.
    internal static bool IsWarpTerminal { get; } =
        Environment.GetEnvironmentVariable("TERM_PROGRAM")
            ?.Contains("Warp", StringComparison.OrdinalIgnoreCase) == true;

    /// Compact Unicode badge for a known agent. Falls back to the first
    /// letter (uppercased) for unknown agents so unfamiliar IDs still
    /// render as a one-cell glyph in the Agents column.
    internal static string AgentIcon(string? agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return "?";
        var key = agentId.Trim().ToLowerInvariant();
        return key switch
        {
            "claude" or "claude-code" or "claude_code" or "claudecode" => "⟁",
            "cursor" => "◫",
            "codex" or "openai-codex" => "◎",
            "gemini" or "gemini-cli" => "✦",
            "opencode" or "open-code" => "⬡",
            _ => char.ToUpperInvariant(key[0]).ToString(),
        };
    }

    /// Concatenate one icon per distinct agent ID, preserving discovery
    /// order. Returns "—" when the list is empty.
    internal static string AgentBadges(IEnumerable<string> agentIds)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        foreach (var id in agentIds)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (!seen.Add(id)) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(AgentIcon(id));
        }
        return sb.Length == 0 ? "—" : sb.ToString();
    }

    /// Severity of a transient status message. Drives the color of the
    /// notification bar and is the only thing the caller has to decide.
    internal enum NotificationLevel
    {
        Info,
        Success,
        Warn,
        Error,
    }

    /// Build a Scheme whose Normal attribute matches the requested
    /// notification level. Intended for the bottom status bar Label.
    internal static Scheme CreateStatusScheme(NotificationLevel level)
    {
        var (fg, bg) = level switch
        {
            NotificationLevel.Success => (StandardColor.Black, StandardColor.Green),
            NotificationLevel.Warn => (StandardColor.Black, StandardColor.Yellow),
            NotificationLevel.Error => (StandardColor.White, StandardColor.Red),
            _ => (StandardColor.White, StandardColor.Black),
        };
        var normal = new Attribute(fg, bg);
        return new Scheme
        {
            Normal = normal,
            HotNormal = normal,
            Focus = normal,
            HotFocus = normal,
            Active = normal,
            HotActive = normal,
            Highlight = normal,
            Editable = normal,
            ReadOnly = normal,
            Disabled = normal,
            Code = normal,
        };
    }

    /// Open a URL or local path in the platform's default handler — browser
    /// for http(s) URLs, Explorer/Finder/file-manager for directories.
    /// Returns true on success. Uses ProcessStartInfo with UseShellExecute on
    /// Windows; falls back to `xdg-open` on Linux and `open` on macOS so the
    /// AOT-published binary doesn't pull in shell-execute machinery it can't
    /// use on Unix.
    internal static bool OpenInDefaultHandler(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target)
                {
                    UseShellExecute = true,
                });
                return true;
            }
            var opener = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = opener,
                ArgumentList = { target },
                UseShellExecute = false,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
    /// Truncate text to `maxLen` characters, appending "…" if it was clipped.
    internal static string Truncate(string? text, int maxLen)
    {
        if (string.IsNullOrEmpty(text) || maxLen <= 0) return string.Empty;

        const string ellipsis = "…";
        if (text.GetColumns() <= maxLen) return text;
        if (maxLen <= ellipsis.GetColumns()) return ellipsis;

        var budget = maxLen - ellipsis.GetColumns();
        var width = 0;
        var builder = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var runeWidth = rune.GetColumns();
            if (width + runeWidth > budget)
            {
                break;
            }

            builder.Append(rune.ToString());
            width += runeWidth;
        }

        if (builder.Length == 0) return ellipsis;
        builder.Append(ellipsis);
        return builder.ToString();
    }

    /// Shorten a filesystem path for table display. Keeps the last `segments`
    /// path components and prefixes with "…/" when truncated.
    internal static string ShortenPath(string? path, int segments = 3)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var parts = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= segments) return path;
        return "…/" + string.Join("/", parts[^segments..]);
    }

    /// Human-friendly short labels for cleanup CandidateKind values so the
    /// "Kind" column doesn't overflow narrow terminals.
    internal static string ShortKind(CleanupClassifier.CandidateKind kind) => kind switch
    {
        CleanupClassifier.CandidateKind.Malformed => "malformed",
        CleanupClassifier.CandidateKind.SourceOrphaned => "orphan",
        CleanupClassifier.CandidateKind.Duplicate => "duplicate",
        CleanupClassifier.CandidateKind.EmptyDirectory => "empty-dir",
        CleanupClassifier.CandidateKind.BrokenSharedMapping => "broken-map",
        CleanupClassifier.CandidateKind.HiddenNestedResidue => "hidden-nest",
        CleanupClassifier.CandidateKind.BrokenSymlink => "broken-link",
        CleanupClassifier.CandidateKind.OrphanCanonicalCopy => "orphan-copy",
        _ => kind.ToString(),
    };

    /// Extract a short error snippet from stderr for inline display.
    /// Returns the first non-empty line, truncated to `maxLen`.
    internal static string ErrorSnippet(string? stderr, int maxLen = 60)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return string.Empty;
        foreach (var rawLine in stderr.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length > 0) return Truncate(line, maxLen);
        }
        return string.Empty;
    }

    internal static bool IsPreviewKey(Key key) =>
        key.KeyCode == KeyCode.Enter || key.AsRune.Value is 'v' or 'V' or 'p' or 'P';

    internal static void ApplyScheme(string schemeName, params View?[] views)
    {
        foreach (var view in views)
        {
            if (view is not null)
            {
                view.SchemeName = schemeName;
            }
        }
    }

    internal static void ConfigureReadOnlyPane(TextView view, string schemeName, bool wordWrap = true)
    {
        view.ReadOnly = true;
        view.WordWrap = wordWrap;
        view.ViewportSettings |= ViewportSettingsFlags.HasVerticalScrollBar;
        view.SchemeName = schemeName;
        view.SetScheme(CreateReadOnlyPaneScheme());
    }

    /// Configure a Terminal.Gui Markdown view for the preview pane. The
    /// built-in Markdown view uses Markdig to render styled headings, code
    /// blocks, tables, links, and lists with a built-in vertical scrollbar.
    internal static void ConfigureMarkdownPane(Markdown view, string schemeName)
    {
        view.SchemeName = schemeName;
        view.SetScheme(CreateReadOnlyPaneScheme());
    }

    internal static void ConfigureTextInput(TextField view, string schemeName)
    {
        view.SchemeName = schemeName;
        view.SetScheme(CreateEditableInputScheme());
    }

    /// Key bindings help text for the main window, shared between the
    /// welcome message and the F1 help dialog. The preview-key list adapts
    /// to Warp (which intercepts Enter) by promoting Ctrl+J + p/v.
    internal static string HelpText { get; } =
        "/  focus the search box\n" +
        (IsWarpTerminal
            ? "Ctrl+J, →, p, v  preview when results are focused\n"
            : "Enter, →, p, v  preview when results are focused\n") +
        "i  install the selected search result\n" +
        "o  open the skill (GitHub URL or local folder)\n" +
        "e  toggle raw / rendered SKILL.md preview\n" +
        "l  show or hide logs\n" +
        "d  open Doctor\n" +
        "I  show installed skills\n" +
        "u  update installed skills\n" +
        "c  review cleanup candidates\n" +
        "   in Installed: x removes the selected skill\n" +
        "F1 show this help\n" +
        "q  quit";

    /// Compact single-line hint shown in the welcome/preview pane. Same
    /// adaptation as `HelpText`: Warp gets Ctrl+J/p/v, others get →/p/v.
    internal static string WelcomeHint { get; } =
        (IsWarpTerminal ? "/ search · Ctrl+J/p/v preview" : "/ search · →/p/v preview")
        + " · i install · o open · e raw/render · l logs · d doctor · I installed · u update · c cleanup · F1 help · q quit";

    internal static string PreviewHint { get; } = IsWarpTerminal
        ? "Select a result and press Ctrl+J, p, or v to preview."
        : "Select a result and press Enter, →, p, or v to preview.";

    /// Create an explicit scheme for editable text inputs using only basic
    /// ANSI colors that render correctly on 16-, 256-, and true-color terminals.
    private static Scheme CreateEditableInputScheme()
    {
        // Black background avoids the teal/green hue that DarkSlateGray
        // produces on 16-color terminals.
        var normal = new Attribute(StandardColor.White, StandardColor.Black);
        var focus = new Attribute(StandardColor.Black, StandardColor.Cyan);
        var disabled = new Attribute(StandardColor.Gray, StandardColor.Black);

        return new Scheme
        {
            Normal = normal,
            HotNormal = normal,
            Focus = focus,
            HotFocus = focus,
            Active = focus,
            HotActive = focus,
            Highlight = focus,
            Editable = normal,
            ReadOnly = normal,
            Disabled = disabled,
            Code = normal,
        };
    }

    /// Create an explicit scheme for read-only panes (preview, logs).
    private static Scheme CreateReadOnlyPaneScheme()
    {
        var normal = new Attribute(StandardColor.White, StandardColor.Black);
        var focus = new Attribute(StandardColor.White, StandardColor.Blue);
        var disabled = new Attribute(StandardColor.Gray, StandardColor.Black);

        return new Scheme
        {
            Normal = normal,
            HotNormal = normal,
            Focus = focus,
            HotFocus = focus,
            Active = normal,
            HotActive = focus,
            Highlight = focus,
            Editable = normal,
            ReadOnly = normal,
            Disabled = disabled,
            Code = normal,
        };
    }

    /// Distribute a budget of `available` cells across columns according to
    /// per-column min widths and weights. Columns with weight 0 stay at min.
    /// The remaining width (after minimums are paid) is split by weight and
    /// added to each non-zero-weight column. Useful for proportional, terminal-
    /// width-aware truncation in TableView row producers.
    internal static int[] DistributeWidths(int available, IReadOnlyList<(int Min, double Weight)> specs)
    {
        var n = specs.Count;
        var widths = new int[n];
        if (n == 0) return widths;
        var totalMin = 0;
        var totalWeight = 0d;
        for (var i = 0; i < n; i++)
        {
            widths[i] = specs[i].Min;
            totalMin += specs[i].Min;
            totalWeight += specs[i].Weight;
        }
        var leftover = available - totalMin;
        if (leftover <= 0 || totalWeight <= 0) return widths;
        for (var i = 0; i < n; i++)
        {
            if (specs[i].Weight > 0)
            {
                widths[i] += (int)Math.Round(leftover * specs[i].Weight / totalWeight);
            }
        }
        return widths;
    }

    /// Apply per-column widths to a results TableView. The Skill / Repo / ★ /
    /// Description headers are matched by name so the row producer's order
    /// can change without breaking styling. The last column expands to fill
    /// any rounding leftover.
    internal static void ApplyColumnStyles(TableView table, int skillWidth, int repoWidth, int starsWidth, int descWidth)
    {
        if (table.Table is null) return;

        var style = table.Style;
        style.ExpandLastColumn = true;

        for (var i = 0; i < table.Table.Columns; i++)
        {
            var header = table.Table.ColumnNames[i];
            var cs = style.GetOrCreateColumnStyle(i);
            switch (header)
            {
                case "Skill":
                    cs.MinWidth = Math.Min(8, skillWidth);
                    cs.MaxWidth = Math.Max(8, skillWidth);
                    break;
                case "Repo":
                    cs.MinWidth = Math.Min(8, repoWidth);
                    cs.MaxWidth = Math.Max(8, repoWidth);
                    break;
                case "★":
                    cs.MinWidth = 1;
                    cs.MaxWidth = Math.Max(3, starsWidth);
                    break;
                case "Description":
                    cs.MinWidth = Math.Min(15, descWidth);
                    break;
            }
        }
    }

    /// Apply a high-contrast color scheme to a TableView so the selected
    /// row is clearly visible (inverted colors for Focus state).
    internal static void ConfigureTableScheme(TableView table)
    {
        var normal = new Attribute(StandardColor.White, StandardColor.Black);
        var selected = new Attribute(StandardColor.Black, StandardColor.Cyan);
        var disabled = new Attribute(StandardColor.Gray, StandardColor.Black);

        table.SetScheme(new Scheme
        {
            Normal = normal,
            HotNormal = normal,
            Focus = selected,
            HotFocus = selected,
            Active = selected,
            HotActive = selected,
            Highlight = selected,
            Editable = normal,
            ReadOnly = normal,
            Disabled = disabled,
            Code = normal,
        });
    }

    /// Disable type-to-search on a TableView. Terminal.Gui v2 RC4's
    /// OnKeyDown intercepts ALL unbound letter keys for type-to-search
    /// before the KeyDown event fires, preventing single-letter shortcuts
    /// from reaching event handlers. Replacing the Matcher disables that.
    /// TODO(tg2): remove once upstream supports CollectionNavigator = null
    /// without NRE.
    internal static void DisableTypeToSearch(TableView table)
    {
        table.CollectionNavigator.Matcher = NoSearchMatcher.Instance;
    }

    /// Disable type-to-search on a TableView and register preview shortcut
    /// key bindings (p, v → Command.Accept → CellActivated).
    ///
    /// Terminal.Gui v2 RC4 has a known issue where TableView.OnKeyDown
    /// intercepts ALL unbound letter keys for type-to-search *before* the
    /// KeyDown event fires. This prevents single-letter shortcuts from
    /// reaching event handlers. Replacing the Matcher disables that feature
    /// while adding explicit bindings routes p/v through CellActivated.
    internal static void ConfigureTableKeyBindings(TableView table)
    {
        DisableTypeToSearch(table);

        // Route p/v/→ through CellActivated (same path as Enter).
        // Right arrow is intuitive: it "points toward" the preview pane.
        // CursorRight already has a default binding in TableView, so replace it.
        table.KeyBindings.Add(KeyCode.P, Command.Accept);
        table.KeyBindings.Add(KeyCode.P | KeyCode.ShiftMask, Command.Accept);
        table.KeyBindings.Add(KeyCode.V, Command.Accept);
        table.KeyBindings.Add(KeyCode.V | KeyCode.ShiftMask, Command.Accept);
        table.KeyBindings.ReplaceCommands(KeyCode.CursorRight, Command.Accept);
    }
}

/// Matcher that rejects all keys, effectively disabling TableView's
/// built-in type-to-search (CollectionNavigator) feature. Setting
/// CollectionNavigator to null is documented as supported but causes
/// a NullReferenceException in TG2 v2 RC4; this workaround avoids
/// the NRE while achieving the same effect.
internal sealed class NoSearchMatcher : ICollectionNavigatorMatcher
{
    internal static readonly NoSearchMatcher Instance = new();
    public bool IsCompatibleKey(Key key) => false;
    public bool IsMatch(string search, object value) => false;
}
