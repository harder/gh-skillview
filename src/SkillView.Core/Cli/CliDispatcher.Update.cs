using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Ui;

namespace SkillView.Cli;

public static partial class CliDispatcher
{

    private static async Task<int> UpdateAsync(
        AppOptions options,
        TuiServices services,
        CancellationToken cancellationToken)
    {
        var parsed = ParseUpdateArgs(options.SubcommandArgs);
        if (parsed.Error is not null)
        {
            Console.Error.WriteLine($"skillview: {parsed.Error}");
            return ExitCodes.InvalidUsage;
        }
        var report = await services.EnvironmentProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!report.BaselineOk)
        {
            Console.Error.WriteLine("skillview: gh or gh skill not available (run `skillview doctor`)");
            return ExitCodes.EnvironmentError;
        }

        if (!parsed.All && parsed.Skills.Count == 0 && !parsed.DryRun
            && parsed.Path is null && parsed.InstallationId is null)
        {
            Console.Error.WriteLine("skillview: update requires at least one skill, --all, or --dry-run");
            Console.Error.WriteLine(
                "usage: skillview update [<skill>]... [--all] [--dry-run] [--force] [--unpin]" +
                " [--path <path> | --install-id <id>] [--json]");
            return ExitCodes.InvalidUsage;
        }

        // Pre-snapshot for diffing: additions + version changes.
        var preSnapshot = await services.InventoryService.CaptureAsync(
            report.GhPath,
            new Inventory.LocalInventoryService.Options(
                ScanRoots: options.ScanRoots, WorkspaceDirectories: options.WorkspaceDirectories, Profiles: options.Profiles,
                AllowHiddenDirs: parsed.AllowHiddenDirectories),
            cancellationToken
        ).ConfigureAwait(false);

        if (parsed.Path is not null)
            parsed = parsed with { Path = Path.GetFullPath(parsed.Path, services.Runner.EffectiveWorkingDirectory) };

        bool MatchesSelector(InstalledSkill skill, string name) => name == skill.Name
            || skill.Agents.Any(agent => agent.DiscoveryRoot is not null
                && name == Path.GetRelativePath(agent.DiscoveryRoot, agent.Path).Replace(Path.DirectorySeparatorChar, '/'));
        var selected = preSnapshot.Skills.AsEnumerable();
        if (parsed.Skills.Count > 0)
            selected = selected.Where(skill => parsed.Skills.Any(name => MatchesSelector(skill, name)));
        if (parsed.Path is not null)
            selected = selected.Where(skill => PathIdentity.Equals(skill.ResolvedPath, parsed.Path)
                || skill.Agents.Any(agent => PathIdentity.Equals(agent.Path, parsed.Path)));
        if (parsed.InstallationId is not null)
            selected = selected.Where(skill => string.Equals(skill.InstallationId, parsed.InstallationId, StringComparison.OrdinalIgnoreCase));
        var targets = selected.ToArray();
        if (targets.Length == 0)
        {
            Console.Error.WriteLine("skillview: no physical installations match the selection");
            return ExitCodes.NoMatches;
        }
        if (parsed.Path is null && parsed.InstallationId is null && parsed.Skills.Count > 0
            && parsed.Skills.Any(name => targets.Where(target => MatchesSelector(target, name))
                .Select(target => target.InstallationId).Distinct(StringComparer.Ordinal).Count() > 1))
        {
            Console.Error.WriteLine("skillview: a name matches multiple installations; select --path or --install-id");
            return ExitCodes.UserError;
        }

        var updateOptions = new GhSkillUpdateService.Options(
            Skills: parsed.Skills,
            All: parsed.All,
            DryRun: parsed.DryRun,
            Force: parsed.Force,
            Unpin: parsed.Unpin,
            Targets: targets,
            AllowHiddenDirectories: parsed.AllowHiddenDirectories);

