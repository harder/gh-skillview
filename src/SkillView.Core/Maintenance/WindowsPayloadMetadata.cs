using System.Collections.Immutable;
using System.Text;

namespace SkillView.Maintenance;

internal sealed record WindowsPayloadStream(string Name, byte[] Bytes);

/// Independently versioned evidence; absent on all older/Unix archives. Stream
/// bytes are charged as both bounded metadata and raw retained payload bytes.
internal sealed record WindowsPayloadMetadata(int SchemaVersion, byte[] SecurityDescriptor,
    uint FileAttributes, ImmutableArray<WindowsPayloadStream> Streams)
{
    internal long StreamBytes => Streams.IsDefault ? 0 : Streams.Sum(stream => (long)stream.Bytes.Length);
    internal long Validate()
    {
        if (SchemaVersion != 1 || SecurityDescriptor is null || SecurityDescriptor.Length is < 20 or > PayloadNativeMetadata.MaxEntryMetadataBytes
            || Streams.IsDefault || Streams.Length > PayloadNativeMetadata.MaxAttributes)
            throw new IOException("Windows metadata schema or bounded descriptor/streams are unavailable.");
        long bytes = SecurityDescriptor.Length;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stream in Streams)
        {
            if (stream is null || !ValidStreamName(stream.Name) || !names.Add(stream.Name)
                || stream.Bytes is null || stream.Bytes.Length > PayloadNativeMetadata.MaxAttributeBytes)
                throw new IOException("Windows alternate stream is malformed or exceeds its budget.");
            bytes += Encoding.Unicode.GetByteCount(stream.Name) + stream.Bytes.Length;
            if (bytes > PayloadNativeMetadata.MaxEntryMetadataBytes) throw new IOException("Windows metadata exceeds its entry budget.");
        }
        return bytes;
    }

    internal static bool ValidStreamName(string? name) => name is { Length: > 7 and <= 260 }
        && name[0] == ':' && name.EndsWith(":$DATA", StringComparison.Ordinal)
        && char.IsAsciiLetterOrDigit(name[1])
        && name[name.Length - 7] != '.'
        && name.AsSpan(1, name.Length - 7).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._-") < 0;

    internal static bool Same(WindowsPayloadMetadata? left, WindowsPayloadMetadata? right) => left is null ? right is null
        : right is not null && left.SchemaVersion == right.SchemaVersion && left.FileAttributes == right.FileAttributes
            && left.SecurityDescriptor.AsSpan().SequenceEqual(right.SecurityDescriptor)
            && left.Streams.Length == right.Streams.Length
            && left.Streams.All(stream => right.Streams.Any(other => stream.Name == other.Name && stream.Bytes.AsSpan().SequenceEqual(other.Bytes)));
}
