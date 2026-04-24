using SkillView.Gh.Models;
using SkillView.Logging;
using SkillView.Subprocess;

namespace SkillView.Gh;

/// Wraps `gh skill install`. Capability-gated flags
/// (`--allow-hidden-dirs`, `--upstream`, `--agent` repeatable, `--repo-path`,
/// `--from-local`) attach only when the probe confirms them. Scope / path /
/// version / pin / force are emitted as baseline v2.91.0 flags.
public sealed class GhSkillInstallService
{
    private readonly ProcessRunner _runner;
    private readonly Logger _logger;

    public GhSkillInstallService(ProcessRunner runner, Logger logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public sealed record Options(
        IReadOnlyList<string>? Agents = null,
        string? Scope = null,
        string? Path = null,
        string? Version = null,
        bool Pin = false,
        bool Overwrite = false,
        string? Upstream = null,
        bool AllowHiddenDirs = false,
        string? RepoPath = null,
        bool FromLocal = false);

    public async Task<InstallResult> InstallAsync(
        string ghPath,
        string repo,
        string? skillName,
        CapabilityProfile capabilities,
        Options? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new Options();
        var args = BuildArgs(repo, skillName, capabilities, options);
        var result = await _runner.RunAsync(ghPath, args, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _logger.Warn("gh.skill.install", $"exit={result.ExitCode} err={result.StdErr.Trim()}");
            return InstallResult.Failure(repo, skillName, options.Version, result.ExitCode, result.StdErr.Trim(), args);
        }

        return new InstallResult
        {
            Repo = repo,
            SkillName = skillName,
            Version = options.Version,
            Succeeded = true,
            ExitCode = 0,
            StdOut = result.StdOut,
            StdErr = result.StdErr,
            ErrorMessage = null,
            CommandLine = args,
        };
    }

    internal static IReadOnlyList<string> BuildArgs(
        string repo,
        string? skillName,
        CapabilityProfile capabilities,
        Options options)
    {
        var args = new List<string> { "skill", "install" };

        // Versioned install uses the `owner/repo@<ref>` shorthand, mirroring
        // `gh skill preview`. Keeps the adapter surface consistent across
        // remote-operation commands.
        if (!string.IsNullOrEmpty(options.Version))
        {
            args.Add($"{repo}@{options.Version}");
        }
        else
        {
            args.Add(repo);
        }

        if (!string.IsNullOrEmpty(skillName))
        {
            args.Add(skillName);
        }

        if (options.Agents is { Count: > 0 } agents)
        {
            foreach (var agent in agents)
            {
                if (string.IsNullOrWhiteSpace(agent)) continue;
                args.Add("--agent");
                args.Add(agent);
            }
        }

        if (!string.IsNullOrEmpty(options.Scope))
        {
            args.Add("--scope");
            args.Add(options.Scope);
        }

        if (!string.IsNullOrEmpty(options.Path))
        {
            args.Add("--path");
            args.Add(options.Path);
        }

        if (options.Pin)
        {
            args.Add("--pin");
        }

        if (options.Overwrite)
        {
            args.Add("--force");
        }

        if (!string.IsNullOrEmpty(options.Upstream) && capabilities.SupportsUpstream)
        {
            args.Add("--upstream");
            args.Add(options.Upstream);
        }

        if (options.AllowHiddenDirs && capabilities.SupportsAllowHiddenDirs)
        {
            args.Add("--allow-hidden-dirs");
        }

        if (!string.IsNullOrEmpty(options.RepoPath) && capabilities.SupportsRepoPath)
        {
            args.Add("--repo-path");
            args.Add(options.RepoPath);
        }

        if (options.FromLocal && capabilities.SupportsFromLocal)
        {
            args.Add("--from-local");
        }

        return args;
    }
}
