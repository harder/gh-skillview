using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Inventory;
using SkillView.Maintenance;
using SkillView.Ui;

namespace SkillView.Cli;

public static partial class CliDispatcher
{
    private static async Task<int> LocalReviewAsync(AppOptions options, TuiServices services, CancellationToken token)
    {
        var command = options.SubcommandName!;
        string? id = null, expected = null;
        var json = false; var hidden = false;
        var choices = new List<LocalPathChoice>();
        var resolutions = new List<LocalResolutionInput>();
        var args = options.SubcommandArgs;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--json" && !json) { json = true; continue; }
            if (args[i] == "--allow-hidden-dirs" && !hidden) { hidden = true; continue; }
            if (command == "update-merge-choose" && args[i] == "--resolve" && i + 2 < args.Count)
            {
                var target = args[++i]; var file = args[++i];
                choices.Add(new(target, "resolved")); resolutions.Add(new(target, file)); continue;
            }
            if (i + 1 < args.Count)
            {
                if (args[i] == "--id" && id is null) { id = args[++i]; continue; }
                if (args[i] == "--expect" && expected is null) { expected = args[++i]; continue; }
                if (command == "update-merge-choose" && args[i] is "--choose-installed" or "--choose-source" or "--choose-resolved")
                {
                    var side = args[i] == "--choose-installed" ? "installed" : args[i] == "--choose-source" ? "source" : "resolved";
                    choices.Add(new(args[++i], side)); continue;
                }
            }
            Console.Error.WriteLine("skillview: unsupported or incomplete local-review argument");
            return ExitCodes.InvalidUsage;
        }
        if (!QuarantineStore.ValidId(id!) || !QuarantineStore.ValidHash(expected!))
        {
            Console.Error.WriteLine("skillview: local review needs --id and --expect. Review uses the source plan fingerprint; choose/build use the current local review fingerprint.");
            return ExitCodes.InvalidUsage;
        }
        try
        {
            resolutions = resolutions.Select(input => input with { SourceFile = Path.GetFullPath(input.SourceFile, services.Runner.EffectiveWorkingDirectory) }).ToList();
            var gh = services.GhLocator.FindOnPath();
            var inventory = await services.InventoryService.CaptureAsync(gh,
                new LocalInventoryService.Options(options.ScanRoots, hidden, WorkspaceDirectories: options.WorkspaceDirectories, Profiles: options.Profiles), token).ConfigureAwait(false);
            var backup = MaintenanceBackupStore.ResolveRoot(services.Runner);
            var store = new ReplacementPlanStore(Path.Combine(Path.GetDirectoryName(backup)!, "replacements"), services.Logger, runner: services.Runner);
            if (command is "update-merge-plan" or "update-preserve")
            {
                var plan = await Task.Run(() => command == "update-preserve" ? store.PreserveInstalled(id!, expected!, inventory, token)
                    : store.FinalizeLocalChanges(id!, expected!, inventory, token), token).ConfigureAwait(false);
                if (json) WriteJson(w => JsonSerializer.Serialize(w, plan, ReplacementJsonContext.Default.ReplacementResult));
                else Console.WriteLine(TerminalEscapeSanitizer.Sanitize($"{plan.Id}  {plan.Status}\nPlan: {plan.PlanFingerprint}\nManifest: {plan.PlanPath}\n{plan.Message}"
                    + (command == "update-merge-plan" ? $"\nApply with update-apply --id {plan.Id} --yes --disposition merge-reviewed --expect {plan.PlanFingerprint}" : "")));
            }
            else
            {
                var review = await Task.Run(() => command == "update-merge-review"
                    ? store.ReviewLocalChanges(id!, expected!, inventory, token)
                    : store.ChooseLocalChanges(id!, expected!, choices, inventory, token, resolutions), token).ConfigureAwait(false);
                if (json) WriteJson(w => JsonSerializer.Serialize(w, review, LocalReviewJsonContext.Default.LocalUpdateReview));
                else
                {
                    Console.WriteLine(TerminalEscapeSanitizer.Sanitize($"{review.Id}  local-review\nReview: {review.ReviewFingerprint}\nEvidence: {review.ReviewPath}\n{review.Message}"));
                    foreach (var row in review.Review.Paths)
                        Console.WriteLine(TerminalEscapeSanitizer.Sanitize($"{row.Path}: {row.Selection ?? "unresolved"} ({row.Reason})"));
                    foreach (var issue in review.Review.TopologyConflicts.Concat(review.MetadataRefusals))
                        Console.WriteLine(TerminalEscapeSanitizer.Sanitize(issue));
                }
            }
            return ExitCodes.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException
            || ex is OperationCanceledException && token.IsCancellationRequested)
        {
            if (json) WriteJson(w =>
            {
                w.WriteStartObject(); w.WriteBoolean("succeeded", false); w.WriteString("error", ex.Message);
                w.WriteBoolean("canceled", ex is OperationCanceledException); w.WriteEndObject();
            });
            else Console.Error.WriteLine("skillview: " + TerminalEscapeSanitizer.Sanitize(ex.Message));
            return ex is OperationCanceledException ? ExitCodes.Cancelled : ExitCodes.UserError;
        }
    }
}
