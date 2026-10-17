using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkillView.Inventory;
using SkillView.Maintenance;

namespace SkillView.Evaluation;

internal sealed record EvaluationPrompt(string Id, string Text);
internal sealed record EvaluationEnvironment(string DockerExecutable, string DockerEndpoint,
    string Image, string ImageId, string Entrypoint, ImmutableArray<string> Arguments,
    string Evaluator, string Context, ImmutableArray<EvaluationPrompt> Prompts, int TimeoutSeconds, int MemoryMiB);

/// A review is deliberately independent of Docker discovery. Its image/runtime
/// fields are declarations until a consented run verifies them locally.
internal sealed class EvaluationReview
{
    internal const int MaxInputBytes = 1024 * 1024;
    internal const int MaxPayloadBytes = 16 * 1024 * 1024;
    internal const int MaxEntries = 1024;
    internal const int OutputCharacters = 65536;
    internal const string Policy = "skillview.offline-docker-evaluation.v2";
    internal const string Scope = "Operator-selected offline evaluator and prompts only; no SkillView quality score or safety verdict. Docker daemon/image are trusted prerequisites; runtime isolation has not been independently proved. Bindings describe frozen requested inputs; Docker pathname mounting is not atomic against same-UID interference.";
    internal EvaluationEnvironment Environment { get; }
    internal PayloadSnapshot Payload { get; }
    internal string ConfigSha256 { get; }
    internal string Fingerprint { get; }
    internal byte[] Request { get; }
    internal ImmutableDictionary<string, byte[]> Files { get; }

