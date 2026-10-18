using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkillView.Inventory;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace SkillView.PackageChecking;

/// Literal declarations are evidence of requirements, never an installed-package
/// inventory. Only held, bounded, hash-verified text reaches this scanner.
internal static class PackageDeclarationScanner
{
    private static readonly Regex Requirement = new(
        @"^[A-Za-z0-9][A-Za-z0-9._-]*(?:\[[A-Za-z0-9][A-Za-z0-9._-]*(?:,[A-Za-z0-9][A-Za-z0-9._-]*)*\])?(?:\s*(?:===|==|!=|~=|<=|>=|<|>)\s*[A-Za-z0-9][A-Za-z0-9.*+!_-]*(?:\s*,\s*(?:===|==|!=|~=|<=|>=|<|>)\s*[A-Za-z0-9][A-Za-z0-9.*+!_-]*)*)?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static bool Select(string path) => Path.GetFileName(path) is "package.json" or "requirements.txt" or "pyproject.toml" or "Cargo.toml" or "go.mod"
        || path == "agents/openai.yaml"
        || path.StartsWith("scripts/", StringComparison.Ordinal) && Path.GetExtension(path) is ".py" or ".sh" or ".js" or ".ts" or ".ps1" or ".cmd" or ".bat";

    internal static void HeaderShapes(string text, Action<string> unknown, CancellationToken token)
    {
        // Parsed projection lists intentionally omit non-string members. Inspect
        // their original YAML shapes before those projections support a report.
        using var lines = new StringReader(text.TrimStart('\uFEFF'));
        if (lines.ReadLine() != "---") return;
        var block = new StringBuilder();
        while (lines.ReadLine() is { } line && line is not ("---" or "..."))
        {
            token.ThrowIfCancellationRequested();
            if (block.Length + line.Length > FrontMatterParser.MaxYamlChars) { unknown("Root declaration shape exceeded its YAML bound."); return; }
            block.AppendLine(line);
        }
        try
        {
            var parser = new Parser(new StringReader(block.ToString()));
            parser.Consume<StreamStart>(); parser.Consume<DocumentStart>(); parser.Consume<MappingStart>();
            var nodes = 0;
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                token.ThrowIfCancellationRequested();
                var key = parser.Consume<Scalar>().Value;
                var shape = Shape(0);
                if (key == "dependencies" || key == "compatibility" && shape != "string"
                    || key is "allowed-tools" or "agents" && shape is not ("string" or "string-array"))
                    unknown("Root " + key + " declaration has unsupported syntax; no dependency, agent selection or permission default was inferred.");
            }

            string Shape(int depth)
            {
                token.ThrowIfCancellationRequested();
                if (depth > FrontMatterParser.MaxDepth || ++nodes > FrontMatterParser.MaxNodes) throw new FormatException();
                if (parser.TryConsume<Scalar>(out var scalar))
                {
                    var value = scalar.Value;
                    var explicitString = scalar.Tag.ToString() == "tag:yaml.org,2002:str";
                    var typed = !scalar.Tag.IsEmpty && !explicitString || scalar.Style == ScalarStyle.Plain && !explicitString
                        && (value.Length == 0 || value is "~" || value.Equals("null", StringComparison.OrdinalIgnoreCase)
                            || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase)
                            || decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
                    return typed ? "typed" : "string";
                }
                if (parser.TryConsume<SequenceStart>(out _))
                {
                    var strings = true;
                    while (!parser.TryConsume<SequenceEnd>(out _)) strings &= Shape(depth + 1) == "string";
                    return strings ? "string-array" : "array";
                }
                if (!parser.TryConsume<MappingStart>(out _)) throw new FormatException();
                while (!parser.TryConsume<MappingEnd>(out _)) { _ = Shape(depth + 1); _ = Shape(depth + 1); }
                return "table";
            }
        }
        catch (Exception exception) when (exception is YamlException or FormatException)
        { unknown("Root declaration shapes were not completely understood; no defaults were inferred."); }
    }

