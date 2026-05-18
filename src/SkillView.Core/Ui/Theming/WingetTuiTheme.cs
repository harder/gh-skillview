using SkillView.Bootstrapping;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace SkillView.Ui.Theming;

/// Color palette and scheme factories matching the winget-tui (shanselman/winget-tui)
/// Ratatui implementation, ported to Terminal.Gui 2.1 24-bit color attributes.
///
/// All colors are RGB literals — the truecolor codepath fires on any terminal
/// whose $COLORTERM declares truecolor / 24bit support. Terminals that don't
/// will see Terminal.Gui's nearest-StandardColor fallback automatically.
internal static class WingetTuiTheme
{
    // Palette — see plan §"Theme — exact winget-tui palette"
    internal static readonly Color Accent          = new(0xEE, 0xC9, 0x8D); // gold
    internal static readonly Color TextPrimary     = new(0xE8, 0xDC, 0xB7); // warm beige
    internal static readonly Color TextSecondary   = new(0x9E, 0x9E, 0x9E); // dim gray
    internal static readonly Color Surface         = new(0x2D, 0x2D, 0x2D); // panel
    internal static readonly Color Background      = new(0x1E, 0x1E, 0x1E); // app base
    internal static readonly Color Success         = new(0x56, 0xB9, 0x7F); // mint green
    internal static readonly Color Danger          = new(0xE7, 0x48, 0x56); // red
    internal static readonly Color Info            = new(0x61, 0xAF, 0xEF); // blue
    internal static readonly Color Selection       = new(0xC6, 0x78, 0xDD); // purple
    internal static readonly Color Black           = new(0, 0, 0);
    internal static readonly Color DarkGray        = new(0x44, 0x44, 0x44);

    /// Base scheme: dim background, warm primary text, gold accent on selection.
    /// Mirrors winget-tui's `Style::default()` + `theme::selected_row()` pairing.
    internal static Scheme CreateBaseScheme()
    {
        var normal  = new Attribute(TextPrimary, Background);
        var focus   = new Attribute(Black, Accent, TextStyle.Bold);   // selected_row()
        var active  = focus;
        var disabled = new Attribute(TextSecondary, Background);
        var hot     = new Attribute(Accent, Background);             // dimmed accent for hotkey hints

        return new Scheme
        {
            Normal    = normal,
            HotNormal = hot,
            Focus     = focus,
            HotFocus  = new Attribute(Accent, Accent, TextStyle.Bold), // matches selected bg
            Active    = active,
            HotActive = hot,
            Highlight = focus,
            Editable  = normal,
            ReadOnly  = normal,
            Disabled  = disabled,
            Code      = new Attribute(Info, Background),
        };
    }

    /// Dialog / modal scheme: slightly lighter surface so overlays float
    /// visually above the base background.
    internal static Scheme CreateDialogScheme()
    {
        var normal  = new Attribute(TextPrimary, Surface);
        var focus   = new Attribute(Black, Accent, TextStyle.Bold);
        var disabled = new Attribute(TextSecondary, Surface);
        var hot     = new Attribute(Accent, Surface);

        return new Scheme
        {
            Normal    = normal,
            HotNormal = hot,
            Focus     = focus,
            HotFocus  = focus,
            Active    = focus,
            HotActive = hot,
            Highlight = focus,
            Editable  = normal,
            ReadOnly  = normal,
            Disabled  = disabled,
            Code      = new Attribute(Info, Surface),
        };
    }

    /// Status bar scheme tuned for a notification severity. Replaces the old
    /// StandardColor-only `CreateStatusScheme` for `AppTheme.Default`.
    internal static Scheme CreateStatusScheme(TuiHelpers.NotificationLevel level)
    {
        var (fg, bg) = level switch
        {
            TuiHelpers.NotificationLevel.Success => (Black,       Success),
            TuiHelpers.NotificationLevel.Warn    => (Black,       Accent),
            TuiHelpers.NotificationLevel.Error   => (TextPrimary, Danger),
            _                                    => (TextPrimary, Surface),
        };
        var normal = new Attribute(fg, bg);
        return AllSame(normal);
    }

    /// Read-only pane (preview, logs, detail metadata).
    internal static Scheme CreateReadOnlyPaneScheme()
    {
        var normal   = new Attribute(TextPrimary, Background);
        var focus    = new Attribute(TextPrimary, Surface);
        var disabled = new Attribute(TextSecondary, Background);

        return new Scheme
        {
            Normal    = normal,
            HotNormal = normal,
            Focus     = focus,
            HotFocus  = focus,
            Active    = normal,
            HotActive = focus,
            Highlight = focus,
            Editable  = normal,
            ReadOnly  = normal,
            Disabled  = disabled,
            Code      = new Attribute(Info, Background),
        };
    }

    /// Editable text input — distinct background so the field is visible
    /// against panel fills, accent on focus.
    internal static Scheme CreateEditableInputScheme()
    {
        var normal   = new Attribute(TextPrimary, Surface);
        var focus    = new Attribute(Black, Accent);
        var disabled = new Attribute(TextSecondary, Background);

        return new Scheme
        {
            Normal    = normal,
            HotNormal = normal,
            Focus     = focus,
            HotFocus  = focus,
            Active    = focus,
            HotActive = focus,
            Highlight = focus,
            Editable  = normal,
            ReadOnly  = normal,
            Disabled  = disabled,
            Code      = normal,
        };
    }

    /// TableView scheme — selected row uses Accent bg to match winget-tui
    /// `selected_row()`. Multi-select "marked" rows are colored at the cell
    /// level by individual table renderers, not via Scheme.
    internal static Scheme CreateTableScheme()
    {
        var normal   = new Attribute(TextPrimary, Background);
        var selected = new Attribute(Black, Accent, TextStyle.Bold);
        var disabled = new Attribute(TextSecondary, Background);

        return new Scheme
        {
            Normal    = normal,
            HotNormal = normal,
            Focus     = selected,
            HotFocus  = selected,
            Active    = selected,
            HotActive = selected,
            Highlight = selected,
            Editable  = normal,
            ReadOnly  = normal,
            Disabled  = disabled,
            Code      = normal,
        };
    }

    /// Register the winget-tui-flavored schemes with Terminal.Gui's
    /// SchemeManager. AddScheme already replaces existing entries, so this is
    /// idempotent and works on built-in scheme names. Only registers schemes
    /// for AppTheme.Default; HighContrast keeps using the StandardColor schemes
    /// in TuiHelpers so high-contrast users get terminal-default 16-color
    /// fidelity.
    internal static void Register(AppTheme theme)
    {
        if (theme == AppTheme.HighContrast)
        {
            return;
        }

        SchemeManager.AddScheme(SchemeNames.Base,   CreateBaseScheme());
        SchemeManager.AddScheme(SchemeNames.Dialog, CreateDialogScheme());
    }

    private static Scheme AllSame(Attribute attr) => new()
    {
        Normal    = attr,
        HotNormal = attr,
        Focus     = attr,
        HotFocus  = attr,
        Active    = attr,
        HotActive = attr,
        Highlight = attr,
        Editable  = attr,
        ReadOnly  = attr,
        Disabled  = attr,
        Code      = attr,
    };
}

/// Stable scheme name constants. `Base` and `Dialog` shadow Terminal.Gui's
/// built-in names so `View.SchemeName = "Base"` picks up the winget-tui
/// version after Register() runs.
internal static class SchemeNames
{
    internal const string Base   = "Base";
    internal const string Dialog = "Dialog";
}
