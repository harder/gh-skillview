using SkillView.Gh;
using SkillView.Logging;

namespace SkillView.Diagnostics;

/// Orchestrates the three probes (binary, auth, capabilities) into a single
/// `EnvironmentReport`. Each probe tolerates the previous failing — a missing
/// `gh` yields an empty report rather than throwing.
public sealed class EnvironmentProbe
{
    private readonly GhBinaryLocator _locator;
    private readonly GhAuthService _auth;
    private readonly GhSkillCapabilityProbe _capabilities;
    private readonly Logger _logger;
    private readonly string? _logDirectory;

    public EnvironmentProbe(
        GhBinaryLocator locator,
        GhAuthService auth,
        GhSkillCapabilityProbe capabilities,
        Logger logger,
        string? logDirectory)
    {
        _locator = locator;
        _auth = auth;
        _capabilities = capabilities;
        _logger = logger;
        _logDirectory = logDirectory;
    }

    public async Task<EnvironmentReport> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var path = _locator.FindOnPath();
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
                Capabilities = CapabilityProfile.Empty,
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

        // Capability probe is only meaningful above the minimum — older gh
        // doesn't ship `gh skill` at all. Skip the fan-out if we know it'll fail.
        var capabilities = meets
            ? await _capabilities.ProbeAsync(path, cancellationToken).ConfigureAwait(false)
            : CapabilityProfile.Empty;

        return new EnvironmentReport
        {
            GhPath = path,
            GhVersionRaw = versionRaw,
            GhVersion = version,
            GhMeetsMinimum = meets,
            Auth = auth,
            Capabilities = capabilities,
            LogDirectory = _logDirectory,
        };
    }
}
