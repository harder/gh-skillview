using System.Collections.Immutable;
using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Ui;

namespace SkillView.Cli;

public static partial class CliDispatcher
{

    private static async Task<int> CleanupAsync(
        AppOptions options,
        TuiServices services,
        CancellationToken cancellationToken)
    {
        var parsed = ParseCleanupArgs(options.SubcommandArgs);
        var report = await services.EnvironmentProbe.ProbeAsync(cancellationToken, includeAuthentication: false).ConfigureAwait(false);
        var snapshot = await services.InventoryService.CaptureAsync(
            report.GhPath,
            new Inventory.LocalInventoryService.Options(
                ScanRoots: options.ScanRoots, WorkspaceDirectories: options.WorkspaceDirectories, Profiles: options.Profiles,
                AllowHiddenDirs: false),
            cancellationToken
        ).ConfigureAwait(false);

        var candidates = CleanupClassifier.ClassifyWithCancellation(
            snapshot,
            snapshot.ScannedRoots,
            options: null,
            cancellationToken);
        if (parsed.KindFilter is { Count: > 0 })
        {
            candidates = candidates.Where(c => parsed.KindFilter.Contains(c.Kind.ToString(), StringComparer.OrdinalIgnoreCase)).ToImmutableArray();
        }

        var applied = new List<(CleanupClassifier.Candidate C, RemoveService.RemoveReport R)>();
        var duplicateSkips = ImmutableArray<CleanupClassifier.Candidate>.Empty;
        var environmentRefusal = false;
        if (parsed.Apply)
        {
            if (!parsed.Yes)
            {
                Console.Error.WriteLine("skillview: cleanup --apply requires --yes");
                return ExitCodes.UserError;
            }
            var execution = await ApplyCleanupAsync(candidates, snapshot, services.RemoveService,
                services.Runner, cancellationToken).ConfigureAwait(false);
            applied.AddRange(execution.Applied);
            duplicateSkips = execution.Duplicates;
            environmentRefusal = execution.EnvironmentRefusal;
            if (execution.Applied.Any(item => item.R.Succeeded || item.R.FilesDeleted > 0 || item.R.DirectoriesDeleted > 0))
                services.ListAdapter.Invalidate();
        }

        if (parsed.Json) WriteCleanupJson(candidates, applied, duplicateSkips, parsed, snapshot);
        else WriteCleanupText(candidates, applied, duplicateSkips, parsed);

        if (parsed.Output is not null)
        {
            try
            {
                File.WriteAllText(parsed.Output, RenderCleanupReport(candidates));
                Console.Error.WriteLine($"wrote report to {parsed.Output}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"skillview: failed to write {parsed.Output}: {ex.Message}");
                return ExitCodes.EnvironmentError;
            }
        }

        if (applied.Any(item => item.R.IsCanceled)) return ExitCodes.Cancelled;
        if (candidates.Length == 0) return ExitCodes.Success;
        if (parsed.Apply && environmentRefusal) return ExitCodes.EnvironmentError;
        if (parsed.Apply && applied.Any(a => !a.R.Succeeded)) return ExitCodes.UserError;
        return ExitCodes.Success;
    }

    internal sealed record CleanupExecution(
        IReadOnlyList<(CleanupClassifier.Candidate C, RemoveService.RemoveReport R)> Applied,
        ImmutableArray<CleanupClassifier.Candidate> Duplicates, bool EnvironmentRefusal);

    internal static async Task<CleanupExecution> ApplyCleanupAsync(ImmutableArray<CleanupClassifier.Candidate> candidates,
        InventorySnapshot snapshot, RemoveService service, SkillView.Subprocess.ProcessRunner? runner, CancellationToken token)
    {
        var applied = new List<(CleanupClassifier.Candidate C, RemoveService.RemoveReport R)>();
        CleanupClassifier.CandidateSelection selection;
        try { selection = CleanupClassifier.DeduplicateByPath(candidates, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new(candidates.Select(candidate => (candidate, CanceledBeforeStart(candidate))).ToArray(), [], false);
        }
        var environmentRefusal = false;
        foreach (var candidate in selection.Unique)
        {
            if (token.IsCancellationRequested) { applied.Add((candidate, CanceledBeforeStart(candidate))); continue; }
            RemoveValidator.RemoveValidation validation;
            try { validation = ValidateCleanupCandidate(candidate, snapshot.ProtectedRoots.ToArray(), snapshot.Skills, token, runner); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { applied.Add((candidate, CanceledBeforeStart(candidate))); continue; }
            validation = validation with { Impact = validation.Impact ?? candidate.Impact };
            if (!validation.Allowed || validation.RequiresSecondConfirm)
            {
                environmentRefusal |= IsEnvironmentRefusal(validation);
                var reason = validation.Allowed ? "requires second-confirm"
                    : string.Join("; ", validation.Errors.Select(error => $"{error.Kind}: {error.Detail}"));
                applied.Add((candidate, RemoveService.RemoveReport.Refused(validation.ResolvedPath, reason) with { Impact = validation.Impact }));
                continue;
            }
            applied.Add((candidate, await service.RemoveWithOutcomeAsync(validation, cancellationToken: token).ConfigureAwait(false)));
        }
        return new(applied, selection.Duplicates, environmentRefusal);
    }

    private static RemoveService.RemoveReport CanceledBeforeStart(CleanupClassifier.Candidate candidate) =>
        new(false, candidate.Path, 0, 0, [], false) { IsCanceled = true, NotAttempted = true, Impact = candidate.Impact };

    private static bool IsEnvironmentRefusal(
        RemoveValidator.RemoveValidation validation) =>
        validation.Errors.Any(error =>
            error.Kind == RemoveValidator.ErrorKind.FilesystemIdentityUnavailable);

    internal record ParsedCleanupArgs(
        IReadOnlyList<string>? KindFilter,
        bool Apply,
        bool Yes,
        bool Json,
        string? Output);

    internal static ParsedCleanupArgs ParseCleanupArgs(IReadOnlyList<string> args)
    {
        List<string>? kinds = null;
        bool apply = false, yes = false, json = false;
        string? output = null;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--apply") { apply = true; continue; }
            if (a == "--yes" || a == "-y") { yes = true; continue; }
            if (a == "--json") { json = true; continue; }
            if (a.StartsWith("--candidates=", StringComparison.Ordinal))
            {
                kinds = a["--candidates=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                continue;
            }
            if (a == "--candidates"
                && i + 1 < args.Count
                && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
            {
                kinds = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                continue;
            }
            if (a.StartsWith("--output=", StringComparison.Ordinal)) { output = a["--output=".Length..]; continue; }
            if (a == "--output" && i + 1 < args.Count) { output = args[++i]; continue; }
        }
        return new ParsedCleanupArgs(kinds, apply, yes, json, output);
    }

    private static RemoveValidator.RemoveValidation ValidateCleanupCandidate(
        CleanupClassifier.Candidate candidate,
        IReadOnlyList<ScanRoot> scanRoots,
        IReadOnlyList<InstalledSkill> allSkills,
        CancellationToken cancellationToken, SkillView.Subprocess.ProcessRunner? ownershipRunner = null)
    {
        return candidate.ReviewOnly ? RefuseUnsupportedCleanupCandidate(candidate) : candidate.Kind switch
        {
            CleanupClassifier.CandidateKind.BrokenSymlink =>
                RemoveValidator.ValidateBrokenSymlink(candidate.Path, scanRoots, cancellationToken, ownershipRunner),
            CleanupClassifier.CandidateKind.EmptyDirectory =>
                RemoveValidator.ValidateEmptyDirectory(candidate.Path, scanRoots, cancellationToken, ownershipRunner),
            _ when candidate.Skill is not null => RemoveValidator.Validate(candidate.Skill, scanRoots, allSkills, cancellationToken),
            _ => RefuseUnsupportedCleanupCandidate(candidate),
        };
    }

    private static RemoveValidator.RemoveValidation RefuseUnsupportedCleanupCandidate(
        CleanupClassifier.Candidate candidate) =>
        new(
            ImmutableArray.Create(new RemoveValidator.Error(
                RemoveValidator.ErrorKind.NotASkillDirectory,
                $"cleanup candidate kind '{candidate.Kind}' requires comparison or ownership review, not bulk removal")),
            ImmutableArray<RemoveValidator.Warning>.Empty,
            candidate.Path,
            ImmutableArray<string>.Empty);

    private static void WriteCleanupText(
        IReadOnlyList<CleanupClassifier.Candidate> candidates,
        IReadOnlyList<(CleanupClassifier.Candidate C, RemoveService.RemoveReport R)> applied,
        IReadOnlyList<CleanupClassifier.Candidate> duplicateSkips,
        ParsedCleanupArgs p)
    {
        Console.Out.WriteLine($"cleanup: {candidates.Count} candidate(s){(p.Apply ? " (--apply)" : "")}");
        foreach (var c in candidates)
        {
            Console.Out.WriteLine($"  {c.Kind,-22}  {c.Path}");
            Console.Out.WriteLine($"    why: {c.Reason}");
            WriteImpactText(c.Impact);
        }
        if (applied.Count > 0 || duplicateSkips.Count > 0)
        {
            var ok = applied.Count(a => a.R.Succeeded);
            Console.Out.WriteLine();
            Console.Out.WriteLine($"applied: {ok}/{applied.Count} succeeded");
            if (duplicateSkips.Count > 0)
            {
                Console.Out.WriteLine(
                    $"skipped: {duplicateSkips.Count} duplicate candidate path(s)");
            }
            foreach (var (c, r) in applied.Where(a => !a.R.Succeeded))
            {
                Console.Out.WriteLine($"  ✗ {c.Path}: {string.Join("; ", r.Errors)}");
            }
        }
    }

    private static void WriteCleanupJson(
        IReadOnlyList<CleanupClassifier.Candidate> candidates,
        IReadOnlyList<(CleanupClassifier.Candidate C, RemoveService.RemoveReport R)> applied,
        IReadOnlyList<CleanupClassifier.Candidate> duplicateSkips,
        ParsedCleanupArgs p, InventorySnapshot? snapshot = null)
        => WriteJson(w => WriteCleanupJson(w, candidates, applied, duplicateSkips, p, snapshot));

    internal static string RenderCleanupJson(
        IReadOnlyList<CleanupClassifier.Candidate> candidates,
        IReadOnlyList<(CleanupClassifier.Candidate C, RemoveService.RemoveReport R)> applied,
        ParsedCleanupArgs p,
        IReadOnlyList<CleanupClassifier.Candidate>? duplicateSkips = null)
        => RenderJson(w => WriteCleanupJson(
            w,
            candidates,
            applied,
            duplicateSkips ?? [],
            p));

    private static void WriteCleanupJson(
        Utf8JsonWriter w,
        IReadOnlyList<CleanupClassifier.Candidate> candidates,
        IReadOnlyList<(CleanupClassifier.Candidate C, RemoveService.RemoveReport R)> applied,
        IReadOnlyList<CleanupClassifier.Candidate> duplicateSkips,
        ParsedCleanupArgs p, InventorySnapshot? snapshot = null)
    {
        w.WriteStartObject();
        w.WriteBoolean("apply", p.Apply);
        w.WriteBoolean("partial", (snapshot?.IsPartial ?? false) || applied.Any(item => item.R.IsCanceled));
        w.WriteBoolean("canceled", applied.Any(item => item.R.IsCanceled));
        w.WriteStartArray("inaccessiblePaths");
        foreach (var path in snapshot?.Diagnostics.InaccessiblePaths ?? []) w.WriteStringValue(path);
        w.WriteEndArray();
        w.WriteNumber("candidates", candidates.Count);
        w.WriteStartArray("entries");
        foreach (var c in candidates)
        {
            w.WriteStartObject();
            w.WriteString("kind", c.Kind.ToString());
            w.WriteBoolean("reviewOnly", c.ReviewOnly);
            w.WriteString("path", c.Path);
            w.WriteString("reason", c.Reason);
            w.WriteString("name", c.Skill?.Name);
            WriteImpact(w, c.Impact);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        if (p.Apply)
        {
            w.WriteNumber("skippedCandidates", duplicateSkips.Count);
            w.WriteStartArray("applied");
            foreach (var (c, r) in applied)
            {
                w.WriteStartObject();
                w.WriteString("path", c.Path);
                w.WriteBoolean("succeeded", r.Succeeded);
                w.WriteBoolean("canceled", r.IsCanceled);
                w.WriteString("status", r.NotAttempted ? "not-attempted" : r.IsCanceled ? "canceled" : r.Succeeded ? "completed" : "refused-or-failed");
                WriteImpact(w, r.Impact ?? c.Impact);
                w.WriteNumber("filesDeleted", r.FilesDeleted);
                w.WriteNumber("directoriesDeleted", r.DirectoriesDeleted);
                w.WriteStartArray("errors");
                foreach (var e in r.Errors) w.WriteStringValue(e);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("skipped");
            foreach (var candidate in duplicateSkips)
            {
                w.WriteStartObject();
                w.WriteString("path", candidate.Path);
                w.WriteString("kind", candidate.Kind.ToString());
                w.WriteBoolean("reviewOnly", candidate.ReviewOnly);
                w.WriteString("reason", "duplicate candidate path");
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    internal static string RenderCleanupReport(IReadOnlyList<CleanupClassifier.Candidate> candidates)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# SkillView cleanup report — {DateTimeOffset.UtcNow:O}");
        sb.AppendLine($"candidates: {candidates.Count}");
        foreach (var c in candidates)
        {
            sb.AppendLine();
            sb.AppendLine($"- kind : {c.Kind}");
            sb.AppendLine($"  path : {c.Path}");
            sb.AppendLine($"  why  : {c.Reason}");
        }
        return sb.ToString();
    }
}
