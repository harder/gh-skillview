using System.Collections.Immutable;
using Microsoft.Win32.SafeHandles;
using SkillView.Inventory;

namespace SkillView.Maintenance;

internal sealed record PayloadExtendedAttribute(string Name, byte[] Value);

/// Metadata observed through the same native object as the captured bytes.
/// Refusals are review data; a draft may be inspected/preserved even when it
/// cannot be materialized without losing native metadata.
internal sealed record PayloadNativeMetadata(string Platform, SecureFileIdentity Identity,
    string Kind, int? UnixMode, uint? UserId, uint? GroupId, ulong? LinkCount,
    bool DirectoryLink, ImmutableArray<PayloadExtendedAttribute> Attributes,
    string EnumerationCoverage, ImmutableArray<string> Refusals)
{
    internal bool Supported => !Refusals.IsDefault && Refusals.IsEmpty
        && EnumerationCoverage is "macos-native-owner" or "linux-standard-namespaces-with-cap-sys-admin"
        && UserId.HasValue && GroupId.HasValue && LinkCount.HasValue && UnixMode is >= 0 and <= 511
        && Kind is "file" or "directory" or "link" && !Attributes.IsDefault
        && (Kind == "directory" || LinkCount == 1)
        && (Platform == "macos" ? EnumerationCoverage == "macos-native-owner"
            : Platform == "linux" && EnumerationCoverage == "linux-standard-namespaces-with-cap-sys-admin");
    internal const int MaxAttributeBytes = 64 * 1024;
    internal const int MaxEntryMetadataBytes = 128 * 1024;
    internal const int MaxAttributes = 64;
    internal const int MaxArchiveMetadataBytes = 16 * 1024 * 1024;

    internal long Validate()
    {
        if (Kind is not ("file" or "directory" or "link") || Platform is not ("macos" or "linux" or "windows")
            || Attributes.IsDefault || Attributes.Length > MaxAttributes || Refusals.IsDefault || Refusals.Length > MaxAttributes + 16
            || EnumerationCoverage is not ("macos-native-owner" or "linux-standard-namespaces-with-cap-sys-admin" or "caller-visible-xattr-names" or "unavailable"))
            throw new IOException("Captured native metadata is malformed.");
        long bytes = 0; var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in Attributes)
        {
            if (attribute is null || string.IsNullOrEmpty(attribute.Name) || attribute.Name.Contains('\0') || !names.Add(attribute.Name)
                || attribute.Value is null || attribute.Value.Length > MaxAttributeBytes)
                throw new IOException("Captured native attributes are malformed.");
            bytes += System.Text.Encoding.UTF8.GetByteCount(attribute.Name) + attribute.Value.Length;
            if (bytes > MaxEntryMetadataBytes) throw new IOException("Captured native attributes exceed their byte budget.");
        }
        foreach (var refusal in Refusals)
        {
            if (string.IsNullOrEmpty(refusal) || refusal.Length > MaxAttributeBytes) throw new IOException("Captured native metadata refusal is malformed.");
            bytes += System.Text.Encoding.UTF8.GetByteCount(refusal);
            if (bytes > MaxEntryMetadataBytes) throw new IOException("Captured native metadata exceeds its byte budget.");
        }
        return bytes;
    }

    internal static PayloadNativeMetadata Capture(NativePayloadItem item, CancellationToken token, PayloadMetadataAuthority? authority = null)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return UnixSecureRemovalBackend.CapturePayloadMetadata(item, token, authority);
        return new("windows", item.Identity, item.Kind, null, null, null, null,
            item.Kind == "link" && item.Identity.IsDirectory, [], "unavailable",
            ["Windows security descriptors and alternate streams need native metadata capture before merge publication."]);
    }

    internal static void Apply(SafeFileHandle target, PayloadNativeMetadata metadata, int? mode, CancellationToken token, PayloadMetadataAuthority? authority = null)
    {
        token.ThrowIfCancellationRequested();
        metadata.Validate();
        if (!metadata.Supported) throw new IOException("Native metadata cannot be preserved: " + string.Join(" ", metadata.Refusals));
        if (OperatingSystem.IsWindows()) throw new IOException("Windows native metadata publication is not available yet.");
        UnixSecureRemovalBackend.ApplyPayloadMetadata(target, metadata, mode, token, authority);
    }
}
