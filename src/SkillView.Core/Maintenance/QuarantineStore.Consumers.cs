using SkillView.Inventory;
using SkillView.Inventory.Models;

namespace SkillView.Maintenance;

internal sealed partial class QuarantineStore
{
    internal static void EnsureNoConsumers(string boundaryPath, InventorySnapshot inventory,
        CancellationToken token, InstalledSkill? selected = null)
    {
        var boundary = ResolveExposureAddress(boundaryPath, token);
        foreach (var root in inventory.ConfiguredRoots.Concat(inventory.ScannedRoots))
        {
            token.ThrowIfCancellationRequested();
            var entry = OwnershipPath.EntryAddress(Path.TrimEndingDirectorySeparator(root.Path))
                ?? throw new IOException("Configured discovery root entry cannot be inspected.");
            var destination = ResolveExposureAddress(root.Path, token);
            if (PathIdentity.IsInside(entry, boundary) || PathIdentity.IsInside(destination, boundary))
                throw new IOException("Relocation would move or activate a configured discovery root; review its placement first.");
        }
        foreach (var skill in inventory.Skills)
        {
            token.ThrowIfCancellationRequested();
            if (selected is not null && skill.InstallationId == selected.InstallationId
                && OwnershipPath.Same(skill.ResolvedPath, selected.ResolvedPath)) continue;
            foreach (var path in skill.Agents.Select(agent => agent.Path).Prepend(skill.ResolvedPath))
            {
                token.ThrowIfCancellationRequested();
                var entryAddress = OwnershipPath.EntryAddress(Path.TrimEndingDirectorySeparator(path))
                    ?? throw new IOException("Discovered exposure entry address cannot be inspected.");
                var address = ResolveExposureAddress(path, token);
                if (PathIdentity.IsInside(entryAddress, boundary) || PathIdentity.IsInside(boundary, entryAddress)
                    || PathIdentity.IsInside(address, boundary) || PathIdentity.IsInside(boundary, address))
                    throw new IOException("A discovered installation/exposure overlaps the relocated directory or original destination; review its consumers before proceeding.");
            }
        }
    }

    // A broken exposure's inventory address is the link itself. Resolve its
    // intended destination as well, including relative targets, missing suffixes
    // and aliases. This is non-destructive policy evidence, never move authority.
    internal static string ResolveExposureAddress(string path, CancellationToken token)
    {
        var full = Path.GetFullPath(path);
        for (var hop = 0; hop < 32; hop++)
        {
            token.ThrowIfCancellationRequested();
            if (OwnershipPath.CanonicalCandidate(full) is { } canonical) return canonical;
            var cursor = full;
            var suffix = new Stack<string>();
            var followed = false;
            for (var depth = 0; depth < 128; depth++)
            {
                token.ThrowIfCancellationRequested();
                if (PathResolver.IsSymlink(cursor))
                {
                    var rawTarget = new DirectoryInfo(cursor).LinkTarget ?? new FileInfo(cursor).LinkTarget
                        ?? throw new IOException("Discovered exposure target cannot be inspected.");
                    RefuseAmbiguousParentTraversal(cursor, rawTarget, token);
                    var target = new DirectoryInfo(cursor).ResolveLinkTarget(returnFinalTarget: false)
                        ?? new FileInfo(cursor).ResolveLinkTarget(returnFinalTarget: false)
                        ?? throw new IOException("Discovered exposure target cannot be inspected.");
                    full = suffix.Aggregate(Path.GetFullPath(target.FullName), Path.Combine);
                    followed = true;
                    break;
                }
                var parent = Path.GetDirectoryName(cursor);
                if (parent is null) break;
                suffix.Push(Path.GetFileName(cursor));
                cursor = parent;
            }
            if (!followed) throw new IOException("Discovered exposure address cannot be resolved; review inventory before relocation.");
        }
        throw new IOException("Discovered exposure link chain is cyclic or exceeds the policy limit.");
    }
    private static void RefuseAmbiguousParentTraversal(string link, string rawTarget, CancellationToken token)
    {
        // ResolveLinkTarget supplies a normalized FullName. A '..' immediately
        // after a link component can have different physical/lexical meanings.
        // Keep that uncertain observation out of relocation admission.
        var root = Path.GetPathRoot(rawTarget);
        var cursor = Path.IsPathFullyQualified(rawTarget) ? root! : Path.GetDirectoryName(link)!;
        var parts = (Path.IsPathFullyQualified(rawTarget) ? rawTarget[root!.Length..] : rawTarget)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            token.ThrowIfCancellationRequested();
            if (part == ".") continue;
            if (part == "..")
            {
                if (PathResolver.IsSymlink(cursor)) throw new IOException("Exposure target traverses a link followed by '..'; review its physical destination before relocation.");
                cursor = Path.GetDirectoryName(cursor) ?? cursor;
            }
            else cursor = Path.Combine(cursor, part);
        }
    }

}
