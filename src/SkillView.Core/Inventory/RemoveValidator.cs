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
        OwnerRecordNeedsPlan,
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
        internal SkillView.Subprocess.ProcessRunner? OwnershipRunner { get; init; }
        internal ImmutableArray<string> OwnershipPaths { get; init; } = [];
        internal ImmutableArray<ScanRoot> ManagedRoots { get; init; } = [];
    }

    /// Validate removal of `target`. `knownRoots` is the union of scan roots
    /// SkillView resolved for this session (project/user seeds + user-provided
    /// `--scan-root`s). `otherSkills` lets the canonical-copy-with-incoming-
    /// symlinks guard inspect siblings.
    public static RemoveValidation Validate(
        InstalledSkill target,
        IReadOnlyList<ScanRoot> knownRoots,
        IReadOnlyList<InstalledSkill> otherSkills,
        CancellationToken cancellationToken = default) =>
        ValidateCore(target, knownRoots, otherSkills, allowUnpinnedPreview: false,
            cancellationToken: cancellationToken);

    /// Performs the same non-mutating policy checks for a dry run. On a host
    /// where the native secure backend is unavailable, this may use managed
    /// path inspection for preview data; the returned validation deliberately
    /// contains no execution identity and therefore cannot authorize deletion.
    public static RemoveValidation ValidateForPreview(
        InstalledSkill target,
        IReadOnlyList<ScanRoot> knownRoots,
        IReadOnlyList<InstalledSkill> otherSkills,
        CancellationToken cancellationToken = default) =>
        ValidateCore(target, knownRoots, otherSkills, allowUnpinnedPreview: true,
            cancellationToken: cancellationToken);

    internal static RemoveValidation ValidateWithBackendAvailabilityForTests(
        InstalledSkill target,
        IReadOnlyList<ScanRoot> knownRoots,
        IReadOnlyList<InstalledSkill> otherSkills,
        bool allowUnpinnedPreview,
        bool secureBackendSupported,
        Action<string>? beforeManagedOwnershipForTests = null) =>
        ValidateCore(
            target,
            knownRoots,
            otherSkills,
            allowUnpinnedPreview,
            secureBackendSupported,
            beforeManagedOwnershipForTests: beforeManagedOwnershipForTests);

    private static RemoveValidation ValidateCore(
        InstalledSkill target,
        IReadOnlyList<ScanRoot> knownRoots,
        IReadOnlyList<InstalledSkill> otherSkills,
        bool allowUnpinnedPreview,
        bool? secureBackendSupportedOverride = null,
        CancellationToken cancellationToken = default,
        Action<string>? beforeManagedOwnershipForTests = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var errors = ImmutableArray.CreateBuilder<Error>();
        var warnings = ImmutableArray.CreateBuilder<Warning>();
        var incoming = ImmutableArray<string>.Empty;
        if (target.AllOwnershipClaims.Any() || !target.OwnershipDiagnostics.IsDefaultOrEmpty)
            errors.Add(new(ErrorKind.OwnerRecordNeedsPlan,
                "This installation has a contextual owner record. Use a coordinated maintenance plan before deleting its payload or links."));

        var targetPath = target.ResolvedPath;
        var resolved = targetPath;
        SecureFileIdentity? executionIdentity = null;
        SecureFileIdentity? rootIdentity = null;
        SecureDirectoryValidationSnapshot? directorySnapshot = null;
        string? previewCanonicalRoot = null;
        bool? previewHasSkillFile = null;
        bool? previewHasGitDirectory = null;

        // Rule 12.1.1: must be inside a known scan root before resolution.
        var matchedRootByRawPath = FindContainingRoot(target.ResolvedPath, knownRoots);
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
        if (matchedRootByRawPath is not null
            && PathKeysEqual(matchedRootByRawPath.Path, target.ResolvedPath))
        {
            errors.Add(new Error(ErrorKind.TargetIsScanRoot,
                $"'{target.ResolvedPath}' is itself a scan root"));
        }
        else if (matchedRootByRawPath is not null)
        {
            var secureBackendSupported = secureBackendSupportedOverride
                ?? SecureRemovalBackend.IsSupported;
            string? identityError = null;
            if (secureBackendSupported
                && SecureRemovalBackend.TryCaptureDirectoryValidationWithinRoot(
                    matchedRootByRawPath.Path,
                    target.ResolvedPath,
                    out var rootedSnapshot,
                    out identityError,
                    cancellationToken))
            {
                var capturedSnapshot = rootedSnapshot.Directory;
                directorySnapshot = capturedSnapshot;
                executionIdentity = capturedSnapshot.Identity;
                rootIdentity = rootedSnapshot.RootIdentity;
                resolved = capturedSnapshot.Identity.CanonicalPath;
            }
            else if (allowUnpinnedPreview
                && !secureBackendSupported
                && TryCaptureManagedPreview(
                    matchedRootByRawPath.Path,
                    target.ResolvedPath,
                    out resolved,
                    out previewCanonicalRoot,
                    out var managedHasSkillFile,
                    out var managedHasGitDirectory,
                    out identityError))
            {
                // Preview-only inspection intentionally leaves
                // ExecutionIdentity null. RemoveService refuses every real
                // deletion without the native identity contract.
                previewHasSkillFile = managedHasSkillFile;
                previewHasGitDirectory = managedHasGitDirectory;
            }
            else
            {
                identityError ??= SecureRemovalBackend.UnsupportedReason
                    ?? "secure removal is not supported on this operating system";
                errors.Add(new Error(
                    ErrorKind.FilesystemIdentityUnavailable,
                    $"could not pin the selected filesystem object: {identityError}"));
            }
        }

        // Rule 12.1.2: resolved path must still be inside a known scan root.
        var canonicalRoot = rootIdentity?.CanonicalPath ?? previewCanonicalRoot;
        if (canonicalRoot is not null
            && !PathResolver.IsInside(resolved, canonicalRoot))
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
        if (directorySnapshot is { HasSkillFile: false }
            || previewHasSkillFile is false)
        {
            errors.Add(new Error(ErrorKind.NotASkillDirectory,
                $"'{resolved}' does not contain {LocalSkillScanner.SkillFileName} or recognizable skill metadata"));
        }

        // Rule 12.1.5: reject in-place clones.
        if (directorySnapshot is { HasGitDirectory: true }
            || previewHasGitDirectory is true)
        {
            errors.Add(new Error(ErrorKind.ContainsGitDirectory,
                $"'{resolved}' contains a .git metadata entry — looks like a repository or linked worktree"));
        }

        // Never-delete: the scan root itself.
        foreach (var root in knownRoots)
        {
            if (PathKeysEqual(root.Path, target.ResolvedPath)
                || (canonicalRoot is not null
                    && PathKeysEqual(canonicalRoot, resolved))
                || (TryCanonicalizeForComparison(root.Path, out var comparisonRoot)
                    && PathKeysEqual(comparisonRoot, resolved))
                || (allowUnpinnedPreview
                    && TryCanonicalizeManaged(root.Path, out comparisonRoot)
                    && PathKeysEqual(comparisonRoot, resolved)))
            {
                if (!errors.Any(error => error.Kind == ErrorKind.TargetIsScanRoot))
                {
                    errors.Add(new Error(ErrorKind.TargetIsScanRoot,
                        $"'{target.ResolvedPath}' is itself a scan root"));
                }
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

        beforeManagedOwnershipForTests?.Invoke(resolved);
        var ownershipRunner = ManagedSkillDiscovery.CaptureContext(target.OwnershipRunner);
        var ownershipPaths = target.Agents.Select(agent => agent.Path).Append(target.ResolvedPath).Append(resolved).Distinct(StringComparer.Ordinal).ToImmutableArray();
        CheckManagedOwnership(ownershipPaths, knownRoots, errors, cancellationToken, ownershipRunner);
        return new RemoveValidation(
            errors.ToImmutable(),
            warnings.ToImmutable(),
            resolved,
            incoming)
        {
            ExecutionIdentity = executionIdentity,
            OwnershipRunner = ownershipRunner,
            OwnershipPaths = ownershipPaths,
            ManagedRoots = knownRoots.Where(root => root.Managed).ToImmutableArray(),
        };
    }

    internal static RemoveValidation ValidateEmptyDirectory(
        string path,
        IReadOnlyList<ScanRoot> knownRoots,
        CancellationToken cancellationToken = default, SkillView.Subprocess.ProcessRunner? ownershipRunner = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var errors = ImmutableArray.CreateBuilder<Error>();
        var fullPath = Path.GetFullPath(path);
        var resolved = fullPath;
        SecureFileIdentity? executionIdentity = null;
        SecureFileIdentity? rootIdentity = null;
        SecureDirectoryValidationSnapshot? directorySnapshot = null;
        var matchedRootByRawPath = FindContainingRoot(fullPath, knownRoots);
        if (matchedRootByRawPath is null)
        {
            errors.Add(new Error(ErrorKind.OutsideKnownRoots,
                $"'{fullPath}' not inside any scan root"));
        }

        if (matchedRootByRawPath is not null
            && PathKeysEqual(matchedRootByRawPath.Path, fullPath))
        {
            errors.Add(new Error(ErrorKind.TargetIsScanRoot,
                $"'{fullPath}' is itself a scan root"));
        }
        else if (matchedRootByRawPath is not null)
        {
            if (SecureRemovalBackend.TryCaptureDirectoryValidationWithinRoot(
                    matchedRootByRawPath.Path,
                    fullPath,
                    out var rootedSnapshot,
                    out var identityError,
                    cancellationToken))
            {
                var capturedSnapshot = rootedSnapshot.Directory;
                directorySnapshot = capturedSnapshot;
                executionIdentity = capturedSnapshot.Identity;
                rootIdentity = rootedSnapshot.RootIdentity;
                resolved = capturedSnapshot.Identity.CanonicalPath;
                if (!capturedSnapshot.Identity.IsDirectory
                    || capturedSnapshot.Identity.IsReparsePoint)
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

        if (executionIdentity is not null
            && (rootIdentity is null
                || !PathResolver.IsInside(
                    resolved,
                    rootIdentity.Value.CanonicalPath)))
        {
            errors.Add(new Error(ErrorKind.ResolvedOutsideKnownRoots,
                $"resolved path '{resolved}' is not inside any known scan root"));
        }

        foreach (var root in knownRoots)
        {
            if (PathKeysEqual(root.Path, fullPath)
                || (rootIdentity is not null
                    && PathKeysEqual(rootIdentity.Value.CanonicalPath, resolved))
                || (TryCanonicalizeForComparison(root.Path, out var canonicalRoot)
                    && PathKeysEqual(canonicalRoot, resolved)))
            {
                if (!errors.Any(error => error.Kind == ErrorKind.TargetIsScanRoot))
                {
                    errors.Add(new Error(ErrorKind.TargetIsScanRoot,
                        $"'{fullPath}' is itself a scan root"));
                }
                break;
            }
        }

        if (directorySnapshot is { HasGitDirectory: true })
        {
            errors.Add(new Error(ErrorKind.ContainsGitDirectory,
                $"'{resolved}' contains .git"));
        }

        if (directorySnapshot is { IsEmpty: false })
        {
            errors.Add(new Error(ErrorKind.NotASkillDirectory,
                $"'{resolved}' is no longer empty"));
        }

        ownershipRunner = ManagedSkillDiscovery.CaptureContext(ownershipRunner);
        CheckManagedOwnership([fullPath, resolved], knownRoots, errors, cancellationToken, ownershipRunner);
        return new RemoveValidation(
            errors.ToImmutable(),
            ImmutableArray<Warning>.Empty,
            resolved,
            ImmutableArray<string>.Empty)
        {
            ExecutionIdentity = executionIdentity,
            RequiresEmptyDirectory = true,
            OwnershipRunner = ownershipRunner,
            OwnershipPaths = [fullPath, resolved],
            ManagedRoots = knownRoots.Where(root => root.Managed).ToImmutableArray(),
        };
    }

    internal static RemoveValidation ValidateBrokenSymlink(
        string path,
        IReadOnlyList<ScanRoot> knownRoots, CancellationToken cancellationToken = default,
        SkillView.Subprocess.ProcessRunner? ownershipRunner = null) =>
        ValidateSymlink(path, knownRoots, requireBroken: true, cancellationToken, ownershipRunner);

    internal static RemoveValidation ValidateSymlink(
        string path,
        IReadOnlyList<ScanRoot> knownRoots,
        bool requireBroken = false, CancellationToken cancellationToken = default,
        SkillView.Subprocess.ProcessRunner? ownershipRunner = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var errors = ImmutableArray.CreateBuilder<Error>();
        var fullPath = Path.GetFullPath(path);
        var resolved = fullPath;
        SecureLinkIdentity? executionLinkIdentity = null;
        SecureFileIdentity? rootIdentity = null;
        var matchedRootByRawPath = FindContainingRoot(fullPath, knownRoots);
        if (matchedRootByRawPath is null)
        {
            errors.Add(new Error(ErrorKind.OutsideKnownRoots,
                $"'{fullPath}' not inside any scan root"));
        }
        if (matchedRootByRawPath is not null
            && PathKeysEqual(matchedRootByRawPath.Path, fullPath))
        {
            errors.Add(new Error(ErrorKind.TargetIsScanRoot,
                $"'{fullPath}' is itself a scan root"));
        }
        else if (matchedRootByRawPath is not null)
        {
            if (SecureRemovalBackend.TryCaptureLinkValidationWithinRoot(
                    matchedRootByRawPath.Path,
                    fullPath,
                    out var rootedSnapshot,
                    out var identityError))
            {
                var capturedSnapshot = rootedSnapshot.Link;
                executionLinkIdentity = capturedSnapshot.Identity;
                rootIdentity = rootedSnapshot.RootIdentity;
                resolved = capturedSnapshot.Identity.CanonicalPath;
                if (requireBroken && !capturedSnapshot.IsBroken)
                {
                    errors.Add(new Error(ErrorKind.NotASkillDirectory,
                        $"'{fullPath}' is no longer broken"));
                }
            }
            else
            {
                errors.Add(new Error(ErrorKind.FilesystemIdentityUnavailable,
                    $"could not pin the selected link and its parent: {identityError}"));
            }
        }

        if (executionLinkIdentity is not null
            && (rootIdentity is null
                || !PathResolver.IsInside(
                    resolved,
                    rootIdentity.Value.CanonicalPath)))
        {
            errors.Add(new Error(ErrorKind.ResolvedOutsideKnownRoots,
                $"resolved link path '{resolved}' is not inside any known scan root"));
        }

        foreach (var root in knownRoots)
        {
            if (PathKeysEqual(root.Path, fullPath)
                || (rootIdentity is not null
                    && PathKeysEqual(rootIdentity.Value.CanonicalPath, resolved))
                || (TryCanonicalizeForComparison(root.Path, out var canonicalRoot)
                    && PathKeysEqual(canonicalRoot, resolved)))
            {
                if (!errors.Any(error => error.Kind == ErrorKind.TargetIsScanRoot))
                {
                    errors.Add(new Error(ErrorKind.TargetIsScanRoot,
                        $"'{fullPath}' is itself a scan root"));
                }
                break;
            }
        }

        ownershipRunner = ManagedSkillDiscovery.CaptureContext(ownershipRunner);
        CheckManagedOwnership([fullPath, resolved], knownRoots, errors, cancellationToken, ownershipRunner);
        return new RemoveValidation(
            errors.ToImmutable(),
            ImmutableArray<Warning>.Empty,
            resolved,
            ImmutableArray<string>.Empty)
        {
            ExecutionLinkIdentity = executionLinkIdentity,
            RemovesLinkOnly = true,
            OwnershipRunner = ownershipRunner,
            OwnershipPaths = [fullPath, resolved],
            ManagedRoots = knownRoots.Where(root => root.Managed).ToImmutableArray(),
        };
    }

    private static void CheckManagedOwnership(IEnumerable<string> selected, IReadOnlyList<ScanRoot> roots,
        ImmutableArray<Error>.Builder errors, CancellationToken token, SkillView.Subprocess.ProcessRunner? runner = null)
    {
        if (errors.Any(error => error.Kind == ErrorKind.OwnerRecordNeedsPlan)) return;
        try
        {
            var paths = selected.ToArray();
            ManagedSkillDiscovery.RefuseKnownRoots(roots, paths, token);
            ManagedSkillDiscovery.RefusePaths(paths, token, runner);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { errors.Add(new(ErrorKind.OwnerRecordNeedsPlan, ex.Message)); }
    }

    private static ScanRoot? FindContainingRoot(
        string path,
        IReadOnlyList<ScanRoot> roots)
    {
        foreach (var root in roots)
        {
            if (PathResolver.IsInside(path, root.Path)) return root;
        }
        return null;
    }

    private static bool TryCaptureManagedPreview(
        string rootPath,
        string path,
        out string resolvedPath,
        out string canonicalRoot,
        out bool hasSkillFile,
        out bool hasGitDirectory,
        out string? error)
    {
        resolvedPath = string.Empty;
        canonicalRoot = string.Empty;
        hasSkillFile = false;
        hasGitDirectory = false;
        error = null;
        try
        {
            if (!TryCanonicalizeManaged(rootPath, out canonicalRoot))
            {
                error = $"could not resolve scan root '{rootPath}' for preview";
                return false;
            }
            if (!TryCanonicalizeManaged(path, out resolvedPath)
                || !Directory.Exists(resolvedPath))
            {
                error = $"could not resolve directory '{path}' for preview";
                return false;
            }

            hasSkillFile = File.Exists(
                Path.Combine(resolvedPath, LocalSkillScanner.SkillFileName));
            try { _ = File.GetAttributes(Path.Combine(resolvedPath, ".git")); hasGitDirectory = true; }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryCanonicalizeManaged(
        string path,
        out string canonicalPath)
    {
        canonicalPath = PathResolver.Resolve(path) ?? string.Empty;
        return canonicalPath.Length > 0;
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
