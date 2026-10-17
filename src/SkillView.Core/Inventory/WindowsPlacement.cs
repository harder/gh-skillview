using System.Collections.Immutable;

namespace SkillView.Inventory;

internal sealed partial class WindowsSecureRemovalBackend
{
    internal const int PlacementCaptureAttempts = 4;
    private sealed class PlacementParentChangedException(string message) : IOException(message);

    internal static NativePlacementLease OpenPlacement(string path, CancellationToken token,
        Action<int, string, string>? beforeParentCheckForTests = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return OpenPlacementAttempt(path, token, beforeParentCheckForTests is null ? null
                    : (parent, name) => beforeParentCheckForTests(attempt, parent, name));
            }
            catch (PlacementParentChangedException)
            {
                // No placement evidence has escaped this attempt. Release every
                // handle and start from the volume root; never reuse a child or
                // weaken a generation comparison after namespace churn.
                token.ThrowIfCancellationRequested();
                if (attempt + 1 >= PlacementCaptureAttempts) throw;
            }
        }
    }

    private static NativePlacementLease OpenPlacementAttempt(string path, CancellationToken token,
        Action<string, string>? beforeParentCheck)
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
                    beforeParentCheck?.Invoke(ReadFinalPath(current), parts[index]);
                    token.ThrowIfCancellationRequested();
                    if (!Matches(before, ReadIdentity(current))) throw new PlacementParentChangedException("Placement parent changed during missing observation.");
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
                    beforeParentCheck?.Invoke(ReadFinalPath(current), parts[index]);
                    token.ThrowIfCancellationRequested();
                    if (!ReadIdentity(next).IsDirectory) throw new IOException("Source placement requires an ordinary directory.");
                    if (!Matches(before, ReadIdentity(current))) throw new PlacementParentChangedException("Source placement parent changed.");
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
