using System.Runtime.InteropServices;
using SkillView.Diagnostics;
using SkillView.Logging;
using SkillView.Subprocess;

namespace SkillView.Gh;

/// Locates the `gh` binary, records its version, and reports whether
/// the version meets SkillView's hard minimum.
public sealed class GhBinaryLocator
{
    /// Security floor: gh 2.102.0 fixes interactive skill-search option injection
    /// (GHSA-qcwj-mr2r-2cx7) and symlink-destination writes
    /// (GHSA-39wj-f2f4-978v). Keep trusted flags before -- independently.
    /// The earlier 2.97.0 command surface remains available above this floor.
    public static readonly SemVer MinimumVersion = new(2, 102, 0);

    private readonly ProcessRunner _runner;
    private readonly Logger _logger;
    private readonly Func<string?> _pathProvider;
    private readonly Func<string?> _extensionGhPathProvider;
    private readonly Func<string?> _extensionFlagProvider;
    private readonly Func<string, bool> _fileExists;

    public GhBinaryLocator(ProcessRunner runner, Logger logger)
        : this(
            runner,
            logger,
            () => Environment.GetEnvironmentVariable("PATH"),
            File.Exists,
            () => Environment.GetEnvironmentVariable("GH_PATH"),
            () => Environment.GetEnvironmentVariable("GH_EXTENSION"))
    {
    }

    internal GhBinaryLocator(
        ProcessRunner runner,
        Logger logger,
        Func<string?> pathProvider,
        Func<string, bool> fileExists,
        Func<string?>? extensionGhPathProvider = null,
        Func<string?>? extensionFlagProvider = null)
    {
        _runner = runner;
        _logger = logger;
        _pathProvider = pathProvider;
        _fileExists = fileExists;
        _extensionGhPathProvider = extensionGhPathProvider ?? (() => null);
        _extensionFlagProvider = extensionFlagProvider ?? (() => null);
    }

    public string? FindOnPath(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // gh 2.101+ passes the exact invoking executable to extensions. This
        // also works when the parent gh was launched by absolute path and its
        // directory is absent from PATH. Older gh versions fall back to PATH.
        if (_extensionFlagProvider() == "1")
        {
            cancellationToken.ThrowIfCancellationRequested();
            var extensionGhPath = _extensionGhPathProvider();
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(extensionGhPath) &&
                Path.IsPathFullyQualified(extensionGhPath) &&
                _fileExists(extensionGhPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return extensionGhPath;
            }
        }

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
    public static bool SatisfiesMinimum(string? version)
    {
        if (version is null) return false;
        version = version.Trim();
        var prerelease = version.IndexOf('-');
        var suffix = version.IndexOfAny(['+', ' ', '(']);
        var stable = prerelease < 0 || (suffix >= 0 && prerelease > suffix);
        return stable && SemVer.TryParse(version, out var v) && v >= MinimumVersion;
    }
}
