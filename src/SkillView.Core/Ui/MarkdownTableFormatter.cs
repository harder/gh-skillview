namespace SkillView.Ui;

internal static class MarkdownTableFormatter
{
    internal static string FormatCodeSpan(string value)
    {
        var normalized = FormatTableCell(value);
        var longestBacktickRun = 0;
        var currentRun = 0;
        foreach (var ch in normalized)
        {
            if (ch == '`')
            {
                currentRun++;
                if (currentRun > longestBacktickRun)
                {
                    longestBacktickRun = currentRun;
                }
            }
            else
            {
                currentRun = 0;
            }
        }

        if (longestBacktickRun == 0)
        {
            return $"`{normalized}`";
        }

        var delimiter = new string('`', longestBacktickRun + 1);
        return $"{delimiter} {normalized} {delimiter}";
    }

    internal static string FormatTableCell(string value) =>
        NormalizeTableValue(value)
            .Replace("|", "\\|", StringComparison.Ordinal);

    internal static string NormalizeTableValue(string value) =>
        (TerminalEscapeSanitizer.Sanitize(value) ?? string.Empty)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}
