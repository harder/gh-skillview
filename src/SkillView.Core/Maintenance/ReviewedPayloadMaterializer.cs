using System.Text;
using SkillView.Inventory;
using SkillView.Inventory.Models;

namespace SkillView.Maintenance;

/// Builds draft authored content only. A caller must separately bind and verify
/// native metadata, B/I/C evidence and review choices before saving an applyable
/// plan. This method grants no authority to replace an installation.
internal static class ReviewedPayloadMaterializer
{
    internal static PayloadSnapshot Stage(NativeStateDirectory operation, string name,
        ThreeWayPayloadReview review, IReadOnlyDictionary<string, CapturedPayloadArchive.Reader> readers,
        SkillFrontMatter approvedTracking, CancellationToken token, Action<string>? beforeCaptureForTests = null)
    {
        token.ThrowIfCancellationRequested();
        if (!review.CanMaterialize) throw new IOException("Resolve every path and topology conflict before building a draft.");
        var selected = review.Paths.Where(row => row.Selected is not null).ToArray();
        foreach (var row in selected)
        {
            token.ThrowIfCancellationRequested();
            if (!readers.TryGetValue(row.Selection!, out var reader) || reader.Entry(row.Path) != row.Selected)
                throw new IOException("The selected path differs from its immutable captured evidence.");
        }
        if (!selected.Any(row => row.Path == "SKILL.md" && row.Selected!.Kind == "file"))
            throw new IOException("The reviewed result needs a regular SKILL.md.");
        var children = selected.GroupBy(row => Parent(row.Path), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(row => row.Path, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        using var root = operation.CreatePayloadDirectory(name, token);
        Build(root, "", review.RootMode.Selected, 0);
        operation.ValidateLocation(token);
        beforeCaptureForTests?.Invoke(root.CanonicalPath);
        var result = root.FlushAndCapture(token);
        if (result.FrontMatter is not { } metadata || !metadata.Diagnostics.IsEmpty
            || metadata.Upstream != approvedTracking.Upstream || metadata.GithubTreeSha != approvedTracking.GithubTreeSha
            || metadata.GithubRef != approvedTracking.GithubRef || metadata.GithubPath != approvedTracking.GithubPath
            || metadata.LocalPath != approvedTracking.LocalPath || metadata.Pinned != approvedTracking.Pinned
            || metadata.PinnedRef != approvedTracking.PinnedRef)
            throw new IOException("The draft result does not match its approved source tracking.");
        VerifyResult(result);
        return result;

        void Build(NativePayloadDirectory directory, string parent, int? mode, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (depth > PayloadSnapshot.MaxDepth) throw new IOException("Draft payload depth exceeds its budget.");
            foreach (var row in children.GetValueOrDefault(parent) ?? [])
            {
                token.ThrowIfCancellationRequested();
                var entry = row.Selected!; var leaf = entry.Path[(entry.Path.LastIndexOf('/') + 1)..];
                var reader = readers[row.Selection!];
                switch (entry.Kind)
                {
                    case "directory":
                        using (var child = directory.CreateDirectory(leaf, token)) Build(child, entry.Path, entry.UnixMode, depth + 1);
                        break;
                    case "file":
                        using (var output = directory.CreateFile(leaf, token))
                        {
                            if (entry.Path == "SKILL.md")
                            {
                                if (entry.Bytes > PayloadSnapshot.MaxSkillBytes) throw new IOException("Resolved SKILL.md exceeds its metadata budget.");
                                using var input = new MemoryStream(); reader.CopyEntry(entry.Path, input, token);
                                var text = new UTF8Encoding(false, true).GetString(input.GetBuffer(), 0, (int)input.Length);
                                var bound = Encoding.UTF8.GetBytes(RecordedTrackingWriter.Bind(text, approvedTracking));
                                if (bound.Length > PayloadSnapshot.MaxSkillBytes) throw new IOException("Bound SKILL.md exceeds its metadata budget.");
                                output.Write(bound);
                            }
                            else reader.CopyEntry(entry.Path, output, token);
                            if (!OperatingSystem.IsWindows())
                            {
                                if (entry.UnixMode is null or < 0 or > 511) throw new IOException("Unsupported draft file permission bits.");
                                File.SetUnixFileMode(output.SafeFileHandle, (UnixFileMode)entry.UnixMode.Value);
                            }
                            else if (entry.UnixMode is not null) throw new IOException("A Windows file cannot carry a Unix mode.");
                            output.Flush(true);
                        }
                        break;
                    case "link":
                        using (var input = new MemoryStream())
                        {
                            reader.CopyEntry(entry.Path, input, token);
                            // Reparse buffers do not encode the directory flag.
                            // Windows link kind must be bound by the native
                            // metadata adapter before this draft can support it.
                            if (OperatingSystem.IsWindows()) throw new IOException("Windows link materialization requires reviewed native link-kind metadata.");
                            directory.CreateLink(leaf, input.GetBuffer().AsSpan(0, (int)input.Length), entry.UnixMode, false, token);
                        }
                        break;
                    default: throw new IOException("Unsupported selected draft entry kind.");
                }
            }
            directory.Seal(mode, token);
        }

        void VerifyResult(PayloadSnapshot result)
        {
            if (result.RootUnixMode != review.RootMode.Selected || result.Entries.Length != selected.Length)
                throw new IOException("Materialized draft mode or path set differs from review.");
            var actual = result.Entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
            foreach (var row in selected)
            {
                var expected = row.Selected!;
                if (!actual.TryGetValue(row.Path, out var entry) || entry.Kind != expected.Kind || entry.UnixMode != expected.UnixMode
                    || (entry.Path == "SKILL.md" ? entry.ComparisonHash != expected.ComparisonHash : entry.ContentHash != expected.ContentHash))
                    throw new IOException("Materialized draft content differs from the reviewed authored choices.");
            }
        }
    }

    private static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }
}
