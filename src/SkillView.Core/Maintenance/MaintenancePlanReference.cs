using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkillView.Inventory;

namespace SkillView.Maintenance;

/// Read-only compatibility for references emitted by the retired, unreleased CLI
/// facade. File contents are unverified locators, never store evidence or approval.
internal sealed record MaintenancePlanReference(int SchemaVersion, string Id, string Purpose,
    string PlanFingerprint, string ContextFingerprint, string InstallationId, string OriginalPath)
{
    internal const int MaxBytes = 256 * 1024;

    internal static MaintenancePlanReference Read(string path, CancellationToken token)
    {
        byte[]? captured = null;
        RegularFileInspection.Capture(path, item =>
        {
            var stream = item.Content ?? throw new IOException("Plan reference is not a regular file.");
            var length = stream.Length;
            if (length is < 1 or > MaxBytes) throw new IOException("Plan reference exceeds its bounded file contract.");
            var bytes = new byte[(int)length];
            stream.ReadExactly(bytes);
            token.ThrowIfCancellationRequested();
            if (stream.ReadByte() != -1) throw new IOException("Plan reference grew during capture.");
            captured = bytes;
            return new(Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length);
        }, token);
        using var document = JsonDocument.Parse(captured ?? throw new IOException("Plan reference capture failed."),
            new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new IOException("Plan reference must be a JSON object.");
        // Accept a reference object or a complete plan/inspection JSON response.
        // The outer response is evidence only; no operation or payload is read.
        var references = root.EnumerateObject().Count(property => property.Name == "planReference");
        if (references > 1) throw new IOException("Duplicate planReference envelope member.");
        if (references == 0 && (root.TryGetProperty("operation", out _) || root.TryGetProperty("savedPlan", out _)
            || root.TryGetProperty("unchanged", out _)))
            throw new IOException("This response contains no retained local plan reference. An unchanged result needs no apply; use specialist inspection with the saved ID.");
        if (root.TryGetProperty("planReference", out var reference)) root = reference;
        if (root.ValueKind != JsonValueKind.Object) throw new IOException("No saved local plan reference is present.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!names.Add(property.Name)) throw new IOException("Duplicate plan-reference member: " + property.Name);
        var result = root.Deserialize(MaintenancePlanReferenceJsonContext.Default.MaintenancePlanReference)
            ?? throw new IOException("Plan reference is empty.");
        if (result.SchemaVersion != 1 || !QuarantineStore.ValidId(result.Id)
            || !QuarantineStore.ValidHash(result.PlanFingerprint) || !QuarantineStore.ValidHash(result.ContextFingerprint)
            || !QuarantineStore.ValidHash(result.InstallationId) || !Path.IsPathFullyQualified(result.OriginalPath)
            || result.Purpose is not ("update-revision" or "restore-recorded" or "unpin-only" or "local-reimport" or "switch-local-source"))
            throw new IOException("Unsupported or incomplete local plan reference.");
        return result;
    }

    internal string SpecialistPrefix => Purpose switch
    {
        "update-revision" => "update",
        "restore-recorded" => "restore",
        "unpin-only" => "unpin",
        "local-reimport" => "reimport",
        "switch-local-source" => "source-switch",
        _ => throw new IOException("Unsupported local plan purpose.")
    };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(MaintenancePlanReference))]
internal partial class MaintenancePlanReferenceJsonContext : JsonSerializerContext;
