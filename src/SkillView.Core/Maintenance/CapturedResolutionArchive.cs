using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using SkillView.Inventory;

namespace SkillView.Maintenance;

internal sealed record LocalResolutionInput(string Path, string SourceFile);
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

    internal static string Capture(NativeStateDirectory parent, string name, IReadOnlyList<LocalResolutionInput> inputs,
        long remainingBytes, long remainingManifestBytes, CancellationToken token,
        Action<string>? afterCopyForTests = null)
    {
        token.ThrowIfCancellationRequested();
        if (inputs.Count is 0 or > PayloadSnapshot.MaxEntries) throw new IOException("Resolution input count exceeds its budget.");
        if (inputs.Any(input => input is null || string.IsNullOrEmpty(input.Path) || string.IsNullOrEmpty(input.SourceFile))
            || inputs.Select(input => input.Path).Distinct(StringComparer.Ordinal).Count() != inputs.Count)
            throw new IOException("Every resolution needs one distinct target path and regular input file.");
        using var archive = parent.CreateStagingDirectory(name, token);
        using var authority = PayloadMetadataAuthority.Open(archive, create: true, token);
        var entries = ImmutableArray.CreateBuilder<ArchivedPayloadEntry>();
        var bytesBudget = new WriteBudget(remainingBytes); long metadataBytes = 0;
        foreach (var input in inputs.OrderBy(input => input.Path, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            ArchivedPayloadEntry? captured = null;
            RegularFileInspection.Capture(input.SourceFile, item =>
            {
                var file = item.Content ?? throw new IOException("Resolution regular-file content is unavailable.");
                var length = file.Length;
                if (length > bytesBudget.Remaining || input.Path == "SKILL.md" && length > PayloadSnapshot.MaxSkillBytes)
                    throw new IOException("Resolution input exceeds its remaining byte budget.");
                var metadata = PayloadNativeMetadata.Capture(item, token, authority);
                metadataBytes += metadata.Validate();
                if (metadataBytes > PayloadNativeMetadata.MaxArchiveMetadataBytes)
                    throw new IOException("Resolution metadata exceeds its archive budget.");
                var blob = $"entry-{entries.Count:D5}";
                using var destination = archive.OpenFile(blob, false, token);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var skill = input.Path == "SKILL.md" ? new MemoryStream() : null;
                var buffer = new byte[64 * 1024]; long stored = 0;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var count = file.Read(buffer);
                    if (count == 0) break;
                    stored += count;
                    if (stored > length) throw new IOException("Resolution input grew during capture.");
                    bytesBudget.Consume(count); destination.Write(buffer, 0, count);
                    hash.AppendData(buffer.AsSpan(0, count)); skill?.Write(buffer, 0, count);
                }
                if (stored != length) throw new IOException("Resolution input changed length during capture.");
                var raw = Convert.ToHexString(hash.GetHashAndReset()); var comparison = raw;
                if (skill is not null)
                {
                    var text = new UTF8Encoding(false, true).GetString(skill.GetBuffer(), 0, checked((int)skill.Length));
                    var (_, frontMatter, parsed) = FrontMatterParser.Parse(text);
                    if (!parsed || !frontMatter.Diagnostics.IsEmpty) throw new IOException("Resolved SKILL.md needs valid, unambiguous frontmatter.");
                    comparison = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(InstallerMetadataNormalizer.Normalize(text))));
                }
                destination.Flush(true);
                captured = new(new(input.Path, "file", raw, comparison, stored, item.UnixMode, null), blob, stored, metadata);
                afterCopyForTests?.Invoke(input.Path);
                return new(raw, stored);
            }, token);
            entries.Add(captured ?? throw new IOException("Resolution input was not captured."));
        }
        var result = new CapturedResolutionArchive(entries.ToImmutable());
        result.Validate();
        using (var file = archive.OpenFile("manifest.json", false, token))
        {
            using var bounded = new BudgetedWriteStream(file, new(remainingManifestBytes));
            var hash = BoundedReviewJson.Write(bounded, new ResolutionArchiveManifest(1, result.Entries),
                ResolutionJsonContext.Default.ResolutionArchiveManifest, CapturedPayloadArchive.MaxManifestBytes, token);
            file.Flush(true); archive.FlushNamespace(token); archive.FlushPublication(token);
            archive.SealStagingDirectory(448, token);
            return hash;
        }
    }

    internal static CapturedResolutionArchive Load(NativeStateDirectory archive, string expectedHash, CancellationToken token)
    {
        using var file = archive.OpenExistingFile("manifest.json", false, token);
        var (manifest, hash) = BoundedReviewJson.Read(file, ResolutionJsonContext.Default.ResolutionArchiveManifest,
            CapturedPayloadArchive.MaxManifestBytes, token);
        if (!QuarantineStore.ValidHash(expectedHash) || hash != expectedHash || manifest.SchemaVersion != 1)
            throw new IOException("Resolution archive manifest differs from its exact reviewed evidence.");
        var result = new CapturedResolutionArchive(manifest.Entries); result.Validate();
        var expected = result.Entries.ToDictionary(item => item.Blob!, item => item.StoredBytes, StringComparer.Ordinal);
        var actual = archive.EnumerateEntries(token).Take(PayloadSnapshot.MaxEntries + 2).ToArray();
        if (actual.Length != result.Entries.Length + 1 || actual.Any(item => item.Error is not null
            || item.Name != "manifest.json" && (!expected.TryGetValue(item.Name, out var size) || size != item.Bytes)))
            throw new IOException("Resolution archive has unknown, missing or resized entries.");
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

    private sealed class WriteBudget(long remaining)
    {
        internal long Remaining { get; private set; } = remaining;
        internal void Consume(int count)
        {
            if (count > Remaining) throw new IOException("Retained resolution storage budget exceeded; partial evidence retained.");
            Remaining -= count;
        }
    }
    private sealed class BudgetedWriteStream(Stream output, WriteBudget budget) : Stream
    {
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => output.Flush();
        public override void Write(byte[] buffer, int offset, int count) { budget.Consume(count); output.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { budget.Consume(buffer.Length); output.Write(buffer); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ResolutionArchiveManifest))]
internal partial class ResolutionJsonContext : JsonSerializerContext;
