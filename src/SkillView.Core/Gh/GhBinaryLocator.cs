using System.Runtime.InteropServices;
using SkillView.Diagnostics;
using SkillView.Logging;
using SkillView.Subprocess;

namespace SkillView.Gh;

/// Locates the `gh` binary on PATH, records its version, and reports whether
/// the version meets SkillView's hard minimum (§11.1, §5.3).
public sealed class GhBinaryLocator
{
    /// Minimum supported `gh` — the first release that shipped the `gh skill`
    /// subcommand set SkillView depends on (§5.3).
    public static readonly SemVer MinimumVersion = new(2, 91, 0);

    private readonly ProcessRunner _runner;
    private readonly Logger _logger;

    public GhBinaryLocator(ProcessRunner runner, Logger logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public string? FindOnPath()
    {
        var executable = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "gh.exe" : "gh";
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(entry, executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // malformed PATH entry — skip
            }
        }
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

        // `gh version 2.91.0 (2026-03-…)` — first non-empty line, second token
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
