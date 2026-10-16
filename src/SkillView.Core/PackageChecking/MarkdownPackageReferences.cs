using System.Collections.Immutable;
using System.Text;

namespace SkillView.PackageChecking;

internal sealed record PackageMarkdownReference(string Target, int Line, bool DecodeUrl, bool UndefinedLabel = false);
internal sealed record PackageMarkdownScan(ImmutableArray<PackageMarkdownReference> References, ImmutableArray<string> Reasons);

/// Deliberately limited local-reference syntax. This is not a full Markdown or shell parser.
internal static class MarkdownPackageReferences
{
    internal static PackageMarkdownScan Scan(string text, int maximum, CancellationToken token, int lineOffset = 0)
    {
        var definitions = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var definitionLines = new HashSet<int>();
        var references = ImmutableArray.CreateBuilder<PackageMarkdownReference>();
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (line, number) in Lines(text, token, reasons))
        {
            var trimmed = line.TrimStart(' ');
            if (line.Length - trimmed.Length > 3 || !trimmed.StartsWith('[')) continue;
            var end = trimmed.IndexOf("]:", StringComparison.Ordinal);
            if (end < 1) continue;
            definitionLines.Add(number);
            var label = Label(trimmed[1..end]);
            if (label is null || definitions.Count >= maximum)
            { reasons.Add("Reference definition limit or unsupported label."); continue; }
            if (!Destination(trimmed, end + 2, out var target, out var after, token)
                || !Tail(trimmed, after, closingParenthesis: false, out _, token))
            { definitions[label] = null; reasons.Add("Unsupported reference definition syntax."); continue; }
            if (!definitions.TryAdd(label, target))
            { definitions[label] = null; reasons.Add("Duplicate reference labels are not resolved."); }
        }
        foreach (var (line, number) in Lines(text, token, reasons))
        {
            if (definitionLines.Contains(number)) continue;
            for (var index = 0; index < line.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                if (line[index] == '\\') { index++; continue; }
                if (line[index] == '`')
                {
                    var ticks = 1;
                    while (index + ticks < line.Length && line[index + ticks] == '`') ticks++;
                    if (ticks > 32) { reasons.Add("Unsupported inline code delimiter."); break; }
                    var close = ClosingCode(line, index + ticks, ticks, token);
                    if (close < 0) { reasons.Add("Multiline or unclosed inline code is not inspected."); break; }
                    var value = line[(index + ticks)..close].Trim();
                    if (value.Length > 4096) reasons.Add("Inline code path length limit exceeded.");
                    else if (CodePath(value)) Add(value, number, decode: false);
                    index = close + ticks - 1;
                    continue;
                }
                if (line[index] != '[') continue;
                var endLabel = ClosingLabel(line, index, token);
                if (endLabel < 0) continue;
                if (line.AsSpan(index + 1, endLabel - index - 1).Contains('[')) reasons.Add("Nested link-label references are not fully inspected.");
                var next = endLabel + 1;
                if (next < line.Length && line[next] == '(')
                {
                    if (!Destination(line, next + 1, out var target, out var after, token)
                        || !Tail(line, after, closingParenthesis: true, out var end, token))
                    { reasons.Add("Unsupported or multiline inline link syntax."); break; }
                    Add(target, number, decode: true);
                    index = end;
                }
                else if (next < line.Length && line[next] == '[')
                {
                    var end = line.IndexOf(']', next + 1);
                    if (end < 0) { reasons.Add("Multiline reference links are not inspected."); break; }
                    var label = Label(end == next + 1 ? line[(index + 1)..endLabel] : line[(next + 1)..end]);
                    Resolve(label, number);
                    index = end;
                }
                else
                {
                    var label = Label(line[(index + 1)..endLabel]);
                    // Undefined shortcut syntax is ordinary prose, not proof of a broken link.
                    if (label is not null && definitions.ContainsKey(label)) Resolve(label, number);
                    index = endLabel;
                }
                if (references.Count >= maximum)
                { reasons.Add("Reference count limit reached; later references were not inspected."); return Result(); }
            }
        }
        return Result();

