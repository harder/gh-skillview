using System.Collections.Immutable;
using SkillView.Inventory;

namespace SkillView.Maintenance;

/// Basis remains observed I evidence; Desired is an application descriptor,
/// never fabricated native observation of the new object.
internal sealed record PayloadMetadataRecipe(string Path, string BasisPath, PayloadNativeMetadata Basis,
    string DestinationKind, int? SelectedMode, bool CopyAttributes, PayloadNativeMetadata? AttributeSource = null, bool? SelectedDirectoryLink = null)
{
    internal PayloadNativeMetadata Desired => Basis with
    {
        Kind = DestinationKind,
        UnixMode = SelectedMode,
        LinkCount = DestinationKind == "directory" ? Basis.LinkCount : 1,
        Attributes = CopyAttributes ? Basis.Attributes : AttributeSource?.Attributes ?? [],
        // New Windows nodes inherit reviewed installed-parent security while
        // copying selected source stream/attribute evidence. This is a desired
        // descriptor; the source observation and native identity stay separate.
        Windows = ProjectWindows(),
        DirectoryLink = DestinationKind == "link" && (SelectedDirectoryLink ?? (AttributeSource ?? Basis).DirectoryLink)
    };
    private WindowsPayloadMetadata? ProjectWindows()
    {
        var result = CopyAttributes ? Basis.Windows : AttributeSource?.Windows is { } incoming && Basis.Windows is { } parent
            ? incoming with { SecurityDescriptor = parent.SecurityDescriptor } : null;
        return result is not null && DestinationKind == "link"
            ? result with { FileAttributes = (result.FileAttributes & ~0x10u) | ((SelectedDirectoryLink ?? (AttributeSource ?? Basis).DirectoryLink) ? 0x10u : 0) }
            : result;
    }
}

internal sealed record VerifiedMergedPayload(PayloadSnapshot Snapshot, string MetadataPolicy,
    ImmutableArray<PayloadMetadataRecipe> Metadata);

internal sealed class PayloadMetadataReview : IDisposable
{
    internal const string Policy = "native-owner-group-mode-observed-xattrs-v1; compatible installed attributes preserved; new source-node attributes copied; times regenerated; unsupported/unknown metadata refused";
    private const string WindowsPolicy = "windows-full-security-backup-streams-attributes-v1; compatible installed metadata preserved; new source streams/attributes and installed-parent security; times regenerated; unsupported/unknown metadata refused";
    internal static string PolicyFor(PayloadNativeMetadata metadata) => metadata.Platform == "windows" ? WindowsPolicy : Policy;
    internal string PolicyName => PolicyFor(recipes[""].Basis);
    private readonly Dictionary<string, PayloadMetadataRecipe> recipes = new(StringComparer.Ordinal);
    internal PayloadMetadataAuthority Authority { get; }
    internal ImmutableArray<PayloadMetadataRecipe> Recipes => recipes.Values.OrderBy(recipe => recipe.Path, StringComparer.Ordinal).ToImmutableArray();
    internal PayloadMetadataRecipe this[string path] => recipes[path];

