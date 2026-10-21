using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SkillView.Inventory;

internal sealed partial class UnixSecureRemovalBackend
{
    internal const int PlacementCaptureAttempts = 4;
    private sealed class PlacementParentChangedException(string message) : IOException(message);

    private sealed class PlacementBudget
    {
        internal int Components = 128;
        internal int Links = 16;
        internal int LinkBytes = 16 * 1024;
    }

    internal static NativePlacementLease OpenPlacement(string path, CancellationToken token,
        Action<int, string, string>? beforeParentCheckForTests = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return OpenPlacementCore(path, new PlacementBudget(), token, beforeParentCheckForTests is null ? null
                    : (parent, name) => beforeParentCheckForTests(attempt, parent, name));
            }
            catch (PlacementParentChangedException)
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
        Action<string, string>? beforeParentCheck)
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
                    if (!Matches(before, ReadStat(current, buffer.Pointer))) throw new PlacementParentChangedException("Placement parent changed during missing observation.");
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
                    NativePlacementLease? resolved = OpenPlacementCore(expanded, budget, token, beforeParentCheck);
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
                        var parentChanged = !Matches(before, ReadStat(current, buffer.Pointer));
                        if (!TryReadStatAt(current, parts[index], buffer.Pointer, out var after, out _) || !Matches(named, after)
                            || !raw.AsSpan().SequenceEqual(LinkBytes(current, parts[index], budget, token)))
                            throw new IOException("Placement ancestor link changed during target resolution.");
                        if (parentChanged || !Matches(before, ReadStat(current, buffer.Pointer)))
                            throw new PlacementParentChangedException("Placement ancestor parent changed during target resolution.");
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
                    if (!Matches(named, ReadStat(next, buffer.Pointer))) throw new IOException("Placement opened child differs from its named native observation.");
                    beforeParentCheck?.Invoke(ReadFinalPath(current), parts[index]);
                    token.ThrowIfCancellationRequested();
                    var parentChanged = !Matches(before, ReadStat(current, buffer.Pointer));
                    if (!TryReadStatAt(current, parts[index], buffer.Pointer, out var after, out _) || !Matches(named, after))
                        throw new IOException("Source placement changed during relative observation.");
                    if (parentChanged || !Matches(before, ReadStat(current, buffer.Pointer)))
                        throw new PlacementParentChangedException("Source placement parent changed during relative observation.");
                    current.Dispose(); current = next; next = null!;
                }
                finally { next?.Dispose(); }
            }
            return Finish([]);

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
