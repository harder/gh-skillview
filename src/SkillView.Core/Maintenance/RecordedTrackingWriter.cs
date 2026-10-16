using System.Text.Json;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace SkillView.Maintenance;

/// Rebinds only installer-owned metadata in a private, immutable-tree stage.
/// Authored YAML bytes, comments and body are preserved by the normalizer.
internal static class RecordedTrackingWriter
{
    internal static string Bind(string source, SkillFrontMatter installed)
    {
        try { return BindCore(source, installed); }
        catch (YamlException ex) { throw new IOException("Recorded frontmatter YAML is malformed or unsupported.", ex); }
    }

    private static string BindCore(string source, SkillFrontMatter installed)
    {
        var normalized = InstallerMetadataNormalizer.Normalize(source);
        var offset = normalized.Length - normalized.TrimStart('\uFEFF').Length;
        var first = normalized.IndexOf('\n', offset);
        var start = first + 1;
        var close = start;
        while (close < normalized.Length)
        {
            var end = normalized.IndexOf('\n', close);
            if (normalized[close..(end < 0 ? normalized.Length : end)].TrimEnd('\r') is "---" or "...") break;
            close = end < 0 ? normalized.Length : end + 1;
        }
        if (first < 0 || close == normalized.Length || close - start > FrontMatterParser.MaxYamlChars)
            throw new IOException("Staged frontmatter is incomplete or exceeds its limit.");
        var block = normalized[start..close];
        var parser = new Parser(new StringReader(block));
        var insert = close;
        var indent = "  ";
        var header = "metadata:\n";
        var depth = 0;
        var metadataSeen = false;
        Scalar? previous = null;
        while (parser.MoveNext())
        {
            switch (parser.Current)
            {
                case MappingStart mapping:
                    if (depth == 1 && previous?.Value == "metadata")
                    {
                        if (metadataSeen || mapping.Style == YamlDotNet.Core.Events.MappingStyle.Flow)
                            throw new IOException("Staged metadata must be one block mapping.");
                        metadataSeen = true;
                        var end = block.IndexOf('\n', checked((int)previous.End.Index));
                        if (end < 0) throw new IOException("Staged metadata header is incomplete.");
                        insert = start + end + 1;
                        header = "";
                    }
                    depth++; previous = null; break;
                case MappingEnd: depth--; previous = null; break;
                case Scalar scalar:
                    if (metadataSeen && depth == 2 && header.Length == 0 && scalar.Start.Index >= insert - start)
                    {
                        var lineStart = block.LastIndexOf('\n', Math.Max(0, checked((int)scalar.Start.Index) - 1)) + 1;
                        var padding = block[lineStart..checked((int)scalar.Start.Index)];
                        if (padding.All(character => character == ' ') && padding.Length > 0) indent = padding;
                        // The first existing child defines indentation. No later
                        // scalar or nested value may change this decision.
                        goto Parsed;
                    }
                    previous = scalar; break;
                case SequenceStart: depth++; previous = null; break;
                case SequenceEnd: depth--; previous = null; break;
            }
        }
    Parsed:
        var values = new (string Key, string? Value)[]
        {
            ("github-repo", installed.Upstream), ("github-ref", installed.GithubRef),
            ("github-tree-sha", installed.GithubTreeSha), ("github-path", installed.GithubPath),
            ("github-pinned", installed.PinnedRef)
        };
        var entries = string.Concat(values.Where(value => value.Value is not null)
            .Select(value => indent + value.Key + ": " + JsonSerializer.Serialize(value.Value, ReplacementStringJsonContext.Default.String) + "\n"));
        var added = entries.Length == 0 ? "" : header + entries;
        var result = normalized.Insert(insert, added);
        var (_, parsed, valid) = FrontMatterParser.Parse(result);
        if (!valid || !parsed.Diagnostics.IsEmpty || InstallerMetadataNormalizer.Normalize(result) != normalized)
            throw new IOException("Installer metadata rebinding changed authored content or produced unsupported YAML.");
        ReplacementPlanStore.SameTracking(installed, parsed);
        return result;
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal partial class ReplacementStringJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
