using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkillView.Inventory;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;
using Tomlyn.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace SkillView.Loading;

/// Internal parsed data is never emitted as configuration output: files can
/// contain credentials. Output includes only file evidence and selected rules.
internal sealed record LoadingValue(string Kind, string? Text = null, bool? Boolean = null)
{
    internal ImmutableDictionary<string, LoadingValue> Fields { get; init; } = ImmutableDictionary<string, LoadingValue>.Empty;
    // Native permission rules can use the last matching pattern. Hash-map
    // enumeration is not a substitute for the configuration's source order.
    internal ImmutableArray<string> FieldOrder { get; init; } = [];
    internal ImmutableArray<LoadingValue> Items { get; init; } = [];
    internal LoadingValue? Get(params string[] path)
    {
        LoadingValue? value = this;
        foreach (var part in path) value = value?.Fields.GetValueOrDefault(part);
        return value;
    }
}

internal sealed record LoadingConfigurationDocument(LoadingFileEvidence Evidence, LoadingValue? Root);
internal enum LoadingJsonSyntax { Strict, Comments, Jsonc }
internal enum LoadingContentFormat { FromExtension, Json, Toml, Yaml }

[TomlSerializable(typeof(TomlTable))]
[TomlSourceGenerationOptions(MaxDepth = 16, DuplicateKeyHandling = TomlDuplicateKeyHandling.Error)]
internal partial class LoadingTomlContext : TomlSerializerContext;

/// Bounded read-only parsing; aliases, duplicate keys, unsupported structure and
/// exhausted budgets are unknown configuration, never an empty enabled policy.
internal sealed class LoadingConfigurationReader
{
    internal const int MaxFileBytes = 256 * 1024;
    internal const int MaxTotalBytes = 2 * 1024 * 1024;
    internal const int MaxFiles = 64;
    internal const int MaxNodes = 8192;
    private int _files;
    private int _bytes;
    private readonly int _maxFiles;
    private readonly int _maxTotalBytes;

