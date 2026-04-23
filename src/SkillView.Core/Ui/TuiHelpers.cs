using SkillView.Inventory;

namespace SkillView.Ui;

/// Shared formatting utilities for TUI screens. Keeps column rendering
/// consistent and avoids duplicating truncation / label logic.
internal static class TuiHelpers
{
    /// Truncate text to `maxLen` characters, appending "…" if it was clipped.
    internal static string Truncate(string? text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= maxLen ? text : string.Concat(text.AsSpan(0, maxLen - 1), "…");
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

    /// Key bindings help text for the main window, shared between the
    /// welcome message and the F1 help dialog.
    internal const string HelpText =
        "/  focus search\n" +
        "v  preview selected\n" +
        "l  toggle logs pane\n" +
        "d  doctor (environment + gh capabilities)\n" +
        "I  installed skills inventory\n" +
        "s  full search screen (owner/limit, install staging)\n" +
        "u  update installed skills (dry-run + execute)\n" +
        "c  cleanup screen (malformed, orphan, duplicate)\n" +
        "   in Installed: x removes selected skill\n" +
        "F1 this help\n" +
        "q  quit";

    /// Compact single-line hint shown in the welcome/preview pane.
    internal const string WelcomeHint =
        "/ search · v preview · l logs · d doctor · I installed · s search+ · u update · c cleanup · F1 help · q quit";
}
