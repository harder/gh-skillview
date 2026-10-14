using SkillView.Inventory;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace SkillView.Maintenance;

/// Removes only recognized installer-owned YAML entries. All other bytes,
/// including authored metadata, comments outside those entries and the body,
/// remain part of the comparison. Unsupported YAML fails closed.
internal static class InstallerMetadataNormalizer
{
    private static readonly HashSet<string> TrackingKeys = new(StringComparer.Ordinal)
    {
        "github-repo", "github-ref", "github-tree-sha", "github-path", "github-pinned", "local-path",
    };

    internal static string Normalize(string source)
    {
        var text = source.TrimStart('\uFEFF');
        var first = text.IndexOf('\n');
        if (first < 0 || text[..first].TrimEnd('\r') != "---")
            throw new IOException("payload comparison requires valid skill frontmatter");
        var start = first + 1;
        var close = start;
        while (close < text.Length)
        {
            var end = text.IndexOf('\n', close);
            var line = (end < 0 ? text[close..] : text[close..end]).TrimEnd('\r');
            if (line is "---" or "...") break;
            if (end < 0 || end - start > FrontMatterParser.MaxYamlChars)
                throw new IOException("frontmatter comparison limit exceeded");
            close = end + 1;
        }
        if (close == text.Length || close - start > FrontMatterParser.MaxYamlChars)
            throw new IOException("unterminated or oversized frontmatter");
        var block = text[start..close];
        var parser = new Parser(new StringReader(block));
        var ranges = new List<(int Start, int End)>();
        var nodes = 0;
        parser.Consume<StreamStart>();
        parser.Consume<DocumentStart>();
        parser.Consume<MappingStart>();
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var key = parser.Consume<Scalar>();
            if (key.Value == "metadata" && parser.TryConsume<MappingStart>(out _))
            {
                var retained = 0;
                var metadataRanges = new List<(int Start, int End)>();
                MappingEnd? mappingEnd;
                while (!parser.TryConsume<MappingEnd>(out mappingEnd))
                {
                    var child = parser.Consume<Scalar>();
                    var last = SkipValue(parser, 1, ref nodes);
                    if (TrackingKeys.Contains(child.Value))
                        metadataRanges.Add(CompleteEntryRange(block, checked((int)child.Start.Index), last));
                    else retained++;
                }
                ranges.AddRange(metadataRanges);
                if (retained == 0 && metadataRanges.Count > 0)
                {
                    var headerStart = checked((int)key.Start.Index);
                    var lineEnd = block.IndexOf('\n', headerStart);
                    if (lineEnd < 0) lineEnd = block.Length;
                    var comment = block.IndexOf('#', checked((int)key.End.Index), lineEnd - checked((int)key.End.Index));
                    ranges.Add(comment >= 0 ? (headerStart, comment) : (headerStart, Math.Min(block.Length, lineEnd + 1)));
                }
            }
            else SkipValue(parser, 0, ref nodes);
        }
        parser.Consume<DocumentEnd>();
        parser.Consume<StreamEnd>();
        foreach (var range in ranges.OrderByDescending(r => r.Start))
            block = block.Remove(range.Start, range.End - range.Start);
        return source[..(source.Length - text.Length)] + text[..start] + block + text[close..];
    }

    private static int SkipValue(IParser parser, int depth, ref int nodes)
    {
        if (depth > FrontMatterParser.MaxDepth || ++nodes > FrontMatterParser.MaxNodes)
            throw new IOException("frontmatter comparison complexity limit exceeded");
        if (parser.TryConsume<Scalar>(out var scalar)) return checked((int)scalar.End.Index);
        if (parser.TryConsume<MappingStart>(out _))
        {
            MappingEnd? end;
            while (!parser.TryConsume<MappingEnd>(out end))
            {
                SkipValue(parser, depth + 1, ref nodes);
                SkipValue(parser, depth + 1, ref nodes);
            }
            return checked((int)end.End.Index);
        }
        if (parser.TryConsume<SequenceStart>(out _))
        {
            SequenceEnd? end;
            while (!parser.TryConsume<SequenceEnd>(out end)) SkipValue(parser, depth + 1, ref nodes);
            return checked((int)end.End.Index);
        }
        throw new IOException("unsupported YAML in payload comparison");
    }

    private static (int Start, int End) CompleteEntryRange(string block, int start, int end)
    {
        // gh writes block mappings. Refuse flow entries instead of removing
        // punctuation or another authored value from a shared line.
        var lineStart = block.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        if (block[lineStart..start].Any(c => c is not (' ' or '\t')))
            throw new IOException("flow tracking metadata requires explicit comparison");
        var nextLine = end < block.Length ? block.IndexOf('\n', end) : -1;
        if (end > 0 && block[end - 1] == '\n') return (lineStart, end);
        return (lineStart, nextLine < 0 ? block.Length : nextLine + 1);
    }
}
