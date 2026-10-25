namespace SkillView.Ui;

/// Shortest distinguishing sanitized suffixes. Display labels never route actions.
internal static class DistinguishingPathLabels
{
    internal static IReadOnlyDictionary<string, string> Build(IEnumerable<string> input, string skillName, int width, CancellationToken token)
    {
        width = Math.Max(12, width);
        var paths = input.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var parts = paths.Select(path =>
        {
            var components = Clean(path).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            return components.Length > 1 && components[^1].Equals(skillName, StringComparison.OrdinalIgnoreCase) ? components[..^1] : components;
        }).ToArray();
        var depths = Enumerable.Repeat(1, paths.Length).ToArray();
        var suffixes = new string[paths.Length];
        bool expanded;
        do
        {
            token.ThrowIfCancellationRequested();
            for (var i = 0; i < paths.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                suffixes[i] = string.Join(Path.DirectorySeparatorChar, parts[i].TakeLast(depths[i]));
            }
            var counts = suffixes.GroupBy(value => value, StringComparer.Ordinal)
                .ToDictionary(values => values.Key, values => values.Count(), StringComparer.Ordinal);
            expanded = false;
            for (var i = 0; i < paths.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                if (counts[suffixes[i]] <= 1 || depths[i] >= parts[i].Length) continue;
                depths[i]++;
                expanded = true;
            }
        } while (expanded);

        var bounded = suffixes.Select(value => Bound(value, width)).ToArray();
        // Sanitization, omitted skill names or very long common components can
        // erase distinctions. Number this group rather than pretending the
        // shortened labels still identify a unique location. Full paths stay
        // in the detail pane and exact frozen routing entries.
        var needsMarkers = bounded.Distinct(StringComparer.Ordinal).Count() != paths.Length;
        var pathLabels = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < paths.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var marker = needsMarkers ? $"#{i + 1} " : "";
            pathLabels[paths[i]] = marker + Bound(suffixes[i], width - marker.Length);
        }
        return pathLabels;
    }

    internal static string Clean(string value) => (TerminalEscapeSanitizer.Sanitize(value) ?? string.Empty)
        .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);

    internal static string Bound(string value, int width)
    {
        if (value.Length <= width) return value;
        var left = (width - 1) / 2;
        return value[..left] + "…" + value[^(width - left - 1)..];
    }
}
