using SkillView.Gh;
using SkillView.Logging;
using SkillView.Subprocess;

namespace SkillView.Diagnostics;

/// Orchestrates the binary, version, and auth probes into a single
/// `EnvironmentReport`. Each probe tolerates the previous failing — a missing
/// `gh` yields an empty report rather than throwing.
///
/// SkillView requires gh ≥ 2.99.0, which guarantees the full `gh skill`
/// surface, so there is no per-flag capability probe — a single
/// `gh skill --help` smoke check confirms the command is present.
public sealed class EnvironmentProbe
{
    private readonly GhBinaryLocator _locator;
    private readonly GhAuthService _auth;
    private readonly ProcessRunner _runner;
    private readonly Logger _logger;
    private readonly string? _logDirectory;

    public EnvironmentProbe(
        GhBinaryLocator locator,
        GhAuthService auth,
        ProcessRunner runner,
        Logger logger,
        string? logDirectory)
    {
        _locator = locator;
        _auth = auth;
        _runner = runner;
        _logger = logger;
        _logDirectory = logDirectory;
    }

    public async Task<EnvironmentReport> ProbeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = _locator.FindOnPath(cancellationToken);
        if (path is null)
        {
            _logger.Warn("env", "gh binary not found on PATH");
            return new EnvironmentReport
            {
                GhPath = null,
                GhVersionRaw = null,
                GhVersion = null,
                GhMeetsMinimum = false,
                Auth = GhAuthStatus.Unknown,
                GhSkillAvailable = false,
                LogDirectory = _logDirectory,
            };
        }

        var versionRaw = await _locator.GetVersionAsync(path, cancellationToken).ConfigureAwait(false);
        SemVer? version = SemVer.TryParse(versionRaw, out var v) ? v : null;
        var meets = version is SemVer sv && sv >= GhBinaryLocator.MinimumVersion;

        if (version is null)
        {
            _logger.Warn("env", $"could not parse gh version from '{versionRaw ?? "<null>"}'");
        }
        else if (!meets)
        {
            _logger.Warn("env",
                $"gh {version} is below the required minimum {GhBinaryLocator.MinimumVersion}");
        }
        else
        {
            _logger.Info("env", $"gh {version} at {path} meets minimum {GhBinaryLocator.MinimumVersion}");
        }

        var auth = await _auth.GetStatusAsync(path, cancellationToken).ConfigureAwait(false);

        // Single smoke check — only meaningful above the minimum, since older
        // gh doesn't ship `gh skill` at all.
        var skillAvailable = meets && await ProbeSkillCommandAsync(path, cancellationToken).ConfigureAwait(false);

        return new EnvironmentReport
        {
            GhPath = path,
            GhVersionRaw = versionRaw,
            GhVersion = version,
            GhMeetsMinimum = meets,
            Auth = auth,
            GhSkillAvailable = skillAvailable,
            LogDirectory = _logDirectory,
        };
    }

    private async Task<bool> ProbeSkillCommandAsync(string ghPath, CancellationToken cancellationToken)
    {
        var result = await _runner
            .RunAsync(ghPath, new[] { "skill", "--help" }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var available = result.Succeeded;
        if (!available)
        {
            _logger.Warn("env", $"`gh skill --help` not usable (exit {result.ExitCode})");
        }
        return available;
    }
}
