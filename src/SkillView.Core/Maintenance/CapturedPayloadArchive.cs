using System.Collections.Immutable;
using System.Security.Cryptography;
using SkillView.Inventory;

namespace SkillView.Maintenance;

internal sealed record ArchivedPayloadEntry(PayloadEntry Entry, string? Blob, long StoredBytes);

/// Immutable byte evidence for a draft, not a materialized installation. Numbered
/// private regular files retain raw link bytes without creating links in state.
/// Native ACLs, xattrs, streams and hard-link topology are deliberately not attested.
internal sealed record CapturedPayloadArchive(PayloadSnapshot Snapshot,
    ImmutableArray<ArchivedPayloadEntry> Entries)
{
    internal static CapturedPayloadArchive Capture(NativeStateDirectory parent, string name,
        NativeDirectoryMoveLease source, CancellationToken token,
        Action<NativePayloadItem>? inspectMetadata = null)
    {
        token.ThrowIfCancellationRequested();
        using var archive = parent.CreateStagingDirectory(name, token);
        var entries = ImmutableArray.CreateBuilder<ArchivedPayloadEntry>();
        long stored = 0;
        var snapshot = source.CaptureInto((item, entry) =>
        {
            token.ThrowIfCancellationRequested();
            inspectMetadata?.Invoke(item);
            if (entry.Kind == "directory")
            {
                entries.Add(new(entry, null, 0));
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
            entries.Add(new(entry, blob, bytes));
        }, inspectMetadata, token);
        // Sealing is admitted only after the native walker has checked every
        // observed identity, content stream and namespace again.
        archive.FlushPublication(token);
        archive.SealStagingDirectory(448, token);
        return new(snapshot, entries.OrderBy(item => item.Entry.Path, StringComparer.Ordinal).ToImmutableArray());
    }

    internal void CopyEntry(NativeStateDirectory archive, string path, Stream destination, CancellationToken token)
        => OpenReader(archive).CopyEntry(path, destination, token);

    internal Reader OpenReader(NativeStateDirectory archive)
    {
        Validate();
        return new(archive, Entries.ToDictionary(item => item.Entry.Path, StringComparer.Ordinal));
    }

    internal sealed class Reader(NativeStateDirectory archive, IReadOnlyDictionary<string, ArchivedPayloadEntry> entries)
    {
        internal PayloadEntry Entry(string path) => Get(path).Entry;
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
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var blobs = new HashSet<string>(StringComparer.Ordinal);
        long stored = 0;
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
        }
        if (PayloadSnapshot.Digest(Snapshot.Entries, false) != Snapshot.Fingerprint
            || PayloadSnapshot.Digest(Snapshot.Entries, true) != Snapshot.ComparisonFingerprint)
            throw new IOException("Captured archive payload fingerprint is inconsistent.");
        if (!ThreeWayPayloadReview.Analyze(Snapshot, Snapshot, Snapshot).CanMaterialize)
            throw new IOException("Captured archive topology is inconsistent.");
    }

    private static bool ValidBlob(string name) => name.Length == 11
        && name.StartsWith("entry-", StringComparison.Ordinal) && name.AsSpan(6).IndexOfAnyExceptInRange('0', '9') < 0;

    private static long CopyVerified(Stream source, Stream destination, string expectedHash, long length,
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
