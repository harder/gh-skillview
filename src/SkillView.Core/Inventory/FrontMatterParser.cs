using System.Collections.Immutable;
using System.Globalization;

namespace SkillView.Inventory;

/// Hand-rolled SKILL.md front-matter parser. We handle the subset actually
/// emitted in the wild — scalar keys, string-or-boolean scalars, and block /
/// flow arrays — rather than pulling in YamlDotNet (a heavy AOT surface for a
/// file format we only read). Unknown keys land in `Extra`.
public static class FrontMatterParser
{
    /// Returns (body, frontMatter). If the document has no front-matter block,
    /// `FrontMatter` is `Empty` and `Body` is the whole input.
    public static (string Body, Models.SkillFrontMatter FrontMatter, bool Parsed) Parse(string source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return (string.Empty, Models.SkillFrontMatter.Empty, false);
        }

        // Accept `---\n` or `---\r\n` opening. Must be at position 0.
        if (!source.StartsWith("---", StringComparison.Ordinal))
        {
            return (source, Models.SkillFrontMatter.Empty, false);
        }

        // Allow an optional BOM and whitespace up to the first newline after the opener.
        var firstNl = source.IndexOf('\n', 3);
        if (firstNl < 0) return (source, Models.SkillFrontMatter.Empty, false);
        var afterOpen = firstNl + 1;

        // Find closing `---` line.
        var closeIdx = FindCloseFence(source, afterOpen);
        if (closeIdx < 0)
        {
            return (source, Models.SkillFrontMatter.Empty, false);
        }

        var blockEnd = closeIdx;
        var afterClose = source.IndexOf('\n', closeIdx + 3);
        var body = afterClose < 0 ? string.Empty : source[(afterClose + 1)..];

        var block = source.Substring(afterOpen, blockEnd - afterOpen);
        var fm = ParseBlock(block);
        return (body, fm, true);
    }

    private static int FindCloseFence(string source, int start)
    {
        var idx = start;
        while (idx < source.Length)
        {
            // Look for a line starting with `---` or `...`.
            var lineEnd = source.IndexOf('\n', idx);
            var line = lineEnd < 0 ? source[idx..] : source[idx..lineEnd];
            var trimmed = line.TrimEnd('\r');
            if (trimmed == "---" || trimmed == "...") return idx;
            if (lineEnd < 0) return -1;
            idx = lineEnd + 1;
        }
        return -1;
    }

    private static Models.SkillFrontMatter ParseBlock(string block)
    {
        string? name = null, description = null, version = null, license = null, upstream = null, sha = null;
        bool pinned = false;
        var tools = ImmutableArray.CreateBuilder<string>();
        var agents = ImmutableArray.CreateBuilder<string>();
        var extra = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        var lines = block.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            if (raw.Length == 0) continue;
            // Skip comment-only or blank lines.
            var ltrim = raw.TrimStart();
            if (ltrim.Length == 0 || ltrim[0] == '#') continue;
            // Only top-level keys (no leading whitespace) are recognized.
            if (raw.Length != ltrim.Length) continue;

            var colon = raw.IndexOf(':');
            if (colon <= 0) continue;
            var key = raw[..colon].Trim();
            var value = raw[(colon + 1)..].Trim();

            // Flow array `key: [a, b]`
            if (value.StartsWith('[') && value.EndsWith(']'))
            {
                var items = ParseFlowArray(value);
                switch (NormalizeKey(key))
                {
                    case "allowed-tools": tools.AddRange(items); break;
                    case "agents": agents.AddRange(items); break;
                    default:
                        extra[key] = string.Join(", ", items);
                        break;
                }
                continue;
            }

            // Block array:
            //   key:
            //     - item
            //     - item
            if (value.Length == 0 && i + 1 < lines.Length && IsBlockArrayStart(lines[i + 1]))
            {
                var items = ImmutableArray.CreateBuilder<string>();
                while (i + 1 < lines.Length && IsBlockArrayStart(lines[i + 1]))
                {
                    var item = lines[++i].TrimStart();
                    // Strip leading "- " and surrounding quotes.
                    item = item[1..].TrimStart();
                    items.Add(Unquote(item));
                }
                switch (NormalizeKey(key))
                {
                    case "allowed-tools": tools.AddRange(items); break;
                    case "agents": agents.AddRange(items); break;
                    default:
                        extra[key] = string.Join(", ", items);
                        break;
                }
                continue;
            }

            var scalar = Unquote(value);
            switch (NormalizeKey(key))
            {
                case "name": name = scalar; break;
                case "description": description = scalar; break;
                case "version": version = scalar; break;
                case "license": license = scalar; break;
                case "upstream": upstream = scalar; break;
                case "github-tree-sha":
                case "tree-sha":
                case "treesha":
                    sha = scalar;
                    break;
                case "pinned":
                    pinned = string.Equals(scalar, "true", StringComparison.OrdinalIgnoreCase);
                    break;
                default:
                    extra[key] = scalar;
                    break;
            }
        }

        return new Models.SkillFrontMatter
        {
            Name = name,
            Description = description,
            Version = version,
            License = license,
            Upstream = upstream,
            GithubTreeSha = sha,
            Pinned = pinned,
            AllowedTools = tools.ToImmutable(),
            Agents = agents.ToImmutable(),
            Extra = extra.ToImmutable(),
        };
    }

    private static bool IsBlockArrayStart(string line)
    {
        var i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
        if (i >= line.Length) return false;
        if (line[i] != '-') return false;
        // Require at least one leading space (i.e. indented under the key) OR
        // the next char being a space — guards against `---` closers.
        if (i == 0) return false;
        return i + 1 < line.Length && line[i + 1] == ' ';
    }

    internal static IEnumerable<string> ParseFlowArray(string value)
    {
        // Assumes value starts with '[' and ends with ']'.
        var inner = value[1..^1];
        var parts = new List<string>();
        var sb = new System.Text.StringBuilder();
        char? quote = null;
        foreach (var ch in inner)
        {
            if (quote is not null)
            {
                if (ch == quote) { quote = null; continue; }
                sb.Append(ch);
                continue;
            }
            if (ch == '"' || ch == '\'') { quote = ch; continue; }
            if (ch == ',')
            {
                AppendTrimmed(parts, sb);
                continue;
            }
            sb.Append(ch);
        }
        AppendTrimmed(parts, sb);
        return parts;
    }

    private static void AppendTrimmed(List<string> parts, System.Text.StringBuilder sb)
    {
        var piece = sb.ToString().Trim();
        sb.Clear();
        if (piece.Length > 0) parts.Add(piece);
    }

    internal static string Unquote(string s)
    {
        if (s.Length >= 2 &&
            ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
        {
            return s[1..^1];
        }
        return s;
    }

    private static string NormalizeKey(string key) => key.ToLower(CultureInfo.InvariantCulture);
}
