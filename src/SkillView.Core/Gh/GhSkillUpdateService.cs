using System.Collections.Immutable;
using SkillView.Gh.Models;
using SkillView.Logging;
using SkillView.Subprocess;

namespace SkillView.Gh;

/// Wraps `gh skill update`. SkillView requires gh ≥ 2.99.0, so `--dry-run`,
/// `--all`, `--force`, and `--unpin` are all guaranteed; flags emit
/// unconditionally and positional skill names pass straight through.
///
/// `gh skill update --all` is non-interactive on its own — it updates without
/// prompting and skips metadata-less skills with a notice, so no
/// `--yes`/`--non-interactive` workaround is needed. `gh skill update` has no
/// `--json` flag; SkillView's own `--json` output is rendered by the CLI layer
/// from the parsed textual result.
public sealed class GhSkillUpdateService
{
    private readonly ProcessRunner _runner;
    private readonly Logger _logger;

    public GhSkillUpdateService(ProcessRunner runner, Logger logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public sealed record Options(
        IReadOnlyList<string>? Skills = null,
        bool All = false,
        bool DryRun = false,
        bool Force = false,
        bool Unpin = false);

    public async Task<UpdateResult> UpdateAsync(
        string ghPath,
        Options? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new Options();
        var args = BuildArgs(options);
        var result = await _runner.RunAsync(ghPath, args, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            _logger.Warn("gh.skill.update", $"exit={result.ExitCode} err={Logger.ErrorSnippet(result.StdErr)}");
            return UpdateResult.Failure(options.DryRun, result.ExitCode, result.StdErr.Trim(), args);
        }

        return new UpdateResult
        {
            DryRun = options.DryRun,
            Succeeded = true,
            ExitCode = 0,
            StdOut = result.StdOut,
            StdErr = result.StdErr,
            ErrorMessage = null,
            CommandLine = args,
            Entries = ParseEntries(result.StdOut),
        };
    }

    internal static IReadOnlyList<string> BuildArgs(Options options)
    {
        var args = new List<string> { "skill", "update" };

        if (options.All)
        {
            args.Add("--all");
        }

        if (options.DryRun)
        {
            args.Add("--dry-run");
        }

        if (options.Force)
        {
            args.Add("--force");
        }

        if (options.Unpin)
        {
            args.Add("--unpin");
        }

        if (options.Skills is { Count: > 0 } skills)
        {
            foreach (var s in skills)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                args.Add(s);
            }
        }

        return args;
    }

    /// Best-effort parse of `gh skill update` / `gh skill update --dry-run`
    /// textual output. Upstream wording is not frozen, so we accept a set of
    /// common line shapes:
    ///   "- foo: would update v1.0.0 → v1.1.0"
    ///   "foo: up-to-date"
    ///   "foo (pinned): skipped"
    ///   "Updating foo from v1.0.0 to v1.1.0"
    /// Anything we can't classify is attached with Status = "unknown" so the
    /// UI / CLI still shows the row. When `--json` lands, switch to structured
    /// parsing.
    internal static ImmutableArray<UpdateEntry> ParseEntries(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return ImmutableArray<UpdateEntry>.Empty;

        var lines = stdout.Replace("\r\n", "\n").Split('\n');
        var builder = ImmutableArray.CreateBuilder<UpdateEntry>();
        foreach (var raw in lines)
        {
            var line = raw.TrimStart(' ', '\t', '-', '*', '•');
            if (line.Length == 0) continue;
            if (line.StartsWith('#')) continue;

            var entry = TryClassify(line);
            if (entry is not null) builder.Add(entry);
        }
        return builder.ToImmutable();
    }

    private static UpdateEntry? TryClassify(string line)
    {
        // "Updating foo from v1 to v2" / "would update foo v1 → v2"
        foreach (var arrow in new[] { "→", "->", " to " })
        {
            var arrowIdx = line.IndexOf(arrow, StringComparison.OrdinalIgnoreCase);
            if (arrowIdx < 0) continue;

            var head = line[..arrowIdx].Trim().TrimEnd(':').Trim();
            var tail = line[(arrowIdx + arrow.Length)..].Trim();
            var (name, fromV) = SplitLastVersion(head);
            var toV = StripVersion(tail);
            if (!string.IsNullOrEmpty(name))
            {
                return new UpdateEntry
                {
                    Name = name,
                    FromVersion = fromV,
                    ToVersion = toV,
                    Status = "updated",
                };
            }
        }

        // "foo: up-to-date" / "foo: pinned" / "foo: skipped" / "foo: failed"
        var colon = line.IndexOf(':');
        if (colon > 0 && colon < line.Length - 1)
        {
            var name = line[..colon].Trim();
            var rest = line[(colon + 1)..].Trim().ToLowerInvariant();
            if (rest.Contains("up-to-date") || rest.Contains("up to date"))
                return new UpdateEntry { Name = name, Status = "up-to-date" };
            if (rest.Contains("pinned"))
                return new UpdateEntry { Name = name, Status = "pinned" };
            if (rest.Contains("skip"))
                return new UpdateEntry { Name = name, Status = "skipped" };
            if (rest.Contains("fail") || rest.Contains("error"))
                return new UpdateEntry { Name = name, Status = "failed" };
        }

        return null;
    }

    private static (string Name, string? Version) SplitLastVersion(string s)
    {
        // Handles "foo v1.0.0" and "Updating foo from v1.0.0".
        var tokens = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return (string.Empty, null);

        var last = tokens[^1];
        if (LooksLikeVersion(last))
        {
            var nameTokens = tokens[..^1];
            // Drop a trailing "from" keyword if present.
            if (nameTokens.Length > 0 && nameTokens[^1].Equals("from", StringComparison.OrdinalIgnoreCase))
                nameTokens = nameTokens[..^1];
            var name = string.Join(' ', nameTokens).Trim();
            // "Updating foo" → "foo"
            if (name.StartsWith("Updating ", StringComparison.OrdinalIgnoreCase))
                name = name["Updating ".Length..].Trim();
            if (name.StartsWith("would update ", StringComparison.OrdinalIgnoreCase))
                name = name["would update ".Length..].Trim();
            return (name, last);
        }

        return (s, null);
    }

    private static string? StripVersion(string s)
    {
        var t = s.Trim().TrimEnd('.', ',', ';');
        return LooksLikeVersion(t) ? t : t.Length == 0 ? null : t;
    }

    private static bool LooksLikeVersion(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        if (s[0] == 'v' || char.IsDigit(s[0])) return s.Contains('.') || s.Length <= 12;
        return false;
    }
}
