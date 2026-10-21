using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SkillView.Inventory;

internal sealed partial class UnixSecureRemovalBackend
{
    internal const int PlacementCaptureAttempts = 4;
    private sealed class PlacementGenerationChangedException(string message) : IOException(message);

    private sealed class PlacementBudget
    {
        internal int Components = 128;
        internal int Links = 16;
        internal int LinkBytes = 16 * 1024;
    }

    internal static NativePlacementLease OpenPlacement(string path, CancellationToken token,
        Action<int, string, string>? beforeParentCheckForTests = null,
        Action<int, string, string>? beforeChildCheckForTests = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return OpenPlacementCore(path, new PlacementBudget(), token, beforeParentCheckForTests is null ? null
                    : (parent, name) => beforeParentCheckForTests(attempt, parent, name), beforeChildCheckForTests is null ? null
                    : (parent, name) => beforeChildCheckForTests(attempt, parent, name));
            }
            catch (PlacementGenerationChangedException)
            {
                // No evidence escaped this attempt. Dispose all anchors and
                // repeat the entire lexical walk, including alias expansion.
                // Never refresh a published lease or weaken its comparisons.
                token.ThrowIfCancellationRequested();
                if (attempt + 1 >= PlacementCaptureAttempts) throw;
            }
        }
    }

    private static NativePlacementLease OpenPlacementCore(string path, PlacementBudget budget, CancellationToken token,
        Action<string, string>? beforeParentCheck, Action<string, string>? beforeChildCheck)
    {
        var full = Path.GetFullPath(path);
        if (Encoding.UTF8.GetByteCount(full) > 32 * 1024) throw new IOException("Placement path bound exceeded.");
        var parts = Path.GetRelativePath(Path.GetPathRoot(full)!, full).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = OpenDirectory(Path.GetPathRoot(full)!);
        try
        {
            using var buffer = new StatBuffer();
            for (var index = 0; index < parts.Length && parts[index] != "."; index++)
            {
                token.ThrowIfCancellationRequested();
                if (--budget.Components < 0) throw new IOException("Placement aggregate component bound exceeded.");
                var before = ReadStat(current, buffer.Pointer);
                if (FStatAtWithRetry(current.FileDescriptor, parts[index], buffer.Pointer, UnixConstants.NoFollowFlag) != 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error != 2) throw new IOException("Source placement is unknown: " + new Win32Exception(error).Message);
                    beforeParentCheck?.Invoke(ReadFinalPath(current), parts[index]);
                    token.ThrowIfCancellationRequested();
                    RequireStable(before, ReadStat(current, buffer.Pointer), index, "parent");
                    return Finish(parts.Skip(index).ToImmutableArray());
                }
                var named = ParseStat(buffer.Pointer);
                if ((!named.IsDirectory && !named.IsSymlink) || index == parts.Length - 1 && named.IsSymlink)
                    throw new IOException("Source placement requires an ordinary final directory; links and non-directories refuse.");
                if (named.IsSymlink)
                {
                    if (--budget.Links < 0) throw new IOException("Placement symlink expansion bound exceeded.");
                    var raw = LinkBytes(current, parts[index], budget, token);
                    string target;
                    try { target = new UTF8Encoding(false, true).GetString(raw); }
                    catch (DecoderFallbackException ex) { throw new IOException("Placement ancestor target is not strict UTF-8.", ex); }
                    if (string.IsNullOrEmpty(target) || target.Any(char.IsControl)
                        || target.Split('/').Any(part => part is "." or ".."))
                        throw new IOException("Placement ancestor link targets with dot components or invalid text are unsupported.");
                    // Restart under the SAME root-based mount boundary. Native
                    // NO_XDEV rejects even same-mount absolute symlink jumps;
                    // it is never waived to follow one. Raw '..' was refused
                    // before lexical normalization could change kernel meaning.
                    var targetPath = Path.IsPathFullyQualified(target) ? target : Path.Combine(ReadFinalPath(current), target);
                    var remaining = parts.Skip(index + 1).ToArray();
                    var expanded = remaining.Aggregate(targetPath, Path.Combine);
                    NativePlacementLease? resolved = OpenPlacementCore(expanded, budget, token, beforeParentCheck, beforeChildCheck);
                    try
                    {
                        // If missingness began inside targetPath, its suffix
                        // would contain MORE components than remaining. Enforce
                        // at every recursive expansion: a dangling link target
                        // cannot be mistaken for an allowed missing old suffix.
                        if (resolved.Evidence.MissingSuffix.Length > remaining.Length)
                            throw new IOException("Placement ancestor link target is missing; broken aliases are not absence evidence.");
                        beforeParentCheck?.Invoke(ReadFinalPath(current), parts[index]);
                        token.ThrowIfCancellationRequested();
                        var parentAfter = ReadStat(current, buffer.Pointer);
                        if (!TryReadStatAt(current, parts[index], buffer.Pointer, out var after, out _))
                            throw Changed(index, "alias", "entry unavailable");
                        if (!Matches(named, after)) throw Changed(index, "alias", IdentityDifference(named, after) ?? "ctime");
                        if (!raw.AsSpan().SequenceEqual(LinkBytes(current, parts[index], budget, token)))
                            throw Changed(index, "alias", "link target");
                        RequireStable(before, parentAfter, index, "parent");
                        RequireStable(before, ReadStat(current, buffer.Pointer), index, "parent");
                        var addressed = resolved.AtLexicalAddress(path);
                        resolved = null;
                        return addressed;
                    }
                    finally { resolved?.Dispose(); }
                }
                var next = OperatingSystem.IsLinux() ? OpenPlacementChild(current, parts[index])
                    : OpenDirectoryAt(current, parts[index], false, followFinalComponent: false);
                try
                {
                    beforeChildCheck?.Invoke(ReadFinalPath(current), parts[index]);
                    token.ThrowIfCancellationRequested();
                    var opened = ReadStat(next, buffer.Pointer);
                    RequireIdentity(named, opened, index, "opened child");
                    beforeParentCheck?.Invoke(ReadFinalPath(current), parts[index]);
                    token.ThrowIfCancellationRequested();
                    var parentAfter = ReadStat(current, buffer.Pointer);
                    if (!TryReadStatAt(current, parts[index], buffer.Pointer, out var after, out _))
                        throw Changed(index, "named child", "entry unavailable");
                    // Refuse a replacement before classifying any ctime-only
                    // change, including a moved opened child with a new name peer.
                    RequireIdentity(named, after, index, "named child");
                    RequireIdentity(before, parentAfter, index, "parent");
                    RequireStable(named, opened, index, "opened child");
                    RequireStable(named, after, index, "named child");
                    RequireStable(before, parentAfter, index, "parent");
                    RequireStable(before, ReadStat(current, buffer.Pointer), index, "parent");
                    current.Dispose(); current = next; next = null!;
                }
                finally { next?.Dispose(); }
            }
            return Finish([]);

            string? IdentityDifference(NativeStat expected, NativeStat actual) =>
                expected.Device != actual.Device ? "device" : expected.Inode != actual.Inode ? "inode"
                : expected.FileType != actual.FileType ? "type" : null;

            IOException Changed(int index, string role, string field, bool restart = false)
            {
                var component = Path.Combine(Path.GetPathRoot(full)!, Path.Combine(parts.Take(index + 1).ToArray()));
                var message = $"Source placement {role} changed during relative observation at '{component}' ({field}).";
                return restart ? new PlacementGenerationChangedException(message) : new IOException(message);
            }

            void RequireIdentity(NativeStat expected, NativeStat actual, int index, string role)
            {
                if (IdentityDifference(expected, actual) is { } field) throw Changed(index, role, field);
            }

            void RequireStable(NativeStat expected, NativeStat actual, int index, string role)
            {
                RequireIdentity(expected, actual, index, role);
                if (!Matches(expected, actual)) throw Changed(index, role, "ctime", restart: true);
            }

            NativePlacementLease Finish(ImmutableArray<string> suffix)
            {
                token.ThrowIfCancellationRequested();
                var canonical = ReadFinalPath(current);
                var identity = ToSecureIdentity(ReadStat(current, buffer.Pointer), canonical);
                var projected = suffix.Aggregate(canonical, Path.Combine);
                var owned = current;
                var lease = new NativePlacementLease(new(path, projected, identity, suffix), owned, () =>
                {
                    using var stat = new StatBuffer();
                    return ToSecureIdentity(ReadStat(owned, stat.Pointer), ReadFinalPath(owned));
                });
                current = null!;
                return lease;
            }
        }
        finally { current?.Dispose(); }
    }

    private static byte[] LinkBytes(SafeUnixHandle parent, string name, PlacementBudget budget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var bytes = new byte[4097];
        var count = ReadLinkAtWithRetry(parent.FileDescriptor, name, bytes);
        if (count <= 0 || count >= bytes.Length) throw new IOException("Placement raw link target unavailable or exceeds its bound.");
        budget.LinkBytes -= checked((int)count);
        if (budget.LinkBytes < 0) throw new IOException("Placement aggregate raw link read bound exceeded.");
        return bytes.AsSpan(0, checked((int)count)).ToArray();
    }

    // Placement follows bracketed aliases only by checked root restarts. Keep
    // destructive traversal's BENEATH/NO_SYMLINKS helper unchanged.
    private static SafeUnixHandle OpenPlacementChild(SafeUnixHandle parent, string name)
    {
        var how = new OpenHow
        {
            Flags = unchecked((ulong)UnixConstants.DirectoryFollowOpenFlags),
            Resolve = UnixConstants.ResolveNoCrossDevice
        };
        var fd = checked((int)OpenAt2WithRetry(UnixConstants.OpenAt2SystemCall, parent.FileDescriptor,
            name, ref how, (nuint)Marshal.SizeOf<OpenHow>()));
        if (fd < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException(error == 18
                ? "Source-switch placement crosses a Linux mount boundary; bind aliases and paths on another mounted filesystem are unsupported."
                : "Native placement mount-boundary resolution is unavailable: " + new Win32Exception(error).Message);
        }
        return new SafeUnixHandle(fd);
    }
}
