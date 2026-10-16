using System.Text;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace SkillView.Maintenance;

/// Removes one verified installer pin member. Every byte outside that member,
/// including its trailing comment and line ending, remains unchanged.
internal static class PinTrackingWriter
{
    internal static byte[] Unpin(byte[] bytes)
    {
        try { return UnpinCore(bytes); }
        catch (Exception ex) when (ex is YamlException or DecoderFallbackException or OverflowException)
        { throw new IOException("Pin metadata has unsupported YAML or UTF-8 framing.", ex); }
    }

    private static byte[] UnpinCore(byte[] bytes)
    {
        if (bytes.Length > PayloadSnapshot.MaxSkillBytes) throw new IOException("SKILL.md exceeds the pin review byte limit.");
        var utf8 = new UTF8Encoding(false, true);
        var source = utf8.GetString(bytes);
        var (_, before, valid) = FrontMatterParser.Parse(source);
        if (!valid || !before.Diagnostics.IsEmpty) throw new IOException("Pin removal needs valid unambiguous frontmatter.");
        if (before.Extra.TryGetValue("pinned", out var rootPin) && rootPin.Equals("true", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Authored root pinned:true is independent policy; standalone unpin cannot clear it.");
        if (string.IsNullOrWhiteSpace(before.PinnedRef))
        {
            if (before.Pinned) throw new IOException("The observed pin cannot be represented by the installer pin member.");
            return bytes.ToArray();
        }
        var offset = source.Length > 0 && source[0] == '\uFEFF' ? 1 : 0;
        var start = source.IndexOf('\n', offset) + 1;
        var close = start;
        while (close < source.Length)
        {
            var end = source.IndexOf('\n', close);
            if (source[close..(end < 0 ? source.Length : end)].TrimEnd('\r') is "---" or "...") break;
            close = end < 0 ? source.Length : end + 1;
        }
        if (start <= 0 || close == source.Length) throw new IOException("Pin frontmatter framing is incomplete.");
        var block = source[start..close];
        var parser = new Parser(new StringReader(block));
        parser.Consume<StreamStart>(); parser.Consume<DocumentStart>();
        var root = ReadMapping(parser);
        var found = false; var removeStart = 0; var removeEnd = 0;
        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var key = ReadScalar(parser);
            if (key.Value == "metadata")
            {
                ReadMapping(parser);
                var count = 0;
                while (!parser.TryConsume<MappingEnd>(out _))
                {
                    var child = ReadScalar(parser); var value = ReadScalar(parser); count++;
                    if (child.Value != "github-pinned") continue;
                    if (found || child.Start.Line != value.End.Line || value.Value != before.PinnedRef)
                        throw new IOException("Pin member must be one unambiguous single-line scalar.");
                    found = true;
                    removeStart = checked((int)child.Start.Index); removeEnd = checked((int)value.End.Index);
                    var lineStart = block.LastIndexOf('\n', Math.Max(0, removeStart - 1)) + 1;
                    if (!block[lineStart..removeStart].All(c => c == ' '))
                        throw new IOException("Pin member must occupy a block mapping line.");
                    var lineEnd = block.IndexOf('\n', removeEnd);
                    var trailing = block[removeEnd..(lineEnd < 0 ? block.Length : lineEnd)].Trim();
                    if (trailing.Length != 0 && !trailing.StartsWith('#'))
                        throw new IOException("Pin member has unsupported trailing YAML.");
                }
                if (count < 2) throw new IOException("Standalone unpin cannot remove the sole metadata member; review its parent mapping first.");
            }
            else Skip(parser);
        }
        parser.Consume<DocumentEnd>(); parser.Consume<StreamEnd>();
        if (!found) throw new IOException("No exact installer pin mapping member was captured.");
        var firstByte = utf8.GetByteCount(source.AsSpan(0, start + removeStart));
        var lastByte = utf8.GetByteCount(source.AsSpan(0, start + removeEnd));
        var result = new byte[bytes.Length - (lastByte - firstByte)];
        bytes.AsSpan(0, firstByte).CopyTo(result);
        bytes.AsSpan(lastByte).CopyTo(result.AsSpan(firstByte));
        var (_, after, parsed) = FrontMatterParser.Parse(utf8.GetString(result));
        if (!parsed || !after.Diagnostics.IsEmpty || after.Pinned || after.PinnedRef is not null
            || !SameMap(before.Extra.Remove("metadata.github-pinned"), after.Extra)
            || !SameMap(before.Metadata.Remove("github-pinned"), after.Metadata))
            throw new IOException("Pin splice changed policy outside the approved installer member.");
        return result;
    }

    private static bool SameMap(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after) =>
        before.Count == after.Count && before.All(item => after.TryGetValue(item.Key, out var value) && value == item.Value);

    private static MappingStart ReadMapping(IParser parser)
    {
        var node = parser.Consume<MappingStart>();
        if (node.Style != MappingStyle.Block || !node.Anchor.IsEmpty || !node.Tag.IsEmpty)
            throw new IOException("Standalone unpin requires untagged block mappings without anchors.");
        return node;
    }
    private static Scalar ReadScalar(IParser parser)
    {
        var node = parser.Consume<Scalar>();
        if (!node.Anchor.IsEmpty || !node.Tag.IsEmpty || node.Style is ScalarStyle.Literal or ScalarStyle.Folded)
            throw new IOException("Standalone unpin refuses aliases, anchors, tags and block scalars.");
        return node;
    }
    private static void Skip(IParser parser)
    {
        if (parser.Accept<Scalar>(out _)) { ReadScalar(parser); return; }
        if (parser.Accept<MappingStart>(out _))
        {
            ReadMapping(parser);
            while (!parser.TryConsume<MappingEnd>(out _)) { ReadScalar(parser); Skip(parser); }
            return;
        }
        if (parser.TryConsume<SequenceStart>(out var sequence))
        {
            if (!sequence.Anchor.IsEmpty || !sequence.Tag.IsEmpty || sequence.Style == SequenceStyle.Flow)
                throw new IOException("Standalone unpin refuses flow sequences and anchored/tagged nodes.");
            while (!parser.TryConsume<SequenceEnd>(out _)) Skip(parser);
            return;
        }
        throw new IOException("Standalone unpin encountered unsupported YAML nodes.");
    }
}
