using System.Collections.Immutable;
using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace SkillView.Inventory;

/// Bounded event-based YAML reading. No object deserialization, reflection,
/// custom-tag execution, or alias expansion; safe for the native AOT hosts.
public static class FrontMatterParser
{
    public const int MaxYamlChars = 64 * 1024;
    public const int MaxNodes = 4096;
    public const int MaxDepth = 16;

    public static (string Body, Models.SkillFrontMatter FrontMatter, bool Parsed) Parse(string source)
    {
        if (string.IsNullOrEmpty(source)) return (string.Empty, Models.SkillFrontMatter.Empty, false);
        var text = source.TrimStart('\uFEFF');
        var firstNl = text.IndexOf('\n');
        if (firstNl < 0 || text[..firstNl].TrimEnd('\r') != "---")
            return (source, Models.SkillFrontMatter.Empty, false);
        var start = firstNl + 1;
        var close = start;
        while (close < text.Length)
        {
            var end = text.IndexOf('\n', close);
            var line = (end < 0 ? text[close..] : text[close..end]).TrimEnd('\r');
            if (line is "---" or "...")
            {
                var body = end < 0 ? string.Empty : text[(end + 1)..];
                if (close - start > MaxYamlChars) return (body, Models.SkillFrontMatter.Empty, false);
                try { return (body, ParseBlock(text[start..close]), true); }
                catch (Exception ex) when (ex is YamlException or FormatException)
                {
                    return (body, Models.SkillFrontMatter.Empty with { Diagnostics = [ex.Message] }, false);
                }
            }
            if (end < 0) break;
            close = end + 1;
            if (close - start > MaxYamlChars) break;
        }
        return (source, Models.SkillFrontMatter.Empty, false);
    }

    private static Models.SkillFrontMatter ParseBlock(string block)
    {
        var parser = new Parser(new StringReader(block));
        parser.Consume<StreamStart>();
        parser.Consume<DocumentStart>();
        var nodes = 0;
        var root = ReadNode(parser, 0, ref nodes) as Dictionary<string, object>
            ?? throw new FormatException("Skill frontmatter must be a mapping.");
        parser.Consume<DocumentEnd>();
        parser.Consume<StreamEnd>();
        var metadata = root.GetValueOrDefault("metadata") as Dictionary<string, object> ?? [];
        string? Field(string name) => root.GetValueOrDefault(name) as string;
        string? Meta(string name) => metadata.GetValueOrDefault(name) as string;
        var diagnostics = ImmutableArray.CreateBuilder<string>();
        if (root.TryGetValue("metadata", out var metadataNode) && metadataNode is not Dictionary<string, object>)
            diagnostics.Add("Metadata must be a mapping of strings to strings.");
        foreach (var (key, value) in metadata)
            if (value is not string) diagnostics.Add($"Metadata field '{key}' must be a string.");
        var name = Field("name");
        var description = Field("description");
        if (string.IsNullOrWhiteSpace(name)) diagnostics.Add("Required name is missing or empty.");
        else if (!ValidName(name)) diagnostics.Add("Name must be 1–64 lowercase letters, digits or single hyphens.");
        if (string.IsNullOrWhiteSpace(description)) diagnostics.Add("Required description is missing or empty.");
        else if (description.Length > 1024) diagnostics.Add("Description exceeds 1024 characters.");
        var compatibility = Field("compatibility");
        if (compatibility?.Length > 500) diagnostics.Add("Compatibility exceeds 500 characters.");
        var extra = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        Flatten(root, "", extra);
        var pinnedRef = Meta("github-pinned");
        return new Models.SkillFrontMatter
        {
            Name = name,
            Description = description,
            Version = Field("version") ?? Meta("version"),
            License = Field("license"),
            Compatibility = compatibility,
            Upstream = Meta("github-repo") ?? Field("upstream"),
            GithubTreeSha = Meta("github-tree-sha") ?? Field("github-tree-sha") ?? Field("tree-sha") ?? Field("treesha"),
            GithubRef = Meta("github-ref"),
            GithubPath = Meta("github-path"),
            PinnedRef = pinnedRef,
            LocalPath = Meta("local-path"),
            Pinned = !string.IsNullOrWhiteSpace(pinnedRef)
                || string.Equals(Field("pinned"), "true", StringComparison.OrdinalIgnoreCase)
                || root.GetValueOrDefault("pinned") is NonStringScalar { Value: "true" },
            AllowedTools = StringList(root.GetValueOrDefault("allowed-tools"), splitScalar: true),
            Agents = StringList(root.GetValueOrDefault("agents"), splitScalar: false),
            Extra = extra.ToImmutable(),
            Metadata = metadata.Where(x => x.Value is string).ToImmutableDictionary(x => x.Key, x => (string)x.Value, StringComparer.Ordinal),
            Diagnostics = diagnostics.ToImmutable(),
        };
    }