    private EvaluationReview(EvaluationEnvironment environment, PayloadSnapshot payload, string configHash,
        ImmutableDictionary<string, byte[]> files)
    {
        Environment = environment; Payload = payload; ConfigSha256 = configHash; Files = files;
        using (var request = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(request))
            {
                writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 1); writer.WriteString("skillPath", "/input/skill");
                writer.WriteString("evaluator", environment.Evaluator); writer.WriteString("context", environment.Context);
                writer.WriteStartArray("prompts");
                foreach (var prompt in environment.Prompts)
                { writer.WriteStartObject(); writer.WriteString("id", prompt.Id); writer.WriteString("text", prompt.Text); writer.WriteEndObject(); }
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            Request = request.ToArray();
        }
        using var binding = new MemoryStream();
        using (var writer = new BinaryWriter(binding, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Policy); writer.Write(payload.Path); writer.Write(payload.Fingerprint);
            writer.Write(payload.ObjectStateFingerprint ?? throw new IOException("Native payload evidence unavailable."));
            writer.Write(configHash); writer.Write(Hash(Request));
        }
        Fingerprint = Hash(binding.ToArray());
    }

    internal static readonly ImmutableArray<string> ContainerEnvironment = ["HOME=/work", "XDG_CONFIG_HOME=/work/config",
        "XDG_CACHE_HOME=/work/cache", "XDG_DATA_HOME=/work/data", "XDG_STATE_HOME=/work/state", "TMPDIR=/tmp"];

    internal static EvaluationReview Capture(string path, string configPath, CancellationToken token,
        Func<string, Action<NativePayloadItem, PayloadEntry>, CancellationToken, PayloadSnapshot>? captureForTests = null)
    {
        token.ThrowIfCancellationRequested();
        byte[]? config = null;
        RegularFileInspection.Capture(configPath, item =>
        {
            config = Read(item.Content ?? throw new IOException("Config stream unavailable."), MaxInputBytes, token);
            return new(Hash(config).ToUpperInvariant(), config.Length);
        }, token);
        var environment = Parse(config ?? throw new IOException("Config capture unavailable."));
        var files = ImmutableDictionary.CreateBuilder<string, byte[]>(StringComparer.Ordinal);
        long bytes = 0; var entries = 0;
        var payload = (captureForTests ?? PayloadSnapshot.CaptureWithEntrySink)(path, (item, entry) =>
        {
            token.ThrowIfCancellationRequested();
            if (++entries > MaxEntries || entry.Bytes > MaxPayloadBytes - bytes)
                throw new IOException("Evaluation payload exceeds 1024 entries or 16 MiB.");
            if (entry.Kind is not ("file" or "directory") || entry.Path.Split('/').Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Evaluation refuses links, special files and .git metadata; prepare a separate regular-file package.");
            foreach (var part in entry.Path.Split('/')) NativePayloadDirectory.CheckName(part);
            if (entry.Kind != "file") return;
            var content = Read(item.Content ?? throw new IOException("Held payload stream unavailable."), MaxPayloadBytes - checked((int)bytes), token);
            if (content.Length != entry.Bytes || Hash(content).ToUpperInvariant() != entry.ContentHash)
                throw new IOException("Held evaluation bytes differ from the captured payload fingerprint.");
            bytes += content.Length; files.Add(entry.Path, content);
        }, token);
        if (!files.ContainsKey("SKILL.md")) throw new IOException("Evaluation requires an exact regular root SKILL.md.");
        // No partial callback evidence escapes a failed final native capture.
        return new(environment, payload, Hash(config), files.ToImmutable());
    }

    internal static EvaluationEnvironment Parse(byte[] bytes)
    {
        if (bytes.Length > MaxInputBytes) throw new ArgumentException("Evaluation config exceeds 1 MiB.");
        using var document = JsonDocument.Parse(bytes, new() { MaxDepth = 16 });
        var root = document.RootElement;
        RequireProperties(root, ["schemaVersion", "dockerExecutable", "dockerEndpoint", "image", "imageId", "entrypoint", "arguments", "evaluator", "context", "prompts", "timeoutSeconds", "memoryMiB"]);
        if (root.GetProperty("schemaVersion").GetInt32() != 1) throw new ArgumentException("Unsupported evaluation config schemaVersion.");
        var executable = Text(root, "dockerExecutable", 4096); var endpoint = Text(root, "dockerEndpoint", 4096);
        var image = Text(root, "image", 512); var imageId = Text(root, "imageId", 71);
        var entrypoint = Text(root, "entrypoint", 4096);
        // This declaration describes the Linux runner even during read-only
        // review on another host. Windows Path APIs give '/' a different meaning;
        // Linux absolute path grammar requires that leading slash, not a drive.
        if (!executable.StartsWith('/')) throw new ArgumentException("dockerExecutable must be an explicit absolute Linux path.");
        if (!endpoint.StartsWith("unix:///", StringComparison.Ordinal) || endpoint.IndexOfAny(['?', '#']) >= 0
            || endpoint[7..].Split('/').Any(part => part is "." or ".."))
            throw new ArgumentException("dockerEndpoint must be an explicit local absolute unix:/// socket, without query or traversal.");
        if (!Regex.IsMatch(image, "^[a-zA-Z0-9][a-zA-Z0-9._:/-]*@sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant)
            || !Regex.IsMatch(imageId, "^sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Specify a repository@sha256 digest and expected local sha256 imageId; tags alone are refused.");
        if (!entrypoint.StartsWith('/') || entrypoint.Split('/').Any(part => part is "." or ".."))
            throw new ArgumentException("entrypoint must be an explicit absolute in-image executable.");
        var arguments = root.GetProperty("arguments");
        if (arguments.ValueKind != JsonValueKind.Array || arguments.GetArrayLength() > 32) throw new ArgumentException("arguments must be an array of at most 32 strings.");
        var argv = arguments.EnumerateArray().Select(item => String(item, 4096, allowEmpty: true)).ToImmutableArray();
        var prompts = root.GetProperty("prompts");
        if (prompts.ValueKind != JsonValueKind.Array || prompts.GetArrayLength() is < 1 or > 16) throw new ArgumentException("Provide 1 to 16 representative prompts.");
        var promptBuilder = ImmutableArray.CreateBuilder<EvaluationPrompt>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prompt in prompts.EnumerateArray())
        {
            RequireProperties(prompt, ["id", "text"]);
            var id = Text(prompt, "id", 128);
            if (!ids.Add(id)) throw new ArgumentException("Prompt IDs must be unique.");
            promptBuilder.Add(new(id, String(prompt.GetProperty("text"), 32768, allowLines: true)));
        }
        var timeout = root.GetProperty("timeoutSeconds").GetInt32(); var memory = root.GetProperty("memoryMiB").GetInt32();
        if (timeout is < 1 or > 300 || memory is < 64 or > 8192) throw new ArgumentException("timeoutSeconds must be 1..300; memoryMiB must be 64..8192.");
        return new(executable, endpoint, image, imageId, entrypoint, argv, Text(root, "evaluator", 1024),
            String(root.GetProperty("context"), 32768, allowEmpty: true, allowLines: true), promptBuilder.ToImmutable(), timeout, memory);
    }

    private static void RequireProperties(JsonElement element, string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new ArgumentException("Evaluation config objects must be JSON objects.");
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !observed.Add(property.Name))
                throw new ArgumentException("Unknown or duplicate evaluation property: " + property.Name);
        if (observed.Count != names.Length) throw new ArgumentException("Evaluation config is missing required properties.");
    }

    private static string Text(JsonElement element, string name, int limit) => String(element.GetProperty(name), limit);
    private static string String(JsonElement element, int limit, bool allowEmpty = false, bool allowLines = false)
    {
        if (element.ValueKind != JsonValueKind.String) throw new ArgumentException("Evaluation values must have the documented JSON types.");
        var value = element.GetString()!;
        if (value.Length > limit || !allowEmpty && string.IsNullOrWhiteSpace(value)
            || value.Any(character => char.IsControl(character) && !(allowLines && character is '\n' or '\r' or '\t')))
            throw new ArgumentException("Evaluation text is blank, oversized or contains unsupported control characters.");
        return value;
    }

    internal static byte[] Read(Stream stream, int limit, CancellationToken token)
    {
        var length = stream.Length;
        if (length < 0 || length > limit) throw new IOException("Evaluation input byte bound exceeded.");
        var result = new byte[checked((int)length)]; var offset = 0;
        while (offset < result.Length)
        {
            token.ThrowIfCancellationRequested();
            var count = stream.Read(result, offset, Math.Min(65536, result.Length - offset));
            if (count == 0) throw new IOException("Evaluation input changed during capture.");
            offset += count;
        }
        if (stream.Length != length || stream.ReadByte() != -1) throw new IOException("Evaluation input length changed.");
        return result;
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
