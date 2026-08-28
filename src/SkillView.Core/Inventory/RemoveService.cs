using System.Collections.Immutable;
using System.IO;
using SkillView.Inventory.Models;
using SkillView.Logging;

namespace SkillView.Inventory;

/// Executes a validated removal with per-file .NET APIs ("never compose
/// file ops via shell"). Partial failures are logged and survivable: the
/// method walks every file and collects errors rather than aborting at the
/// first one.
public sealed class RemoveService
{
    private const int MaxTraversalDepth = 256;
    private readonly Logger _logger;

    public RemoveService(Logger logger) { _logger = logger; }

    public sealed record Options(bool DryRun = false);

    public sealed record RemoveReport(
        bool Succeeded,
        string ResolvedPath,
        int FilesDeleted,
        int DirectoriesDeleted,
        ImmutableArray<string> Errors,
        bool DryRun)
    {
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
        public static BatchRemoveReport FromSingle(RemoveReport report, int targetsDeleted) => new(
            Succeeded: report.Succeeded,
            TargetsDeleted: targetsDeleted,
            FilesDeleted: report.FilesDeleted,
            DirectoriesDeleted: report.DirectoriesDeleted,
            Errors: report.Errors,
            DryRun: report.DryRun);
    }

    /// Removes a previously-validated skill directory. Callers MUST run
    /// `RemoveValidator.Validate` first and honor its errors and warnings;
    /// this method does NOT re-run the policy rules. Execution still rechecks
    /// that every entry is inside the selected target and that no ancestor
    /// introduced after validation is a reparse point.
    public RemoveReport Remove(
        RemoveValidator.RemoveValidation validation,
        Options? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new Options();
        cancellationToken.ThrowIfCancellationRequested();
        if (!validation.Allowed)
        {
            var reason = string.Join("; ", validation.Errors.Select(e => $"{e.Kind}: {e.Detail}"));
            _logger.Error("remove", $"refused: {reason}");
            return RemoveReport.Refused(validation.ResolvedPath, reason);
        }

        var target = Path.GetFullPath(validation.ResolvedPath);
        if (PathResolver.IsSymlink(target))
        {
            if (options.DryRun)
            {
                _logger.Info("remove.dryrun", $"would remove symlink {target}");
                return new RemoveReport(true, target, 1, 0, ImmutableArray<string>.Empty, DryRun: true);
            }

            try
            {
                TryDeleteSymlink(target);
                _logger.Info("remove", $"removed symlink {target}");
                return new RemoveReport(true, target, 1, 0, ImmutableArray<string>.Empty, DryRun: false);
            }
            catch (Exception ex)
            {
                _logger.Error("remove", $"delete symlink {target} failed: {ex.Message}");
                return new RemoveReport(false, target, 0, 0,
                    ImmutableArray.Create($"{target}: {ex.Message}"), DryRun: false);
            }
        }

        if (!Directory.Exists(target))
        {
            _logger.Warn("remove", $"target missing at execute time: {target}");
            return RemoveReport.Refused(target, $"target '{target}' no longer exists");
        }

        var errors = ImmutableArray.CreateBuilder<string>();
        int files = 0, dirs = 0;
        var pending = new Stack<TraversalEntry>();
        pending.Push(new TraversalEntry(target, Depth: 0, DeleteAfterChildren: false));

        while (pending.TryPop(out var entry))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryValidateEntry(target, entry.Path, allowLeafReparsePoint: entry.Path != target,
                    out var attributes, out var validationError))
            {
                RecordFailure(entry.Path, validationError!, errors);
                continue;
            }

            var isReparsePoint = attributes.HasFlag(FileAttributes.ReparsePoint);
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);

            // A child link is always a leaf. Never enumerate its target, even
            // when its directory bit is set (Unix symlink, Windows junction,
            // mount-point reparse point, or another link-like filesystem entry).
            if (isReparsePoint)
            {
                DeleteLeaf(entry.Path, expectedReparsePoint: true, options.DryRun,
                    target, ref files, errors);
                continue;
            }

            if (!isDirectory)
            {
                if (entry.DeleteAfterChildren)
                {
                    RecordFailure(entry.Path, "directory changed into a file during removal", errors);
                    continue;
                }

                DeleteLeaf(entry.Path, expectedReparsePoint: false, options.DryRun,
                    target, ref files, errors);
                continue;
            }

            if (entry.DeleteAfterChildren)
            {
                DeleteDirectory(entry.Path, options.DryRun, target, ref dirs, errors);
                continue;
            }

            if (entry.Depth >= MaxTraversalDepth)
            {
                RecordFailure(entry.Path,
                    $"directory nesting exceeds the safety limit of {MaxTraversalDepth}", errors);
                continue;
            }

            IReadOnlyList<string> children;
            try
            {
                // Enumerate one directory at a time. This avoids recursive API
                // link traversal and bounds retained path memory to the widest
                // single directory plus the current traversal depth.
                children = Directory.EnumerateFileSystemEntries(entry.Path).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RecordFailure(entry.Path, $"enumerate failed: {ex.Message}", errors);
                continue;
            }

            pending.Push(entry with { DeleteAfterChildren = true });
            for (var index = children.Count - 1; index >= 0; index--)
            {
                pending.Push(new TraversalEntry(children[index], entry.Depth + 1,
                    DeleteAfterChildren: false));
            }
        }

        if (options.DryRun)
        {
            _logger.Info("remove.dryrun", $"would remove {target}: {files} file(s), {dirs} dir(s)");
            return new RemoveReport(errors.Count == 0, target, files, dirs,
                errors.ToImmutable(), DryRun: true);
        }

        if (errors.Count == 0)
        {
            _logger.Info("remove", $"removed {target}: {files} file(s), {dirs} dir(s)");
        }
        else
        {
            _logger.Error("remove", $"remove {target} completed with {errors.Count} error(s)");
        }

        return new RemoveReport(
            Succeeded: errors.Count == 0,
            ResolvedPath: target,
            FilesDeleted: files,
            DirectoriesDeleted: dirs,
            Errors: errors.ToImmutable(),
            DryRun: false);
    }

    private readonly record struct TraversalEntry(string Path, int Depth, bool DeleteAfterChildren);

    private void DeleteLeaf(
        string path,
        bool expectedReparsePoint,
        bool dryRun,
        string target,
        ref int files,
        ImmutableArray<string>.Builder errors)
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
        ImmutableArray<string>.Builder errors)
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
        ImmutableArray<string>.Builder errors)
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
        CancellationToken cancellationToken = default)
    {
        options ??= new Options();

        var errors = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var targetsDeleted = 0;
        var filesDeleted = 0;
        var directoriesDeleted = 0;

        foreach (var validation in validations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = PathResolver.Normalize(validation.ResolvedPath);
            if (!seen.Add(key))
            {
                continue;
            }

            var report = Remove(validation, options, cancellationToken);
            filesDeleted += report.FilesDeleted;
            directoriesDeleted += report.DirectoriesDeleted;
            if (report.Succeeded)
            {
                targetsDeleted++;
            }

            foreach (var error in report.Errors)
            {
                errors.Add($"{validation.ResolvedPath}: {error}");
            }
        }

        return new BatchRemoveReport(
            Succeeded: errors.Count == 0,
            TargetsDeleted: targetsDeleted,
            FilesDeleted: filesDeleted,
            DirectoriesDeleted: directoriesDeleted,
            Errors: errors.ToImmutable(),
            DryRun: options.DryRun);
    }
}
