using System.Collections.Immutable;
using System.Text.Json.Serialization;
using SkillView.Inventory;

namespace SkillView.Maintenance;

internal sealed record ResolutionEntryReference(string Path, string Archive, string ManifestFingerprint);
internal sealed record LocalResolutionSet(ImmutableArray<ResolutionEntryReference> Entries);
internal sealed record ResolutionArchiveManifest(int SchemaVersion, ImmutableArray<ArchivedPayloadEntry> Entries);

/// Captured regular-file bytes with their real source observation. There is no
/// synthetic directory identity, and the input pathname stops being a dependency
/// after capture. Readers verify private blobs rather than reopening that input.
internal sealed record CapturedResolutionArchive(ImmutableArray<ArchivedPayloadEntry> Entries)
{
    internal const long MaxRetainedBytes = PayloadSnapshot.MaxBytes;
    internal const long MaxRetainedManifestBytes = 64L * 1024 * 1024;

    internal static CapturedResolutionArchive Load(NativeStateDirectory archive, string expectedHash, CancellationToken token)
    {
        ResolutionArchiveManifest manifest;
        string hash;
        // Enumeration must reopen each entry after this exclusive Windows read.
        using (var file = archive.OpenExistingFile("manifest.json", false, token))
        {
            (manifest, hash) = BoundedReviewJson.Read(file, ResolutionJsonContext.Default.ResolutionArchiveManifest,
                CapturedPayloadArchive.MaxManifestBytes, token);
        }
        if (!QuarantineStore.ValidHash(expectedHash) || hash != expectedHash || manifest.SchemaVersion != 1)
            throw new IOException("Resolution archive manifest differs from its exact reviewed evidence.");
        var result = new CapturedResolutionArchive(manifest.Entries); result.Validate();
        var expected = result.Entries.ToDictionary(item => item.Blob!, item => item.StoredBytes, StringComparer.Ordinal);
        var actual = archive.EnumerateEntries(token).Take(PayloadSnapshot.MaxEntries + 2).ToArray();
        if (actual.Length != result.Entries.Length + 1 || actual.Any(item => item.Error is not null
            || item.Name != "manifest.json" && (!expected.TryGetValue(item.Name, out var size) || size != item.Bytes)))
            throw new IOException("Resolution archive has unknown, missing or resized entries. "
                + CapturedPayloadArchive.DescribeEntryFailure(actual, expected));
        return result;
    }

    internal void Validate()
    {
        if (Entries.IsDefaultOrEmpty || Entries.Length > PayloadSnapshot.MaxEntries) throw new IOException("Resolution archive entry budget exceeded.");
        var paths = new HashSet<string>(StringComparer.Ordinal); var blobs = new HashSet<string>(StringComparer.Ordinal);
        long bytes = 0; long metadata = 0;
        foreach (var item in Entries)
        {
            if (item is null || item.Entry is not { } entry || entry.Kind != "file" || entry.LinkTarget is not null
                || string.IsNullOrEmpty(entry.Path) || Path.IsPathRooted(entry.Path)
                || entry.Path.Split('/').Any(part => part is "" or "." or ".." || part.Contains('\0'))
                || OperatingSystem.IsWindows() && entry.Path.Contains('\\') || !paths.Add(entry.Path)
                || item.Blob is null || item.Blob.Length != 11 || !item.Blob.StartsWith("entry-", StringComparison.Ordinal)
                || item.Blob.AsSpan(6).IndexOfAnyExceptInRange('0', '9') >= 0 || !blobs.Add(item.Blob)
                || item.StoredBytes < 0 || item.StoredBytes != entry.Bytes || (bytes += item.StoredBytes) > PayloadSnapshot.MaxBytes
                || entry.Path == "SKILL.md" && entry.Bytes > PayloadSnapshot.MaxSkillBytes
                || !EntryHash(entry.ContentHash) || !EntryHash(entry.ComparisonHash)
                || item.Metadata is null || item.Metadata.Kind != "file" || item.Metadata.UnixMode != entry.UnixMode
                || !Path.IsPathFullyQualified(item.Metadata.Identity.CanonicalPath) || item.Metadata.Identity.IsDirectory || item.Metadata.Identity.IsReparsePoint)
                throw new IOException("Resolution archive entry is malformed or exceeds its budget.");
            metadata += item.Metadata.Validate();
            bytes += item.Metadata.AlternateStreamBytes;
            if (bytes > PayloadSnapshot.MaxBytes) throw new IOException("Resolution alternate streams exceed the raw byte budget.");
            if (metadata > PayloadNativeMetadata.MaxArchiveMetadataBytes) throw new IOException("Resolution archive metadata exceeds its budget.");
        }
    }
    private static bool EntryHash(string value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    internal IPayloadEvidenceReader OpenReader(NativeStateDirectory archive) { Validate(); return new Reader(archive, Entries); }
    private sealed class Reader(NativeStateDirectory archive, ImmutableArray<ArchivedPayloadEntry> entries) : IPayloadEvidenceReader
    {
        private readonly Dictionary<string, ArchivedPayloadEntry> entries = entries.ToDictionary(item => item.Entry.Path, StringComparer.Ordinal);
        public PayloadEntry Entry(string path) => entries[path].Entry;
        public PayloadNativeMetadata? Metadata(string path) => entries[path].Metadata;
        public void CopyEntry(string path, Stream destination, CancellationToken token)
        {
            var entry = entries[path];
            using var file = archive.OpenExistingFile(entry.Blob!, false, token);
            CapturedPayloadArchive.CopyVerified(file, destination, entry.Entry.ContentHash, entry.StoredBytes, true, token);
        }
    }


}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ResolutionArchiveManifest))]
internal partial class ResolutionJsonContext : JsonSerializerContext;
