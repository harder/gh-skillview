using System.Collections.Immutable;
using Microsoft.Win32.SafeHandles;
using SkillView.Inventory;
using System.Text.Json.Serialization;

namespace SkillView.Maintenance;

internal sealed record PayloadExtendedAttribute(string Name, byte[] Value);

/// Metadata observed through the same native object as the captured bytes.
/// Refusals are review data; a draft may be inspected/preserved even when it
/// cannot be materialized without losing native metadata.
internal sealed record PayloadNativeMetadata(string Platform, SecureFileIdentity Identity,
    string Kind, int? UnixMode, uint? UserId, uint? GroupId, ulong? LinkCount,
    bool DirectoryLink, ImmutableArray<PayloadExtendedAttribute> Attributes,
    string EnumerationCoverage, ImmutableArray<string> Refusals,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WindowsPayloadMetadata? Windows = null)
{
    internal bool Supported => !Refusals.IsDefault && Refusals.IsEmpty
        && Kind is "file" or "directory" or "link" && !Attributes.IsDefault
        && LinkCount.HasValue && (Kind == "directory" || LinkCount == 1)
        && (Platform == "windows" ? Windows is { SchemaVersion: 1 } && UnixMode is null && UserId is null && GroupId is null
                && EnumerationCoverage == "windows-full-security-and-backup-streams-v1" && Attributes.IsEmpty
            : Windows is null && UserId.HasValue && GroupId.HasValue && UnixMode is >= 0 and <= 511
                && (Platform == "macos" ? EnumerationCoverage == "macos-native-owner"
                    : Platform == "linux" && EnumerationCoverage == "linux-standard-namespaces-with-cap-sys-admin"));
    internal long AlternateStreamBytes => Windows?.StreamBytes ?? 0;
    internal const int MaxAttributeBytes = 64 * 1024;
    internal const int MaxEntryMetadataBytes = 128 * 1024;
    internal const int MaxAttributes = 64;
    internal const int MaxArchiveMetadataBytes = 16 * 1024 * 1024;

    internal long Validate()
    {
        if (Kind is not ("file" or "directory" or "link") || Platform is not ("macos" or "linux" or "windows")
            || Attributes.IsDefault || Attributes.Length > MaxAttributes || Refusals.IsDefault || Refusals.Length > MaxAttributes + 16
            || Windows is not null && Platform != "windows"
            || EnumerationCoverage is not ("macos-native-owner" or "linux-standard-namespaces-with-cap-sys-admin" or "caller-visible-xattr-names" or "unavailable" or "windows-full-security-and-backup-streams-v1"))
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
        bytes += Windows?.Validate() ?? 0;
        if (bytes > MaxEntryMetadataBytes) throw new IOException("Captured native metadata exceeds its byte budget.");
        return bytes;
    }

    internal static PayloadNativeMetadata Capture(NativePayloadItem item, CancellationToken token, PayloadMetadataAuthority? authority = null,
        long maxAlternateBytes = MaxEntryMetadataBytes)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return UnixSecureRemovalBackend.CapturePayloadMetadata(item, token, authority);
        return WindowsSecureRemovalBackend.CapturePayloadMetadata(item, token, maxAlternateBytes);
    }

    internal static void Apply(SafeFileHandle target, PayloadNativeMetadata metadata, int? mode, CancellationToken token, PayloadMetadataAuthority? authority = null)
    {
        token.ThrowIfCancellationRequested();
        metadata.Validate();
        if (!metadata.Supported) throw new IOException("Native metadata cannot be preserved: " + string.Join(" ", metadata.Refusals));
        if (OperatingSystem.IsWindows()) { WindowsSecureRemovalBackend.ApplyPayloadMetadata(target, metadata, mode, token); return; }
        UnixSecureRemovalBackend.ApplyPayloadMetadata(target, metadata, mode, token, authority);
    }
}