        void Add(string target, int line, bool decode, bool undefined = false)
        {
            if (references.Count >= maximum) { reasons.Add("Reference count limit reached; later references were not inspected."); return; }
            references.Add(new(target, line + lineOffset, decode, undefined));
        }
        void Resolve(string? label, int line)
        {
            if (label is null) { reasons.Add("Unsupported reference label."); return; }
            if (!definitions.TryGetValue(label, out var target)) Add(label, line, decode: false, undefined: true);
            else if (target is null) reasons.Add("Ambiguous reference target was not inspected.");
            else Add(target, line, decode: true);
        }
        PackageMarkdownScan Result() => new(references.ToImmutable(), reasons.Order(StringComparer.Ordinal).ToImmutableArray());
    }

    private static IEnumerable<(string Line, int Number)> Lines(string text, CancellationToken token, HashSet<string> reasons)
    {
        using var reader = new StringReader(text);
        char fence = '\0'; var width = 0; var number = 0; var comment = false; var html = false;
        while (reader.ReadLine() is { } line)
        {
            token.ThrowIfCancellationRequested(); number++;
            var trimmed = line.TrimStart(' ');
            var indent = line.Length - trimmed.Length;
            if (fence == '\0')
            {
                if (comment)
                { if (line.Contains("-->", StringComparison.Ordinal)) comment = false; continue; }
                var commentStart = line.IndexOf("<!--", StringComparison.Ordinal);
                if (commentStart >= 0)
                {
                    comment = line.IndexOf("-->", commentStart + 4, StringComparison.Ordinal) < 0;
                    reasons.Add("HTML comments are not inspected."); continue;
                }
                if (html)
                { if (string.IsNullOrWhiteSpace(line)) html = false; continue; }
                if (trimmed.StartsWith('<'))
                { html = true; reasons.Add("Raw HTML blocks are not inspected; a blank line ends this limited exclusion."); continue; }
                if (trimmed.StartsWith('>'))
                { reasons.Add("Blockquote containers are not inspected."); continue; }
            }
            var run = 0;
            if (trimmed.Length > 0 && trimmed[0] is '`' or '~')
                while (run < trimmed.Length && trimmed[run] == trimmed[0]) run++;
            if (fence != '\0')
            {
                if (indent <= 3 && run >= width && trimmed[0] == fence && string.IsNullOrWhiteSpace(trimmed[run..])) fence = '\0';
                continue;
            }
            if (indent <= 3 && run >= 3) { fence = trimmed[0]; width = run; continue; }
            if (indent >= 4 || line.StartsWith('\t'))
            { reasons.Add("Indented code blocks are not inspected."); continue; }
            if (line.Contains("<img", StringComparison.OrdinalIgnoreCase) || line.Contains("<a ", StringComparison.OrdinalIgnoreCase))
            { reasons.Add("HTML references are not inspected."); continue; }
            yield return (line, number);
        }
        if (fence != '\0') reasons.Add("Unclosed fenced code block; remaining text was not inspected.");
    }

    private static string? Label(string value)
    {
        if (value.Length is 0 or > 512 || value.Contains('[') || value.Contains(']') || value.Contains('\\')) return null;
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static int ClosingCode(string line, int start, int width, CancellationToken token)
    {
        for (var index = start; index < line.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            if (line[index] != '`') continue;
            var count = 1;
            while (index + count < line.Length && line[index + count] == '`') count++;
            if (count == width) return index;
            index += count - 1;
        }
        return -1;
    }

    private static int ClosingLabel(string line, int start, CancellationToken token)
    {
        var depth = 0;
        for (var index = start; index < line.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            if (line[index] == '\\') { index++; continue; }
            if (line[index] == '[' && ++depth > 8) return -1;
            if (line[index] == ']' && --depth == 0) return index;
        }
        return -1;
    }

    private static bool CodePath(string value)
    {
        var relative = value.StartsWith("./", StringComparison.Ordinal) ? value[2..] : value;
        return relative.StartsWith("scripts/", StringComparison.Ordinal) || relative.StartsWith("assets/", StringComparison.Ordinal)
            || relative.StartsWith("references/", StringComparison.Ordinal)
            ? !value.Any(character => char.IsWhiteSpace(character) || "`$*?[]{}<>|;&\"'\\".Contains(character))
            : false;
    }

    private static bool Destination(string line, int start, out string target, out int end, CancellationToken token)
    {
        target = ""; end = start;
        while (end < line.Length && char.IsWhiteSpace(line[end])) { token.ThrowIfCancellationRequested(); end++; }
        if (end >= line.Length) return false;
        var builder = new StringBuilder();
        var angle = line[end] == '<';
        if (angle) end++;
        var depth = 0;
        for (; end < line.Length; end++)
        {
            token.ThrowIfCancellationRequested();
            if (end - start > 4096) return false;
            var character = line[end];
            if (character == '\\')
            {
                if (end + 1 >= line.Length || !"!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~".Contains(line[end + 1])) return false;
                builder.Append(line[++end]); continue;
            }
            if (angle && character == '>') { end++; target = builder.ToString(); return true; }
            if (!angle && (char.IsWhiteSpace(character) || character == ')' && depth == 0))
            { target = builder.ToString(); return true; }
            if (!angle && character == '(' && ++depth > 8) return false;
            if (!angle && character == ')') depth--;
            if (char.IsControl(character) || character == '<') return false;
            builder.Append(character);
        }
        target = builder.ToString();
        return !angle && depth == 0;
    }

    private static bool Tail(string line, int start, bool closingParenthesis, out int end, CancellationToken token)
    {
        end = start;
        while (end < line.Length && char.IsWhiteSpace(line[end])) { token.ThrowIfCancellationRequested(); end++; }
        if (end < line.Length && line[end] is '\'' or '"')
        {
            var quote = line[end++];
            while (end < line.Length && line[end] != quote) { token.ThrowIfCancellationRequested(); if (line[end] == '\\') end++; end++; }
            if (end >= line.Length) return false;
            end++;
            while (end < line.Length && char.IsWhiteSpace(line[end])) { token.ThrowIfCancellationRequested(); end++; }
        }
        return closingParenthesis ? end < line.Length && line[end] == ')' : end == line.Length;
    }
}
