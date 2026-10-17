namespace SkillView.Inventory;

internal sealed partial class WindowsSecureRemovalBackend
{
    internal static bool StateDirectoryIsMissing(string path, CancellationToken token)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var parts = Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 128 || parts.Any(part => part is "." or ".."))
            throw new IOException("Desired-state presence path exceeds its component contract.");
        var current = OpenCanonicalDirectory(root);
        try
        {
            foreach (var part in parts)
            {
                token.ThrowIfCancellationRequested();
                var parent = ReadIdentity(current);
                Microsoft.Win32.SafeHandles.SafeFileHandle next;
                try { next = OpenEntryAt(current, part, null, false, requestDeleteAccess: false); }
                catch (WindowsOpenException ex) when (ex.NativeError is ErrorFileNotFound or ErrorPathNotFound)
                {
                    token.ThrowIfCancellationRequested();
                    if (!Matches(parent, ReadIdentity(current))) throw new IOException("Desired-state absence parent changed.");
                    return true;
                }
                try
                {
                    token.ThrowIfCancellationRequested();
                    var child = ReadIdentity(next);
                    if (!child.IsDirectory || child.IsReparsePoint) throw new IOException("Desired-state presence has a link or non-directory ancestor.");
                    if (!Matches(parent, ReadIdentity(current))) throw new IOException("Desired-state presence parent changed.");
                    current.Dispose(); current = next; next = null!;
                }
                finally { next?.Dispose(); }
            }
            return false;
        }
        finally { current.Dispose(); }
    }
}
