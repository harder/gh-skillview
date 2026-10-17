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
    private static async Task<int> RestoreAsync(AppOptions options, TuiServices services, CancellationToken token,
        MaintenancePlanReference? reference = null)
    {
        var requestedCommand = options.SubcommandName!;
        var sourceSwitch = requestedCommand.StartsWith("source-switch-", StringComparison.Ordinal);
        var localReimport = requestedCommand.StartsWith("reimport-", StringComparison.Ordinal);
        var unpinOnly = requestedCommand.StartsWith("unpin-", StringComparison.Ordinal);
        var command = sourceSwitch ? "restore-" + requestedCommand[14..] : requestedCommand.StartsWith("update-", StringComparison.Ordinal)
            ? "restore-" + requestedCommand[7..] : unpinOnly ? "restore-" + requestedCommand[6..] : localReimport ? "restore-" + requestedCommand[9..] : requestedCommand;
        var expectedPurpose = sourceSwitch ? ReplacementPlanStore.LocalSourceSwitchPurpose : localReimport ? "local-reimport" : unpinOnly ? "unpin-only" : requestedCommand.StartsWith("update-", StringComparison.Ordinal) ? "update-revision" : "restore-recorded";
        var latest = requestedCommand == "update-plan";
        var args = options.SubcommandArgs;
        string? path = null, installId = null, id = null, expected = null, disposition = null, newSource = null, sourceReview = null;
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
                if (args[index] == "--new-source" && sourceSwitch && command == "restore-plan" && newSource is null) { newSource = args[++index]; continue; }
                if (args[index] == "--path" && command == "restore-plan" && path is null && installId is null) { path = args[++index]; continue; }
                if (args[index] == "--install-id" && command == "restore-plan" && path is null && installId is null) { installId = args[++index]; continue; }
                if (args[index] == "--id" && command != "restore-plan" && id is null) { id = args[++index]; continue; }
                if (args[index] == "--review" && command == "restore-ack-source" && sourceReview is null) { sourceReview = args[++index]; continue; }
                if (args[index] == "--expect" && command is "restore-ack-source" or "restore-apply" or "restore-recover" or "restore-undo" or "restore-repair-owner" && expected is null) { expected = args[++index]; continue; }
                if (args[index] == "--disposition" && command == "restore-apply" && disposition is null) { disposition = args[++index]; continue; }
            }
            Console.Error.WriteLine("skillview: unsupported or incomplete saved replacement argument");
            return ExitCodes.InvalidUsage;
        }
        if (sourceSwitch && command == "restore-plan" && (string.IsNullOrWhiteSpace(newSource) || !Path.IsPathFullyQualified(newSource) || newSource.Any(char.IsControl))
            || command == "restore-plan" && ((path is null) == (installId is null) || path is not null && string.IsNullOrWhiteSpace(path))
            || command != "restore-plan" && !QuarantineStore.ValidId(id!)
            || command == "restore-apply" && (!execute || !(sourceSwitch ? disposition == ReplacementPlanStore.LocalSourceSwitchDisposition : localReimport ? disposition == "replace-local-retain-original" : unpinOnly ? disposition == "unpin-only" : disposition == "retain-original" || requestedCommand == "update-apply" && disposition == "merge-reviewed") || !QuarantineStore.ValidHash(expected!))
            || command == "restore-ack-source" && (!execute || !QuarantineStore.ValidHash(expected!) || !QuarantineStore.ValidHash(sourceReview!))
            || command is "restore-recover" or "restore-undo" or "restore-repair-owner" && (!execute || !QuarantineStore.ValidHash(expected!)))
        {
            Console.Error.WriteLine("skillview: restore-plan/update-plan/unpin-plan/reimport-plan need one --path/--install-id. source-switch-plan also requires --new-source <absolute directory>; source-switch-apply requires replace-local-source-retain-original. Apply needs --id --yes --disposition retain-original (merge-reviewed for a merged update; unpin-only for unpin-apply; replace-local-retain-original for reimport-apply) --expect <plan fingerprint>. Source acknowledgement needs --id --expect <plan fingerprint> --review <source-review fingerprint> --yes. Recovery/undo/owner repair need --id --yes --expect <current inspection fingerprint>.");
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
                runner: services.Runner, subjectObserved: impact => frozenImpact = impact with { Operation = requestedCommand }, sourceReviewRequired: true);
            if (reference is not null)
            {
                var referenced = store.Inspect(id!, inventory, token, expectedPurpose);
                reference.RequireMatch(referenced.Reference);
                if (command == "restore-recover" && referenced.Status == "ready")
                    throw new IOException("The saved candidate has not replaced the installation. Use apply with explicit retention consent; repair cannot start a ready plan. If inspection reports an interrupted pre-move journal, review the specialist recovery command explicitly.");
            }
            ReplacementResult result;
            if (command == "restore-plan")
            {
                if (gh is null && !unpinOnly) throw new IOException("Saved replacement staging requires GitHub CLI.");
                InstalledSkill target;
                if (localReimport || sourceSwitch) target = SelectLocalReimportTarget(inventory, path, installId);
                else
                {
                    var selected = inventory.Skills.Where(skill => installId is not null ? skill.InstallationId == installId
                        : PathIdentity.Equals(skill.ResolvedPath, path!)).ToArray();
                    if (selected.Length != 1) throw new IOException("Select one exact captured installation; put --scan-root before the command.");
                    target = selected[0];
                }
                frozenImpact = OperationImpact.Payload(OperationSubject.From(target), requestedCommand);
                if (latest || unpinOnly || localReimport || sourceSwitch)
                {
                    var preparation = sourceSwitch
                        ? await Task.Run(() => store.PlanLocalSourceSwitchAsync(gh!, target, newSource!, inventory, token), token).ConfigureAwait(false)
                        : localReimport
                        ? await Task.Run(() => store.PlanLocalReimportAsync(gh!, target, inventory, token), token).ConfigureAwait(false)
                        : unpinOnly
                        ? await Task.Run(() => store.PlanUnpin(target, inventory, token), token).ConfigureAwait(false)
                        : await Task.Run(() => store.PlanLatestAsync(gh!, target, inventory, unpin, token), token).ConfigureAwait(false);
                    if (preparation.Unchanged is { } current)
                    {
                        frozenImpact = frozenImpact with
                        {
                            PayloadRetained = true,
                            RetainedPayloadPath = current.Path,
                            RetentionEvidence = localReimport ? "verified-local-reimport-no-op; original payload and effective metadata remain installed" : unpinOnly ? "verified-unpin-plan-no-op; original payload remains installed" : "verified-update-plan-no-op; original payload remains installed"
                        };
                        if (json) WriteJson(w =>
                        {
                            w.WriteStartObject(); w.WriteNumber("schemaVersion", 1); w.WriteString("sessionState", "unknown"); w.WriteBoolean("securityIsolation", false);
                            w.WriteString("status", "unchanged"); w.WriteString("reason", current.Message);
                            w.WriteBoolean("partial", current.CleanupWarning is not null);
                            w.WriteBoolean("succeeded", current.CleanupWarning is null); w.WriteNull("operation");
                            w.WritePropertyName("unchanged"); JsonSerializer.Serialize(w, current, ReplacementJsonContext.Default.ReplacementUnchanged);
                            WriteImpact(w, frozenImpact);
                            w.WriteEndObject();
                        });
                        else Console.WriteLine(TerminalEscapeSanitizer.Sanitize($"unchanged  {current.Path}\n{current.Message}\n{current.CleanupWarning}"
                            + (current.LocalSource is { } source ? $"\nLocal source: {source.Status}: {source.Message}" : "")));
                        if (!json) WriteImpactText(frozenImpact);
                        return current.CleanupWarning is null ? ExitCodes.Success : ExitCodes.UserError;
                    }
                    result = preparation.Plan!;
                }
                else result = await Task.Run(() => store.PlanAsync(target, inventory, async (destination, ct) =>
                {
                    await new GhRecordedTreeStager(services.Runner).StageIntoAsync(gh!, target.FrontMatter.Upstream!, target.TreeSha!, destination, bytes =>
                    {
                        if (bytes.Length > PayloadSnapshot.MaxSkillBytes) throw new IOException("Staged SKILL.md exceeds its metadata budget.");
                        var text = new UTF8Encoding(false, true).GetString(bytes);
                        return Encoding.UTF8.GetBytes(RecordedTrackingWriter.Bind(text, target.FrontMatter));
                    }, ct).ConfigureAwait(false);
                }, token), token).ConfigureAwait(false);
            }
            else result = await Task.Run(() => command switch
            {
                "restore-ack-source" => store.AcknowledgeSourceReview(id!, expected!, sourceReview!, inventory, token, expectedPurpose),
                "restore-inspect" => store.Inspect(id!, inventory, token, expectedPurpose),
                "restore-apply" => store.Apply(id!, expected!, inventory, token, disposition!, expectedPurpose),
                "restore-repair-owner" => store.RepairOwner(id!, expected!, inventory, token, expectedPurpose),
                _ => store.Recover(id!, expected!, inventory, command == "restore-undo", token, expectedPurpose)
            }, token).ConfigureAwait(false);
            if (json) WriteJson(w =>
            {
                w.WriteStartObject(); w.WriteNumber("schemaVersion", 1); w.WriteString("sessionState", "unknown"); w.WriteBoolean("securityIsolation", false);
                w.WriteString("status", result.Canceled ? "canceled" : result.Succeeded ? result.Status : result.MutationObserved ? "partial" : "refused");
                w.WriteString("reason", result.Message); w.WriteBoolean("partial", !result.Succeeded || result.Canceled);
                w.WritePropertyName("operation"); JsonSerializer.Serialize(w, result, ReplacementJsonContext.Default.ReplacementResult);
                WritePlanReference(w, result);
                w.WriteString("diskVerification", result.Succeeded
                    ? command == "restore-ack-source" ? "not-performed; source-review acknowledgement only" : "verified-saved-locations"
                    : "incomplete; inspect operation status and retained evidence");
                if ((unpinOnly || localReimport || sourceSwitch) && command == "restore-plan")
                {
                    w.WriteStartArray("applyArguments");
                    foreach (var argument in new[] { sourceSwitch ? "source-switch-apply" : localReimport ? "reimport-apply" : "unpin-apply", "--id", result.Id, "--yes", "--disposition", sourceSwitch ? ReplacementPlanStore.LocalSourceSwitchDisposition : localReimport ? "replace-local-retain-original" : "unpin-only", "--expect", result.PlanFingerprint }) w.WriteStringValue(argument);
                    if (allowHidden) w.WriteStringValue("--allow-hidden-dirs");
                    w.WriteEndArray();
                }
                WriteImpact(w, result.Impact is { } impact ? impact with { Operation = requestedCommand } : frozenImpact);
                w.WriteEndObject();
            });
            else Console.WriteLine(TerminalEscapeSanitizer.Sanitize(
                $"{result.Id}  {result.Status}  {result.OriginalPath}\nPlan: {result.PlanFingerprint}\nReview: {result.ReviewFingerprint}\nRetained: {result.RetainedPath}\nManifest: {result.PlanPath}\nRoot mode: {result.OriginalRootMode} -> {result.CandidateRootMode}\n"
                + string.Join("\n", result.ChangedPaths.Take(128).Select(change => "Changed: " + change)) + (result.ChangedPaths.Length > 128 ? $"\nOmitted changed paths: {result.ChangedPaths.Length - 128}; complete list in the saved plan." : "") + "\n" + result.Message));
            if (!json) Console.WriteLine(SourceReviewScreen.RenderText(result));
            if (!json && result.LocalImport is not null) Console.WriteLine(LocalReimportScreen.RenderText(result));
            if (!json) WriteImpactText(result.Impact ?? frozenImpact);
            if (!json && (unpinOnly || localReimport || sourceSwitch) && command == "restore-plan")
                Console.WriteLine($"Apply: skillview {(sourceSwitch ? "source-switch-apply" : localReimport ? "reimport-apply" : "unpin-apply")} --id {result.Id} --yes --disposition {(sourceSwitch ? ReplacementPlanStore.LocalSourceSwitchDisposition : localReimport ? "replace-local-retain-original" : "unpin-only")} --expect {result.PlanFingerprint}{(allowHidden ? " --allow-hidden-dirs" : "")}");
            return result.Canceled ? ExitCodes.Cancelled : result.Succeeded ? ExitCodes.Success : ExitCodes.UserError;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or EntryPointNotFoundException
            || ex is OperationCanceledException && token.IsCancellationRequested)
        {
            if (json) WriteJson(w =>
            {
                w.WriteStartObject(); w.WriteNumber("schemaVersion", 1); w.WriteBoolean("succeeded", false); w.WriteString("error", ex.Message);
                w.WriteString("status", ex is OperationCanceledException ? "canceled" : "refused");
                w.WriteString("reason", ex.Message); w.WriteString("sessionState", "unknown");
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
    internal static InstalledSkill SelectLocalReimportTarget(InventorySnapshot inventory, string? path, string? installId)
    {
        if ((path is null) == (installId is null)) throw new ArgumentException("Choose exactly one path or installation ID.");
        var selected = inventory.Skills.Where(skill => installId is not null ? skill.InstallationId == installId
            : PathIdentity.Equals(skill.ResolvedPath, path!) || skill.Agents.Any(agent => PathIdentity.Equals(agent.Path, path!))).ToArray();
        return selected.Length == 1 ? selected[0]
            : throw new IOException("Select one exact captured installation; put --scan-root before the command.");
    }

}
