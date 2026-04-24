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

    /// Lightweight markdown-to-plaintext formatter for the preview pane.
    /// Strips common markdown syntax (bold, italic, headings, horizontal
    /// rules) and collapses excessive blank lines to improve readability
    /// in a plain-text TextView. Code fences are preserved verbatim.
    internal static string FormatPreviewText(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "(empty preview)";

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var output = new StringBuilder(markdown.Length);
        var consecutiveBlanks = 0;
        var inCodeBlock = false;

        foreach (var rawLine in lines)
        {
            // Track code fences — indent content, show language hint
            if (rawLine.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCodeBlock = !inCodeBlock;
                consecutiveBlanks = 0;
                if (inCodeBlock)
                {
                    var lang = rawLine.TrimStart().Length > 3
                        ? rawLine.TrimStart()[3..].Trim()
                        : "";
                    output.AppendLine(lang.Length > 0 ? $"  ┌─ {lang} ─" : "  ┌─");
                }
                else
                {
                    output.AppendLine("  └─");
                }
                continue;
            }

            if (inCodeBlock)
            {
                consecutiveBlanks = 0;
                output.Append("  │ ");
                output.AppendLine(rawLine);
                continue;
            }

            var trimmed = rawLine.TrimEnd();

            // Skip horizontal rules
            if (IsHorizontalRule(trimmed))
            {
                continue;
            }

            // Collapse excessive blank lines (max 1 consecutive)
            if (trimmed.Length == 0)
            {
                consecutiveBlanks++;
                if (consecutiveBlanks <= 1)
                {
                    output.AppendLine();
                }
                continue;
            }

            consecutiveBlanks = 0;

            // Strip heading markers — uppercase + underline for visual separation
            if (trimmed.StartsWith('#'))
            {
                var headingText = trimmed.TrimStart('#', ' ');
                if (output.Length > 0)
                {
                    output.AppendLine();
                }
                var upper = headingText.ToUpperInvariant();
                output.AppendLine(upper);
                output.AppendLine(new string('─', Math.Min(upper.Length, 60)));
                continue;
            }

            // Convert markdown list markers to bullet characters
            var stripped = trimmed.TrimStart();
            if (stripped.StartsWith("- ", StringComparison.Ordinal) || stripped.StartsWith("* ", StringComparison.Ordinal))
            {
                var indent = trimmed.Length - stripped.Length;
                var bullet = new string(' ', indent) + "• " + StripInlineMarkdown(stripped[2..]);
                output.AppendLine(bullet);
                continue;
            }

            // Numbered lists: keep number, clean up inline markdown
            if (stripped.Length > 1 && char.IsDigit(stripped[0]))
            {
                var dotIdx = stripped.IndexOf(". ", StringComparison.Ordinal);
                if (dotIdx > 0 && dotIdx <= 3)
                {
                    var indent = trimmed.Length - stripped.Length;
                    var numbered = new string(' ', indent) + stripped[..(dotIdx + 2)] + StripInlineMarkdown(stripped[(dotIdx + 2)..]);
                    output.AppendLine(numbered);
                    continue;
                }
            }

            // Strip inline markdown: **bold**, *italic*, __bold__, _italic_, `code`
            var formatted = StripInlineMarkdown(trimmed);
            output.AppendLine(formatted);
        }

        return output.ToString().TrimEnd();
    }

    private static bool IsHorizontalRule(string line)
    {
        var stripped = line.Replace(" ", "");
        return stripped.Length >= 3
               && (stripped.All(c => c == '-') || stripped.All(c => c == '*') || stripped.All(c => c == '_'));
    }

    private static string StripInlineMarkdown(string text)
    {
        // Strip bold: **text** or __text__
        // Strip italic: *text* or _text_ (single)
        // Strip code: `text`
        // Process in order: bold first (longer markers), then italic
        var result = new StringBuilder(text.Length);
        var i = 0;

        while (i < text.Length)
        {
            // Bold: ** or __
            if (i + 1 < text.Length && ((text[i] == '*' && text[i + 1] == '*') || (text[i] == '_' && text[i + 1] == '_')))
            {
                var marker = text[i];
                var end = text.IndexOf(new string(marker, 2), i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    result.Append(text, i + 2, end - i - 2);
                    i = end + 2;
                    continue;
                }
            }

            // Inline code: `text`
            if (text[i] == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    result.Append(text, i + 1, end - i - 1);
                    i = end + 1;
                    continue;
                }
            }

            // Italic: * or _ (single, only if not preceded by another marker)
            if ((text[i] == '*' || text[i] == '_') && i + 1 < text.Length && text[i + 1] != text[i])
            {
                var marker = text[i];
                var end = text.IndexOf(marker, i + 1);
                if (end > i + 1 && (end + 1 >= text.Length || text[end + 1] != marker))
                {
                    result.Append(text, i + 1, end - i - 1);
                    i = end + 1;
                    continue;
                }
            }

            result.Append(text[i]);
            i++;
        }

        return result.ToString();
    }

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
        view.SchemeName = schemeName;
        view.SetScheme(CreateReadOnlyPaneScheme());
    }

    internal static void ConfigureTextInput(TextField view, string schemeName)
    {
        view.SchemeName = schemeName;
        view.SetScheme(CreateEditableInputScheme());
    }

    /// Key bindings help text for the main window, shared between the
    /// welcome message and the F1 help dialog.
    internal const string HelpText =
        "/  focus the search box\n" +
        "Enter, →, p, v  preview when results are focused\n" +
        "l  show or hide logs\n" +
        "d  open Doctor\n" +
        "I  show installed skills\n" +
        "s  open advanced search\n" +
        "u  update installed skills\n" +
        "c  review cleanup candidates\n" +
        "   in Installed: x removes the selected skill\n" +
        "F1 show this help\n" +
        "q  quit";

    /// Compact single-line hint shown in the welcome/preview pane.
    internal const string WelcomeHint =
        "/ search · →/p/v preview · l logs · d doctor · I installed · s advanced search · u update · c cleanup · F1 help · q quit";

    internal const string PreviewHint =
        "Select a result and press Enter, →, p, or v to preview.\n\nTip: In Warp terminal, use Ctrl+J instead of Enter.";

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

    /// Apply consistent column widths to a results TableView. Column order
    /// must match the EnumerableTableSource definition: Skill, Repo, ★, Description.
    /// Looks up columns by header name so order changes don't break styling.
    internal static void ApplyColumnStyles(TableView table)
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
                    cs.MinWidth = 10;
                    cs.MaxWidth = 24;
                    break;
                case "Repo":
                    cs.MinWidth = 12;
                    cs.MaxWidth = 30;
                    break;
                case "★":
                    cs.MinWidth = 1;
                    cs.MaxWidth = 5;
                    break;
                case "Description":
                    cs.MinWidth = 15;
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
        table.KeyBindings.Add(KeyCode.P, Command.Accept);
        table.KeyBindings.Add(KeyCode.P | KeyCode.ShiftMask, Command.Accept);
        table.KeyBindings.Add(KeyCode.V, Command.Accept);
        table.KeyBindings.Add(KeyCode.V | KeyCode.ShiftMask, Command.Accept);
        table.KeyBindings.Add(KeyCode.CursorRight, Command.Accept);
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
