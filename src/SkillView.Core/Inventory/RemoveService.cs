using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using SkillView.Inventory.Models;
using SkillView.Logging;

namespace SkillView.Inventory;

/// Executes a validated removal without composing file operations through a
/// shell. Supported platforms use opened-directory/native-handle traversal;
/// partial failures are logged and survivable rather than aborting at the
/// first one.
public sealed class RemoveService
{
    private const int MaxTraversalDepth = 256;
    internal const int MaxRetainedErrors = 128;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);
    private readonly Logger _logger;
    private readonly Action<string>? _entryObservedForTests;
    private readonly Action<string, bool>? _entryDeletingForTests;

    public RemoveService(Logger logger) : this(
        logger,
        entryObservedForTests: null,
        entryDeletingForTests: null)
    { }

    internal RemoveService(Logger logger, Action<string>? entryObservedForTests)
        : this(logger, entryObservedForTests, entryDeletingForTests: null)
    { }

    internal RemoveService(
        Logger logger,
        Action<string>? entryObservedForTests,
        Action<string, bool>? entryDeletingForTests)
    {
        _logger = logger;
        _entryObservedForTests = entryObservedForTests;
        _entryDeletingForTests = entryDeletingForTests;
    }

    public sealed record Options(bool DryRun = false);

    public sealed record RemoveProgress(
        int TargetsProcessed,
        int TargetsDeleted,
        int FilesProcessed,
        int DirectoriesProcessed,
        int Errors,
        string CurrentPath,
        bool IsCompleted,
        bool IsCanceled);

    public sealed record RemoveReport(
        bool Succeeded,
        string ResolvedPath,
        int FilesDeleted,
        int DirectoriesDeleted,
        ImmutableArray<string> Errors,
        bool DryRun)
    {
        public int ErrorCount { get; init; } = Errors.Length;
        public bool IsCanceled { get; init; }

        public static RemoveReport Refused(string resolved, string reason) => new(
            Succeeded: false,
            ResolvedPath: resolved,
            FilesDeleted: 0,
            DirectoriesDeleted: 0,
            Errors: ImmutableArray.Create(reason),
            DryRun: false);
    }

    public sealed record BatchRemoveReport(
        bool Succeeded,
        int TargetsDeleted,
        int FilesDeleted,
        int DirectoriesDeleted,
        ImmutableArray<string> Errors,
        bool DryRun)
    {
        public int ErrorCount { get; init; } = Errors.Length;
        public bool IsCanceled { get; init; }

        public static BatchRemoveReport FromSingle(RemoveReport report, int targetsDeleted) => new(
            Succeeded: report.Succeeded,
            TargetsDeleted: targetsDeleted,
            FilesDeleted: report.FilesDeleted,
            DirectoriesDeleted: report.DirectoriesDeleted,
            Errors: report.Errors,
            DryRun: report.DryRun)
        {
            ErrorCount = report.ErrorCount,
            IsCanceled = report.IsCanceled,
        };
    }

    /// Removes a previously-validated skill directory. Callers MUST run
    /// `RemoveValidator.Validate` first and honor its errors and warnings;
    /// this method does NOT re-run the policy rules. Execution still rechecks
    /// that the selected filesystem object still has the identity captured by
    /// validation. Native traversal holds directory handles while enumerating
    /// and deleting children, so replacing an ancestor pathname cannot redirect
    /// recursion into another tree.
    public RemoveReport Remove(
        RemoveValidator.RemoveValidation validation,
        Options? options = null,
        CancellationToken cancellationToken = default,
        IProgress<RemoveProgress>? progress = null)
    {
        options ??= new Options();
        var target = Path.GetFullPath(validation.ResolvedPath);
        var progressTracker = new ProgressTracker(progress, _logger);
        progressTracker.Publish(0, 0, 0, 0, target, force: true);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            progressTracker.Publish(0, 0, 0, 0, target, force: true, isCanceled: true);
            throw;
        }

        if (!validation.Allowed)
        {
            var reason = string.Join("; ", validation.Errors.Select(e => $"{e.Kind}: {e.Detail}"));
            _logger.Error("remove", $"refused: {reason}");
            progressTracker.Publish(1, 0, 0, 1, target,
                force: true, isCompleted: true, targetsDeleted: 0);
            return RemoveReport.Refused(validation.ResolvedPath, reason);
        }

        if (!options.DryRun
            && validation.ExecutionIdentity is null
            && !validation.RemovesLinkOnly)
        {
            const string reason = "real directory removal requires a pinned filesystem identity";
            _logger.Error("remove", $"refused: {reason}: {target}");
            progressTracker.Publish(1, 0, 0, 1, target,
                force: true, isCompleted: true, targetsDeleted: 0);
            return RemoveReport.Refused(target, reason);
        }

        // A normal skill validation pins the selected directory identity. Route
        // that operation through the native backend before inspecting the
        // current pathname: a post-validation replacement with a symlink must
        // be rejected as an identity change, never handled as a link cleanup.
        if (!options.DryRun
            && SecureRemovalBackend.IsSupported
            && validation.ExecutionIdentity is not null)
        {
            return RemoveWithSecureBackend(
                validation.ExecutionIdentity.Value,
                validation.RequiresEmptyDirectory,
                target,
                cancellationToken,
                progressTracker);
        }

        if (PathResolver.IsSymlink(target))
        {
            if (options.DryRun)
            {
                _logger.Info("remove.dryrun", $"would remove symlink {target}");
                progressTracker.Publish(1, 1, 0, 0, target, force: true, isCompleted: true);
                return new RemoveReport(true, target, 1, 0, ImmutableArray<string>.Empty, DryRun: true);
            }

            try
            {
                if (SecureRemovalBackend.IsSupported)
                {
                    var linkErrors = new FailureCollector();
                    SecureRemovalBackend.RemoveLink(
                        target,
                        (_, _) => { },
                        (path, detail) => RecordFailure(path, detail, linkErrors),
                        cancellationToken);
                    if (linkErrors.Count > 0)
                    {
                        progressTracker.Publish(1, 0, 0, linkErrors.Count, target,
                            force: true, isCompleted: true);
                        return new RemoveReport(false, target, 0, 0,
                            linkErrors.ToImmutable(), DryRun: false)
                        {
                            ErrorCount = linkErrors.Count,
                        };
                    }
                }
                else
                {
                    TryDeleteSymlink(target);
                }
                _logger.Info("remove", $"removed symlink {target}");
                progressTracker.Publish(1, 1, 0, 0, target, force: true, isCompleted: true);
                return new RemoveReport(true, target, 1, 0, ImmutableArray<string>.Empty, DryRun: false);
            }
            catch (Exception ex)
            {
                _logger.Error("remove", $"delete symlink {target} failed: {ex.Message}");
                progressTracker.Publish(1, 0, 0, 1, target, force: true, isCompleted: true);
                return new RemoveReport(false, target, 0, 0,
                    ImmutableArray.Create($"{target}: {ex.Message}"), DryRun: false);
            }
        }

        if (validation.RemovesLinkOnly)
        {
            const string reason = "validated link target is no longer a symlink or reparse point";
            _logger.Error("remove", $"refused: {reason}: {target}");
            progressTracker.Publish(1, 0, 0, 1, target,
                force: true, isCompleted: true, targetsDeleted: 0);
            return RemoveReport.Refused(target, reason);
        }

        if (!Directory.Exists(target))
        {
            _logger.Warn("remove", $"target missing at execute time: {target}");
            progressTracker.Publish(1, 0, 0, 1, target, force: true, isCompleted: true);
            return RemoveReport.Refused(target, $"target '{target}' no longer exists");
        }

        if (!options.DryRun && SecureRemovalBackend.IsSupported)
        {
            return RemoveWithSecureBackend(
                validation.ExecutionIdentity!.Value,
                validation.RequiresEmptyDirectory,
                target,
                cancellationToken,
                progressTracker);
        }

        var errors = new FailureCollector();
        int files = 0, dirs = 0;
        var pending = new Stack<TraversalFrame>();
        pending.Push(new TraversalFrame(target, depth: 0));

        try
        {
            while (pending.TryPeek(out var frame))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!frame.EnumerationStarted)
                {
                    if (!TryValidateEntry(target, frame.Path,
                            allowLeafReparsePoint: frame.Path != target,
                            out var attributes, out var validationError))
                    {
                        RecordFailure(frame.Path, validationError!, errors);
                        progressTracker.Publish(0, files, dirs, errors.Count, frame.Path);
                        PopAndDispose(pending);
                        continue;
                    }

                    var isReparsePoint = attributes.HasFlag(FileAttributes.ReparsePoint);
                    var isDirectory = attributes.HasFlag(FileAttributes.Directory);

                    // A child link is always a leaf. Never enumerate its target,
                    // even when its directory bit is set (Unix symlink, Windows
                    // junction, mount-point reparse point, or another link-like
                    // filesystem entry).
                    if (isReparsePoint)
                    {
                        DeleteLeaf(frame.Path, expectedReparsePoint: true, options.DryRun,
                            target, ref files, errors);
                        progressTracker.Publish(0, files, dirs, errors.Count, frame.Path);
                        PopAndDispose(pending);
                        continue;
                    }

                    if (!isDirectory)
                    {
                        DeleteLeaf(frame.Path, expectedReparsePoint: false, options.DryRun,
                            target, ref files, errors);
                        progressTracker.Publish(0, files, dirs, errors.Count, frame.Path);
                        PopAndDispose(pending);
                        continue;
                    }

                    if (frame.Depth >= MaxTraversalDepth)
                    {
                        RecordFailure(frame.Path,
                            $"directory nesting exceeds the safety limit of {MaxTraversalDepth}", errors);
                        progressTracker.Publish(0, files, dirs, errors.Count, frame.Path);
                        PopAndDispose(pending);
                        continue;
                    }

                    try
                    {
                        // Keep one lazy enumerator per active depth. This
                        // retains O(depth) traversal state rather than every
                        // unvisited sibling path, and lets cancellation run
                        // between individual MoveNext calls.
                        frame.StartEnumeration(
                            Directory.EnumerateFileSystemEntries(frame.Path).GetEnumerator());
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        RecordFailure(frame.Path, $"enumerate failed: {ex.Message}", errors);
                        progressTracker.Publish(0, files, dirs, errors.Count, frame.Path);
                        PopAndDispose(pending);
                    }
                    continue;
                }

                bool hasNext;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    hasNext = frame.MoveNext();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    RecordFailure(frame.Path, $"enumerate failed: {ex.Message}", errors);
                    progressTracker.Publish(0, files, dirs, errors.Count, frame.Path);
                    PopAndDispose(pending);
                    continue;
                }

                if (hasNext)
                {
                    var child = frame.Current;
                    _entryObservedForTests?.Invoke(child);
                    cancellationToken.ThrowIfCancellationRequested();
                    progressTracker.Publish(0, files, dirs, errors.Count, child);
                    pending.Push(new TraversalFrame(child, frame.Depth + 1));
                    continue;
                }

                var completedPath = frame.Path;
                PopAndDispose(pending);
                DeleteDirectory(completedPath, options.DryRun, target, ref dirs, errors);
                progressTracker.Publish(0, files, dirs, errors.Count, completedPath);
            }
        }
        catch (OperationCanceledException)
        {
            progressTracker.Publish(0, files, dirs, errors.Count, target,
                force: true, isCanceled: true);
            throw;
        }
        finally
        {
            while (pending.Count > 0)
            {
                PopAndDispose(pending);
            }
        }

        if (options.DryRun)
        {
            _logger.Info("remove.dryrun", $"would remove {target}: {files} file(s), {dirs} dir(s)");
            progressTracker.Publish(1, files, dirs, errors.Count, target,
                force: true, isCompleted: true);
            return new RemoveReport(errors.Count == 0, target, files, dirs,
                errors.ToImmutable(), DryRun: true)
            {
                ErrorCount = errors.Count,
            };
        }

        if (errors.Count == 0)
        {
            _logger.Info("remove", $"removed {target}: {files} file(s), {dirs} dir(s)");
        }
        else
        {
            _logger.Error("remove", $"remove {target} completed with {errors.Count} error(s)");
        }

        progressTracker.Publish(1, files, dirs, errors.Count, target,
            force: true, isCompleted: true);

        return new RemoveReport(
            Succeeded: errors.Count == 0,
            ResolvedPath: target,
            FilesDeleted: files,
            DirectoriesDeleted: dirs,
            Errors: errors.ToImmutable(),
            DryRun: false)
        {
            ErrorCount = errors.Count,
        };
    }

    /// Runs filesystem-bound removal work on the thread pool so TUI callers do
    /// not block Terminal.Gui's event loop. The delegate deliberately observes
    /// cancellation inside the traversal rather than passing the token to
    /// Task.Run, which guarantees already-canceled calls still publish their
    /// terminal progress state.
    public Task<RemoveReport> RemoveAsync(
        RemoveValidator.RemoveValidation validation,
        Options? options = null,
        CancellationToken cancellationToken = default,
        IProgress<RemoveProgress>? progress = null) =>
        Task.Run(() => Remove(validation, options, cancellationToken, progress));

    /// Removes one inventory-observed symlink without following its target.
    /// This is used for the wizard's "unlink from agent" action, which has no
    /// skill-directory validation object because it intentionally leaves the
    /// canonical installation in place.
    public Task<RemoveReport> RemoveLinkAsync(
        string path,
        CancellationToken cancellationToken = default,
        IProgress<RemoveProgress>? progress = null) =>
        Task.Run(() =>
        {
            var fullPath = Path.GetFullPath(path);
            var progressTracker = new ProgressTracker(progress, _logger);
            progressTracker.Publish(0, 0, 0, 0, fullPath, force: true);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                progressTracker.Publish(0, 0, 0, 0, fullPath,
                    force: true, isCanceled: true);
                throw;
            }

            if (!PathResolver.IsSymlink(fullPath))
            {
                const string detail = "path is no longer a symlink";
                _logger.Warn("remove.agent", $"{fullPath}: {detail}");
                progressTracker.Publish(1, 0, 0, 1, fullPath, force: true, isCompleted: true);
                return new RemoveReport(
                    Succeeded: false,
                    ResolvedPath: fullPath,
                    FilesDeleted: 0,
                    DirectoriesDeleted: 0,
                    Errors: ImmutableArray.Create($"{fullPath}: {detail}"),
                    DryRun: false);
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SecureRemovalBackend.IsSupported)
                {
                    var errors = new FailureCollector();
                    SecureRemovalBackend.RemoveLink(
                        fullPath,
                        (_, _) => { },
                        (path, detail) => RecordFailure(path, detail, errors),
                        cancellationToken);
                    if (errors.Count > 0)
                    {
                        progressTracker.Publish(1, 0, 0, errors.Count, fullPath,
                            force: true, isCompleted: true);
                        return new RemoveReport(
                            Succeeded: false,
                            ResolvedPath: fullPath,
                            FilesDeleted: 0,
                            DirectoriesDeleted: 0,
                            Errors: errors.ToImmutable(),
                            DryRun: false)
                        {
                            ErrorCount = errors.Count,
                        };
                    }
                }
                else
                {
                    TryDeleteSymlink(fullPath);
                }
                _logger.Info("remove.agent", $"unlinked {fullPath}");
                progressTracker.Publish(1, 1, 0, 0, fullPath, force: true, isCompleted: true);
                return new RemoveReport(
                    Succeeded: true,
                    ResolvedPath: fullPath,
                    FilesDeleted: 1,
                    DirectoriesDeleted: 0,
                    Errors: ImmutableArray<string>.Empty,
                    DryRun: false);
            }
            catch (OperationCanceledException)
            {
                progressTracker.Publish(0, 0, 0, 0, fullPath, force: true, isCanceled: true);
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Warn("remove.agent", $"{fullPath}: {ex.Message}");
                progressTracker.Publish(1, 0, 0, 1, fullPath, force: true, isCompleted: true);
                return new RemoveReport(
                    Succeeded: false,
                    ResolvedPath: fullPath,
                    FilesDeleted: 0,
                    DirectoriesDeleted: 0,
                    Errors: ImmutableArray.Create($"{fullPath}: {ex.Message}"),
                    DryRun: false);
            }
        });

    private RemoveReport RemoveWithSecureBackend(
        SecureFileIdentity expectedIdentity,
        bool requireEmptyDirectory,
        string target,
        CancellationToken cancellationToken,
        ProgressTracker progressTracker)
    {
        var errors = new FailureCollector();
        var files = 0;
        var directories = 0;
        try
        {
            SecureRemovalBackend.RemoveTree(
                target,
                expectedIdentity,
                requireEmptyDirectory,
                MaxTraversalDepth,
                path =>
                {
                    _entryObservedForTests?.Invoke(path);
                    progressTracker.Publish(0, files, directories, errors.Count, path);
                },
                (path, isDirectory) =>
                {
                    _entryDeletingForTests?.Invoke(path, isDirectory);
                },
                (path, isDirectory) =>
                {
                    if (isDirectory) directories++;
                    else files++;
                    progressTracker.Publish(0, files, directories, errors.Count, path);
                },
                (path, detail) =>
                {
                    RecordFailure(path, detail, errors);
                    progressTracker.Publish(0, files, directories, errors.Count, path);
                },
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            progressTracker.Publish(0, files, directories, errors.Count, target,
                force: true, isCanceled: true);
            throw;
        }

        if (errors.Count == 0)
        {
            _logger.Info("remove", $"removed {target}: {files} file(s), {directories} dir(s)");
        }
        else
        {
            _logger.Error("remove", $"remove {target} completed with {errors.Count} error(s)");
        }
        progressTracker.Publish(1, files, directories, errors.Count, target,
            force: true, isCompleted: true);
        return new RemoveReport(
            Succeeded: errors.Count == 0,
            ResolvedPath: target,
            FilesDeleted: files,
            DirectoriesDeleted: directories,
            Errors: errors.ToImmutable(),
            DryRun: false)
        {
            ErrorCount = errors.Count,
        };
    }

    private static void PopAndDispose(Stack<TraversalFrame> frames) =>
        frames.Pop().Dispose();

    private sealed class TraversalFrame(string path, int depth) : IDisposable
    {
        private IEnumerator<string>? _enumerator;

        internal string Path { get; } = path;
        internal int Depth { get; } = depth;
        internal bool EnumerationStarted => _enumerator is not null;
        internal string Current => _enumerator!.Current;

        internal void StartEnumeration(IEnumerator<string> enumerator)
        {
            if (_enumerator is not null)
            {
                throw new InvalidOperationException("Traversal enumeration has already started.");
            }
            _enumerator = enumerator;
        }

        internal bool MoveNext() => _enumerator!.MoveNext();

        public void Dispose()
        {
            _enumerator?.Dispose();
            _enumerator = null;
        }
    }

    private void DeleteLeaf(
        string path,
        bool expectedReparsePoint,
        bool dryRun,
        string target,
        ref int files,
        FailureCollector errors)
    {
        if (!TryValidateEntry(target, path, allowLeafReparsePoint: true,
                out var attributes, out var validationError))
        {
            RecordFailure(path, validationError!, errors);
            return;
        }

        var isReparsePoint = attributes.HasFlag(FileAttributes.ReparsePoint);
        if (expectedReparsePoint != isReparsePoint)
        {
            RecordFailure(path, "entry type changed during removal", errors);
            return;
        }
        if (!isReparsePoint && attributes.HasFlag(FileAttributes.Directory))
        {
            RecordFailure(path, "file changed into a directory during removal", errors);
            return;
        }

        if (dryRun)
        {
            files++;
            _logger.Debug("remove.dryrun", $"leaf: {path}");
            return;
        }

        try
        {
            if (isReparsePoint) TryDeleteSymlink(path);
            else File.Delete(path);
            files++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RecordFailure(path, ex.Message, errors);
        }
    }

    private void DeleteDirectory(
        string path,
        bool dryRun,
        string target,
        ref int dirs,
        FailureCollector errors)
    {
        if (!TryValidateEntry(target, path, allowLeafReparsePoint: false,
                out var attributes, out var validationError))
        {
            RecordFailure(path, validationError!, errors);
            return;
        }
        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            RecordFailure(path, "directory changed into a file during removal", errors);
            return;
        }

        if (dryRun)
        {
            dirs++;
            _logger.Debug("remove.dryrun", $"dir: {path}");
            return;
        }

        try
        {
            Directory.Delete(path, recursive: false);
            dirs++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RecordFailure(path, ex.Message, errors);
        }
    }

    private void RecordFailure(
        string path,
        string detail,
        FailureCollector errors)
    {
        _logger.Warn("remove", $"{path}: {detail}");
        errors.Add($"{path}: {detail}");
    }

    private static bool TryValidateEntry(
        string target,
        string candidate,
        bool allowLeafReparsePoint,
        out FileAttributes attributes,
        out string? error)
    {
        attributes = default;
        error = null;

        var targetFull = Path.GetFullPath(target);
        var candidateFull = Path.GetFullPath(candidate);
        if (!PathResolver.IsInside(candidateFull, targetFull))
        {
            error = $"refused path outside selected target '{targetFull}'";
            return false;
        }

        var relative = Path.GetRelativePath(targetFull, candidateFull);
        var components = relative == "."
            ? Array.Empty<string>()
            : relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

        var cursor = targetFull;
        try
        {
            attributes = File.GetAttributes(cursor);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                error = $"selected target '{targetFull}' became a reparse point";
                return false;
            }

            for (var index = 0; index < components.Length; index++)
            {
                cursor = Path.Combine(cursor, components[index]);
                attributes = File.GetAttributes(cursor);
                var isLeaf = index == components.Length - 1;
                if (attributes.HasFlag(FileAttributes.ReparsePoint)
                    && !(allowLeafReparsePoint && isLeaf))
                {
                    error = $"ancestor '{cursor}' is a reparse point";
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"filesystem state could not be revalidated: {ex.Message}";
            return false;
        }
    }

    private static void TryDeleteSymlink(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (UnauthorizedAccessException)
        {
            Directory.Delete(path, recursive: false);
        }
        catch (IOException)
        {
            Directory.Delete(path, recursive: false);
        }

        if (PathResolver.IsSymlink(path) || File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException($"symlink '{path}' still exists after delete attempt");
        }
    }

    public BatchRemoveReport RemoveMany(
        IEnumerable<RemoveValidator.RemoveValidation> validations,
        Options? options = null,
        CancellationToken cancellationToken = default,
        IProgress<RemoveProgress>? progress = null)
    {
        options ??= new Options();

        var errors = new FailureCollector();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var targetsDeleted = 0;
        var filesDeleted = 0;
        var directoriesDeleted = 0;
        var progressAdapter = new BatchProgressAdapter(progress, _logger);

        try
        {
            foreach (var validation in validations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = PathIdentity.NormalizeKey(validation.ResolvedPath);
                if (!seen.Add(key))
                {
                    continue;
                }

                var report = Remove(validation, options, cancellationToken, progressAdapter);
                filesDeleted += report.FilesDeleted;
                directoriesDeleted += report.DirectoriesDeleted;
                if (report.Succeeded)
                {
                    targetsDeleted++;
                }

                errors.AddRange(
                    report.Errors.Select(error => $"{validation.ResolvedPath}: {error}"),
                    report.ErrorCount);
                progressAdapter.CompleteTarget(
                    targetsDeleted,
                    filesDeleted,
                    directoriesDeleted,
                    errors.Count,
                    validation.ResolvedPath);
            }
        }
        catch (OperationCanceledException)
        {
            progressAdapter.CancelBatch();
            throw;
        }

        progressAdapter.CompleteBatch(
            targetsDeleted,
            filesDeleted,
            directoriesDeleted,
            errors.Count);

        return new BatchRemoveReport(
            Succeeded: errors.Count == 0,
            TargetsDeleted: targetsDeleted,
            FilesDeleted: filesDeleted,
            DirectoriesDeleted: directoriesDeleted,
            Errors: errors.ToImmutable(),
            DryRun: options.DryRun)
        {
            ErrorCount = errors.Count,
        };
    }

    public Task<BatchRemoveReport> RemoveManyAsync(
        IEnumerable<RemoveValidator.RemoveValidation> validations,
        Options? options = null,
        CancellationToken cancellationToken = default,
        IProgress<RemoveProgress>? progress = null) =>
        Task.Run(() => RemoveMany(validations, options, cancellationToken, progress));

    private sealed class ProgressTracker(IProgress<RemoveProgress>? progress, Logger logger)
    {
        private long _lastPublished;
        private bool _disabled;

        internal void Publish(
            int targets,
            int files,
            int directories,
            int errors,
            string currentPath,
            bool force = false,
            bool isCompleted = false,
            bool isCanceled = false,
            int? targetsDeleted = null)
        {
            if (_disabled || progress is null)
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            if (!force && _lastPublished != 0
                && Stopwatch.GetElapsedTime(_lastPublished, now) < ProgressInterval)
            {
                return;
            }

            _lastPublished = now;
            try
            {
                progress.Report(new RemoveProgress(
                    targets,
                    targetsDeleted ?? (isCompleted && errors == 0 ? targets : 0),
                    files,
                    directories,
                    errors,
                    currentPath,
                    isCompleted,
                    isCanceled));
            }
            catch (Exception ex)
            {
                _disabled = true;
                logger.Warn("remove.progress", $"progress observer disabled: {ex.Message}");
            }
        }
    }

    internal sealed class FailureCollector
    {
        private readonly ImmutableArray<string>.Builder _retained =
            ImmutableArray.CreateBuilder<string>(MaxRetainedErrors);

        internal int Count { get; private set; }

        internal void Add(string detail)
        {
            Count++;
            if (_retained.Count < MaxRetainedErrors)
            {
                _retained.Add(detail);
            }
        }

        internal void AddRange(IEnumerable<string> details, int totalCount)
        {
            Count += totalCount;
            if (_retained.Count >= MaxRetainedErrors)
            {
                return;
            }

            foreach (var detail in details)
            {
                if (_retained.Count >= MaxRetainedErrors)
                {
                    break;
                }
                _retained.Add(detail);
            }
        }

        internal ImmutableArray<string> ToImmutable()
        {
            var retained = _retained.ToImmutable();
            var omitted = Count - retained.Length;
            return omitted > 0
                ? retained.Add($"… {omitted} additional error(s) omitted")
                : retained;
        }
    }

    private sealed class BatchProgressAdapter(IProgress<RemoveProgress>? progress, Logger logger)
        : IProgress<RemoveProgress>
    {
        private int _targetsProcessed;
        private int _targetsDeleted;
        private int _filesBeforeTarget;
        private int _directoriesBeforeTarget;
        private int _errorsBeforeTarget;
        private int _latestTargets;
        private int _latestTargetsDeleted;
        private int _latestFiles;
        private int _latestDirectories;
        private int _latestErrors;
        private string _currentPath = string.Empty;
        private long _lastPublished;
        private bool _disabled;

        public void Report(RemoveProgress value)
        {
            _currentPath = value.CurrentPath;
            var aggregate = value with
            {
                TargetsProcessed = _targetsProcessed + value.TargetsProcessed,
                TargetsDeleted = _targetsDeleted + value.TargetsDeleted,
                FilesProcessed = _filesBeforeTarget + value.FilesProcessed,
                DirectoriesProcessed = _directoriesBeforeTarget + value.DirectoriesProcessed,
                Errors = _errorsBeforeTarget + value.Errors,
                IsCompleted = false,
            };
            Remember(aggregate);
            Publish(aggregate, force: value.IsCanceled);
        }

        internal void CompleteTarget(
            int targetsDeleted,
            int files,
            int directories,
            int errors,
            string currentPath)
        {
            _targetsProcessed++;
            _targetsDeleted = targetsDeleted;
            _filesBeforeTarget = files;
            _directoriesBeforeTarget = directories;
            _errorsBeforeTarget = errors;
            _currentPath = currentPath;
            var aggregate = new RemoveProgress(
                _targetsProcessed,
                _targetsDeleted,
                files,
                directories,
                errors,
                currentPath,
                IsCompleted: false,
                IsCanceled: false);
            Remember(aggregate);
            Publish(aggregate, force: false);
        }

        internal void CompleteBatch(
            int targetsDeleted,
            int files,
            int directories,
            int errors)
        {
            _targetsDeleted = targetsDeleted;
            var aggregate = new RemoveProgress(
                _targetsProcessed,
                _targetsDeleted,
                files,
                directories,
                errors,
                _currentPath,
                IsCompleted: true,
                IsCanceled: false);
            Remember(aggregate);
            Publish(aggregate, force: true);
        }

        internal void CancelBatch() =>
            Publish(new RemoveProgress(
                _latestTargets,
                _latestTargetsDeleted,
                _latestFiles,
                _latestDirectories,
                _latestErrors,
                _currentPath,
                IsCompleted: false,
                IsCanceled: true), force: true);

        private void Remember(RemoveProgress value)
        {
            _latestTargets = value.TargetsProcessed;
            _latestTargetsDeleted = value.TargetsDeleted;
            _latestFiles = value.FilesProcessed;
            _latestDirectories = value.DirectoriesProcessed;
            _latestErrors = value.Errors;
        }

        private void Publish(RemoveProgress value, bool force)
        {
            if (_disabled || progress is null)
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            if (!force && _lastPublished != 0
                && Stopwatch.GetElapsedTime(_lastPublished, now) < ProgressInterval)
            {
                return;
            }

            _lastPublished = now;
            try
            {
                progress.Report(value);
            }
            catch (Exception ex)
            {
                _disabled = true;
                logger.Warn("remove.progress", $"batch progress observer disabled: {ex.Message}");
            }
        }
    }
}
