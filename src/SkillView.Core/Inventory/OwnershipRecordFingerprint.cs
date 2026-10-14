using System.Text.Json;

namespace SkillView.Inventory;

/// Entry-level evidence survives formatting and unrelated registry updates.
internal static class OwnershipRecordFingerprint
{
    private static readonly HashSet<string> GhStringFields =
        ["source", "sourceType", "sourceUrl", "skillPath", "skillFolderHash", "installedAt", "updatedAt", "pinnedRef"];

    internal static string Compute(JsonElement value, bool normalizeGhStrings = false)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, value, normalizeGhStrings);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)));
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value, bool normalizeGhStrings = false)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                // Go's typed writer adds required empty strings and omits empty
                // optional strings on all unrelated records. Both mean absent
                // metadata; retain every nonempty value and unknown extension.
                if (normalizeGhStrings && GhStringFields.Contains(property.Name)
                    && property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() == "") continue;
                writer.WritePropertyName(property.Name); Write(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) Write(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}
