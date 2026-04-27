using System.Collections.Immutable;
using SkillView.Logging;
using SkillView.Subprocess;

namespace SkillView.Gh;

/// Builds a `CapabilityProfile` by invoking `gh skill <sub> --help` for every
/// subcommand SkillView cares about and scanning the output for flag tokens.
/// Uses flag-token membership, never structural help parsing.
public sealed class GhSkillCapabilityProbe
{
    private readonly ProcessRunner _runner;
    private readonly Logger _logger;

    public GhSkillCapabilityProbe(ProcessRunner runner, Logger logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public async Task<CapabilityProfile> ProbeAsync(string ghPath, CancellationToken cancellationToken = default)
    {
        // Start with the parent subcommand to distinguish "gh skill exists" from
        // "gh skill doesn't exist yet". Subcommand probes that follow cost
        // nothing beyond a few process spawns.
        var parent = await _runner
            .RunAsync(ghPath, new[] { "skill", "--help" }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var skillPresent = parent.Succeeded &&
                           CapabilityProbeParser.LooksLikeHelpOutput(parent.StdOut + parent.StdErr);
        if (!skillPresent)
        {
            _logger.Warn("probe", $"`gh skill --help` not usable (exit {parent.ExitCode})");
            return CapabilityProfile.Empty;
        }

        var search = await ProbeSubAsync(ghPath, "search", cancellationToken).ConfigureAwait(false);
        var install = await ProbeSubAsync(ghPath, "install", cancellationToken).ConfigureAwait(false);
        var update = await ProbeSubAsync(ghPath, "update", cancellationToken).ConfigureAwait(false);
        var preview = await ProbeSubAsync(ghPath, "preview", cancellationToken).ConfigureAwait(false);
        // `gh skill list` does not exist in gh 2.91 (cli/cli#13215). Probing
        // it just emits an "unknown command" error. When the upstream lands,
        // restore the probe and the list-related capability flags below.
        // var list = await ProbeSubAsync(ghPath, "list", cancellationToken).ConfigureAwait(false);

        return new CapabilityProfile
        {
            SkillSubcommandPresent = true,
            ListSubcommandPresent = false,
            SearchFlags = search.flags,
            InstallFlags = install.flags,
            UpdateFlags = update.flags,
            ListFlags = ImmutableHashSet<string>.Empty,
            PreviewFlags = preview.flags,
        };
    }

    private async Task<(bool present, ImmutableHashSet<string> flags)> ProbeSubAsync(
        string ghPath, string subcommand, CancellationToken cancellationToken)
    {
        var result = await _runner
            .RunAsync(ghPath, new[] { "skill", subcommand, "--help" }, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var combined = result.StdOut + "\n" + result.StdErr;
        var present = result.Succeeded && CapabilityProbeParser.LooksLikeHelpOutput(combined);
        if (!present)
        {
            return (false, ImmutableHashSet<string>.Empty);
        }

        var tokens = CapabilityProbeParser.ProbedTokens.TryGetValue(subcommand, out var t)
            ? t
            : ImmutableArray<string>.Empty;
        var flags = CapabilityProbeParser.ScanTokens(combined, tokens);
        _logger.Debug("probe", $"gh skill {subcommand}: {flags.Count}/{tokens.Length} known flags present");
        return (true, flags);
    }
}
