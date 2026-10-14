namespace SkillView.Inventory;

/// Non-destructive context matching. This never supplies removal authority.
internal static class OwnershipPath
{
    internal static bool Same(string left, string right) => PathIdentity.Equals(left, right)
        || (CanonicalCandidate(left) is { } a && CanonicalCandidate(right) is { } b && PathIdentity.Equals(a, b))
        || (EntryAddress(left) is { } c && EntryAddress(right) is { } d && PathIdentity.Equals(c, d));

    internal static string? EntryAddress(string path)
    {
        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full);
        return parent is not null && CanonicalCandidate(parent) is { } canonical
            ? Path.Combine(canonical, Path.GetFileName(full)) : null;
    }

    internal static string? CanonicalCandidate(string path)
    {
        var cursor = Path.GetFullPath(path);
        var suffix = new Stack<string>();
        for (var depth = 0; depth < 128; depth++)
        {
            if (SecureRemovalBackend.TryCanonicalizePath(cursor, out var canonical, out _))
                return suffix.Aggregate(canonical, Path.Combine);
            if (Directory.Exists(cursor) || File.Exists(cursor) || PathResolver.IsSymlink(cursor)) return null;
            var parent = Path.GetDirectoryName(cursor);
            if (parent is null) break;
            suffix.Push(Path.GetFileName(cursor));
            cursor = parent;
        }
        return null;
    }
}
