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
    /// this method does NOT re-run the safety rules.
    public RemoveReport Remove(
        RemoveValidator.RemoveValidation validation,
        Options? options = null)
    {
        options ??= new Options();
        if (!validation.Allowed)
        {
            var reason = string.Join("; ", validation.Errors.Select(e => $"{e.Kind}: {e.Detail}"));
            _logger.Error("remove", $"refused: {reason}");
            return RemoveReport.Refused(validation.ResolvedPath, reason);
        }

        var target = validation.ResolvedPath;
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

        // Walk files bottom-up so we can remove directories after their
        // contents have been cleared. `EnumerateFiles` recursive is fine —
        // we already refused on ancestor-symlink escapes in validation, and
        // `Directory.Delete(..., recursive: true)` would let a single bad
        // file abort the whole op.
        IEnumerable<string> allFiles;
        IEnumerable<string> allDirs;
        try
        {
            allFiles = Directory.EnumerateFiles(target, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = 0,
                IgnoreInaccessible = true,
            }).ToList();
            allDirs = Directory.EnumerateDirectories(target, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = 0,
                IgnoreInaccessible = true,
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.Error("remove", $"enumerate {target} failed: {ex.Message}");
            return new RemoveReport(false, target, 0, 0,
                ImmutableArray.Create($"enumerate failed: {ex.Message}"), options.DryRun);
        }

        foreach (var file in allFiles)
        {
            if (options.DryRun)
            {
                files++;
                _logger.Debug("remove.dryrun", $"file: {file}");
                continue;
            }
            try
            {
                File.Delete(file);
                files++;
            }
            catch (Exception ex)
            {
                _logger.Warn("remove", $"delete {file} failed: {ex.Message}");
                errors.Add($"{file}: {ex.Message}");
            }
        }

        // Deepest directories first so parents are empty by the time we reach
        // them. Longest path key wins.
        foreach (var dir in allDirs.OrderByDescending(p => p.Length))
        {
            if (options.DryRun)
            {
                dirs++;
                _logger.Debug("remove.dryrun", $"dir: {dir}");
                continue;
            }
            try
            {
                if (!Directory.Exists(dir)) continue;
                Directory.Delete(dir, recursive: false);
                dirs++;
            }
            catch (Exception ex)
            {
                _logger.Warn("remove", $"rmdir {dir} failed: {ex.Message}");
                errors.Add($"{dir}: {ex.Message}");
            }
        }

        if (options.DryRun)
        {
            dirs++;
            _logger.Info("remove.dryrun", $"would remove {target}: {files} file(s), {dirs} dir(s)");
            return new RemoveReport(true, target, files, dirs, errors.ToImmutable(), DryRun: true);
        }

        try
        {
            Directory.Delete(target, recursive: false);
            dirs++;
            _logger.Info("remove", $"removed {target}: {files} file(s), {dirs} dir(s)");
        }
        catch (Exception ex)
        {
            _logger.Error("remove", $"rmdir {target} failed: {ex.Message}");
            errors.Add($"{target}: {ex.Message}");
        }

        return new RemoveReport(
            Succeeded: errors.Count == 0,
            ResolvedPath: target,
            FilesDeleted: files,
            DirectoriesDeleted: dirs,
            Errors: errors.ToImmutable(),
            DryRun: false);
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
        Options? options = null)
    {
        options ??= new Options();

        var errors = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var targetsDeleted = 0;
        var filesDeleted = 0;
        var directoriesDeleted = 0;

        foreach (var validation in validations)
        {
            var key = PathResolver.Normalize(validation.ResolvedPath);
            if (!seen.Add(key))
            {
                continue;
            }

            var report = Remove(validation, options);
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
