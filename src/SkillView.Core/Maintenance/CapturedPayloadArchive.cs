using System.Collections.Immutable;
using System.Security.Cryptography;
using SkillView.Inventory;

namespace SkillView.Maintenance;

internal sealed record ArchivedPayloadEntry(PayloadEntry Entry, string? Blob, long StoredBytes,
    PayloadNativeMetadata? Metadata = null);

/// Immutable byte evidence for a draft, not a materialized installation. Numbered
/// private regular files retain raw link bytes without creating links in state.
/// Bounded native metadata includes explicit refusals for unsupported observations.
internal sealed partial record CapturedPayloadArchive(PayloadSnapshot Snapshot,
    ImmutableArray<ArchivedPayloadEntry> Entries, PayloadNativeMetadata? RootMetadata = null)
{
    internal static CapturedPayloadArchive Capture(NativeStateDirectory parent, string name,
        NativeDirectoryMoveLease source, CancellationToken token,
        Action<NativePayloadItem>? inspectMetadata = null)
        => CaptureCore(parent, name, source, token, inspectMetadata, rawDamaged: false);

    internal static CapturedPayloadArchive CaptureDamagedOriginal(NativeStateDirectory parent, string name,
        NativeDirectoryMoveLease source, CancellationToken token) => CaptureCore(parent, name, source, token, null, rawDamaged: true);

    private static CapturedPayloadArchive CaptureCore(NativeStateDirectory parent, string name,
        NativeDirectoryMoveLease source, CancellationToken token, Action<NativePayloadItem>? inspectMetadata, bool rawDamaged)
    {
        token.ThrowIfCancellationRequested();
        using var archive = parent.CreateStagingDirectory(name, token);
        using var authority = PayloadMetadataAuthority.Open(archive, create: true, token);
        var entries = ImmutableArray.CreateBuilder<ArchivedPayloadEntry>();
        long stored = 0; long metadataBytes = 0; PayloadNativeMetadata? rootMetadata = null;
        Func<Action<NativePayloadItem, PayloadEntry>, Action<NativePayloadItem>?, CancellationToken, PayloadSnapshot> capture =
            rawDamaged ? source.CaptureRawInto : source.CaptureInto;
        var snapshot = capture((item, entry) =>
        {
            token.ThrowIfCancellationRequested();
            inspectMetadata?.Invoke(item);
            var metadata = CaptureMetadata(item);
            if (entry.Kind == "directory")
            {
                entries.Add(new(entry, null, 0, metadata));
                return;
            }
            if (entry.Kind == "file" && entry.Bytes > PayloadSnapshot.MaxBytes - stored)
                throw new IOException("Captured archive exceeds its aggregate byte budget.");
            var blob = $"entry-{entries.Count:D5}";
            using var destination = archive.OpenFile(blob, false, token);
            using var link = item.Kind == "link"
                ? new MemoryStream(item.LinkBytes ?? throw new IOException("Raw captured link bytes are missing."), false) : null;
            var input = link ?? item.Content ?? throw new IOException("Captured content is missing.");
            var bytes = CopyVerified(input, destination, entry.ContentHash,
                entry.Kind == "file" ? entry.Bytes : Math.Min(64 * 1024, PayloadSnapshot.MaxBytes - stored),
                exactLength: entry.Kind == "file", token);
            stored = checked(stored + bytes);
            if (stored > PayloadSnapshot.MaxBytes) throw new IOException("Captured archive exceeds its aggregate byte budget.");
            destination.Flush(true);
            entries.Add(new(entry, blob, bytes, metadata));
        }, root =>
        {
            inspectMetadata?.Invoke(root);
            rootMetadata = CaptureMetadata(root);
        }, token);
        // Sealing is admitted only after the native walker has checked every
        // observed identity, content stream and namespace again.
        var result = new CapturedPayloadArchive(snapshot, entries.OrderBy(item => item.Entry.Path, StringComparer.Ordinal).ToImmutableArray(), rootMetadata);
        result.Save(archive, token);
        archive.FlushPublication(token);
        archive.SealStagingDirectory(448, token);
        return result;

        PayloadNativeMetadata CaptureMetadata(NativePayloadItem item)
        {
            var contentBytes = item.Kind == "file" ? item.Content?.Length ?? 0 : item.LinkBytes?.LongLength ?? 0;
            var metadata = PayloadNativeMetadata.Capture(item, token, authority, PayloadSnapshot.MaxBytes - stored - contentBytes);
            metadataBytes += metadata.Validate();
            if (metadataBytes > PayloadNativeMetadata.MaxArchiveMetadataBytes)
                throw new IOException("Captured native metadata exceeds its aggregate byte budget.");
            stored = checked(stored + metadata.AlternateStreamBytes);
            if (stored > PayloadSnapshot.MaxBytes) throw new IOException("Captured Windows streams exceed the aggregate raw byte budget.");
            return metadata;
        }
    }

    internal void CopyEntry(NativeStateDirectory archive, string path, Stream destination, CancellationToken token)
        => OpenReader(archive).CopyEntry(path, destination, token);

    internal Reader OpenReader(NativeStateDirectory archive)
    {
        Validate();
        return new(archive, Entries.ToDictionary(item => item.Entry.Path, StringComparer.Ordinal), RootMetadata);
    }

    internal sealed class Reader(NativeStateDirectory archive, IReadOnlyDictionary<string, ArchivedPayloadEntry> entries,
        PayloadNativeMetadata? rootMetadata) : IPayloadEvidenceReader
    {
        internal PayloadNativeMetadata? RootMetadata => rootMetadata;
        internal PayloadEntry Entry(string path) => Get(path).Entry;
        internal PayloadNativeMetadata? Metadata(string path) => Get(path).Metadata;
        internal bool TryEntry(string path, out ArchivedPayloadEntry? entry) => entries.TryGetValue(path, out entry);
        internal PayloadMetadataAuthority OpenMetadataAuthority(CancellationToken token) => PayloadMetadataAuthority.Open(archive, create: false, token);
        PayloadEntry IPayloadEvidenceReader.Entry(string path) => Entry(path);
        PayloadNativeMetadata? IPayloadEvidenceReader.Metadata(string path) => Metadata(path);
        void IPayloadEvidenceReader.CopyEntry(string path, Stream destination, CancellationToken token) => CopyEntry(path, destination, token);
        private ArchivedPayloadEntry Get(string path) => entries.TryGetValue(path, out var entry) ? entry
            : throw new IOException("The path has no captured archive entry.");
        internal void CopyEntry(string path, Stream destination, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var entry = Get(path);
            if (entry.Blob is null) throw new IOException("A captured directory has no byte stream.");
            using var input = archive.OpenExistingFile(entry.Blob, false, token);
            CopyVerified(input, destination, entry.Entry.ContentHash, entry.StoredBytes, exactLength: true, token);
        }
    }

    internal void Validate()
    {
        if (Entries.IsDefault || Entries.Length > PayloadSnapshot.MaxEntries
            || Snapshot.Entries.IsDefault || Snapshot.Entries.Length != Entries.Length)
            throw new IOException("Captured archive entries are missing or exceed their budget.");
        var expected = Snapshot.Entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        var objects = Snapshot.Objects.ToDictionary(item => item.Path, StringComparer.Ordinal);
        if (RootMetadata is null || Snapshot.RootIdentity is null || RootMetadata.Identity != Snapshot.RootIdentity.Value
            || RootMetadata.Kind != "directory" || RootMetadata.UnixMode != Snapshot.RootUnixMode)
            throw new IOException("Captured root metadata is not bound to its native payload object.");
        long metadataBytes = RootMetadata.Validate();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var blobs = new HashSet<string>(StringComparer.Ordinal);
        long stored = RootMetadata.AlternateStreamBytes;
        foreach (var item in Entries)
        {
            var entry = item.Entry;
            if (entry is null || !paths.Add(entry.Path) || !expected.TryGetValue(entry.Path, out var captured) || captured != entry
                || item.StoredBytes < 0 || item.StoredBytes > PayloadSnapshot.MaxBytes
                || entry.Kind == "directory" && (item.Blob is not null || item.StoredBytes != 0)
                || entry.Kind != "directory" && (item.Blob is null || !ValidBlob(item.Blob) || !blobs.Add(item.Blob))
                || entry.Kind == "file" && item.StoredBytes != entry.Bytes
                || entry.Kind == "link" && item.StoredBytes > 64 * 1024
                || entry.Kind is not ("file" or "directory" or "link"))
                throw new IOException("Captured archive manifest is inconsistent.");
            stored = checked(stored + item.StoredBytes);
            if (stored > PayloadSnapshot.MaxBytes) throw new IOException("Captured archive exceeds its aggregate byte budget.");
            if (item.Metadata is not { } metadata || !objects.TryGetValue(entry.Path, out var native)
                || metadata.Identity != native.Identity || metadata.Kind != entry.Kind || metadata.UnixMode != entry.UnixMode)
                throw new IOException("Captured entry metadata is not bound to its native payload object.");
            metadataBytes += metadata.Validate();
            stored = checked(stored + metadata.AlternateStreamBytes);
            if (stored > PayloadSnapshot.MaxBytes) throw new IOException("Captured Windows streams exceed the aggregate raw byte budget.");
            if (metadataBytes > PayloadNativeMetadata.MaxArchiveMetadataBytes)
                throw new IOException("Captured native metadata exceeds its aggregate byte budget.");
        }
        if (PayloadSnapshot.Digest(Snapshot.Entries, false) != Snapshot.Fingerprint
            || PayloadSnapshot.Digest(Snapshot.Entries, true) != Snapshot.ComparisonFingerprint
            || PayloadSnapshot.DigestObjects(new(Snapshot.RootIdentity.Value, Snapshot.RootUnixMode), Snapshot.Objects, CancellationToken.None) != Snapshot.ObjectStateFingerprint)
            throw new IOException("Captured archive payload fingerprint is inconsistent.");
        if (!ThreeWayPayloadReview.Analyze(Snapshot, Snapshot, Snapshot).CanMaterialize)
            throw new IOException("Captured archive topology is inconsistent.");
    }

    private static bool ValidBlob(string name) => name.Length == 11
        && name.StartsWith("entry-", StringComparison.Ordinal) && name.AsSpan(6).IndexOfAnyExceptInRange('0', '9') < 0;

    internal static long CopyVerified(Stream source, Stream destination, string expectedHash, long length,
        bool exactLength, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024]; long bytes = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var count = source.Read(buffer);
            if (count == 0) break;
            bytes += count;
            if (bytes > length) throw new IOException("Captured byte stream exceeded its reviewed length or budget.");
            hash.AppendData(buffer.AsSpan(0, count));
            destination.Write(buffer, 0, count);
        }
        if (exactLength && bytes != length || Convert.ToHexString(hash.GetHashAndReset()) != expectedHash)
            throw new IOException("Captured byte stream differs from its reviewed evidence.");
        token.ThrowIfCancellationRequested();
        return bytes;
    }
}