    private static object ReadNode(IParser parser, int depth, ref int nodes)
    {
        if (depth > MaxDepth || ++nodes > MaxNodes) throw new FormatException("Frontmatter exceeds YAML complexity limits.");
        if (parser.TryConsume<Scalar>(out var scalar))
        {
            var value = scalar.Value;
            var explicitlyString = scalar.Tag.ToString() == "tag:yaml.org,2002:str";
            var typedTag = !scalar.Tag.IsEmpty && !explicitlyString;
            var implicitTyped = scalar.Style == ScalarStyle.Plain && !explicitlyString
                && (value.Length == 0 || value is "~" || value.Equals("null", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase)
                    || decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
            return typedTag || implicitTyped ? new NonStringScalar(value) : value;
        }
        if (parser.TryConsume<MappingStart>(out _))
        {
            var values = new Dictionary<string, object>(StringComparer.Ordinal);
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                var key = ReadNode(parser, depth + 1, ref nodes) as string
                    ?? throw new FormatException("Frontmatter keys must be scalar strings.");
                var value = ReadNode(parser, depth + 1, ref nodes);
                if (!values.TryAdd(key, value)) throw new FormatException($"Duplicate frontmatter key: {key}");
            }
            return values;
        }
        if (parser.TryConsume<SequenceStart>(out _))
        {
            var values = new List<object>();
            while (!parser.TryConsume<SequenceEnd>(out _)) values.Add(ReadNode(parser, depth + 1, ref nodes));
            return values;
        }
        throw new FormatException("Unsupported YAML node (aliases are not expanded).");
    }

    private sealed record NonStringScalar(string Value);

    private static bool ValidName(string name) => name.Length is >= 1 and <= 64
        && name[0] != '-' && name[^1] != '-' && !name.Contains("--", StringComparison.Ordinal)
        && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static ImmutableArray<string> StringList(object? value, bool splitScalar)
    {
        if (value is List<object> list) return list.OfType<string>().ToImmutableArray();
        if (value is not string text || string.IsNullOrWhiteSpace(text)) return [];
        if (!splitScalar) return [text];
        // Spaces inside tool argument patterns belong to the same tool.
        var parts = ImmutableArray.CreateBuilder<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] == '(') depth++;
            if (i < text.Length && text[i] == ')') depth = Math.Max(0, depth - 1);
            if (i == text.Length || (char.IsWhiteSpace(text[i]) && depth == 0))
            {
                if (i > start) parts.Add(text[start..i]);
                start = i + 1;
            }
        }
        return parts.ToImmutable();
    }

    private static void Flatten(Dictionary<string, object> map, string prefix, ImmutableDictionary<string, string>.Builder output)
    {
        foreach (var (key, value) in map)
        {
            var path = prefix + key;
            if (value is string text) output[path] = text;
            else if (value is NonStringScalar typed) output[path] = typed.Value;
            else if (value is Dictionary<string, object> nested) Flatten(nested, path + ".", output);
            else if (value is List<object> list) output[path] = string.Join(", ", list.OfType<string>());
        }
    }

    internal static IEnumerable<string> ParseFlowArray(string value)
    {
        try
        {
            var parser = new Parser(new StringReader(value));
            parser.Consume<StreamStart>();
            parser.Consume<DocumentStart>();
            var nodes = 0;
            return StringList(ReadNode(parser, 0, ref nodes), false);
        }
        catch (Exception ex) when (ex is YamlException or FormatException) { return []; }
    }

    internal static string Unquote(string value) => value.Length >= 2
        && ((value[0] == '\"' && value[^1] == '\"') || (value[0] == '\'' && value[^1] == '\''))
        ? value[1..^1] : value;
}
