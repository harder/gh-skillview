using System.Security.Cryptography;
using System.Text;
using SkillView.Inventory;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace SkillView.Maintenance;

/// A conservative same-revision comparison for gh's YAML map serialization and
/// body delimiter handling. Never authorizes replacement or hides a new tree.
internal static class GhPresentationComparison
{
    internal static string? Fingerprint(string normalized)
    {
        try
        {
            var first = normalized.IndexOf('\n');
            if (first < 0 || normalized[..first].TrimEnd('\r') != "---") return null;
            var start = first + 1;
            var close = start;
            var end = close;
            while (close < normalized.Length)
            {
                end = normalized.IndexOf('\n', close);
                if (normalized[close..(end < 0 ? normalized.Length : end)].TrimEnd('\r') is "---" or "...") break;
                close = end < 0 ? normalized.Length : end + 1;
            }
            if (close == normalized.Length || close - start > FrontMatterParser.MaxYamlChars) return null;
            var parser = new Parser(new StringReader(normalized[start..close]));
            parser.Consume<StreamStart>(); parser.Consume<DocumentStart>();
            var nodes = 0;
            var yaml = ReadNode(parser, 0, ref nodes);
            parser.Consume<DocumentEnd>(); parser.Consume<StreamEnd>();
            var body = end < 0 ? "" : normalized[(end + 1)..].TrimStart('\r', '\n');
            if (body.Length > 0 && !body.EndsWith('\n')) body += "\n";
            using var bytes = new MemoryStream();
            using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
            { writer.Write(yaml.Length); writer.Write(yaml); writer.Write(body); }
            return Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length)));
        }
        catch (Exception ex) when (ex is YamlException or IOException or ArgumentException)
        { return null; }
    }

    private static byte[] ReadNode(IParser parser, int depth, ref int nodes)
    {
        if (depth > FrontMatterParser.MaxDepth || ++nodes > FrontMatterParser.MaxNodes)
            throw new IOException("Presentation comparison complexity limit exceeded.");
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
        if (parser.TryConsume<Scalar>(out var scalar))
        {
            if (!scalar.Anchor.IsEmpty || !scalar.Tag.IsEmpty || !UnambiguousString(scalar))
                throw new IOException("Presentation comparison requires unambiguous string scalars.");
            writer.Write((byte)1); writer.Write(scalar.Value);
        }
        else if (parser.TryConsume<MappingStart>(out var mapping))
        {
            if (!mapping.Anchor.IsEmpty || !mapping.Tag.IsEmpty) throw new IOException("Tagged or anchored mapping.");
            var values = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                if (++nodes > FrontMatterParser.MaxNodes) throw new IOException("Presentation node limit exceeded.");
                var key = parser.Consume<Scalar>();
                if (!key.Anchor.IsEmpty || !key.Tag.IsEmpty || !UnambiguousString(key)
                    || !values.TryAdd(key.Value, ReadNode(parser, depth + 1, ref nodes)))
                    throw new IOException("Ambiguous or duplicate presentation mapping key.");
            }
            writer.Write((byte)2); writer.Write(values.Count);
            foreach (var pair in values) { writer.Write(pair.Key); writer.Write(pair.Value.Length); writer.Write(pair.Value); }
        }
        else if (parser.TryConsume<SequenceStart>(out var sequence))
        {
            if (!sequence.Anchor.IsEmpty || !sequence.Tag.IsEmpty) throw new IOException("Tagged or anchored sequence.");
            var values = new List<byte[]>();
            while (!parser.TryConsume<SequenceEnd>(out _)) values.Add(ReadNode(parser, depth + 1, ref nodes));
            writer.Write((byte)3); writer.Write(values.Count);
            foreach (var value in values) { writer.Write(value.Length); writer.Write(value); }
        }
        else throw new IOException("Unsupported presentation node.");
        return bytes.ToArray();
    }

    private static bool UnambiguousString(Scalar scalar)
    {
        if (scalar.Style is not ScalarStyle.Plain) return true;
        var value = scalar.Value;
        // Numeric/tag resolution differs across YAML schemas. Refuse those
        // forms rather than treating a quoted string as an implicit number.
        return value.Length > 0 && !char.IsAsciiDigit(value[0]) && value[0] is not ('+' or '-' or '.' or '~')
            && value.ToLowerInvariant() is not ("null" or "true" or "false" or "yes" or "no" or "on" or "off");
    }
}
