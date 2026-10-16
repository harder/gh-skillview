using SkillView.Inventory;

namespace SkillView.Maintenance;

/// Copies observed installed content and supported metadata, changing only the
/// explicitly transformed SKILL.md bytes. It makes no source-baseline claim.
internal static class InstalledPayloadMaterializer
{
    internal static PayloadSnapshot Stage(NativeStateDirectory operation, CapturedPayloadArchive installed,
        CapturedPayloadArchive.Reader reader, PayloadMetadataReview metadata, byte[] desiredSkill, CancellationToken token)
    {
        var children = installed.Entries.GroupBy(item => Parent(item.Entry.Path), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Entry.Path, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        using var root = operation.CreateMetadataPayloadDirectory("candidate", token);
        Build(root, "", installed.Snapshot.RootUnixMode, 0);
        return root.FlushAndCapture(token, item => metadata.Verify(item, token));

        void Build(NativePayloadDirectory directory, string parent, int? mode, int depth)
        {
            token.ThrowIfCancellationRequested();
            if (depth > PayloadSnapshot.MaxDepth) throw new IOException("Installed payload exceeds the clone depth budget.");
            foreach (var item in children.GetValueOrDefault(parent) ?? [])
            {
                token.ThrowIfCancellationRequested();
                var entry = item.Entry;
                var leaf = entry.Path[(entry.Path.LastIndexOf('/') + 1)..];
                switch (entry.Kind)
                {
                    case "directory":
                        using (var child = directory.CreateDirectory(leaf, token)) Build(child, entry.Path, entry.UnixMode, depth + 1);
                        break;
                    case "file":
                        using (var output = directory.CreateFile(leaf, token))
                        {
                            if (entry.Path == "SKILL.md") output.Write(desiredSkill);
                            else reader.CopyEntry(entry.Path, output, token);
                            output.Flush(true);
                            PayloadNativeMetadata.Apply(output.SafeFileHandle, metadata[entry.Path].Desired, entry.UnixMode, token, metadata.Authority);
                            output.Flush(true);
                        }
                        break;
                    case "link":
                        using (var input = new MemoryStream())
                        {
                            reader.CopyEntry(entry.Path, input, token);
                            directory.CreateLinkWithMetadata(leaf, input.GetBuffer().AsSpan(0, checked((int)input.Length)), entry.UnixMode,
                                metadata[entry.Path].Desired.DirectoryLink, metadata[entry.Path].Desired, token, metadata.Authority);
                        }
                        break;
                    default: throw new IOException("Unsupported installed payload kind.");
                }
            }
            directory.SealWithMetadata(mode, metadata[parent].Desired, token, metadata.Authority);
        }
    }

    private static string Parent(string path)
    {
        var slash = path.LastIndexOf('/'); return slash < 0 ? "" : path[..slash];
    }
}
