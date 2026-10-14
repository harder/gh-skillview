using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using SkillView.Inventory;
using YamlDotNet.Core;

namespace SkillView.Maintenance;

public sealed record PayloadEntry(string Path, string Kind, string ContentHash,
    string ComparisonHash, long Bytes, int? UnixMode, string? LinkTarget);

public sealed record PayloadSnapshot(string Path, string Fingerprint,
    string ComparisonFingerprint, ImmutableArray<PayloadEntry> Entries)
{
    public SkillView.Inventory.Models.SkillFrontMatter? FrontMatter { get; init; }
    /// Native object and generation evidence is distinct from payload equality.
    /// It does not attest complete ACL/xattr/alternate-stream metadata.
    public string? ObjectStateFingerprint { get; init; }
    public int? RootUnixMode { get; init; }
    internal SecureFileIdentity? RootIdentity { get; init; }
    internal ImmutableArray<PayloadObjectEvidence> Objects { get; init; } = [];

    public const int MaxEntries = 10_000;
    public const long MaxBytes = 256L * 1024 * 1024;
    public const int MaxDepth = 32;
    public const int MaxSkillBytes = 2 * 1024 * 1024;

    public static PayloadSnapshot Capture(string path, CancellationToken cancellationToken = default) =>
        CaptureCore(path, cancellationToken, null);

    internal static PayloadSnapshot CaptureWithObservationForTests(string path, Action<string, string> observation,
        CancellationToken cancellationToken) => CaptureCore(path, cancellationToken, observation);

    internal static PayloadSnapshot CaptureWithEntrySink(string path,
        Action<NativePayloadItem, PayloadEntry> sink, CancellationToken token) => CaptureCore(path, token, null, sink, strictMetadata: false);

    internal static PayloadSnapshot CaptureEvidence(string path, CancellationToken token) =>
        CaptureCore(path, token, null, strictMetadata: false);

    private static PayloadSnapshot CaptureCore(string path, CancellationToken cancellationToken, Action<string, string>? observation,
        Action<NativePayloadItem, PayloadEntry>? sink = null, bool strictMetadata = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = System.IO.Path.GetFullPath(path);
        var entries = ImmutableArray.CreateBuilder<PayloadEntry>();
        var objects = ImmutableArray.CreateBuilder<PayloadObjectEvidence>();
        long totalBytes = 0;
        SkillView.Inventory.Models.SkillFrontMatter? frontMatter = null;
        var identity = SecurePayloadWalker.Walk(root, MaxDepth, item =>
        {
            var evidence = Observe(item);
            observation?.Invoke(item.RelativePath, item.Kind);
            return evidence;
        }, cancellationToken);
        var ordered = entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToImmutableArray();
        var objectEvidence = objects.OrderBy(item => item.Path, StringComparer.Ordinal).ToImmutableArray();
        return new(identity.Identity.CanonicalPath, Digest(ordered, false), Digest(ordered, true), ordered)
        {
            FrontMatter = frontMatter,
            RootIdentity = identity.Identity,
            RootUnixMode = identity.UnixMode,
            Objects = objectEvidence,
            ObjectStateFingerprint = DigestObjects(identity, objectEvidence, cancellationToken)
        };

        PayloadFileEvidence? Observe(NativePayloadItem item)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entries.Count >= MaxEntries) throw new IOException("payload entry limit exceeded");
            objects.Add(new(item.RelativePath, item.Identity, item.UnixMode));
            if (item.Kind == "link")
            {
                var hash = Convert.ToHexString(SHA256.HashData(item.LinkBytes ?? []));
                var entry = new PayloadEntry(item.RelativePath, "link", hash, hash, 0, item.UnixMode, item.LinkTarget);
                entries.Add(entry);
                sink?.Invoke(item, entry);
                return null;
            }
            if (item.Kind == "directory")
            {
                var entry = new PayloadEntry(item.RelativePath, "directory", "", "", 0, item.UnixMode, null);
                entries.Add(entry);
                sink?.Invoke(item, entry);
                return null;
            }
            var stream = item.Content ?? throw new IOException("native payload file stream is missing");
            if (!stream.CanSeek) throw new IOException("payload contains a non-regular file");
            var initialLength = stream.Length;
            if (initialLength > MaxBytes - totalBytes) throw new IOException("payload byte limit exceeded");
            using var hashBuilder = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var skillBytes = strictMetadata && item.RelativePath == LocalSkillScanner.SkillFileName ? new MemoryStream() : null;
            var buffer = new byte[64 * 1024];
            long readBytes = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = stream.Read(buffer);
                if (count == 0) break;
                readBytes += count;
                totalBytes += count;
                if (totalBytes > MaxBytes || (skillBytes is not null && readBytes > MaxSkillBytes))
                    throw new IOException("payload byte limit exceeded");
                hashBuilder.AppendData(buffer.AsSpan(0, count));
                skillBytes?.Write(buffer, 0, count);
            }
            if (readBytes != initialLength || stream.Length != initialLength)
                throw new IOException("payload changed during comparison");
            var rawHash = Convert.ToHexString(hashBuilder.GetHashAndReset());
            var comparison = rawHash;
            if (skillBytes is not null)
            {
                try
                {
                    var text = new UTF8Encoding(false, true).GetString(skillBytes.GetBuffer(), 0, (int)skillBytes.Length);
                    var (_, parsedMetadata, parsed) = FrontMatterParser.Parse(text);
                    if (!parsed && strictMetadata) throw new IOException("invalid frontmatter during payload comparison");
                    frontMatter = parsedMetadata;
                    if (parsed) comparison = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                        InstallerMetadataNormalizer.Normalize(text))));
                }
                catch (Exception ex) when (ex is YamlException or DecoderFallbackException)
                { if (strictMetadata) throw new IOException("invalid frontmatter encoding or YAML during comparison", ex); }
            }
            var fileEntry = new PayloadEntry(item.RelativePath, "file", rawHash, comparison, readBytes, item.UnixMode, null);
            entries.Add(fileEntry);
            if (sink is not null)
            {
                stream.Position = 0;
                sink(item, fileEntry);
            }
            return new(rawHash, readBytes);
        }
    }

    private static string DigestObjects(NativePayloadRoot root, ImmutableArray<PayloadObjectEvidence> objects, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("SkillView.native-object-evidence.v1"u8);
        Append("", root.Identity, root.UnixMode);
        foreach (var item in objects)
        {
            token.ThrowIfCancellationRequested();
            Append(item.Path, item.Identity, item.UnixMode);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());

        void Append(string path, SecureFileIdentity identity, int? mode)
        {
            using var bytes = new MemoryStream();
            using (var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(path); writer.Write(identity.Volume); writer.Write(identity.FileIdLow); writer.Write(identity.FileIdHigh);
                writer.Write(identity.IsDirectory); writer.Write(identity.IsReparsePoint);
                writer.Write(identity.ChangeTimeSeconds); writer.Write(identity.ChangeTimeNanoseconds);
                writer.Write(identity.WindowsCreationTime); writer.Write(identity.WindowsChangeTime);
                writer.Write(mode ?? -1);
            }
            hash.AppendData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length));
        }
    }

    public static ImmutableArray<string> Differences(PayloadSnapshot before, PayloadSnapshot after,
        bool ignoreInstallerMetadata = true)
    {
        var left = before.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var right = after.Entries.ToDictionary(e => e.Path, StringComparer.Ordinal);
        return left.Keys.Concat(right.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Where(path => !left.TryGetValue(path, out var a) || !right.TryGetValue(path, out var b)
                || a.Kind != b.Kind || a.UnixMode != b.UnixMode
                || (ignoreInstallerMetadata ? a.ComparisonHash != b.ComparisonHash : a.ContentHash != b.ContentHash))
            .ToImmutableArray();
    }

    internal static string Digest(ImmutableArray<PayloadEntry> entries, bool normalized)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in entries)
        {
            using var buffer = new MemoryStream();
            using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(entry.Path);
                writer.Write(entry.Kind);
                writer.Write(normalized ? entry.ComparisonHash : entry.ContentHash);
                writer.Write(entry.UnixMode ?? -1);
            }
            hash.AppendData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

internal sealed record PayloadObjectEvidence(string Path, SecureFileIdentity Identity, int? UnixMode);
