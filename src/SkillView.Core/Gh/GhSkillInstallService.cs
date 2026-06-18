using System.Collections.Immutable;
using SkillView.Gh.Models;
using SkillView.Logging;
using SkillView.Subprocess;

namespace SkillView.Gh;

/// Wraps `gh skill install`. SkillView requires gh ≥ 2.95.0, so every flag it
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

    /// Discover the skills a repository offers without installing anything.
    /// Runs `gh skill install <repo>` with no skill name and no `--all`; in a
    /// non-interactive context (stdout is captured, so gh sees a non-TTY) gh
    /// ≥ 2.95.0 lists the discovered skills as tab-separated rows instead of
    /// erroring (cli/cli#13548). The repo is the only thing installed-from, but
    /// nothing is written to disk — this is a read-only discovery call.
    public async Task<RepoSkillListing> ListRepoSkillsAsync(
        string ghPath,
        string repo,
        string? version = null,
        bool allowHiddenDirs = false,
        CancellationToken cancellationToken = default)
    {
        var args = BuildListArgs(repo, version, allowHiddenDirs);
        var result = await _runner.RunAsync(ghPath, args, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _logger.Warn("gh.skill.install.list", $"exit={result.ExitCode} err={result.StdErr.Trim()}");
            return RepoSkillListing.Failure(repo, version, result.ExitCode, result.StdErr.Trim(), args);
        }

        var skills = ParseRepoSkillListing(result.StdOut);
        _logger.Info("gh.skill.install.list", $"{repo} → {skills.Length} skill(s) discovered");
        return new RepoSkillListing
        {
            Repo = repo,
            Version = version,
            Skills = skills,
            Succeeded = true,
            ExitCode = 0,
            ErrorMessage = null,
            CommandLine = args,
        };
    }

    internal static IReadOnlyList<string> BuildListArgs(
        string repo,
        string? version,
        bool allowHiddenDirs)
    {
        // Deliberately no skill name and no `--all`: that combination triggers
        // gh's non-interactive "list available skills" path (cli/cli#13548).
        var args = new List<string> { "skill", "install" };
        args.Add(string.IsNullOrEmpty(version) ? repo : $"{repo}@{version}");
        if (allowHiddenDirs)
        {
            args.Add("--allow-hidden-dirs");
        }
        return args;
    }

    /// Parse the tab-separated `SKILL\tDESCRIPTION` rows gh emits when the
    /// install picker is rendered to a pipe (cli/cli#13548). Tolerant by
    /// design: blank lines are skipped, a `name`-only line (no tab) keeps an
    /// empty description, and an optional `SKILL`/`NAME` header row is dropped.
    internal static ImmutableArray<RepoSkill> ParseRepoSkillListing(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return ImmutableArray<RepoSkill>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<RepoSkill>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lines = stdout.Replace("\r\n", "\n").Split('\n');
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                continue;
            }

            var tab = line.IndexOf('\t');
            var name = NormalizeSkillName((tab >= 0 ? line[..tab] : line).Trim());
            var description = tab >= 0 ? line[(tab + 1)..].Trim() : string.Empty;
            if (name.Length == 0)
            {
                continue;
            }

            // Drop a header row if gh ever emits one in TTY-table mode.
            if (description.Length > 0
                && (name.Equals("SKILL", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("NAME", StringComparison.OrdinalIgnoreCase))
                && (description.Equals("DESCRIPTION", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (seen.Add(name))
            {
                builder.Add(new RepoSkill(name, description));
            }
        }

        return builder.ToImmutable();
    }

    /// gh prefixes each listed skill with its namespace in brackets, e.g.
    /// `[root] code-review` or `[monalisa] code-review`. The bracket tag is
    /// display decoration, not part of the installable argument: root-namespace
    /// skills install by bare name, others as `namespace/name`. Strip the tag so
    /// the picker shows clean names and per-name (subset) installs actually
    /// resolve. Input without a leading `[ns] ` tag is returned unchanged.
    internal static string NormalizeSkillName(string raw)
    {
        if (raw.Length < 2 || raw[0] != '[')
        {
            return raw;
        }

        var close = raw.IndexOf(']');
        if (close < 1)
        {
            return raw;
        }

        var ns = raw[1..close].Trim();
        var rest = raw[(close + 1)..].Trim();
        if (rest.Length == 0)
        {
            return raw;
        }

        return ns.Length == 0 || ns.Equals("root", StringComparison.OrdinalIgnoreCase)
            ? rest
            : $"{ns}/{rest}";
    }

    /// How to install a chosen subset of a repo's discovered skills. When the
    /// user keeps every discovered skill checked, a single `--all` install is
    /// cheaper and matches the existing install-all path; otherwise each
    /// selected skill is installed by name.
    public readonly record struct InstallPlan(bool UseAll, ImmutableArray<string> SkillNames)
    {
        public bool IsEmpty => !UseAll && SkillNames.IsDefaultOrEmpty;
    }

    /// Pure: map (all discovered skills, set of checked names) to an
    /// <see cref="InstallPlan"/>. Selecting every skill collapses to `--all`.
    public static InstallPlan BuildInstallPlan(
        IReadOnlyList<RepoSkill> discovered,
        IReadOnlyCollection<string> selectedNames)
    {
        var selected = discovered
            .Where(s => selectedNames.Contains(s.Name))
            .Select(s => s.Name)
            .ToImmutableArray();

        if (selected.Length > 0 && selected.Length == discovered.Count)
        {
            return new InstallPlan(UseAll: true, ImmutableArray<string>.Empty);
        }

        return new InstallPlan(UseAll: false, selected);
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
