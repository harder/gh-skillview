using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Maintenance;
using SkillView.Ui;

namespace SkillView.Cli;

public static partial class CliDispatcher
{
    internal sealed record QueueArguments(bool Json, string? InstallationId, string? Error);

    internal static QueueArguments ParseQueueArgs(IReadOnlyList<string> arguments)
    {
        var json = arguments.Contains("--json");
        string? id = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (!seen.Add(argument)) return new(json, id, "Duplicate queue option: " + argument);
            if (argument is "--json" or "--allow-hidden-dirs") continue;
            if (argument != "--install-id" || ++index >= arguments.Count)
                return new(json, id, "queue accepts --json, --install-id ID and --allow-hidden-dirs.");
            id = arguments[index];
            if (id.Length != 64 || id.Any(character => !char.IsAsciiHexDigit(character)))
                return new(json, null, "--install-id requires an exact 64-character installation address ID.");
        }
        return new(json, id, null);
    }

    private static async Task<int> QueueAsync(AppOptions options, TuiServices services, CancellationToken token)
    {
        var arguments = ParseQueueArgs(options.SubcommandArgs);
        if (arguments.Error is { } error)
        {
            if (arguments.Json) WriteJson(writer =>
            {
                writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 1);
                writer.WriteString("status", "refused"); writer.WriteString("reason", error); writer.WriteEndObject();
            });
            else Console.Error.WriteLine("skillview: " + TerminalEscapeSanitizer.Sanitize(error));
            return ExitCodes.InvalidUsage;
        }
        // Refresh observations only: no update query, saved plan, skill execution or agent launch.
        services.ListAdapter.Invalidate();
        var snapshot = await CaptureInventoryAsync(options, services, cancellationToken: token).ConfigureAwait(false);
        var queue = MaintenanceQueueBuilder.Build(snapshot, token);
        if (arguments.InstallationId is { } id)
            queue = queue with { Entries = queue.Entries.Where(entry => entry.Subject.InstallationId == id).ToImmutableArray() };
        token.ThrowIfCancellationRequested();
        if (arguments.Json) WriteJson(writer => WriteQueueJson(writer, queue));
        else Console.Write(RenderQueueText(queue));
        return queue.Partial ? ExitCodes.UserError : queue.Entries.IsEmpty ? ExitCodes.NoMatches : ExitCodes.Success;
    }

    internal static string RenderQueueJson(MaintenanceQueue queue) => RenderJson(writer => WriteQueueJson(writer, queue));

    private static void WriteQueueJson(Utf8JsonWriter writer, MaintenanceQueue queue)
    {
        writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 1);
        writer.WriteString("status", queue.Partial ? "partial" : queue.Entries.IsEmpty ? "no-matches" : "observed");
        writer.WriteString("capturedAt", queue.CapturedAt);
        writer.WriteBoolean("partial", queue.Partial); writer.WriteBoolean("mutationAuthority", false);
        writer.WriteString("refresh", "Run queue again to refresh disk inventory; update availability requires an explicit update --dry-run.");
        writer.WriteStartArray("diagnostics");
        foreach (var diagnostic in queue.Diagnostics) writer.WriteStringValue(diagnostic);
        writer.WriteEndArray(); writer.WriteStartArray("entries");
        foreach (var entry in queue.Entries)
        {
            writer.WriteStartObject(); writer.WriteString("id", entry.Id); writer.WriteString("issue", entry.Issue);
            writer.WriteString("title", entry.Title); writer.WriteString("confidence", entry.Confidence);
            writer.WriteString("reason", entry.Reason); writer.WriteString("recommendedAction", entry.RecommendedAction);
            writer.WriteStartArray("affectedPaths");
            foreach (var path in entry.AffectedPaths) writer.WriteStringValue(path);
            writer.WriteEndArray();
            writer.WriteBoolean("cleanupSuppressed", entry.CleanupSuppressed); writer.WriteBoolean("pinned", entry.Pinned);
            writer.WriteString("disablementState", entry.DisablementState); writer.WriteString("migrationState", entry.MigrationState);
            writer.WriteString("updateState", entry.UpdateState); writer.WriteString("updateReason", entry.UpdateReason);
            writer.WriteString("runtimeState", entry.RuntimeState); writer.WriteString("runtimeReason", entry.RuntimeReason);
            WriteSubject(writer, entry.Subject); writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteEndObject();
    }

    private static string RenderQueueText(MaintenanceQueue queue)
    {
        var text = new StringBuilder($"Maintenance observations captured {queue.CapturedAt:O} ({(queue.Partial ? "partial" : "captured")} inventory)\n");
        foreach (var entry in queue.Entries)
        {
            text.AppendLine($"{entry.Issue}: {entry.Title} [{entry.Confidence}]");
            text.AppendLine($"  installation: {entry.Subject.InstallationId}");
            foreach (var path in entry.AffectedPaths) text.AppendLine("  affected: " + path);
            text.AppendLine("  source: " + (entry.Subject.Source.Origin ?? "unknown"));
            text.AppendLine($"  revision: ref={entry.Subject.Revision.RepositoryRef ?? "unknown"}; tree={entry.Subject.Revision.RecordedTree ?? "unknown"}; authored version={entry.Subject.Revision.AuthoredVersion ?? "unknown"}");
            foreach (var owner in entry.Subject.Owners)
                text.AppendLine($"  owner: {owner.Provider}: {owner.Source}; registry={owner.LockFilePath ?? "unknown"}");
            foreach (var exposure in entry.Subject.Exposures)
                text.AppendLine("  exposure: " + OperationImpactFormatter.Exposure(exposure));
            text.AppendLine($"  policies: cleanup suppression={entry.CleanupSuppressed}; recorded pin={entry.Pinned}; disablement={entry.DisablementState}; migration={entry.MigrationState}");
            text.AppendLine("  action: " + entry.RecommendedAction + "; " + entry.Reason);
        }
        foreach (var diagnostic in queue.Diagnostics) text.AppendLine("Coverage: " + diagnostic);
        text.AppendLine("Update availability and active-session state: unknown. Run queue again to refresh disk inventory only.");
        text.AppendLine("The queue grants no mutation approval; use the exact path/installation ID in existing check, compare and saved-plan workflows.");
        return TerminalEscapeSanitizer.Sanitize(text.ToString()) ?? string.Empty;
    }
}
