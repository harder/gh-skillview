using System.Text;
using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Ui;

namespace SkillView.Cli;

public static partial class CliDispatcher
{
    private static async Task<int> RestoreAsync(AppOptions options, TuiServices services, CancellationToken token)
    {
        var requestedCommand = options.SubcommandName!;
        var command = requestedCommand.StartsWith("update-", StringComparison.Ordinal)
            ? "restore-" + requestedCommand[7..] : requestedCommand;
        var latest = requestedCommand == "update-plan";
        var args = options.SubcommandArgs;
        string? path = null, installId = null, id = null, expected = null, disposition = null;
        var execute = false;
        var json = false;
        var allowHidden = false;
        var unpin = false;
        for (var index = 0; index < args.Count; index++)
        {
            if (args[index] == "--allow-hidden-dirs" && !allowHidden) { allowHidden = true; continue; }
            if (args[index] == "--unpin" && latest && !unpin) { unpin = true; continue; }
            if (args[index] == "--json" && !json) { json = true; continue; }
            if (args[index] == "--yes" && !execute && command is not ("restore-plan" or "restore-inspect")) { execute = true; continue; }
            if (index + 1 < args.Count && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                if (args[index] == "--path" && command == "restore-plan" && path is null && installId is null) { path = args[++index]; continue; }
                if (args[index] == "--install-id" && command == "restore-plan" && path is null && installId is null) { installId = args[++index]; continue; }
                if (args[index] == "--id" && command != "restore-plan" && id is null) { id = args[++index]; continue; }
                if (args[index] == "--expect" && command is "restore-apply" or "restore-recover" or "restore-undo" or "restore-repair-owner" && expected is null) { expected = args[++index]; continue; }
                if (args[index] == "--disposition" && command == "restore-apply" && disposition is null) { disposition = args[++index]; continue; }
            }
            Console.Error.WriteLine("skillview: unsupported or incomplete saved replacement argument");
            return ExitCodes.InvalidUsage;
        }
        if (command == "restore-plan" && ((path is null) == (installId is null) || path is not null && string.IsNullOrWhiteSpace(path))
            || command != "restore-plan" && !QuarantineStore.ValidId(id!)
            || command == "restore-apply" && (!execute || disposition != "retain-original" || !QuarantineStore.ValidHash(expected!))
            || command is "restore-recover" or "restore-undo" or "restore-repair-owner" && (!execute || !QuarantineStore.ValidHash(expected!)))
        {
            Console.Error.WriteLine("skillview: restore-plan/update-plan need one --path/--install-id. Apply needs --id --yes --disposition retain-original --expect <plan fingerprint>. Recovery/undo/owner repair need --id --yes --expect <current inspection fingerprint>.");
            return ExitCodes.InvalidUsage;
        }
        OperationImpact? frozenImpact = null;
        try
        {
            if (path is not null) path = Path.GetFullPath(path, services.Runner.EffectiveWorkingDirectory);
            var gh = services.GhLocator.FindOnPath();
            var inventory = await services.InventoryService.CaptureAsync(gh,
                new LocalInventoryService.Options(options.ScanRoots, allowHidden, WorkspaceDirectories: options.WorkspaceDirectories, Profiles: options.Profiles), token).ConfigureAwait(false);
            var backupRoot = MaintenanceBackupStore.ResolveRoot(services.Runner);
            var store = new ReplacementPlanStore(Path.Combine(Path.GetDirectoryName(backupRoot)!, "replacements"), services.Logger,
                runner: services.Runner, subjectObserved: impact => frozenImpact = impact);
            ReplacementResult result;
            if (command == "restore-plan")
            {
                if (gh is null) throw new IOException("Saved replacement staging requires GitHub CLI.");
                var selected = inventory.Skills.Where(skill => installId is not null ? skill.InstallationId == installId
                    : PathIdentity.Equals(skill.ResolvedPath, path!)).ToArray();
                if (selected.Length != 1) throw new IOException("Select one exact captured installation; put --scan-root before the command.");
                var target = selected[0];
                frozenImpact = OperationImpact.Payload(OperationSubject.From(target), requestedCommand);
                if (latest)
                {
                    var preparation = await Task.Run(() => store.PlanLatestAsync(gh, target, inventory, unpin, token), token).ConfigureAwait(false);
                    if (preparation.Unchanged is { } current)
                    {
                        frozenImpact = frozenImpact with
                        {
                            PayloadRetained = true,
                            RetainedPayloadPath = current.Path,
                            RetentionEvidence = "verified-update-plan-no-op; original payload remains installed"
                        };
                        if (json) WriteJson(w =>
                        {
                            w.WriteStartObject(); w.WriteNumber("schemaVersion", 1); w.WriteString("sessionState", "unknown"); w.WriteBoolean("securityIsolation", false);
                            w.WriteBoolean("succeeded", current.CleanupWarning is null); w.WriteNull("operation");
                            w.WritePropertyName("unchanged"); JsonSerializer.Serialize(w, current, ReplacementJsonContext.Default.ReplacementUnchanged);
                            WriteImpact(w, frozenImpact);
                            w.WriteEndObject();
                        });
                        else Console.WriteLine(TerminalEscapeSanitizer.Sanitize($"unchanged  {current.Path}\n{current.Message}\n{current.CleanupWarning}"));
                        if (!json) WriteImpactText(frozenImpact);
                        return current.CleanupWarning is null ? ExitCodes.Success : ExitCodes.UserError;
                    }
                    result = preparation.Plan!;
                }
                else result = await Task.Run(() => store.PlanAsync(target, inventory, async (destination, ct) =>
                {
                    await new GhRecordedTreeStager(services.Runner).StageIntoAsync(gh, target.FrontMatter.Upstream!, target.TreeSha!, destination, bytes =>
                    {
                        if (bytes.Length > PayloadSnapshot.MaxSkillBytes) throw new IOException("Staged SKILL.md exceeds its metadata budget.");
                        var text = new UTF8Encoding(false, true).GetString(bytes);
                        return Encoding.UTF8.GetBytes(RecordedTrackingWriter.Bind(text, target.FrontMatter));
                    }, ct).ConfigureAwait(false);
                }, token), token).ConfigureAwait(false);
            }
            else result = await Task.Run(() => command switch
            {
                "restore-inspect" => store.Inspect(id!, inventory, token),
                "restore-apply" => store.Apply(id!, expected!, inventory, token),
                "restore-repair-owner" => store.RepairOwner(id!, expected!, inventory, token),
                _ => store.Recover(id!, expected!, inventory, command == "restore-undo", token)
            }, token).ConfigureAwait(false);
            if (json) WriteJson(w =>
            {
                w.WriteStartObject(); w.WriteNumber("schemaVersion", 1); w.WriteString("sessionState", "unknown"); w.WriteBoolean("securityIsolation", false);
                w.WritePropertyName("operation"); JsonSerializer.Serialize(w, result, ReplacementJsonContext.Default.ReplacementResult);
                WriteImpact(w, result.Impact is { } impact ? impact with { Operation = requestedCommand } : frozenImpact);
                w.WriteEndObject();
            });
            else Console.WriteLine(TerminalEscapeSanitizer.Sanitize(
                $"{result.Id}  {result.Status}  {result.OriginalPath}\nPlan: {result.PlanFingerprint}\nReview: {result.ReviewFingerprint}\nRetained: {result.RetainedPath}\nManifest: {result.PlanPath}\nRoot mode: {result.OriginalRootMode} -> {result.CandidateRootMode}\n"
                + string.Join("\n", result.ChangedPaths.Select(change => "Changed: " + change)) + "\n" + result.Message));
            if (!json) WriteImpactText(result.Impact ?? frozenImpact);
            return result.Canceled ? ExitCodes.Cancelled : result.Succeeded ? ExitCodes.Success : ExitCodes.UserError;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or EntryPointNotFoundException
            || ex is OperationCanceledException && token.IsCancellationRequested)
        {
            if (json) WriteJson(w =>
            {
                w.WriteStartObject(); w.WriteNumber("schemaVersion", 1); w.WriteBoolean("succeeded", false); w.WriteString("error", ex.Message);
                w.WriteBoolean("canceled", ex is OperationCanceledException);
                WriteImpact(w, frozenImpact is { } impact ? impact with { Operation = requestedCommand } : null);
                if (ex is GhSourceRequestException source)
                { w.WriteString("stdout", source.StdOut); w.WriteString("stderr", source.StdErr); }
                w.WriteEndObject();
            });
            else
            {
                Console.Error.WriteLine("skillview: " + TerminalEscapeSanitizer.Sanitize(ex.Message));
                if (ex is GhSourceRequestException source)
                {
                    if (!string.IsNullOrWhiteSpace(source.StdOut)) Console.Out.WriteLine(TerminalEscapeSanitizer.Sanitize(source.StdOut));
                    if (!string.IsNullOrWhiteSpace(source.StdErr)) Console.Error.WriteLine(TerminalEscapeSanitizer.Sanitize(source.StdErr));
                }
            }
            if (!json) WriteImpactText(frozenImpact);
            return ex is OperationCanceledException ? ExitCodes.Cancelled : ex is ArgumentException ? ExitCodes.InvalidUsage : ExitCodes.UserError;
        }
    }
}
