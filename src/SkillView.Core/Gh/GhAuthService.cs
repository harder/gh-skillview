using SkillView.Logging;
using SkillView.Subprocess;

namespace SkillView.Gh;

/// Runs `gh auth status` and parses the result (§11.1). Never stores tokens —
/// auth state lives inside `gh` (§20.6).
public sealed class GhAuthService
{
    private readonly ProcessRunner _runner;
    private readonly Logger _logger;

    public GhAuthService(ProcessRunner runner, Logger logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public async Task<GhAuthStatus> GetStatusAsync(string ghPath, CancellationToken cancellationToken = default)
    {
        var result = await _runner
            .RunAsync(ghPath, new[] { "auth", "status" }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var status = GhAuthStatusParser.Parse(result.StdOut, result.StdErr, result.ExitCode);
        _logger.Info("auth",
            status.LoggedIn
                ? $"authenticated to {status.ActiveHost ?? "?"} as {status.Account ?? "?"}"
                : "no active gh authentication");
        return status;
    }
}
