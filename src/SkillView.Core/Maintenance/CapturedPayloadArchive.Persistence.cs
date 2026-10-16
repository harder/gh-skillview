using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkillView.Inventory;
using SkillView.Inventory.Models;

namespace SkillView.Maintenance;

// Explicitly persist internal native evidence. Serializing PayloadSnapshot alone
// would silently omit RootIdentity and Objects and could not reconstruct authority.
internal sealed record PayloadArchiveManifest(int SchemaVersion, string Path, string Payload,
    string Comparison, string Objects, int? RootMode, SkillFrontMatter? FrontMatter,
    PayloadNativeMetadata RootMetadata, ImmutableArray<ArchivedPayloadEntry> Entries);

internal sealed partial record CapturedPayloadArchive
{
    internal const int MaxManifestBytes = 64 * 1024 * 1024;

    private void Save(NativeStateDirectory archive, CancellationToken token)
    {
        Validate();
        var manifest = new PayloadArchiveManifest(1, Snapshot.Path, Snapshot.Fingerprint,
            Snapshot.ComparisonFingerprint, Snapshot.ObjectStateFingerprint!, Snapshot.RootUnixMode,
            Snapshot.FrontMatter, RootMetadata!, Entries);
        using var file = archive.OpenFile("manifest.json", false, token);
        BoundedReviewJson.Write(file, manifest, PayloadArchiveJsonContext.Default.PayloadArchiveManifest, MaxManifestBytes, token);
        file.Flush(true);
    }

    internal static (CapturedPayloadArchive Archive, string Fingerprint) Load(NativeStateDirectory archive,
        string? expectedFingerprint, CancellationToken token)
    {
        using var file = archive.OpenExistingFile("manifest.json", false, token);
        var (manifest, fingerprint) = BoundedReviewJson.Read(file, PayloadArchiveJsonContext.Default.PayloadArchiveManifest, MaxManifestBytes, token);
        if (expectedFingerprint is not null && fingerprint != expectedFingerprint)
            throw new IOException("Captured archive manifest differs from its reviewed fingerprint.");
        if (manifest.SchemaVersion != 1 || manifest.RootMetadata is null || manifest.Entries.IsDefault
            || !Path.IsPathFullyQualified(manifest.Path) || manifest.RootMetadata.Identity.CanonicalPath != manifest.Path
            || !QuarantineStore.ValidHash(manifest.Payload) || !QuarantineStore.ValidHash(manifest.Comparison)
            || !QuarantineStore.ValidHash(manifest.Objects))
            throw new IOException("Captured archive manifest schema or address binding is invalid.");
        var objects = manifest.Entries.Select(item => item is { Entry: not null, Metadata: not null }
            ? new PayloadObjectEvidence(item.Entry.Path, item.Metadata.Identity, item.Entry.UnixMode)
            : throw new IOException("Captured archive native entry evidence is missing.")).ToImmutableArray();
        var snapshot = new PayloadSnapshot(manifest.Path, manifest.Payload, manifest.Comparison,
            manifest.Entries.Select(item => item.Entry).ToImmutableArray())
        {
            FrontMatter = manifest.FrontMatter,
            RootIdentity = manifest.RootMetadata.Identity,
            RootUnixMode = manifest.RootMode,
            ObjectStateFingerprint = manifest.Objects,
            Objects = objects
        };
        var result = new CapturedPayloadArchive(snapshot, manifest.Entries, manifest.RootMetadata);
        result.Validate();
        var names = result.Entries.Where(item => item.Blob is not null).Select(item => item.Blob!).ToHashSet(StringComparer.Ordinal);
        names.Add("manifest.json");
        var actual = archive.EnumerateEntries(token).Take(PayloadSnapshot.MaxEntries + 4).ToArray();
        var sizes = actual.ToDictionary(item => item.Name, item => item.Bytes, StringComparer.Ordinal);
        if (actual.Length > PayloadSnapshot.MaxEntries + 2 || actual.Any(item => item.Error is not null || !names.Contains(item.Name))
            || result.Entries.Any(item => item.Blob is not null && (!sizes.TryGetValue(item.Blob, out var size) || size != item.StoredBytes)))
            throw new IOException("Captured archive contains unexpected, missing or resized entries.");
        return (result, fingerprint);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(PayloadArchiveManifest))]
internal partial class PayloadArchiveJsonContext : JsonSerializerContext;
