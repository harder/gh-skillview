using System.Collections.Immutable;
using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Maintenance;
using SkillView.Ui;

namespace SkillView.Cli;

public static partial class CliDispatcher
{
    private static async Task<int> InspectAsync(AppOptions options, TuiServices services, CancellationToken token)
    {
        var arguments = options.SubcommandArgs;
        var json = arguments.Contains("--json");
        try
        {
            if (arguments.Contains("--plan-file"))
            {
                string? path = null;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (var index = 0; index < arguments.Count; index++)
                {
                    var argument = arguments[index];
                    if (!seen.Add(argument)) throw new ArgumentException("Duplicate inspection option: " + argument);
                    if (argument == "--json") continue;
                    if (argument != "--plan-file" || ++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index])
                        || arguments[index].StartsWith("--", StringComparison.Ordinal) || arguments[index].Any(char.IsControl))
                        throw new ArgumentException("Reference inspection accepts only --plan-file FILE and --json.");
                    path = arguments[index];
                }
                var reference = MaintenancePlanReference.Read(Path.GetFullPath(path!, services.Runner.EffectiveWorkingDirectory), token);
                var prefix = GetCommandName(options.InvocationMode) + " " + reference.SpecialistPrefix;
                var inspect = $"{prefix}-inspect --id {reference.Id}";
                var recover = $"{prefix}-recover --id {reference.Id} --yes --expect CURRENT_REVIEW_FINGERPRINT";
                var undo = $"{prefix}-undo --id {reference.Id} --yes --expect CURRENT_REVIEW_FINGERPRINT";
                const string guidance = "Unverified legacy locator only; the local retained store was not inspected. Keep the original HOME, state, CWD and scan/workspace/profile options. Run specialist inspection first and review its exact purpose, address, context and fingerprints. Recovery resumes a saved transaction; it does not repair a skill or replace a ready candidate without specialist review. Recovery/undo require the current inspection fingerprint and separate approval. Retained originals have not been changed.";
                if (json) WriteJson(writer =>
                {
                    writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 1); writer.WriteString("status", "guidance-only");
                    writer.WritePropertyName("reference");
                    JsonSerializer.Serialize(writer, reference, MaintenancePlanReferenceJsonContext.Default.MaintenancePlanReference);
                    writer.WriteBoolean("storeInspected", false); writer.WriteBoolean("mutationObserved", false);
                    writer.WriteString("inspectCommand", inspect); writer.WriteString("recoverCommand", recover); writer.WriteString("undoCommand", undo);
                    writer.WriteString("reason", guidance); writer.WriteString("sessionState", "unknown"); writer.WriteEndObject();
                });
                else Console.WriteLine(TerminalEscapeSanitizer.Sanitize($"Saved ID: {reference.Id}\nPurpose: {reference.Purpose}\nOriginal path: {reference.OriginalPath}\nPlan fingerprint: {reference.PlanFingerprint}\nContext fingerprint: {reference.ContextFingerprint}\n{guidance}\nInspect: {inspect}\nRecover: {recover}\nUndo: {undo}"));
                return ExitCodes.Success;
            }
            var parsed = ParseCheckArgs(arguments);
            if (parsed.Error is not null) throw new ArgumentException(parsed.Error);
            var inventory = await CaptureInventoryAsync(options, services, cancellationToken: token).ConfigureAwait(false);
            var selected = SelectCheckTarget(inventory, parsed, services.Runner.EffectiveWorkingDirectory);
            if (selected is null) return Refuse("The exact installation is missing or ambiguous. Refresh queue; no sibling was selected.", ExitCodes.NoMatches);
            var queue = MaintenanceQueueBuilder.Build(inventory, token);
            queue = queue with { Entries = queue.Entries.Where(entry => entry.Subject.InstallationId == selected.InstallationId).ToImmutableArray() };
            if (json) WriteJson(writer => WriteQueueJson(writer, queue));
            else Console.Write(RenderQueueText(queue));
            return queue.Partial ? ExitCodes.UserError : ExitCodes.Success;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { return Refuse(exception.Message, exception is ArgumentException ? ExitCodes.InvalidUsage : ExitCodes.UserError); }

        int Refuse(string reason, int code)
        {
            if (json) WriteJson(writer =>
            {
                writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 1);
                writer.WriteString("status", code == ExitCodes.NoMatches ? "no-matches" : "refused");
                writer.WriteString("operation", "inspect"); writer.WriteString("reason", reason);
                writer.WriteBoolean("mutationObserved", false); writer.WriteString("sessionState", "unknown"); writer.WriteEndObject();
            });
            else Console.Error.WriteLine("skillview: " + TerminalEscapeSanitizer.Sanitize(reason));
            return code;
        }
    }
}
