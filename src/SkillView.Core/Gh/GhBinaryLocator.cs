using System.Runtime.InteropServices;
using SkillView.Diagnostics;
using SkillView.Logging;
using SkillView.Subprocess;

namespace SkillView.Gh;

/// Locates the `gh` binary on PATH, records its version, and reports whether
/// the version meets SkillView's hard minimum.
public sealed class GhBinaryLocator
{
    /// Minimum supported `gh`. 2.94.0 first shipped the full `gh skill` surface
    /// SkillView relies on (`skill list`, `install --all`, `update --all`,
    /// nested-dir discovery), but 2.95.0 (cli/cli#13449) fixes a data-loss bug
    /// where `gh skill update` could relocate namespaced skills and delete the
    /// original install directory — and SkillView's Updates tab drives
    /// `gh skill update --all`. The minimum is therefore 2.95.0 so every user
    /// gets the atomic in-place update path. Meeting this minimum guarantees the
    /// feature set, so SkillView does not probe individual flags.
    public static readonly SemVer MinimumVersion = new(2, 95, 0);

    private readonly ProcessRunner _runner;
    private readonly Logger _logger;
    private readonly Func<string?> _pathProvider;
    private readonly Func<string, bool> _fileExists;

    public GhBinaryLocator(ProcessRunner runner, Logger logger)
        : this(
            runner,
            logger,
            () => Environment.GetEnvironmentVariable("PATH"),
            File.Exists)
    {
    }

    internal GhBinaryLocator(
        ProcessRunner runner,
        Logger logger,
        Func<string?> pathProvider,
        Func<string, bool> fileExists)
    {
        _runner = runner;
        _logger = logger;
        _pathProvider = pathProvider;
        _fileExists = fileExists;
    }

    public string? FindOnPath(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "gh.exe" : "gh";
        var path = _pathProvider();
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var candidate = Path.Combine(entry, executable);
                var exists = _fileExists(candidate);
                cancellationToken.ThrowIfCancellationRequested();
                if (exists)
                {
                    return candidate;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // malformed PATH entry — skip
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return null;
    }

    public async Task<string?> GetVersionAsync(string ghPath, CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(ghPath, new[] { "--version" }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _logger.Warn("gh", $"`gh --version` exited with {result.ExitCode}");
            return null;
        }

        // `gh version 2.92.0 (2026-03-…)` — first non-empty line, second token
        foreach (var line in result.StdOut.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && parts[0].Equals("gh", StringComparison.OrdinalIgnoreCase))
            {
                return parts[2];
            }
            break;
        }
        return null;
    }

    /// True when `version` parses and is at or above `MinimumVersion`. Unparseable
    /// or missing versions return false — callers should treat that as degraded.
    public static bool SatisfiesMinimum(string? version) =>
        SemVer.TryParse(version, out var v) && v >= MinimumVersion;
}