        var result = await Task.Run(() => services.SavedUpdates.PrepareAsync(
            report.GhPath!, updateOptions, preSnapshot, cancellationToken), cancellationToken).ConfigureAwait(false);
        IReadOnlyList<InstalledSkill> added = Array.Empty<InstalledSkill>();
        IReadOnlyList<UpdateDiffEntry> changed = Array.Empty<UpdateDiffEntry>();
        if (cancellationToken.IsCancellationRequested)
            result = result with { Cancelled = true, Succeeded = false, ExitCode = 130 };

        if (parsed.Json) WriteUpdateJson(result, parsed, added, changed);
        else WriteUpdateText(result, parsed, added, changed);

        return result.ExitCode;
    }

    internal record ParsedUpdateArgs(
        List<string> Skills,
        bool All,
        bool DryRun,
        bool Force,
        bool Unpin,
        bool Json,
        string? Path = null,
        string? InstallationId = null,
        string? Error = null,
        bool AllowHiddenDirectories = false);

    internal static ParsedUpdateArgs ParseUpdateArgs(IReadOnlyList<string> args)
    {
        var skills = new List<string>();
        bool all = false, dryRun = false, force = false, unpin = false, json = false, allowHidden = false;
        string? path = null, installationId = null, error = null;
        bool positionals = false;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (positionals) { skills.Add(a); continue; }
            if (a == "--") { positionals = true; continue; }
            if (a.StartsWith("--path=", StringComparison.Ordinal)) { path = a[7..]; continue; }
            if (a.StartsWith("--install-id=", StringComparison.Ordinal)) { installationId = a[13..]; continue; }
            if (a is "--path" or "--install-id")
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                { error = $"{a} requires a value"; continue; }
                if (a == "--path") path = args[++i]; else installationId = args[++i];
                continue;
            }
            // `--json` selects SkillView's own machine-readable output; it is
            // not passed to `gh skill update` (which has no --json flag).
            if (a == "--json") { json = true; continue; }
            if (a == "--allow-hidden-dirs") { allowHidden = true; continue; }
            if (a == "--all") { all = true; continue; }
            if (a == "--dry-run") { dryRun = true; continue; }
            if (a == "--force") { force = true; continue; }
            if (a == "--unpin") { unpin = true; continue; }
            if (a.StartsWith("-", StringComparison.Ordinal)) { error = $"unsupported update flag: {a}"; continue; }
            skills.Add(a);
        }
        if (path is not null && string.IsNullOrWhiteSpace(path)) error = "--path must not be empty";
        if (installationId is not null && string.IsNullOrWhiteSpace(installationId)) error = "--install-id must not be empty";
        return new ParsedUpdateArgs(skills, all, dryRun, force, unpin, json, path, installationId, error, allowHidden);
    }

    /// Per-skill TreeSha delta: skills present at the same ResolvedPath in
    /// both snapshots where `TreeSha` changed. Captures the "updated" axis
    /// the install diff (additions only) doesn't — Phase 4 carry-forward.
    internal static IReadOnlyList<UpdateDiffEntry> InventoryUpdateDiff(
        Inventory.Models.InventorySnapshot before,
        Inventory.Models.InventorySnapshot after)
    {
        var beforeIndex = new Dictionary<string, InstalledSkill>(StringComparer.Ordinal);
        foreach (var s in before.Skills) beforeIndex[PathIdentity.NormalizeKey(s.ResolvedPath)] = s;

        var changed = new List<UpdateDiffEntry>();
        foreach (var a in after.Skills)
        {
            if (!beforeIndex.TryGetValue(PathIdentity.NormalizeKey(a.ResolvedPath), out var b)) continue;
            var fromSha = b.TreeSha;
            var toSha = a.TreeSha;
            var fromVer = b.FrontMatter.Version;
            var toVer = a.FrontMatter.Version;
            if (!string.Equals(fromSha, toSha, StringComparison.Ordinal) ||
                !string.Equals(fromVer, toVer, StringComparison.Ordinal))
            {
                changed.Add(new UpdateDiffEntry(a.Name, a.ResolvedPath, fromVer, toVer, fromSha, toSha));
            }
        }
        return changed;
    }

    internal sealed record UpdateDiffEntry(
        string Name,
        string ResolvedPath,
        string? FromVersion,
        string? ToVersion,
        string? FromSha,
        string? ToSha);

    private static void WriteUpdateText(
        UpdateResult r, ParsedUpdateArgs p,
        IReadOnlyList<InstalledSkill> added,
        IReadOnlyList<UpdateDiffEntry> changed)
    {
        var header = p.DryRun ? "dry-run" : "update";
        Console.Out.WriteLine($"{header}: exit {r.ExitCode}");
        if (!string.IsNullOrWhiteSpace(r.StdOut)) Console.Out.WriteLine(r.StdOut.TrimEnd());
        if (!string.IsNullOrWhiteSpace(r.StdErr)) Console.Error.WriteLine(r.StdErr.TrimEnd());
        foreach (var outcome in r.Outcomes)
        {
            Console.Out.WriteLine($"  {outcome.Status}: {outcome.Path} · {outcome.Message ?? outcome.ChangedFileCount + " changed file(s)"}");
            WriteImpactText(outcome.Impact);
        }

        if (r.Entries.Length > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("parsed entries:");
            foreach (var e in r.Entries)
            {
                var from = e.FromVersion ?? "?";
                var to = e.ToVersion ?? "?";
                Console.Out.WriteLine($"  {e.Name,-30}  {e.Status,-11}  {from} → {to}");
            }
        }

        // gh 2.94 skips skills without GitHub metadata under --all / non-
        // interactive mode (cli/cli#13469). Surface the count so it's not
        // mistaken for silent success.
        var skipped = r.Entries.Count(e => e.Status == "skipped");
        if (skipped > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine($"note: {skipped} skill(s) skipped (no GitHub metadata — install via gh to enable updates)");
        }

        if (r.SavedPlan is { } plan)
        {
            Console.Out.WriteLine(TerminalEscapeSanitizer.Sanitize(UpdateScreen.RenderPlan(plan)));
            Console.Out.WriteLine($"Apply: skillview update-apply --id {plan.Id} --expect {plan.PlanFingerprint} --yes --disposition retain-original{(p.AllowHiddenDirectories ? " --allow-hidden-dirs" : "")}");
            Console.Out.WriteLine("Keep the same HOME, state, working directory and global scan/workspace/profile options for apply.");
        }
        if (p.DryRun || !r.MutationAttempted) return;

        if (added.Count > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine($"rescan: +{added.Count} new skill(s)");
            foreach (var s in added)
                Console.Out.WriteLine($"  +  {s.Name,-24}  {s.Scope,-7}  {s.ResolvedPath}");
        }
        if (changed.Count > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine($"rescan: Δ{changed.Count} changed skill(s)");
            foreach (var c in changed)
            {
                var from = c.FromVersion ?? c.FromSha ?? "?";
                var to = c.ToVersion ?? c.ToSha ?? "?";
                Console.Out.WriteLine($"  Δ  {c.Name,-24}  {from} → {to}  {c.ResolvedPath}");
            }
        }
        if (added.Count == 0 && changed.Count == 0)
        {
            Console.Out.WriteLine("rescan: no inventory changes detected");
        }
    }

    private static void WriteUpdateJson(
        UpdateResult r, ParsedUpdateArgs p,
        IReadOnlyList<InstalledSkill> added,
        IReadOnlyList<UpdateDiffEntry> changed)
        => WriteJson(w => WriteUpdateJson(w, r, p, added, changed));

    internal static string RenderUpdateJson(
        UpdateResult r, ParsedUpdateArgs p,
        IReadOnlyList<InstalledSkill> added,
        IReadOnlyList<UpdateDiffEntry> changed)
        => RenderJson(w => WriteUpdateJson(w, r, p, added, changed));

    private static void WriteUpdateJson(
        Utf8JsonWriter w,
        UpdateResult r,
        ParsedUpdateArgs p,
        IReadOnlyList<InstalledSkill> added,
        IReadOnlyList<UpdateDiffEntry> changed)
    {
        w.WriteStartObject();
        w.WriteNumber("schemaVersion", 2);
        w.WritePropertyName("savedPlan");
        if (r.SavedPlan is null) w.WriteNullValue();
        else JsonSerializer.Serialize(w, r.SavedPlan, SkillView.Maintenance.ReplacementJsonContext.Default.ReplacementResult);
        if (r.SavedPlan is { } plan)
        {
            w.WriteStartArray("applyArguments");
            foreach (var arg in new[] { "update-apply", "--id", plan.Id, "--expect", plan.PlanFingerprint, "--yes", "--disposition", "retain-original" }) w.WriteStringValue(arg);
            if (p.AllowHiddenDirectories) w.WriteStringValue("--allow-hidden-dirs");
            w.WriteEndArray();
            w.WriteString("applyContext", "Reuse the same HOME, state, working directory and global scan/workspace/profile options.");
        }
        w.WriteString("stdout", r.StdOut);
        w.WriteString("stderr", r.StdErr);
        w.WriteBoolean("canceled", r.Cancelled);
        w.WriteBoolean("mutationAttempted", r.MutationAttempted);
        w.WriteBoolean("partial", r.ReconciliationPartial || !r.Succeeded || r.Outcomes.Any(o => o.Status is "failed" or "refused" or "canceled" or "not-attempted" or "skipped" or "untracked" or "verification-incomplete" or "source-unavailable" or "availability-unknown" or "not-started" or "recovery-required"));
        w.WriteStartArray("outcomes");
        foreach (var outcome in r.Outcomes)
        {
            w.WriteStartObject();
            w.WriteString("installationId", outcome.InstallationId);
            w.WriteString("path", outcome.Path);
            w.WriteString("status", outcome.Status);
            w.WriteString("beforeFingerprint", outcome.BeforeFingerprint);
            w.WriteString("afterFingerprint", outcome.AfterFingerprint);
            w.WriteString("message", outcome.Message);
            w.WriteNumber("changedFileCount", outcome.ChangedFileCount);
            w.WriteStartArray("changedFiles");
            foreach (var file in outcome.ChangedFiles) w.WriteStringValue(file);
            w.WriteEndArray();
            w.WriteStartArray("exposures");
            foreach (var exposure in outcome.Exposures) w.WriteStringValue(exposure);
            w.WriteEndArray();
            WriteImpact(w, outcome.Impact);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("notices");
        foreach (var notice in r.Notices)
        {
            w.WriteStartObject();
            w.WriteString("stream", notice.Stream);
            w.WriteString("text", notice.Text);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteBoolean("dryRun", r.DryRun);
        w.WriteBoolean("succeeded", r.Succeeded);
        w.WriteNumber("exitCode", r.ExitCode);
        if (r.ErrorMessage is not null) w.WriteString("errorMessage", r.ErrorMessage);
        w.WriteBoolean("all", p.All);
        w.WriteBoolean("force", p.Force);
        w.WriteBoolean("unpin", p.Unpin);
        w.WriteBoolean("allowHiddenDirectories", p.AllowHiddenDirectories);

        w.WriteStartArray("skills");
        foreach (var s in p.Skills) w.WriteStringValue(s);
        w.WriteEndArray();

        w.WriteStartArray("commandLine");
        foreach (var arg in r.CommandLine) w.WriteStringValue(arg);
        w.WriteEndArray();

        w.WriteStartArray("entries");
        foreach (var e in r.Entries)
        {
            w.WriteStartObject();
            w.WriteString("name", e.Name);
            w.WriteString("status", e.Status);
            w.WriteString("fromVersion", e.FromVersion);
            w.WriteString("toVersion", e.ToVersion);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("added");
        foreach (var s in added)
        {
            w.WriteStartObject();
            w.WriteString("name", s.Name);
            w.WriteString("resolvedPath", s.ResolvedPath);
            w.WriteString("scope", s.Scope.ToString());
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("changed");
        foreach (var c in changed)
        {
            w.WriteStartObject();
            w.WriteString("name", c.Name);
            w.WriteString("resolvedPath", c.ResolvedPath);
            w.WriteString("fromVersion", c.FromVersion);
            w.WriteString("toVersion", c.ToVersion);
            w.WriteString("fromSha", c.FromSha);
            w.WriteString("toSha", c.ToSha);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteEndObject();
    }
}