    private PayloadMetadataReview(ThreeWayPayloadReview review, CapturedPayloadArchive.Reader installed, PayloadMetadataAuthority authority,
        IPayloadEvidenceReader? source, IPayloadEvidenceReader? resolved)
    {
        Authority = authority;
        var root = Require(installed.RootMetadata, "/");
        recipes.Add("", new("", "", root, "directory", review.RootMode.Selected, true));
        foreach (var row in review.Paths.Where(row => row.Selected is not null))
        {
            var entry = row.Selected!;
            bool? directoryLink = null;
            if (OperatingSystem.IsWindows() && entry.Kind == "link")
            {
                var selectedReader = row.Selection == "source" ? source : row.Selection == "resolved" ? resolved : installed;
                if (selectedReader is null || selectedReader.Entry(row.Path) != entry)
                    throw new IOException("Windows link kind needs its exact selected immutable capture.");
                directoryLink = Require(selectedReader.Metadata(row.Path), row.Path).DirectoryLink;
            }
            if (installed.TryEntry(row.Path, out var local) && local!.Entry.Kind == entry.Kind)
                recipes.Add(row.Path, new(row.Path, row.Path, Require(local.Metadata, row.Path), entry.Kind, entry.UnixMode, true, SelectedDirectoryLink: directoryLink));
            else
            {
                var parent = Parent(row.Path); var basis = root; var basisPath = "";
                while (parent.Length != 0)
                {
                    if (installed.TryEntry(parent, out var ancestor) && ancestor!.Entry.Kind == "directory")
                    { basis = Require(ancestor.Metadata, parent); basisPath = parent; break; }
                    parent = Parent(parent);
                }
                // Supported policies have no nonempty/default ACL or setgid
                // directory. Ownership inherits from this reviewed parent;
                // ordinary xattrs on a parent do not inherit to a new child.
                PayloadNativeMetadata? attributes = null;
                if (row.Selection is "source" or "resolved")
                {
                    var selected = row.Selection == "source" ? source : resolved;
                    if (selected is null || selected.Entry(row.Path) != entry)
                        throw new IOException("New node metadata needs its exact immutable selected capture: " + row.Path);
                    attributes = Require(selected.Metadata(row.Path), row.Path);
                }
                recipes.Add(row.Path, new(row.Path, basisPath, basis, entry.Kind, entry.UnixMode, false, attributes, directoryLink));
            }
            if (OperatingSystem.IsWindows() ? entry.UnixMode is not null : entry.UnixMode is null or < 0 or > 511)
                throw new IOException("The merged path needs a supported reviewed mode: " + row.Path);
        }
        if (review.Paths.Sum(row => row.Selected?.Bytes ?? 0) + recipes.Values.Sum(recipe => recipe.Desired.AlternateStreamBytes) > PayloadSnapshot.MaxBytes)
            throw new IOException("Merged content and Windows alternate streams exceed the raw payload budget.");
    }

    private PayloadMetadataReview(CapturedPayloadArchive installed, PayloadMetadataAuthority authority)
    {
        Authority = authority;
        var root = Require(installed.RootMetadata, "/");
        recipes.Add("", new("", "", root, "directory", installed.Snapshot.RootUnixMode, true));
        foreach (var item in installed.Entries)
        {
            var entry = item.Entry;
            var captured = Require(item.Metadata, entry.Path);
            recipes.Add(entry.Path, new(entry.Path, entry.Path, captured, entry.Kind, entry.UnixMode, true,
                SelectedDirectoryLink: captured.DirectoryLink));
        }
    }

    internal static PayloadMetadataReview PreserveInstalled(CapturedPayloadArchive installed,
        CapturedPayloadArchive.Reader reader, CancellationToken token)
    {
        installed.Validate();
        var authority = reader.OpenMetadataAuthority(token);
        try { return new(installed, authority); }
        catch { authority.Dispose(); throw; }
    }

    internal static PayloadMetadataReview Create(ThreeWayPayloadReview review, CapturedPayloadArchive.Reader installed, CancellationToken token,
        IPayloadEvidenceReader? source = null, IPayloadEvidenceReader? resolved = null)
    {
        var authority = installed.OpenMetadataAuthority(token);
        try { return new(review, installed, authority, source, resolved); }
        catch { authority.Dispose(); throw; }
    }

    internal void Verify(NativePayloadItem item, CancellationToken token)
    {
        var expected = recipes[item.RelativePath].Desired;
        var actual = Require(PayloadNativeMetadata.Capture(item, token, Authority), item.RelativePath);
        if (actual.Kind != expected.Kind || actual.UserId != expected.UserId || actual.GroupId != expected.GroupId
            || actual.UnixMode != expected.UnixMode || actual.DirectoryLink != expected.DirectoryLink
            || !WindowsPayloadMetadata.Same(expected.Windows, actual.Windows) || actual.Attributes.Length != expected.Attributes.Length
            || expected.Attributes.Any(attribute => !actual.Attributes.Any(other => other.Name == attribute.Name && other.Value.AsSpan().SequenceEqual(attribute.Value))))
            throw new IOException("Materialized native metadata differs from review: " + item.RelativePath);
    }

    private static PayloadNativeMetadata Require(PayloadNativeMetadata? metadata, string path)
    {
        if (metadata is null) throw new IOException("Native metadata was not captured for " + path);
        metadata.Validate();
        if (!metadata.Supported) throw new IOException("Native metadata cannot be preserved for " + path + ": " + string.Join(" ", metadata.Refusals));
        return metadata;
    }

    private static string Parent(string path)
    {
        var slash = path.LastIndexOf('/'); return slash < 0 ? "" : path[..slash];
    }
    public void Dispose() => Authority.Dispose();
}
