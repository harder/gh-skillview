using System.Collections.Immutable;
using System.IO;
using SkillView.Inventory.Models;

namespace SkillView.Inventory;

/// Safety validator for remove operations. This is policy, not
/// execution — it reports whether a removal is `Allowed` and emits any
/// warnings that must be re-confirmed before proceeding. Execution lives in
/// `RemoveService`.
public static class RemoveValidator
{
    /// Hard-stop error codes. Presence of any `Error` in a `RemoveValidation`
    /// means the operation is refused outright.
    public enum ErrorKind
    {
        OutsideKnownRoots,
        ResolvedOutsideKnownRoots,
        AncestorSymlinkEscapesRoot,
        NotASkillDirectory,
        ContainsGitDirectory,
        TargetIsScanRoot,
    }

    /// Soft warnings requiring a second confirmation before execution.
    public enum WarningKind
    {
        TrackedByParentGitRepo,
        HasIncomingSymlinks,
        TargetIsSymlinkWithOtherIncoming,
    }

    public sealed record Error(ErrorKind Kind, string Detail);
    public sealed record Warning(WarningKind Kind, string Detail);

    public sealed record RemoveValidation(
        ImmutableArray<Error> Errors,
        ImmutableArray<Warning> Warnings,
        string ResolvedPath,
        ImmutableArray<string> IncomingSymlinkPaths)
    {
        public bool Allowed => Errors.IsDefaultOrEmpty || Errors.Length == 0;
        public bool RequiresSecondConfirm => Warnings.Length > 0;
    }

    /// Validate removal of `target`. `knownRoots` is the union of scan roots
    /// SkillView resolved for this session (project/user seeds + user-provided
    /// `--scan-root`s). `otherSkills` lets the canonical-copy-with-incoming-
    /// symlinks guard inspect siblings.
    public static RemoveValidation Validate(
        InstalledSkill target,
        IReadOnlyList<ScanRoot> knownRoots,
        IReadOnlyList<InstalledSkill> otherSkills)
    {
        var errors = ImmutableArray.CreateBuilder<Error>();
        var warnings = ImmutableArray.CreateBuilder<Warning>();
        var incoming = ImmutableArray<string>.Empty;

        var targetPath = target.ResolvedPath;
        var resolved = PathResolver.Resolve(targetPath) ?? targetPath;

        // Rule 12.1.1: must be inside a known scan root before resolution.
        var matchedRootByRawPath = FindContainingRoot(target.ResolvedPath, knownRoots);
        if (matchedRootByRawPath is null)
        {
            errors.Add(new Error(ErrorKind.OutsideKnownRoots,
                $"target '{target.ResolvedPath}' is not inside any known scan root"));
        }

        // Rule 12.1.2: resolved path must still be inside a known scan root.
        var matchedRootByResolved = FindContainingRoot(resolved, knownRoots);
        if (matchedRootByResolved is null)
        {
            errors.Add(new Error(ErrorKind.ResolvedOutsideKnownRoots,
                $"resolved path '{resolved}' is not inside any known scan root"));
        }

        // Rule 12.1.3: no ancestor on the path from the scan root to the target
        // may be a symlink that escapes outside the scan root.
        if (matchedRootByRawPath is not null &&
            HasEscapingAncestorSymlink(matchedRootByRawPath.Path, target.ResolvedPath, out var escapeDetail))
        {
            errors.Add(new Error(ErrorKind.AncestorSymlinkEscapesRoot, escapeDetail));
        }

        // Rule 12.1.4: target must look like a skill install.
        if (!LooksLikeSkill(resolved))
        {
            errors.Add(new Error(ErrorKind.NotASkillDirectory,
                $"'{resolved}' does not contain {LocalSkillScanner.SkillFileName} or recognizable skill metadata"));
        }

        // Rule 12.1.5: reject in-place clones.
        if (Directory.Exists(Path.Combine(resolved, ".git")))
        {
            errors.Add(new Error(ErrorKind.ContainsGitDirectory,
                $"'{resolved}' contains a .git directory — looks like an in-place clone"));
        }

        // Never-delete: the scan root itself.
        foreach (var root in knownRoots)
        {
            if (PathKeysEqual(root.Path, target.ResolvedPath) ||
                PathKeysEqual(root.Path, resolved))
            {
                errors.Add(new Error(ErrorKind.TargetIsScanRoot,
                    $"'{target.ResolvedPath}' is itself a scan root"));
                break;
            }
        }

        // Warning: target is tracked by a parent git working tree.
        var gitRoot = ScanRootResolver.FindGitRoot(resolved);
        if (gitRoot is not null)
        {
            warnings.Add(new Warning(WarningKind.TrackedByParentGitRepo,
                $"'{resolved}' is inside git working tree at '{gitRoot}'"));
        }

        // Warnings around canonical copies and symlinks. Collect paths from
        // sibling skills whose resolved path matches `resolved`.
        var incomingBuilder = ImmutableArray.CreateBuilder<string>();
        foreach (var other in otherSkills)
        {
            if (ReferenceEquals(other, target)) continue;
            foreach (var agent in other.Agents)
            {
                if (!agent.IsSymlink) continue;
                var linkResolved = PathResolver.Resolve(agent.Path);
                if (linkResolved is null) continue;
                if (PathKeysEqual(linkResolved, resolved))
                {
                    incomingBuilder.Add(agent.Path);
                }
            }
        }
        // Also consider target's own agent memberships, in case the selected
        // record IS the canonical copy and carries sibling symlinks.
        foreach (var agent in target.Agents)
        {
            if (!agent.IsSymlink) continue;
            var linkResolved = PathResolver.Resolve(agent.Path);
            if (linkResolved is null) continue;
            if (PathKeysEqual(linkResolved, resolved) &&
                !PathKeysEqual(agent.Path, resolved))
            {
                incomingBuilder.Add(agent.Path);
            }
        }
        incoming = incomingBuilder.ToImmutable();

        if (incoming.Length > 0)
        {
            if (target.IsSymlinked && !PathKeysEqual(target.ResolvedPath, resolved))
            {
                warnings.Add(new Warning(WarningKind.TargetIsSymlinkWithOtherIncoming,
                    $"canonical copy at '{resolved}' still has {incoming.Length} other incoming symlink(s)"));
            }
            else
            {
                warnings.Add(new Warning(WarningKind.HasIncomingSymlinks,
                    $"{incoming.Length} other install(s) symlink into '{resolved}'"));
            }
        }

        return new RemoveValidation(
            errors.ToImmutable(),
            warnings.ToImmutable(),
            resolved,
            incoming);
    }

