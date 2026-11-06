using SkillView.Bootstrapping;
using SkillView.Maintenance;
using SkillView.Ui;

namespace SkillView.Cli;

public static partial class CliDispatcher
{
    private static async Task<int> ReplacementHistoryAsync(AppOptions options, TuiServices services, CancellationToken token)
    {
        if (options.SubcommandArgs.Any(arg => arg != "--json") || options.SubcommandArgs.Count > 1)
        {
            Console.Error.WriteLine("skillview: replacement-history accepts only --json; use the saved operation's specialist inspect command for full verification.");
            return ExitCodes.InvalidUsage;
        }
        var store = new ReplacementPlanStore(ReplacementPlanStore.ResolveRoot(services.Runner), services.Logger, runner: services.Runner);
        var history = await Task.Run(() => store.ReadHistory(token, options.ScanRoots), token).ConfigureAwait(false);
        if (options.SubcommandArgs.Contains("--json")) WriteJson(w =>
        {
            w.WriteStartObject(); w.WriteNumber("schemaVersion", 1); w.WriteBoolean("readOnly", true);
            w.WriteString("evidence", "saved metadata and entry-name hints only; Inspect to verify; no mutation authority");
            w.WriteNumber("observedCount", history.ObservedCount); w.WriteBoolean("hasOmissions", history.HasOmissions);
            w.WriteString("error", history.Error); w.WriteString("summary", history.Summary);
            w.WriteStartArray("entries");
            foreach (var entry in history.Entries)
            {
                w.WriteStartObject(); w.WriteString("id", entry.Id); w.WriteString("hint", entry.Hint);
                w.WriteBoolean("mayNeedRecovery", entry.MayNeedRecovery); w.WriteString("skill", entry.Skill);
                w.WriteString("purpose", entry.Purpose); w.WriteString("createdAt", entry.CreatedAt?.ToString("O"));
                w.WriteString("savedScanRoot", entry.ScanRoot); w.WriteString("operationPath", entry.OperationPath);
                w.WriteString("savedSource", entry.Source); w.WriteString("savedTarget", entry.OriginalPath);
                if (entry.RecordedSlotBytes is { } size) w.WriteNumber("recordedSlotBytesHint", size); else w.WriteNull("recordedSlotBytesHint");
                w.WriteString("detail", entry.Detail); w.WriteStartArray("slotNameHints");
                if (!entry.Slots.IsDefault) foreach (var slot in entry.Slots) w.WriteStringValue(slot);
                w.WriteEndArray(); w.WriteEndObject();
            }
            w.WriteEndArray(); w.WriteEndObject();
        });
        else
        {
            Console.WriteLine(TerminalEscapeSanitizer.Sanitize(history.Summary));
            foreach (var entry in history.Entries) Console.WriteLine(RetainedHistoryScreen.RenderEntry(entry) + "\n");
        }
        return history.Error is not null ? ExitCodes.EnvironmentError : ExitCodes.Success;
    }
}
