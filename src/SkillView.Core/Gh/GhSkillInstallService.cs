using SkillView.Gh.Models;
using SkillView.Logging;
using SkillView.Subprocess;

namespace SkillView.Gh;

/// Wraps `gh skill install`. SkillView requires gh ≥ 2.94.0, so every flag it
/// emits (`--all`, `--allow-hidden-dirs`, `--upstream`, `--agent` repeatable,
/// `--from-local`, `--scope`, `--dir`, `--pin`, `--force`) is guaranteed to
/// exist — there is no per-flag capability gating. A custom directory maps to
/// gh's `--dir` (gh has no `--path`).
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
        bool FromLocal = false,
        bool All = false);

    public async Task<InstallResult> InstallAsync(
        string ghPath,
        string repo,
        string? skillName,
        Options? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new Options();
        var args = BuildArgs(repo, skillName, options);
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

        // `gh skill install <repo> --all` installs every discovered skill
        // without prompting (gh 2.94.0, cli/cli#13471). Mutually exclusive
        // with a skill-name argument; callers pass one or the other.
        if (options.All)
        {
            args.Add("--all");
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
            // gh's custom-directory flag is `--dir` (it has no `--path`).
            args.Add("--dir");
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

        if (!string.IsNullOrEmpty(options.Upstream))
        {
            args.Add("--upstream");
            args.Add(options.Upstream);
        }

        if (options.AllowHiddenDirs)
        {
            args.Add("--allow-hidden-dirs");
        }

        if (options.FromLocal)
        {
            args.Add("--from-local");
        }

        return args;
    }
}
