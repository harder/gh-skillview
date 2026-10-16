using System.Text;
using System.Text.Json;
using SkillView.Inventory;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace SkillView.Maintenance;

/// Splices only the local source member; authored bytes are never reserialized.
internal static class LocalTrackingWriter
{
    internal static byte[] Bind(byte[] bytes, string path)
    {
        try { return BindCore(bytes, path); }
        catch (Exception ex) when (ex is YamlException or DecoderFallbackException or OverflowException)
        { throw new IOException("Local tracking needs supported unambiguous UTF-8 YAML.", ex); }
    }

    private static byte[] BindCore(byte[] bytes, string path)
    {
        if (bytes.Length > PayloadSnapshot.MaxSkillBytes || !Path.IsPathFullyQualified(path) || path.Any(char.IsControl))
            throw new IOException("Local source path or SKILL.md budget is invalid.");
        var utf8 = new UTF8Encoding(false, true);
        var text = utf8.GetString(bytes);
        var (_, before, valid) = FrontMatterParser.Parse(text);
        if (!valid || !before.Diagnostics.IsEmpty) throw new IOException("Local tracking requires valid captured frontmatter.");
        var start = text.IndexOf('\n') + 1;
        var close = start;
        while (close < text.Length)
        {
            var end = text.IndexOf('\n', close);
            if (text[close..(end < 0 ? text.Length : end)].TrimEnd('\r') is "---" or "...") break;
            close = end < 0 ? text.Length : end + 1;
        }
        if (start == 0 || close == text.Length) throw new IOException("Local tracking framing is incomplete.");
        var block = text[start..close];
        var newline = text.AsSpan(0, start).EndsWith("\r\n") ? "\r\n" : "\n";
        var encoded = JsonSerializer.Serialize(path, ReplacementStringJsonContext.Default.String);
        var first = close; var last = close;
        var replacement = "metadata:" + newline + "    local-path: " + encoded + newline;
        var parser = new Parser(new StringReader(block));
        parser.Consume<StreamStart>(); parser.Consume<DocumentStart>();
        RequireBlock(parser);
        var seen = false;
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var key = parser.Consume<Scalar>();
            if (key.Value != "metadata") { Skip(parser); continue; }
            if (seen) throw new IOException("Duplicate metadata mapping.");
            seen = true;
            RequireBlock(parser);
            var headerEnd = block.IndexOf('\n', checked((int)key.End.Index));
            if (headerEnd < 0) throw new IOException("Metadata header must occupy its own line.");
            first = last = start + headerEnd + 1;
            var indent = "    "; var found = false; var children = 0;
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                var child = parser.Consume<Scalar>();
                if (children++ == 0)
                {
                    var lineStart = block.LastIndexOf('\n', Math.Max(0, checked((int)child.Start.Index) - 1)) + 1;
                    indent = block[lineStart..checked((int)child.Start.Index)];
                    if (indent.Length == 0 || !indent.All(c => c == ' ')) throw new IOException("Metadata children require block indentation.");
                }
                if (child.Value != "local-path") { Skip(parser); continue; }
                var value = parser.Consume<Scalar>();
                if (found || !value.Anchor.IsEmpty || !value.Tag.IsEmpty || value.Style is ScalarStyle.Folded or ScalarStyle.Literal
                    || child.Start.Line != value.End.Line)
                    throw new IOException("Local path must be one unambiguous single-line scalar.");
                found = true;
                first = start + checked((int)value.Start.Index);
                last = start + checked((int)value.End.Index);
            }
            replacement = found ? encoded : indent + "local-path: " + encoded + newline;
        }
        parser.Consume<DocumentEnd>(); parser.Consume<StreamEnd>();
        var result = utf8.GetBytes(text[..first] + replacement + text[last..]);
        var (_, after, parsed) = FrontMatterParser.Parse(utf8.GetString(result));
        if (!parsed || !after.Diagnostics.IsEmpty) throw new IOException("Local tracking splice produced unsupported metadata.");
        ReplacementPlanStore.SameTracking(before with { LocalPath = path }, after);
        if (result.Length > PayloadSnapshot.MaxSkillBytes) throw new IOException("Bound local SKILL.md exceeds its limit.");
        return result;
    }

    private static void RequireBlock(IParser parser)
    {
        var mapping = parser.Consume<MappingStart>();
        if (mapping.Style != MappingStyle.Block || !mapping.Anchor.IsEmpty || !mapping.Tag.IsEmpty)
            throw new IOException("Local tracking requires untagged block mappings.");
    }

    private static void Skip(IParser parser)
    {
        var depth = 0;
        do
        {
            var current = parser.Current ?? throw new IOException("Incomplete local YAML value.");
            if (current is AnchorAlias || current is NodeEvent node && (!node.Anchor.IsEmpty || !node.Tag.IsEmpty))
                throw new IOException("Local tracking refuses aliases, anchors and tags.");
            if (current is MappingStart or SequenceStart) depth++;
            if (current is MappingEnd or SequenceEnd) depth--;
            parser.MoveNext();
        } while (depth > 0);
    }
}
