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

    internal static void CopyInto(NativeStateDirectory source, NativeStateDirectory parent, string name,
        string expectedFingerprint, CancellationToken token)
    {
        var loaded = Load(source, expectedFingerprint, token);
        var reader = loaded.Archive.OpenReader(source);
        var destinationPath = Path.Combine(parent.CanonicalPath, name);
        using (var destination = parent.CreateStagingDirectory(name, token))
        {
            foreach (var entry in loaded.Archive.Entries.Where(entry => entry.Blob is not null))
            {
                token.ThrowIfCancellationRequested();
                using var output = destination.OpenFile(entry.Blob!, false, token);
                reader.CopyEntry(entry.Entry.Path, output, token);
                output.Flush(true);
            }
            using (var input = source.OpenExistingFile("manifest.json", false, token))
            using (var output = destination.OpenFile("manifest.json", false, token))
            {
                if (input.Length is <= 0 or > MaxManifestBytes) throw new IOException("Archive manifest copy exceeds its limit.");
                CopyVerified(input, output, expectedFingerprint.ToUpperInvariant(), input.Length, true, token);
                output.Flush(true);
            }
            destination.FlushPublication(token);
            destination.SealStagingDirectory(448, token);
        }
        // Sealing closes writer admission. Reopen a separate held read session
        // instead of weakening the sealed writer's native namespace checks.
        using var readback = NativeStateDirectory.OpenOrCreate(destinationPath, [], token, allowCreate: false);
        var copied = Load(readback, expectedFingerprint, token);
        var copiedReader = copied.Archive.OpenReader(readback);
        foreach (var entry in copied.Archive.Entries.Where(entry => entry.Blob is not null))
            copiedReader.CopyEntry(entry.Entry.Path, Stream.Null, token);
        source.ValidateLocation(token);
        parent.FlushPublication(token);
    }

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
        PayloadArchiveManifest manifest;
        string fingerprint;
        // Windows state reads are exclusive. Finish the bounded, fingerprinted
        // manifest read before enumeration independently opens every entry.
        using (var file = archive.OpenExistingFile("manifest.json", false, token))
        {
            (manifest, fingerprint) = BoundedReviewJson.Read(file, PayloadArchiveJsonContext.Default.PayloadArchiveManifest, MaxManifestBytes, token);
        }
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
        if (actual.Length != names.Count || actual.Length > PayloadSnapshot.MaxEntries + 2 || actual.Any(item => item.Error is not null || !names.Contains(item.Name))
            || result.Entries.Any(item => item.Blob is not null && (!sizes.TryGetValue(item.Blob, out var size) || size != item.StoredBytes)))
            throw new IOException("Captured archive contains unexpected, missing or resized entries. "
                + DescribeEntryFailure(actual, result.Entries.Where(item => item.Blob is not null)
                    .ToDictionary(item => item.Blob!, item => item.StoredBytes, StringComparer.Ordinal)));
        return (result, fingerprint);
    }

    internal static string DescribeEntryFailure(IReadOnlyList<(string Name, long Bytes, string? Error)> actual,
        IReadOnlyDictionary<string, long> expected)
    {
        var names = actual.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        var failures = actual.Where(item => item.Error is not null || item.Name != "manifest.json"
                && (!expected.TryGetValue(item.Name, out var size) || size != item.Bytes))
            .Take(8).Select(item => $"name={Bound(item.Name)} expected={(expected.TryGetValue(item.Name, out var size) ? size.ToString(System.Globalization.CultureInfo.InvariantCulture) : item.Name == "manifest.json" ? "manifest" : "absent")} actual={item.Bytes} error={Bound(item.Error)}");
        var missing = expected.Keys.Append("manifest.json").Where(name => !names.Contains(name)).Take(8).Select(Bound);
        return $"Observed {actual.Count} entries; expected {expected.Count + 1}. Failures: {string.Join(" | ", failures)}. Missing: {string.Join(", ", missing)}.";

        static string Bound(string? value) => new((value ?? "none").Take(160).Select(c => char.IsControl(c) ? '?' : c).ToArray());
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(PayloadArchiveManifest))]
internal partial class PayloadArchiveJsonContext : JsonSerializerContext;
