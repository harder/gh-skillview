using System.Text.Json;
using SkillView.Inventory;

namespace SkillView.Maintenance;

internal sealed record GhRegistryPatch(byte[] BeforeBytes, byte[] DesiredBytes,
    string BeforeEntryFingerprint, string DesiredEntryFingerprint);
internal sealed record GhRegistryEntry(byte[] Bytes, string Fingerprint);

/// Pure preparation only. The owner transaction must hold gh's data-file lock
/// and bind these bytes to native file authority before writing anything.
internal static class GhRegistryEditor
{
    private static readonly HashSet<string> KnownStrings =
        ["source", "sourceType", "sourceUrl", "skillPath", "skillFolderHash", "installedAt", "updatedAt", "pinnedRef"];

    internal static GhRegistryEntry CaptureEntry(byte[] current, string selector, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (current.Length > SkillLockFileReader.MaxFileBytes || string.IsNullOrWhiteSpace(selector))
            throw new IOException("Registry byte or selector limit exceeded.");
        try
        {
            using var document = JsonDocument.Parse(current, new JsonDocumentOptions { MaxDepth = 32 });
            CheckUnique(document.RootElement, token);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 3
                || !root.TryGetProperty("skills", out var skills) || skills.ValueKind != JsonValueKind.Object
                || skills.EnumerateObject().Take(SkillLockFileReader.MaxEntries + 1).Count() > SkillLockFileReader.MaxEntries
                || !skills.TryGetProperty(selector, out var entry) || entry.ValueKind != JsonValueKind.Object)
                throw new IOException("Registry schema or selected entry is unsupported.");
            if (!entry.TryGetProperty("sourceType", out var sourceType) || sourceType.ValueKind != JsonValueKind.String
                || sourceType.GetString() != "github"
                || !entry.TryGetProperty("skillFolderHash", out var tree) || tree.ValueKind != JsonValueKind.String
                || tree.GetString() is not { Length: 40 } id || !id.All(char.IsAsciiHexDigit)
                || entry.EnumerateObject().Any(property => KnownStrings.Contains(property.Name) && property.Value.ValueKind != JsonValueKind.String))
                throw new IOException("Selected registry entry is not a supported gh GitHub revision.");
            var (start, end) = FindEntry(current, selector, token);
            return new(current.AsSpan(start, end - start).ToArray(), OwnershipRecordFingerprint.Compute(entry, normalizeGhStrings: true));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or OverflowException)
        { throw new IOException("Registry JSON is malformed or incompatible.", ex); }
    }

    internal static GhRegistryPatch ReplaceEntry(byte[] current, string selector, string expectedEntryFingerprint,
        byte[] desiredEntry, CancellationToken token)
    {
        var captured = CaptureEntry(current, selector, token);
        if (captured.Fingerprint != expectedEntryFingerprint) throw new IOException("Selected owner record changed; review a new transition.");
        if (desiredEntry.Length > SkillLockFileReader.MaxFileBytes) throw new IOException("Desired entry byte limit exceeded.");
        try
        {
            using var document = JsonDocument.Parse(desiredEntry, new JsonDocumentOptions { MaxDepth = 32 });
            CheckUnique(document.RootElement, token);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new IOException("Desired owner entry must be an object.");
            var (start, end) = FindEntry(current, selector, token);
            var length = checked(current.Length - (end - start) + desiredEntry.Length);
            if (length > SkillLockFileReader.MaxFileBytes) throw new IOException("Desired registry exceeds its byte limit.");
            var desired = new byte[length];
            current.AsSpan(0, start).CopyTo(desired);
            desiredEntry.CopyTo(desired.AsSpan(start));
            current.AsSpan(end).CopyTo(desired.AsSpan(start + desiredEntry.Length));
            var result = CaptureEntry(desired, selector, token);
            return new(current.ToArray(), desired, captured.Fingerprint, result.Fingerprint);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or OverflowException)
        { throw new IOException("Desired registry entry is malformed or incompatible.", ex); }
    }

    internal static GhRegistryPatch Prepare(byte[] current, string selector, string expectedEntryFingerprint,
        string desiredTree, string? desiredPin, DateTimeOffset updatedAt, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (current.Length > SkillLockFileReader.MaxFileBytes)
            throw new IOException("Registry byte limit exceeded.");
        if (string.IsNullOrWhiteSpace(selector) || selector.Length > 4096
            || expectedEntryFingerprint.Length != 64 || !expectedEntryFingerprint.All(char.IsAsciiHexDigit)
            || desiredTree.Length != 40 || !desiredTree.All(char.IsAsciiHexDigit)
            || desiredPin is not null && (string.IsNullOrWhiteSpace(desiredPin) || desiredPin.Length > 1024 || desiredPin.Any(char.IsControl)))
            throw new IOException("Registry transition selector, revision, pin or fingerprint is invalid.");
        var before = current.ToArray();
        try
        {
            using var document = JsonDocument.Parse(before, new JsonDocumentOptions { MaxDepth = 32 });
            CheckUnique(document.RootElement, token);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version)
                || !version.TryGetInt32(out var number) || number != 3
                || !root.TryGetProperty("skills", out var skills) || skills.ValueKind != JsonValueKind.Object
                || skills.EnumerateObject().Take(SkillLockFileReader.MaxEntries + 1).Count() > SkillLockFileReader.MaxEntries
                || !skills.TryGetProperty(selector, out var entry) || entry.ValueKind != JsonValueKind.Object)
                throw new IOException("Registry schema or selected entry is unsupported; no owner transition was prepared.");
            if (!entry.TryGetProperty("sourceType", out var sourceType) || sourceType.ValueKind != JsonValueKind.String
                || sourceType.GetString() != "github"
                || !entry.TryGetProperty("skillFolderHash", out var tree) || tree.ValueKind != JsonValueKind.String
                || tree.GetString() is not { Length: 40 } oldTree || !oldTree.All(char.IsAsciiHexDigit)
                || entry.EnumerateObject().Any(property => KnownStrings.Contains(property.Name) && property.Value.ValueKind != JsonValueKind.String))
                throw new IOException("Selected registry entry is not a supported gh GitHub revision.");
            var fingerprint = OwnershipRecordFingerprint.Compute(entry, normalizeGhStrings: true);
            if (fingerprint != expectedEntryFingerprint)
                throw new IOException("Selected owner record changed; review a new update plan.");
            using var bytes = new MemoryStream();
            using (var writer = new Utf8JsonWriter(bytes))
            {
                writer.WriteStartObject();
                foreach (var property in entry.EnumerateObject())
                {
                    token.ThrowIfCancellationRequested();
                    if (property.Name is "skillFolderHash" or "updatedAt" or "pinnedRef") continue;
                    writer.WritePropertyName(property.Name);
                    writer.WriteRawValue(property.Value.GetRawText(), skipInputValidation: true);
                }
                writer.WriteString("skillFolderHash", desiredTree.ToLowerInvariant());
                writer.WriteString("updatedAt", updatedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
                if (desiredPin is not null) writer.WriteString("pinnedRef", desiredPin);
                writer.WriteEndObject();
            }
            var replacement = bytes.ToArray();
            var (start, end) = FindEntry(before, selector, token);
            var length = checked(before.Length - (end - start) + replacement.Length);
            if (length > SkillLockFileReader.MaxFileBytes) throw new IOException("Desired registry exceeds its byte limit.");
            var desired = new byte[length];
            before.AsSpan(0, start).CopyTo(desired);
            replacement.CopyTo(desired.AsSpan(start));
            before.AsSpan(end).CopyTo(desired.AsSpan(start + replacement.Length));
            using var afterEntry = JsonDocument.Parse(replacement);
            token.ThrowIfCancellationRequested();
            return new(before, desired, fingerprint, OwnershipRecordFingerprint.Compute(afterEntry.RootElement, normalizeGhStrings: true));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or OverflowException)
        { throw new IOException("Registry JSON is malformed or incompatible; no owner transition was prepared.", ex); }
    }

    private static (int Start, int End) FindEntry(byte[] bytes, string selector, CancellationToken token)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 32 });
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 || reader.GetString() != "skills") continue;
            reader.Read();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                token.ThrowIfCancellationRequested();
                var selected = reader.GetString() == selector;
                reader.Read();
                var start = checked((int)reader.TokenStartIndex);
                reader.Skip();
                if (selected) return (start, checked((int)reader.BytesConsumed));
            }
        }
        throw new IOException("Selected owner entry could not be located in the validated registry.");
    }

    private static void CheckUnique(JsonElement value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new IOException("Registry contains duplicate properties; owner transition is ambiguous.");
                CheckUnique(property.Value, token);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckUnique(item, token);
    }
}