    private static bool LooksLikeSkill(string dir)
    {
        if (!Directory.Exists(dir)) return false;
        return File.Exists(Path.Combine(dir, LocalSkillScanner.SkillFileName));
    }

    private static ScanRoot? FindContainingRoot(string path, IReadOnlyList<ScanRoot> roots)
    {
        foreach (var root in roots)
        {
            if (PathResolver.IsInside(path, root.Path)) return root;
        }
        return null;
    }

    /// True if any directory on the chain from `root` down to `target`
    /// (exclusive of `root`, inclusive of `target`) is a symlink whose
    /// resolved destination is NOT inside `root`.
    private static bool HasEscapingAncestorSymlink(string root, string target, out string detail)
    {
        detail = string.Empty;
        var rootKey = PathResolver.Normalize(root);
        var cursor = Path.GetFullPath(target);
        var rootFull = Path.GetFullPath(root);

        while (!string.IsNullOrEmpty(cursor) &&
               !string.Equals(cursor, rootFull, StringComparison.Ordinal))
        {
            if (PathResolver.IsSymlink(cursor))
            {
                var resolved = PathResolver.Resolve(cursor);
                if (resolved is null)
                {
                    detail = $"ancestor symlink '{cursor}' is broken";
                    return true;
                }
                if (!PathResolver.IsInside(resolved, root))
                {
                    detail = $"ancestor symlink '{cursor}' resolves to '{resolved}' outside root '{root}'";
                    return true;
                }
            }
            var parent = Path.GetDirectoryName(cursor);
            if (parent is null || string.Equals(parent, cursor, StringComparison.Ordinal)) break;
            cursor = parent;
        }
        return false;
    }

    private static bool PathKeysEqual(string a, string b)
    {
        return string.Equals(
            PathResolver.Normalize(a),
            PathResolver.Normalize(b),
            StringComparison.Ordinal);
    }
}
