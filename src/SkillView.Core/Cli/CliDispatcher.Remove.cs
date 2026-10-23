using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Ui;

namespace SkillView.Cli;

public static partial class CliDispatcher
{

    private static async Task<int> RemoveAsync(
        AppOptions options,
        TuiServices services,
        CancellationToken cancellationToken)
    {
        var parsed = ParseRemoveArgs(options.SubcommandArgs);
        if (parsed.Error is not null || parsed.Name is null && parsed.Path is null && parsed.InstallationId is null)
        {
            Console.Error.WriteLine("skillview: " + (parsed.Error ?? "remove requires a skill name or exact selector"));
            Console.Error.WriteLine("usage: skillview remove [name] [--path <path> | --install-id <id>] [--agent <id>] [--yes] [--json]");
            return ExitCodes.InvalidUsage;
        }
        if (parsed.Path is { } path)
        {
            try { parsed = parsed with { Path = Path.GetFullPath(path, services.Runner.EffectiveWorkingDirectory) }; }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            { Console.Error.WriteLine("skillview: invalid remove path: " + TerminalEscapeSanitizer.Sanitize(ex.Message)); return ExitCodes.InvalidUsage; }
        }

        var report = await services.EnvironmentProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await services.InventoryService.CaptureAsync(
            report.GhPath,
            new Inventory.LocalInventoryService.Options(
                ScanRoots: options.ScanRoots, WorkspaceDirectories: options.WorkspaceDirectories, Profiles: options.Profiles,
                AllowHiddenDirs: false),
            cancellationToken
        ).ConfigureAwait(false);

        var matches = SelectRemoveTargets(snapshot, parsed);

        if (matches.Count == 0)
        {
            Console.Error.WriteLine($"skillview: no installed skill matches '{parsed.Name ?? parsed.Path ?? parsed.InstallationId}'" +
                (parsed.Agent is null ? "" : $" for agent '{parsed.Agent}'"));
            return ExitCodes.NoMatches;
        }
        if (matches.Count > 1)
        {
            Console.Error.WriteLine($"skillview: {matches.Count} skills match '{parsed.Name}' — select --path or --install-id");
            foreach (var m in matches) Console.Error.WriteLine($"  · {m.ResolvedPath} ({string.Join(",", m.Agents.Select(a => a.AgentId))})");
            return ExitCodes.UserError;
        }

        var target = matches[0];
        var (validation, result) = await RemoveTargetAsync(target, snapshot, parsed.Yes,
            services.RemoveService, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded && parsed.Yes) services.ListAdapter.Invalidate();

        if (parsed.Json) WriteRemoveJson(result, target, parsed, validation);
        else WriteRemoveText(result, target, parsed, validation);

        if (result.IsCanceled) return ExitCodes.Cancelled;
        if (!validation.Allowed)
        {
            return IsEnvironmentRefusal(validation)
                ? ExitCodes.EnvironmentError
                : ExitCodes.UserError;
        }
        if (!result.Succeeded) return ExitCodes.EnvironmentError;
        return ExitCodes.Success;
    }

    internal static async Task<(RemoveValidator.RemoveValidation Validation, RemoveService.RemoveReport Report)> RemoveTargetAsync(
        InstalledSkill target, InventorySnapshot snapshot, bool execute, RemoveService service, CancellationToken token,
        IProgress<RemoveService.RemoveProgress>? progress = null)
    {
        var impact = OperationImpact.Payload(OperationSubject.From(target), "remove");
        RemoveValidator.RemoveValidation validation;
        try
        {
            validation = execute
                ? RemoveValidator.Validate(target, snapshot.ProtectedRoots.ToArray(), snapshot.Skills, token)
                : RemoveValidator.ValidateForPreview(target, snapshot.ProtectedRoots.ToArray(), snapshot.Skills, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            validation = new([], [], target.ResolvedPath, []) { Impact = impact };
            return (validation, new(false, target.ResolvedPath, 0, 0, [], !execute)
            { IsCanceled = true, Impact = impact });
        }
        var result = await service.RemoveWithOutcomeAsync(validation, new(DryRun: !execute), token, progress).ConfigureAwait(false);
        return (validation, result);
    }

    internal record ParsedRemoveArgs(string? Name, string? Agent, bool Yes, bool Json,
        string? Path = null, string? InstallationId = null, string? Error = null);

    internal static List<InstalledSkill> SelectRemoveTargets(InventorySnapshot snapshot, ParsedRemoveArgs parsed) => snapshot.Skills
        .Where(skill => parsed.Name is null || string.Equals(skill.Name, parsed.Name, StringComparison.OrdinalIgnoreCase))
        .Where(skill => parsed.Agent is null || skill.Agents.Any(agent => AgentIdentity.Matches(agent.AgentId, parsed.Agent)))
        .Where(skill => parsed.Path is null || PathIdentity.Equals(skill.ResolvedPath, parsed.Path)
            || skill.Agents.Any(agent => PathIdentity.Equals(agent.Path, parsed.Path)))
        .Where(skill => parsed.InstallationId is null || string.Equals(skill.InstallationId, parsed.InstallationId, StringComparison.OrdinalIgnoreCase))
        .ToList();

    internal static ParsedRemoveArgs ParseRemoveArgs(IReadOnlyList<string> args)
    {
        string? name = null, agent = null, path = null, id = null, error = null;
        bool yes = false, json = false;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--json") { json = true; continue; }
            if (a == "--yes" || a == "-y") { yes = true; continue; }
            var key = a.Split('=', 2)[0];
            if (key is "--agent" or "--path" or "--install-id")
            {
                var value = a.Contains('=') ? a[(a.IndexOf('=') + 1)..]
                    : i + 1 < args.Count && !args[i + 1].StartsWith('-') ? args[++i] : null;
                if (string.IsNullOrWhiteSpace(value)) { error = key + " requires a nonempty value"; continue; }
                if (key == "--agent") { if (agent is not null) error = "repeat --agent is unsupported"; agent = value; }
                else if (key == "--path") { if (path is not null) error = "repeat --path is unsupported"; path = value; }
                else { if (id is not null) error = "repeat --install-id is unsupported"; id = value; }
                continue;
            }
            if (a.StartsWith('-')) { error = "unsupported remove flag: " + a; continue; }
            if (name is null) name = a; else error = "remove accepts one name";
        }
        if (path is not null && id is not null) error = "select only one of --path and --install-id";
        return new ParsedRemoveArgs(name, agent, yes, json, path, id, error);
    }

