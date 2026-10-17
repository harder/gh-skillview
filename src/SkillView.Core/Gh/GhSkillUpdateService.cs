using System.Collections.Immutable;
using SkillView.Gh.Models;
using SkillView.Logging;
using SkillView.Subprocess;
using SkillView.Inventory.Models;
using System.Text.RegularExpressions;
using SkillView.Ui;
using SkillView.Maintenance;
using SkillView.Inventory;
using SkillView.Preferences;

namespace SkillView.Gh;

/// Wraps `gh skill update`. SkillView requires gh ≥ 2.97.0, so `--dry-run`,
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
    internal ProcessRunner Runner => _runner;
    internal string EffectiveHome => ResolveHome(null);

    private string ResolveHome(IReadOnlyDictionary<string, string>? environment)
    {
        var home = _runner.GetEffectiveEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME", environment)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home)) throw new IOException("Cannot establish gh's global lockfile home.");
        return Path.GetFullPath(home, _runner.EffectiveWorkingDirectory);
    }
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
        bool Unpin = false,
        string? Directory = null,
        bool ConfirmApply = true,
        IReadOnlyList<InstalledSkill>? Targets = null,
        IReadOnlyDictionary<string, string>? ProcessEnvironment = null,
        bool AllowHiddenDirectories = false);

    public async Task<UpdateResult> UpdateAsync(
        string ghPath,
        Options? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new Options();
        var args = BuildArgs(options);
        DesiredRevisionPolicyStore.WriterLease? desiredPolicy = null;
        try
        {
            if (!options.DryRun)
            {
                try
                {
                    GhSharedLockGuard.ValidateCapturedOwners(options.Targets, cancellationToken);
                    var records = GhSharedLockGuard.ValidateCurrent(cancellationToken, ResolveHome(options.ProcessEnvironment));
                    GhSharedLockGuard.ValidateUpdateRecords(records, options.Targets);
                    if (options.Targets is not { Count: 1 } || options.Skills is not { Count: 1 }
                        || options.All || options.Directory is null)
                        throw new IOException("A real adapter update requires one exact captured installation, directory and selector; use the saved update workflow.");
                    var target = options.Targets[0];
                    if (target.Validity != ValidityState.Valid || !target.FrontMatter.Diagnostics.IsDefaultOrEmpty
                        || !target.MetadataConflicts.IsDefaultOrEmpty || !target.OwnershipDiagnostics.IsDefaultOrEmpty)
                        throw new IOException("Captured update metadata or owner evidence is invalid, conflicted or incomplete; refresh before updating.");
                    if (!OwnershipPath.Same(options.Directory, Path.GetDirectoryName(target.ResolvedPath)!)
                        || options.Skills[0] != Path.GetFileName(target.ResolvedPath))
                        throw new IOException("The update selector/directory does not identify its exact captured physical installation.");
                    // Incidental gh registry isolation can change the child's HOME;
                    // policy remains bound to the original application context.
                    desiredPolicy = DesiredRevisionPolicyStore.For(_runner).Acquire([target.ScanRoot, target.ResolvedPath], cancellationToken);
                    var current = PayloadSnapshot.Capture(target.ResolvedPath, cancellationToken).FrontMatter
                        ?? throw new IOException("Current update tracking metadata is unavailable.");
                    if (!current.Diagnostics.IsDefaultOrEmpty)
                        throw new IOException("Current update tracking metadata is invalid or unknown; no update child was admitted.");
                    var live = target with { FrontMatter = current };
                    if (DesiredRevisionPolicyStore.Bind(live) != DesiredRevisionPolicyStore.Bind(target)
                        || current.GithubRef != target.FrontMatter.GithubRef || current.GithubTreeSha != target.FrontMatter.GithubTreeSha
                        || current.Pinned != target.FrontMatter.Pinned || current.PinnedRef != target.FrontMatter.PinnedRef)
                        throw new IOException("The captured update source, revision or pin changed before launch.");
                    DesiredRevisionPolicyGate.RequireAllowed(live, desiredPolicy.Observe(live, cancellationToken),
                        "update-revision", explicitUnpin: options.Unpin);
                    desiredPolicy.Revalidate(cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { return UpdateResult.Failure(false, 2, ex.Message, args); }
            }
            var result = await _runner.RunAsync(ghPath, args, cancellationToken: cancellationToken,
                retainCancellationOutput: true, environment: options.ProcessEnvironment).ConfigureAwait(false);
            if (!result.Succeeded)
                _logger.Warn("gh.skill.update", $"exit={result.ExitCode} err={Logger.ErrorSnippet(result.StdErr)}");
            var entries = ParseEntries(result.StdOut, options.DryRun).AddRange(ParseEntries(result.StdErr, options.DryRun));
            return new UpdateResult
            {
                DryRun = options.DryRun,
                Succeeded = result.Succeeded,
                ExitCode = result.ExitCode,
                StdOut = result.StdOut,
                StdErr = result.StdErr,
                Cancelled = result.Cancelled,
                MutationAttempted = !options.DryRun,
                ErrorMessage = result.Succeeded ? null : result.Cancelled ? "Update canceled; partial changes may exist." : result.StdErr.Trim(),
                CommandLine = args,
                Entries = entries,
                Notices = ParseNotices(result.StdOut, "stdout").AddRange(ParseNotices(result.StdErr, "stderr")),
            };
        }
        finally { desiredPolicy?.Dispose(); }
    }

    internal static IReadOnlyList<string> BuildArgs(Options options)
    {
        var args = new List<string> { "skill", "update" };

        if (options.All || options.ConfirmApply)
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

        if (options.Directory is not null)
        {
            if (string.IsNullOrWhiteSpace(options.Directory)) throw new ArgumentException("Update directory must not be empty.");
            args.Add("--dir");
            args.Add(options.Directory);
        }

        if (options.Skills is { Count: > 0 } skills)
        {
            args.Add("--");
            foreach (var s in skills)
            {
                if (string.IsNullOrWhiteSpace(s) || s.Any(char.IsControl))
                    throw new ArgumentException("Update selectors must be nonblank and contain no control characters.");
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
    internal static ImmutableArray<UpdateEntry> ParseEntries(string stdout, bool dryRun = false)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return ImmutableArray<UpdateEntry>.Empty;

        var lines = stdout.Replace("\r\n", "\n").Split('\n');
        var builder = ImmutableArray.CreateBuilder<UpdateEntry>();
        foreach (var raw in lines)
        {
            var line = (TerminalEscapeSanitizer.Sanitize(raw) ?? string.Empty).TrimStart(' ', '\t', '-', '*', '•', '✓', '✔', '!', '⊘', '✗');
            if (line.Length == 0) continue;
            if (line.StartsWith('#')) continue;

            var entry = TryClassify(line, dryRun);
            if (entry is not null) builder.Add(entry);
            if (builder.Count >= 4096) break;
        }
        return builder.ToImmutable();
    }

    private static UpdateEntry? TryClassify(string line, bool dryRun = false)
    {
        if (line == "All skills are up to date.") return null;
        var completion = Regex.Match(line, @"^Updated (?<name>\S+)$", RegexOptions.CultureInvariant);
        if (completion.Success) return new UpdateEntry { Name = completion.Groups["name"].Value, Status = "updated" };
        var transition = Regex.Match(line, @"^(?<name>\S+) \((?<repo>[^)]+)\) (?<from>\S*) > (?<to>\S+) \[[^]]+\]$", RegexOptions.CultureInvariant);
        if (transition.Success) return new UpdateEntry
        {
            Name = transition.Groups["name"].Value,
            Repo = transition.Groups["repo"].Value,
            FromVersion = transition.Groups["from"].Value,
            ToVersion = transition.Groups["to"].Value,
            Status = "available",
        };
        var reinstall = Regex.Match(line, @"^(?<name>\S+) \((?<repo>[^)]+)\) (?<sha>\S+) \(reinstall\) \[[^]]+\]$", RegexOptions.CultureInvariant);
        if (reinstall.Success) return new UpdateEntry { Name = reinstall.Groups["name"].Value, Repo = reinstall.Groups["repo"].Value, Status = "restore-available" };
        var pin = Regex.Match(line, @"^(?<name>\S+) is pinned to .+ \(skipped\)$", RegexOptions.CultureInvariant);
        if (pin.Success) return new UpdateEntry { Name = pin.Groups["name"].Value, Status = "pinned" };
        var skipped = Regex.Match(line, @"^(?:Skipping (?<name>\S+)|(?<name>\S+) has no GitHub metadata)", RegexOptions.CultureInvariant);
        if (skipped.Success) return new UpdateEntry { Name = skipped.Groups["name"].Value.TrimEnd(':'), Status = "skipped" };
        var missing = Regex.Match(line, @"^Skill (?<name>\S+) not found in ", RegexOptions.CultureInvariant);
        if (missing.Success) return new UpdateEntry { Name = missing.Groups["name"].Value, Status = "source-unavailable" };
        var failed = Regex.Match(line, @"^Failed to update (?<name>[^:]+):", RegexOptions.CultureInvariant);
        if (failed.Success) return new UpdateEntry { Name = failed.Groups["name"].Value, Status = "failed" };
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
                    Status = dryRun ? "available" : "updated",
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

    private static ImmutableArray<UpdateNotice> ParseNotices(string text, string stream) =>
        text.Replace("\r\n", "\n").Split('\n').Where(line => !string.IsNullOrWhiteSpace(line))
            .Take(512).Select(line => new UpdateNotice(stream, line.Length > 4096 ? line[..4096] : line)).ToImmutableArray();

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
