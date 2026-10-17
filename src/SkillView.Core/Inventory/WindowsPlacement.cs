using System.Collections.Immutable;

namespace SkillView.Inventory;

internal sealed partial class WindowsSecureRemovalBackend
{
    internal static NativePlacementLease OpenPlacement(string path, CancellationToken token)
    {
        var full = Path.GetFullPath(path);
        var parts = Path.GetRelativePath(Path.GetPathRoot(full)!, full).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 128) throw new IOException("Placement component bound exceeded.");
        var current = OpenCanonicalDirectory(Path.GetPathRoot(full)!);
        try
        {
            for (var index = 0; index < parts.Length && parts[index] != "."; index++)
            {
                token.ThrowIfCancellationRequested();
                var before = ReadIdentity(current);
                Microsoft.Win32.SafeHandles.SafeFileHandle next;
                try { next = OpenEntryAt(current, parts[index], null, false, requestDeleteAccess: false); }
                catch (WindowsOpenException ex) when (ex.NativeError is ErrorFileNotFound or ErrorPathNotFound)
                {
                    if (!Matches(before, ReadIdentity(current))) throw new IOException("Placement parent changed during missing observation.");
                    return Finish(parts.Skip(index).ToImmutableArray());
                }
                try
                {
                    var named = ReadIdentity(next);
                    if ((!named.IsDirectory && !named.IsReparsePoint) || index == parts.Length - 1 && named.IsReparsePoint)
                        throw new IOException("Source placement requires an ordinary final directory; links and non-directories refuse.");
                    if (named.IsReparsePoint)
                    {
                        var followed = OpenEntryAt(current, parts[index], true, false, openReparsePoint: false, requestDeleteAccess: false);
                        try
                        {
                            using var again = OpenEntryAt(current, parts[index], null, false, requestDeleteAccess: false);
                            if (!Matches(named, ReadIdentity(again))) throw new IOException("Placement ancestor alias changed.");
                            next.Dispose(); next = followed; followed = null!;
                        }
                        finally { followed?.Dispose(); }
                    }
                    if (!ReadIdentity(next).IsDirectory || !Matches(before, ReadIdentity(current))) throw new IOException("Source placement parent changed.");
                    current.Dispose(); current = next; next = null!;
                }
                finally { next?.Dispose(); }
            }
            return Finish([]);

            NativePlacementLease Finish(ImmutableArray<string> suffix)
            {
                token.ThrowIfCancellationRequested();
                var canonical = ReadFinalPath(current);
                var identity = ToSecureIdentity(ReadIdentity(current), canonical);
                var owned = current;
                var lease = new NativePlacementLease(new(path, suffix.Aggregate(canonical, Path.Combine), identity, suffix), owned,
                    () => ToSecureIdentity(ReadIdentity(owned), ReadFinalPath(owned)));
                current = null!;
                return lease;
            }
        }
        finally { current?.Dispose(); }
    }
}