    internal static void Scan(string path, string text, Action<PackageObservation> observe,
        Action<string> unknown, Action<PackageMarkdownReference> local, CancellationToken token)
    {
        // URLs are literal text observations, including examples and comments.
        // They are never a claim that a skill contacts that endpoint.
        using (var lines = new StringReader(text))
        {
            var lineNumber = 0; var urls = 0;
            while (lines.ReadLine() is { } line)
            {
                token.ThrowIfCancellationRequested(); lineNumber++;
                for (var start = 0; start < line.Length; start++)
                {
                    token.ThrowIfCancellationRequested();
                    if (!line.AsSpan(start).StartsWith("https://", StringComparison.Ordinal)
                        && !line.AsSpan(start).StartsWith("http://", StringComparison.Ordinal)) continue;
                    if (++urls > 4096) { unknown(path + ": literal URL limit exceeded."); break; }
                    var end = start;
                    while (end < line.Length && !char.IsWhiteSpace(line[end]) && !"\"'<>`()[]{}".Contains(line[end])) end++;
                    var literal = line[start..Math.Min(end, start + 4096)];
                    if (end - start > 4096) unknown(path + ": literal URL length limit exceeded.");
                    if (end - start <= 4096 && Uri.TryCreate(literal, UriKind.Absolute, out var url) && url.Host.Length > 0)
                        observe(new("network", "literal-url", path, lineNumber, literal, "text-only",
                            "This URL occurs in captured text; it may be an example or documentation. No request was sent and no runtime network behavior was inferred."));
                    start = Math.Max(start, end - 1);
                }
                if (urls > 4096) break;
            }
        }
        if (Path.GetFileName(path) == "package.json")
        {
            var pending = new List<PackageObservation>();
            var pendingLocal = new List<PackageMarkdownReference>();
            try
            {
                using var json = JsonDocument.Parse(text, new() { MaxDepth = 16 });
                if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                var rootKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in json.RootElement.EnumerateObject())
                {
                    token.ThrowIfCancellationRequested();
                    if (!rootKeys.Add(property.Name)) throw new JsonException("Duplicate manifest property.");
                    if (property.Name is not ("dependencies" or "devDependencies" or "optionalDependencies" or "peerDependencies")) continue;
                    if (property.Value.ValueKind != JsonValueKind.Object) throw new JsonException("Dependency declarations must be mappings.");
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var item in property.Value.EnumerateObject())
                    {
                        token.ThrowIfCancellationRequested();
                        if (pending.Count >= 4096) throw new JsonException("Dependency declaration limit exceeded.");
                        if (!names.Add(item.Name) || item.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.Name)
                            || item.Name.Length > 512 || item.Value.GetString() is not { Length: > 0 and <= 4096 } value)
                            throw new JsonException("Unsupported or duplicate dependency declaration.");
                        pending.Add(new("dependency", property.Name, path, null, item.Name + "@" + value, "availability-unknown",
                            $"Provide the reviewed {item.Name}@{value} requirement in your selected Node environment using {path}; reconcile its lockfile there. No installed modules or package manager were inspected."));
                        if (value.StartsWith("file:", StringComparison.Ordinal) || value.StartsWith("link:", StringComparison.Ordinal))
                        {
                            var address = value[5..];
                            if (address.StartsWith('~'))
                            {
                                unknown(path + ": home-relative dependency address is environment-specific; no external path was inspected.");
                                pending.Add(new("dependency", "home-relative-path", path, null, value, "unknown",
                                    "Resolve this home-relative dependency only in your explicitly selected Node environment. SkillView did not expand HOME, probe an external path or infer that the dependency is missing."));
                            }
                            else if (address.Length == 0 || address.IndexOfAny(['%', '#', '?', ':']) >= 0)
                                unknown(path + ": unsupported local dependency URL; no missing path was inferred.");
                            else pendingLocal.Add(new(address, 0, false, Dependency: true));
                        }
                    }
                }
                foreach (var observation in pending) { token.ThrowIfCancellationRequested(); observe(observation); }
                foreach (var reference in pendingLocal) { token.ThrowIfCancellationRequested(); local(reference); }
            }
            catch (JsonException)
            {
                unknown(path + ": malformed, duplicate or unsupported dependency declarations; no absence or default requirements were inferred.");
                observe(new("dependency", "unsupported-manifest", path, null, "package.json", "unknown",
                    "Review package.json syntax and dependency mapping/string values in the authored source, then repeat the check. No installed dependency state was inferred."));
            }
        }
        else if (Path.GetFileName(path) == "requirements.txt")
        {
            using var reader = new StringReader(text);
            var number = 0;
            while (reader.ReadLine() is { } line)
            {
                token.ThrowIfCancellationRequested(); number++;
                var requirement = line.Trim();
                if (requirement.Length == 0 || requirement.StartsWith('#')) continue;
                if (number > 4096) { unknown(path + ": requirement line limit exceeded."); break; }
                if (requirement.StartsWith("-r ", StringComparison.Ordinal))
                {
                    var file = requirement[3..].Trim();
                    if (file.Length > 0 && file.Length <= 4096 && !file.Any(char.IsWhiteSpace))
                    {
                        local(new(file, number, false, Dependency: true));
                        unknown(path + ": -r include checks its local target only; nested requirement content is not expanded.");
                        continue;
                    }
                }
                // Limited plain names with optional extras/version constraints.
                // Markers, direct URLs, includes/options and shell expansion are
                // explicitly unsupported rather than partially interpreted.
                if (requirement.Length > 4096 || !Requirement.IsMatch(requirement))
                {
                    unknown(path + ": unsupported requirement syntax at line " + number + ".");
                    observe(new("dependency", "unsupported-requirement", path, number, requirement[..Math.Min(512, requirement.Length)], "unknown",
                        $"Review {path}:{number} with the declared Python environment; this checker does not resolve options, markers, URLs or generated requirements."));
                    continue;
                }
                observe(new("dependency", "python-requirement", path, number, requirement, "availability-unknown",
                    $"Provide the reviewed requirement {requirement} in your selected Python environment from {path}:{number} (for example with python -m pip install -r {path} after review). No Python environment was inspected."));
            }
        }
        else if (Path.GetFileName(path) is "pyproject.toml" or "Cargo.toml" or "go.mod" || path == "agents/openai.yaml")
        {
            unknown(path + ": this dependency/agent configuration grammar is not modeled.");
            observe(new("declaration", "unsupported-configuration", path, null, Path.GetFileName(path), "unknown",
                "Review the authored configuration against the selected agent or dependency manager's versioned documentation in your intended environment. No defaults, installed dependencies or effective permissions were inferred."));
        }
        else
        {
            using var reader = new StringReader(text);
            var first = reader.ReadLine();
            if (first?.StartsWith("#!", StringComparison.Ordinal) == true)
                observe(new("portability", "script-interpreter", path, 1, first[2..], "environment-required",
                    "Provide this declared interpreter in the intended operating system, or review an authored portable invocation. No interpreter was probed or executed."));
            if (text.Contains("\r\n", StringComparison.Ordinal) && first?.StartsWith("#!", StringComparison.Ordinal) == true)
                observe(new("portability", "shebang-crlf", path, 1, "CRLF", "review",
                    "Unix direct script execution may include a carriage return in its interpreter name. Review source line endings or invoke the intended interpreter explicitly."));
        }
    }

    internal static void PathPortability(string path, Action<PackageObservation> observe)
    {
        foreach (var part in path.Split('/'))
        {
            var stem = part.Split('.')[0];
            if (part.EndsWith(' ') || part.EndsWith('.') || part.Any(c => "<>:\\|?*".Contains(c))
                || stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9')
            {
                observe(new("portability", "windows-path", path, null, part, "review",
                    "This captured name may be unsupported by ordinary Windows path APIs. Review a portable name and update its authored references before distribution; installed bytes were preserved."));
                break;
            }
        }
    }
}
