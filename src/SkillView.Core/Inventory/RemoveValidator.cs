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
        FilesystemIdentityUnavailable,
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
        internal SecureFileIdentity? ExecutionIdentity { get; init; }
        internal SecureLinkIdentity? ExecutionLinkIdentity { get; init; }
        internal bool RequiresEmptyDirectory { get; init; }
        internal bool RemovesLinkOnly { get; init; }
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
        SecureFileIdentity? executionIdentity = null;

        // Rule 12.1.1: must be inside a known scan root before resolution.
        var matchedRootByRawPath = FindContainingRoot(
            target.ResolvedPath,
            knownRoots,
            canonicalizeRoots: false);
        if (matchedRootByRawPath is null)
        {
            errors.Add(new Error(ErrorKind.OutsideKnownRoots,
                $"target '{target.ResolvedPath}' is not inside any known scan root"));
        }

        // Pin the actual object before applying policy to its canonical path.
        // On Unix, realpath can differ from the earlier best-effort resolution
        // if an ancestor is retargeted between those operations. The captured
        // canonical path is also the only path native execution will use, so
        // every remaining policy rule must validate that exact address.
        if (matchedRootByRawPath is not null)
        {
            if (SecureRemovalBackend.TryCaptureIdentity(
                    resolved,
                    out var capturedIdentity,
                    out var identityError))
            {
                executionIdentity = capturedIdentity;
                resolved = capturedIdentity.CanonicalPath;
            }
            else
            {
                errors.Add(new Error(
                    ErrorKind.FilesystemIdentityUnavailable,
                    $"could not pin the selected filesystem object: {identityError}"));
            }
        }

        // Rule 12.1.2: resolved path must still be inside a known scan root.
        var matchedRootByResolved = FindContainingRoot(
            resolved,
            knownRoots,
            canonicalizeRoots: true);
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
                PathKeysEqual(CanonicalizeForComparison(root.Path), resolved))
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
                if (PathKeysEqual(CanonicalizeForComparison(linkResolved), resolved))
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
            if (PathKeysEqual(CanonicalizeForComparison(linkResolved), resolved) &&
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
            incoming)
        {
            ExecutionIdentity = executionIdentity,
        };
    }

    internal static RemoveValidation ValidateEmptyDirectory(
        string path,
        IReadOnlyList<ScanRoot> knownRoots)
    {
        var errors = ImmutableArray.CreateBuilder<Error>();
        var fullPath = Path.GetFullPath(path);
        var resolved = fullPath;
        SecureFileIdentity? executionIdentity = null;
        var matchedRootByRawPath = FindContainingRoot(
            fullPath,
            knownRoots,
            canonicalizeRoots: false);
        if (matchedRootByRawPath is null)
        {
            errors.Add(new Error(ErrorKind.OutsideKnownRoots,
                $"'{fullPath}' not inside any scan root"));
        }

        if (PathResolver.IsSymlink(fullPath))
        {
            errors.Add(new Error(ErrorKind.NotASkillDirectory,
                $"'{fullPath}' is now a symlink"));
        }
        else if (!Directory.Exists(fullPath))
        {
            errors.Add(new Error(ErrorKind.NotASkillDirectory,
                $"'{fullPath}' is no longer a directory"));
        }
        else if (matchedRootByRawPath is not null)
        {
            if (SecureRemovalBackend.TryCaptureIdentity(
                    fullPath,
                    out var capturedIdentity,
                    out var identityError))
            {
                executionIdentity = capturedIdentity;
                resolved = capturedIdentity.CanonicalPath;
                if (!capturedIdentity.IsDirectory || capturedIdentity.IsReparsePoint)
                {
                    errors.Add(new Error(ErrorKind.NotASkillDirectory,
                        $"'{resolved}' is no longer a non-link directory"));
                }
            }
            else
            {
                errors.Add(new Error(ErrorKind.FilesystemIdentityUnavailable,
                    $"could not pin the selected filesystem object: {identityError}"));
            }
        }

        if (FindContainingRoot(resolved, knownRoots, canonicalizeRoots: true) is null)
        {
            errors.Add(new Error(ErrorKind.ResolvedOutsideKnownRoots,
                $"resolved path '{resolved}' is not inside any known scan root"));
        }

        foreach (var root in knownRoots)
        {
            if (PathKeysEqual(root.Path, fullPath)
                || PathKeysEqual(CanonicalizeForComparison(root.Path), resolved))
            {
                errors.Add(new Error(ErrorKind.TargetIsScanRoot,
                    $"'{fullPath}' is itself a scan root"));
                break;
            }
        }

        if (Directory.Exists(Path.Combine(resolved, ".git")))
        {
            errors.Add(new Error(ErrorKind.ContainsGitDirectory,
                $"'{resolved}' contains .git"));
        }

        if (executionIdentity is not null)
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(resolved).Any())
                {
                    errors.Add(new Error(ErrorKind.NotASkillDirectory,
                        $"'{resolved}' is no longer empty"));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add(new Error(ErrorKind.NotASkillDirectory,
                    $"'{resolved}' could not be inspected: {ex.Message}"));
            }
        }

        return new RemoveValidation(
            errors.ToImmutable(),
            ImmutableArray<Warning>.Empty,
            resolved,
            ImmutableArray<string>.Empty)
        {
            ExecutionIdentity = executionIdentity,
            RequiresEmptyDirectory = true,
        };
    }

    internal static RemoveValidation ValidateBrokenSymlink(
        string path,
        IReadOnlyList<ScanRoot> knownRoots) =>
        ValidateSymlink(path, knownRoots, requireBroken: true);

    internal static RemoveValidation ValidateSymlink(
        string path,
        IReadOnlyList<ScanRoot> knownRoots,
        bool requireBroken = false)
    {
        var errors = ImmutableArray.CreateBuilder<Error>();
        var fullPath = Path.GetFullPath(path);
        var resolved = fullPath;
        SecureLinkIdentity? executionLinkIdentity = null;
        var matchedRootByRawPath = FindContainingRoot(
            fullPath,
            knownRoots,
            canonicalizeRoots: false);
        if (matchedRootByRawPath is null)
        {
            errors.Add(new Error(ErrorKind.OutsideKnownRoots,
                $"'{fullPath}' not inside any scan root"));
        }
        if (!PathResolver.IsSymlink(fullPath))
        {
            errors.Add(new Error(ErrorKind.NotASkillDirectory,
                $"'{fullPath}' is no longer a symlink"));
        }
        else if (requireBroken && PathResolver.Resolve(fullPath) is not null)
        {
            errors.Add(new Error(ErrorKind.NotASkillDirectory,
                $"'{fullPath}' is no longer broken"));
        }
        else if (matchedRootByRawPath is not null)
        {
            if (SecureRemovalBackend.TryCaptureLinkIdentity(
                    fullPath,
                    out var capturedIdentity,
                    out var identityError))
            {
                executionLinkIdentity = capturedIdentity;
                resolved = capturedIdentity.CanonicalPath;
            }
            else
            {
                errors.Add(new Error(ErrorKind.FilesystemIdentityUnavailable,
                    $"could not pin the selected link and its parent: {identityError}"));
            }
        }

        if (executionLinkIdentity is not null
            && FindContainingRoot(resolved, knownRoots, canonicalizeRoots: true) is null)
        {
            errors.Add(new Error(ErrorKind.ResolvedOutsideKnownRoots,
                $"resolved link path '{resolved}' is not inside any known scan root"));
        }

        foreach (var root in knownRoots)
        {
            if (PathKeysEqual(root.Path, fullPath)
                || (TryCanonicalizeForComparison(root.Path, out var canonicalRoot)
                    && PathKeysEqual(canonicalRoot, resolved)))
            {
                errors.Add(new Error(ErrorKind.TargetIsScanRoot,
                    $"'{fullPath}' is itself a scan root"));
                break;
            }
        }

        return new RemoveValidation(
            errors.ToImmutable(),
            ImmutableArray<Warning>.Empty,
            resolved,
            ImmutableArray<string>.Empty)
        {
            ExecutionLinkIdentity = executionLinkIdentity,
            RemovesLinkOnly = true,
        };
    }

    private static bool LooksLikeSkill(string dir)
    {
        if (!Directory.Exists(dir)) return false;
        return File.Exists(Path.Combine(dir, LocalSkillScanner.SkillFileName));
    }

    private static ScanRoot? FindContainingRoot(
        string path,
        IReadOnlyList<ScanRoot> roots,
        bool canonicalizeRoots)
    {
        foreach (var root in roots)
        {
            var comparisonRoot = root.Path;
            if (canonicalizeRoots
                && !TryCanonicalizeForComparison(root.Path, out comparisonRoot))
            {
                continue;
            }
            if (PathResolver.IsInside(path, comparisonRoot)) return root;
        }
        return null;
    }

    private static string CanonicalizeForComparison(string path) =>
        TryCanonicalizeForComparison(path, out var canonicalPath)
            ? canonicalPath
            : path;

    private static bool TryCanonicalizeForComparison(
        string path,
        out string canonicalPath) =>
        SecureRemovalBackend.TryCanonicalizePath(path, out canonicalPath, out _);

    /// True if any directory on the chain from `root` down to `target`
    /// (exclusive of `root`, inclusive of `target`) is a symlink whose
    /// resolved destination is NOT inside `root`.
    private static bool HasEscapingAncestorSymlink(string root, string target, out string detail)
    {
        detail = string.Empty;
        var cursor = Path.GetFullPath(target);
        var rootFull = Path.GetFullPath(root);

        while (!string.IsNullOrEmpty(cursor) &&
               !PathIdentity.Equals(cursor, rootFull))
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
            if (parent is null || PathIdentity.Equals(parent, cursor)) break;
            cursor = parent;
        }
        return false;
    }

    private static bool PathKeysEqual(string a, string b)
    {
        return PathIdentity.Equals(a, b);
    }
}