    private static void WriteRemoveText(
        RemoveService.RemoveReport r,
        InstalledSkill target,
        ParsedRemoveArgs p,
        RemoveValidator.RemoveValidation validation)
    {
        Console.Out.WriteLine($"{(r.DryRun ? "remove (dry-run)" : "remove")}: {target.Name}");
        Console.Out.WriteLine($"  path     : {target.ResolvedPath}");
        Console.Out.WriteLine($"  resolved : {validation.ResolvedPath}");
        Console.Out.WriteLine($"  scope    : {target.Scope}");
        WriteImpactText(r.Impact ?? validation.Impact);
        if (validation.Errors.Length > 0)
        {
            Console.Error.WriteLine("REFUSED:");
            foreach (var e in validation.Errors) Console.Error.WriteLine($"  ✗ {e.Kind}: {e.Detail}");
            return;
        }
        if (validation.Warnings.Length > 0)
        {
            Console.Error.WriteLine("WARNINGS:");
            foreach (var w in validation.Warnings) Console.Error.WriteLine($"  ! {w.Kind}: {w.Detail}");
            if (!p.Yes)
            {
                Console.Error.WriteLine("hint: --yes is required to accept these warnings and execute");
            }
        }
        if (r.DryRun)
        {
            Console.Out.WriteLine($"  would remove: {r.FilesDeleted} file(s), {r.DirectoriesDeleted} dir(s)");
            Console.Out.WriteLine("  (dry-run; rerun with --yes to execute)");
        }
        else if (r.Succeeded)
        {
            Console.Out.WriteLine($"  removed: {r.FilesDeleted} file(s), {r.DirectoriesDeleted} dir(s)");
        }
        else
        {
            Console.Error.WriteLine($"  remove failed with {r.ErrorCount} error(s)");
            foreach (var e in r.Errors) Console.Error.WriteLine($"  · {e}");
        }
    }

    private static void WriteRemoveJson(
        RemoveService.RemoveReport r,
        InstalledSkill target,
        ParsedRemoveArgs p,
        RemoveValidator.RemoveValidation validation)
        => WriteJson(w => WriteRemoveJson(w, r, target, p, validation));

    internal static string RenderRemoveJson(
        RemoveService.RemoveReport r,
        InstalledSkill target,
        ParsedRemoveArgs p,
        RemoveValidator.RemoveValidation validation)
        => RenderJson(w => WriteRemoveJson(w, r, target, p, validation));

    private static void WriteRemoveJson(
        Utf8JsonWriter w,
        RemoveService.RemoveReport r,
        InstalledSkill target,
        ParsedRemoveArgs p,
        RemoveValidator.RemoveValidation validation)
    {
        var removalAttempted = validation.Allowed
            && (r.DryRun || !validation.RequiresSecondConfirm || p.Yes);
        w.WriteStartObject();
        w.WriteBoolean("dryRun", r.DryRun);
        w.WriteBoolean("succeeded", r.Succeeded);
        w.WriteBoolean("canceled", r.IsCanceled);
        w.WriteBoolean("allowed", validation.Allowed);
        w.WriteString("name", target.Name);
        w.WriteString("resolvedPath", validation.ResolvedPath);
        w.WriteString("scope", target.Scope.ToString());
        WriteImpact(w, r.Impact ?? validation.Impact);
        w.WriteNumber("filesDeleted", r.FilesDeleted);
        w.WriteNumber("directoriesDeleted", r.DirectoriesDeleted);
        w.WriteNumber("runtimeErrorCount", removalAttempted ? r.ErrorCount : 0);
        w.WriteStartArray("errors");
        foreach (var e in validation.Errors)
        {
            w.WriteStartObject();
            w.WriteString("kind", e.Kind.ToString());
            w.WriteString("detail", e.Detail);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("warnings");
        foreach (var warn in validation.Warnings)
        {
            w.WriteStartObject();
            w.WriteString("kind", warn.Kind.ToString());
            w.WriteString("detail", warn.Detail);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("runtimeErrors");
        if (removalAttempted)
        {
            foreach (var e in r.Errors) w.WriteStringValue(e);
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }
}