    internal LoadingConfigurationReader(int maxFiles = MaxFiles, int maxTotalBytes = MaxTotalBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFiles, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTotalBytes, 1);
        _maxFiles = maxFiles; _maxTotalBytes = maxTotalBytes;
    }

    internal LoadingConfigurationDocument Read(string path, string purpose, CancellationToken token,
        LoadingJsonSyntax jsonSyntax = LoadingJsonSyntax.Strict, bool frontMatter = false,
        LoadingContentFormat format = LoadingContentFormat.FromExtension)
    {
        token.ThrowIfCancellationRequested();
        if (++_files > _maxFiles || _bytes >= _maxTotalBytes)
            return Result("bounded", null, "Configuration capture budget exceeded.");
        byte[] bytes;
        try
        {
            // Resolve a configuration symlink without opening its contents, then
            // inspect the opened target itself. A FIFO/device (or a replacement
            // link) must never reach a synchronous managed read.
            var attributes = File.GetAttributes(path);
            var address = (attributes & FileAttributes.ReparsePoint) != 0
                ? File.ResolveLinkTarget(path, true)?.FullName ?? throw new IOException("Unresolved configuration link.")
                : path;
            token.ThrowIfCancellationRequested();
            using var stream = RegularFileInspection.Open(address);
            if (!frontMatter && (stream.Length > MaxFileBytes || stream.Length > _maxTotalBytes - _bytes))
                return Result("bounded", null, "Configuration file exceeds the capture byte limit.");
            using var capture = new MemoryStream();
            var buffer = new byte[4096];
            var maxBytes = frontMatter ? LocalSkillScanner.MaxFrontMatterPrefixBytes : MaxFileBytes;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, 0, Math.Min(buffer.Length,
                    Math.Min(maxBytes - (int)capture.Length + 1, _maxTotalBytes - _bytes + 1)));
                if (read == 0) break;
                _bytes += read;
                if (_bytes > _maxTotalBytes)
                    return Result("bounded", null, "Configuration grew beyond the capture byte limit.");
                capture.Write(buffer, 0, read);
                if (frontMatter && HeaderRange(capture.GetBuffer().AsSpan(0, (int)capture.Length), out _, out var headerEnd) && headerEnd <= maxBytes) break;
                if (capture.Length > maxBytes)
                    return Result("bounded", null, "Configuration grew beyond the capture byte limit.");
            }
            bytes = capture.ToArray();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { return Result("missing", null, null); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return Result("unavailable", null, "Configuration could not be read; enablement remains unknown."); }
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        try
        {
            if (frontMatter)
            {
                if (!HeaderRange(bytes, out var start, out var end, complete: true)) throw new FormatException();
                // Hash only the bounded observed header, never a partial body
                // whose encoding/content is unrelated to loading policy.
                digest = Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan(0, end)));
                bytes = bytes[start..end];
            }
            var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
            var nodes = 0;
            LoadingValue value;
            var extension = frontMatter || format == LoadingContentFormat.Yaml ? ".yaml"
                : format == LoadingContentFormat.Toml ? ".toml" : format == LoadingContentFormat.Json ? ".json" : Path.GetExtension(path).ToLowerInvariant();
            switch (extension)
            {
                case ".toml":
                    // The DOM converter merges table fragments and does not
                    // reject duplicate scalar keys. Validate TOML semantics
                    // before converting; never treat a lossy model as policy.
                    SyntaxParser.ParseStrict(text, LoadingTomlContext.Default.Options);
                    value = FromToml(TomlSerializer.Deserialize<TomlTable>(text, LoadingTomlContext.Default)!, 0, ref nodes, token);
                    break;
                case ".yaml" or ".yml":
                    var parser = new Parser(new StringReader(text));
                    parser.Consume<StreamStart>(); parser.Consume<DocumentStart>();
                    value = FromYaml(parser, 0, ref nodes, token);
                    parser.Consume<DocumentEnd>(); parser.Consume<StreamEnd>();
                    break;
                default:
                    using (var json = JsonDocument.Parse(text, new()
                    {
                        AllowTrailingCommas = jsonSyntax == LoadingJsonSyntax.Jsonc || extension == ".jsonc",
                        CommentHandling = jsonSyntax != LoadingJsonSyntax.Strict || extension == ".jsonc"
                            ? JsonCommentHandling.Skip : JsonCommentHandling.Disallow,
                        MaxDepth = 16
                    }))
                        value = FromJson(json.RootElement, 0, ref nodes, token);
                    break;
            }
            if (value.Kind != "table") throw new FormatException();
            return new(new(path, "observed", digest, purpose, null), value);
        }
        catch (Exception ex) when (ex is JsonException or TomlException or YamlException or FormatException or DecoderFallbackException)
        { return Result("invalid", digest, "Configuration syntax or complexity is unsupported; review this file without assuming defaults."); }

        LoadingConfigurationDocument Result(string state, string? hash, string? diagnostic) =>
            new(new(path, state, hash, purpose, diagnostic), null);
    }

    private static bool HeaderRange(ReadOnlySpan<byte> bytes, out int start, out int end, bool complete = false)
    {
        start = 0; end = 0;
        var bom = bytes.StartsWith("\uFEFF"u8) ? 3 : 0;
        bytes = bytes[bom..];
        // The offset is based on the original span, including its optional BOM.
        // Keep delimiters byte-based so an incomplete UTF-8 body cannot reject
        // an otherwise complete YAML prefix.
        var newline = bytes.IndexOf((byte)'\n');
        if (newline < 0 || !TrimCr(bytes[..newline]).SequenceEqual("---"u8)) return false;
        var offset = newline + 1;
        start = offset + bom;
        while (offset < bytes.Length)
        {
            var length = bytes[offset..].IndexOf((byte)'\n');
            var line = TrimCr(length < 0 ? bytes[offset..] : bytes.Slice(offset, length));
            if ((length >= 0 || complete) && (line.SequenceEqual("---"u8) || line.SequenceEqual("..."u8))) { end = offset + bom; return true; }
            if (length < 0) break;
            offset += length + 1;
        }
        return false;
    }

    private static ReadOnlySpan<byte> TrimCr(ReadOnlySpan<byte> bytes) => bytes.Length > 0 && bytes[^1] == '\r' ? bytes[..^1] : bytes;

    private static void Admit(int depth, ref int nodes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (depth > 16 || ++nodes > MaxNodes) throw new FormatException();
    }

    private static LoadingValue FromJson(JsonElement value, int depth, ref int nodes, CancellationToken token)
    {
        Admit(depth, ref nodes, token);
        if (value.ValueKind == JsonValueKind.Object)
        {
            var fields = ImmutableDictionary.CreateBuilder<string, LoadingValue>(StringComparer.Ordinal);
            var order = ImmutableArray.CreateBuilder<string>();
            foreach (var property in value.EnumerateObject())
            {
                if (!fields.TryAdd(property.Name, FromJson(property.Value, depth + 1, ref nodes, token))) throw new FormatException();
                order.Add(property.Name);
            }
            return new("table") { Fields = fields.ToImmutable(), FieldOrder = order.ToImmutable() };
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var items = ImmutableArray.CreateBuilder<LoadingValue>();
            foreach (var item in value.EnumerateArray()) items.Add(FromJson(item, depth + 1, ref nodes, token));
            return new("array") { Items = items.ToImmutable() };
        }
        return value.ValueKind switch
        {
            JsonValueKind.String => new("string", value.GetString()),
            JsonValueKind.True => new("boolean", Boolean: true),
            JsonValueKind.False => new("boolean", Boolean: false),
            _ => new("other")
        };
    }

    private static LoadingValue FromToml(object? value, int depth, ref int nodes, CancellationToken token)
    {
        Admit(depth, ref nodes, token);
        if (value is TomlTable table)
        {
            var fields = ImmutableDictionary.CreateBuilder<string, LoadingValue>(StringComparer.Ordinal);
            var order = ImmutableArray.CreateBuilder<string>();
            foreach (var entry in table)
            {
                fields.Add(entry.Key, FromToml(entry.Value, depth + 1, ref nodes, token));
                order.Add(entry.Key);
            }
            return new("table") { Fields = fields.ToImmutable(), FieldOrder = order.ToImmutable() };
        }
        if (value is System.Collections.IEnumerable enumerable and not string)
        {
            var items = ImmutableArray.CreateBuilder<LoadingValue>();
            foreach (var entry in enumerable) items.Add(FromToml(entry, depth + 1, ref nodes, token));
            return new("array") { Items = items.ToImmutable() };
        }
        return value switch { string text => new("string", text), bool flag => new("boolean", Boolean: flag), _ => new("other") };
    }

    private static LoadingValue FromYaml(IParser parser, int depth, ref int nodes, CancellationToken token)
    {
        Admit(depth, ref nodes, token);
        if (parser.TryConsume<MappingStart>(out var mapping))
        {
            if (!mapping.Anchor.IsEmpty || !mapping.Tag.IsEmpty && mapping.Tag.ToString() != "tag:yaml.org,2002:map") throw new FormatException();
            var fields = ImmutableDictionary.CreateBuilder<string, LoadingValue>(StringComparer.Ordinal);
            var order = ImmutableArray.CreateBuilder<string>();
            while (!parser.TryConsume<MappingEnd>(out _))
            {
                var key = FromYaml(parser, depth + 1, ref nodes, token);
                if (key.Kind != "string" || !fields.TryAdd(key.Text!, FromYaml(parser, depth + 1, ref nodes, token))) throw new FormatException();
                order.Add(key.Text!);
            }
            return new("table") { Fields = fields.ToImmutable(), FieldOrder = order.ToImmutable() };
        }
        if (parser.TryConsume<SequenceStart>(out var sequence))
        {
            if (!sequence.Anchor.IsEmpty || !sequence.Tag.IsEmpty && sequence.Tag.ToString() != "tag:yaml.org,2002:seq") throw new FormatException();
            var items = ImmutableArray.CreateBuilder<LoadingValue>();
            while (!parser.TryConsume<SequenceEnd>(out _)) items.Add(FromYaml(parser, depth + 1, ref nodes, token));
            return new("array") { Items = items.ToImmutable() };
        }
        if (!parser.TryConsume<Scalar>(out var scalar)) throw new FormatException();
        if (!scalar.Anchor.IsEmpty) throw new FormatException();
        if ((scalar.Style == ScalarStyle.Plain && scalar.Tag.IsEmpty || scalar.Tag.ToString() == "tag:yaml.org,2002:bool")
            && bool.TryParse(scalar.Value, out var boolean)) return new("boolean", Boolean: boolean);
        if (scalar.Tag.ToString() is "tag:yaml.org,2002:null" or "tag:yaml.org,2002:int" or "tag:yaml.org,2002:float") return new("other");
        if (!scalar.Tag.IsEmpty && scalar.Tag.ToString() != "tag:yaml.org,2002:str") throw new FormatException();
        if (scalar.Tag.IsEmpty && scalar.Style == ScalarStyle.Plain && IsImplicitNonString(scalar.Value)) return new("other");
        return new("string", scalar.Value);
    }

    // YAML's implicit scalar typing must not turn a numeric/null policy into
    // an allow/deny string. Quoted values and explicit !!str stay strings.
    private static bool IsImplicitNonString(string value) => value.Length == 0 || value == "~"
        || value.Equals("null", StringComparison.OrdinalIgnoreCase)
        || Regex.IsMatch(value, @"^[+-]?(?:[0-9][0-9_]*(?:\.[0-9_]*)?(?:[eE][+-]?[0-9]+)?|\.[0-9_]+(?:[eE][+-]?[0-9]+)?|0[xX][0-9a-fA-F_]+|0[oO][0-7_]+|\.(?:inf|Inf|INF|nan|NaN|NAN))$",
            RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
}
