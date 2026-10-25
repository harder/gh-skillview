using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Inventory;
using SkillView.Maintenance;
using SkillView.Ui;

namespace SkillView.Cli;

public static partial class CliDispatcher
{
    private static async Task<int> PreserveUpdateAsync(AppOptions options, TuiServices services, CancellationToken token)
    {
        string? id = null, expected = null;
        var json = false; var hidden = false;
        var args = options.SubcommandArgs;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--json" && !json) { json = true; continue; }
            if (args[i] == "--allow-hidden-dirs" && !hidden) { hidden = true; continue; }
            if (i + 1 < args.Count)
            {
                if (args[i] == "--id" && id is null) { id = args[++i]; continue; }
                if (args[i] == "--expect" && expected is null) { expected = args[++i]; continue; }
            }
            Console.Error.WriteLine("skillview: unsupported or incomplete update-preserve argument");
            return ExitCodes.InvalidUsage;
        }
        if (!QuarantineStore.ValidId(id!) || !QuarantineStore.ValidHash(expected!))
        {
            Console.Error.WriteLine("skillview: update-preserve needs --id and --expect <plan fingerprint>.");
            return ExitCodes.InvalidUsage;
        }
        try
        {
            var gh = services.GhLocator.FindOnPath();
            var inventory = await services.InventoryService.CaptureAsync(gh,
                new LocalInventoryService.Options(options.ScanRoots, hidden, WorkspaceDirectories: options.WorkspaceDirectories, Profiles: options.Profiles), token).ConfigureAwait(false);
            var backup = MaintenanceBackupStore.ResolveRoot(services.Runner);
            var store = new ReplacementPlanStore(Path.Combine(Path.GetDirectoryName(backup)!, "replacements"), services.Logger, runner: services.Runner, sourceReviewRequired: true);
            var plan = await Task.Run(() => store.PreserveInstalled(id!, expected!, inventory, token), token).ConfigureAwait(false);
            if (json) WriteJson(w => JsonSerializer.Serialize(w, plan, ReplacementJsonContext.Default.ReplacementResult));
            else Console.WriteLine(TerminalEscapeSanitizer.Sanitize($"{plan.Id}  {plan.Status}\nPlan: {plan.PlanFingerprint}\nManifest: {plan.PlanPath}\n{plan.Message}"));
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
