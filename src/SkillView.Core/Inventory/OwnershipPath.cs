namespace SkillView.Inventory;

/// Non-destructive context matching. This never supplies removal authority.
internal static class OwnershipPath
{
    internal static bool Same(string left, string right) =>
        Same(left, right, PathIdentity.Equals, CanonicalCandidate, EntryAddress);

    internal static bool Same(string left, string right, Func<string, string, bool> equal,
        Func<string, string?> canonical, Func<string, string?> entry) => equal(left, right)
        || (canonical(left) is { } a && canonical(right) is { } b && equal(a, b))
        || (entry(left) is { } c && entry(right) is { } d && equal(c, d));

    internal static string? EntryAddress(string path) => EntryAddress(path, CanonicalCandidate);

    internal static string? EntryAddress(string path, Func<string, string?> canonicalize)
    {
        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full);
        return parent is not null && canonicalize(parent) is { } canonical
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
