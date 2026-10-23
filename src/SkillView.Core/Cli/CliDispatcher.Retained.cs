using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Ui;

namespace SkillView.Cli;

public static partial class CliDispatcher
{

    private static async Task<int> QuarantineAsync(AppOptions options, TuiServices services, CancellationToken token)
    {
        var command = options.SubcommandName;
        string? path = null, id = null, expected = null;
        var json = false; var execute = false; var standalone = false;
        var args = options.SubcommandArgs;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--json") { json = true; continue; }
            if (args[i] == "--yes" && command != "quarantine-history") { execute = true; continue; }
            if (args[i] == "--standalone" && command == "quarantine") { standalone = true; continue; }
            if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                if (args[i] == "--path" && command == "quarantine" && path is null && id is null) { path = args[++i]; continue; }
                if (args[i] == "--install-id" && command == "quarantine" && path is null && id is null) { id = args[++i]; continue; }
                if (args[i] == "--id" && command == "undo" && id is null) { id = args[++i]; continue; }
                if (args[i] == "--expect" && command == "quarantine" && expected is null) { expected = args[++i]; continue; }
            }
            Console.Error.WriteLine("skillview: unsupported or incomplete quarantine/undo argument");
            return ExitCodes.InvalidUsage;
        }
        if (command == "quarantine" && ((path is null) == (id is null)
                || path is not null && string.IsNullOrWhiteSpace(path) || id is not null && string.IsNullOrWhiteSpace(id)
                || execute && (!standalone || !SkillView.Maintenance.QuarantineStore.ValidHash(expected!)))
            || command == "undo" && !SkillView.Maintenance.QuarantineStore.ValidId(id!)
            || expected is not null && !SkillView.Maintenance.QuarantineStore.ValidHash(expected))
        {
            Console.Error.WriteLine("skillview: quarantine needs one --path/--install-id; apply needs --yes --standalone --expect <preview fingerprint>. Undo needs --id <operation ID>.");
            return ExitCodes.InvalidUsage;
        }
        OperationImpact? frozenImpact = null;
        try
        {
            if (path is not null) path = Path.GetFullPath(path, services.Runner.EffectiveWorkingDirectory);
            var inventory = await services.InventoryService.CaptureAsync(services.GhLocator.FindOnPath(),
                new LocalInventoryService.Options(options.ScanRoots, false, WorkspaceDirectories: options.WorkspaceDirectories, Profiles: options.Profiles), token).ConfigureAwait(false);
            var backupRoot = SkillView.Maintenance.MaintenanceBackupStore.ResolveRoot(services.Runner);
            var store = new SkillView.Maintenance.QuarantineStore(Path.Combine(Path.GetDirectoryName(backupRoot)!, "quarantine"), services.Logger, runner: services.Runner,
                subjectObserved: impact => frozenImpact = impact with { Operation = command! });
            IReadOnlyList<SkillView.Maintenance.QuarantineResult> results;
            if (command == "quarantine-history") results = await Task.Run(() => store.History(inventory, token), token).ConfigureAwait(false);
            else if (command == "undo") results = [await Task.Run(() => store.Undo(id!, inventory, execute, token), token).ConfigureAwait(false)];
            else
            {
                var selected = inventory.Skills.Where(skill => id is not null ? skill.InstallationId == id : PathIdentity.Equals(skill.ResolvedPath, path!)).ToArray();
                if (selected.Length != 1) throw new IOException("Select one captured standalone payload; put its --scan-root before quarantine.");
                frozenImpact = OperationImpact.Payload(OperationSubject.From(selected[0]), command!);
                if (execute) results = [await Task.Run(() => store.Apply(selected[0], inventory, expected!, standalone, token), token).ConfigureAwait(false)];
                else
                {
                    var preview = await Task.Run(() => store.Preview(selected[0], inventory, token), token).ConfigureAwait(false);
                    results = [new(preview.Id, "preview", true, false, false, preview.OriginalPath,
                        Path.Combine(store.Root, preview.Id, "payload"), preview.ReviewFingerprint,
                        "Preview only: relocate this standalone directory on the same filesystem. Retains live permissions and links; agent sessions were not observed. Apply requires --yes --standalone --expect <reviewFingerprint>.")
                    {
                        Impact = frozenImpact with
                        {
                            PayloadRetained = true,
                            RetainedPayloadPath = preview.OriginalPath,
                            RetentionEvidence = "quarantine-preview; original payload observed; no relocation attempted"
                        }
                    }];
                }
            }
            results = results.Select(result => result with
            { Impact = result.Impact is { } impact ? impact with { Operation = command! } : frozenImpact }).ToArray();
            if (json) WriteJson(w =>
            {
                w.WriteStartObject(); w.WriteNumber("schemaVersion", 1); w.WriteBoolean("preview", !execute && command != "quarantine-history");
                w.WriteBoolean("securityIsolation", false); w.WriteString("sessionState", "unknown");
                w.WriteStartArray("operations"); foreach (var result in results) WriteQuarantineResult(w, result);
                w.WriteEndArray(); w.WriteEndObject();
            });
            else foreach (var result in results) Console.WriteLine(TerminalEscapeSanitizer.Sanitize(
                $"{result.Id}  {result.Status}  {result.OriginalPath}\nReview: {result.ReviewFingerprint}\n{result.Message}"));
            if (!json) foreach (var result in results) WriteImpactText(result.Impact);
            return results.Any(result => result.Canceled) ? ExitCodes.Cancelled
                : command == "quarantine-history" ? results.Any(result => result.Status is "prepared" or "undo-prepared" or "needs-inspection" or "unrecognized") ? ExitCodes.UserError : ExitCodes.Success
                : results.All(result => result.Succeeded) ? ExitCodes.Success : ExitCodes.UserError;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or EntryPointNotFoundException
            || ex is OperationCanceledException && token.IsCancellationRequested)
        {
            if (json) WriteJson(w => { w.WriteStartObject(); w.WriteNumber("schemaVersion", 1); w.WriteBoolean("succeeded", false); w.WriteString("error", ex.Message); w.WriteBoolean("canceled", ex is OperationCanceledException); WriteImpact(w, frozenImpact); w.WriteEndObject(); });
            else Console.Error.WriteLine("skillview: " + TerminalEscapeSanitizer.Sanitize(ex.Message));
            if (!json) WriteImpactText(frozenImpact);
            return ex is OperationCanceledException ? ExitCodes.Cancelled : ex is ArgumentException ? ExitCodes.InvalidUsage : ExitCodes.UserError;
        }
    }

    private static void WriteQuarantineResult(Utf8JsonWriter w, SkillView.Maintenance.QuarantineResult result)
    {
        w.WriteStartObject(); w.WriteString("id", result.Id); w.WriteString("status", result.Status);
        w.WriteBoolean("succeeded", result.Succeeded); w.WriteBoolean("mutationObserved", result.MutationObserved);
        w.WriteBoolean("canceled", result.Canceled); w.WriteString("originalPath", result.OriginalPath);
        w.WriteString("retainedPath", result.RetainedPath); w.WriteString("reviewFingerprint", result.ReviewFingerprint);
        w.WriteString("message", result.Message); WriteImpact(w, result.Impact); w.WriteEndObject();
    }

    private static async Task<int> HistoryAsync(AppOptions options, TuiServices services, CancellationToken token)
    {
        var json = false;
        string? id = null;
        var args = options.SubcommandArgs;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--json") { json = true; continue; }
            if (args[i] == "--id" && id is null && i + 1 < args.Count) { id = args[++i]; continue; }
            Console.Error.WriteLine("skillview: unsupported or incomplete history argument");
            return ExitCodes.InvalidUsage;
        }
        if (id is not null && (id.Length != 32 || id.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c))))
        { Console.Error.WriteLine("skillview: history --id requires a lowercase 32-character backup ID"); return ExitCodes.InvalidUsage; }
        try
        {
            var store = new SkillView.Maintenance.MaintenanceBackupStore(SkillView.Maintenance.MaintenanceBackupStore.ResolveRoot(services.Runner), services.Runner);
            var entries = await Task.Run(() => store.ReadHistory(token), token).ConfigureAwait(false);
            if (id is not null) entries = entries.Where(entry => entry.Id == id).ToArray();
            if (json) WriteJson(w =>
            {
                w.WriteStartObject(); w.WriteNumber("schemaVersion", 1); w.WriteString("metadataContract", "verified raw payload evidence; no in-place restore authority");
                w.WriteStartArray("entries");
                foreach (var entry in entries)
                {
                    w.WriteStartObject(); w.WriteString("id", entry.Id); w.WriteString("status", entry.Status);
                    w.WriteString("installationId", entry.Record?.InstallationId); w.WriteString("path", entry.Record?.Path);
                    w.WriteString("fingerprint", entry.Record?.BeforeFingerprint); w.WriteString("archiveHash", entry.Record?.ArchiveHash);
                    w.WriteString("archivePath", entry.Record?.ArchivePath); w.WriteNumber("payloadEntries", entry.PayloadEntries);
                    w.WriteNumber("payloadBytes", entry.PayloadBytes); w.WriteString("error", entry.Error); w.WriteEndObject();
                }
                w.WriteEndArray(); w.WriteEndObject();
            });
            else foreach (var entry in entries) Console.WriteLine(TerminalEscapeSanitizer.Sanitize($"{entry.Id}  {entry.Status}  {entry.Record?.Path ?? entry.Error}"));
            return id is not null && entries.Count == 0 ? ExitCodes.NoMatches
                : entries.Any(entry => entry.Status != "verified") ? ExitCodes.UserError : ExitCodes.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (json) WriteJson(w => { w.WriteStartObject(); w.WriteNumber("schemaVersion", 1); w.WriteBoolean("succeeded", false); w.WriteString("error", ex.Message); w.WriteEndObject(); });
            else Console.Error.WriteLine("skillview: " + TerminalEscapeSanitizer.Sanitize(ex.Message));
            return ExitCodes.EnvironmentError;
        }
    }

    private static async Task<int> BackupAsync(AppOptions options, TuiServices services, CancellationToken token)
    {
        string? path = null, id = null;
        var selectors = 0;
        var json = false;
        var args = options.SubcommandArgs;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--json") { json = true; continue; }
            if (args[i] is "--path" or "--install-id" && i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            { if (++selectors > 1) break; if (args[i] == "--path") path = args[++i]; else id = args[++i]; continue; }
            Console.Error.WriteLine("skillview: unsupported or incomplete backup argument");
            return ExitCodes.InvalidUsage;
        }
        if (selectors != 1 || (path is null) == (id is null) || path is not null && string.IsNullOrWhiteSpace(path)
            || id is not null && string.IsNullOrWhiteSpace(id))
        {
            Console.Error.WriteLine("skillview: backup requires exactly one --path or --install-id");
            return ExitCodes.InvalidUsage;
        }
        if (path is not null)
        {
            try { path = Path.GetFullPath(path, services.Runner.EffectiveWorkingDirectory); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            { Console.Error.WriteLine("skillview: invalid backup path: " + TerminalEscapeSanitizer.Sanitize(ex.Message)); return ExitCodes.InvalidUsage; }
        }
        var snapshot = await services.InventoryService.CaptureAsync(services.GhLocator.FindOnPath(),
            new LocalInventoryService.Options(options.ScanRoots, false, WorkspaceDirectories: options.WorkspaceDirectories, Profiles: options.Profiles), token).ConfigureAwait(false);
        var selected = snapshot.Skills.Where(skill => id is not null ? skill.InstallationId == id
            : PathIdentity.Equals(skill.ResolvedPath, path!) || skill.Agents.Any(agent => PathIdentity.Equals(agent.Path, path!))).ToArray();
        if (selected.Length != 1)
        { Console.Error.WriteLine("skillview: backup selector must identify one captured payload; include its --scan-root before backup"); return ExitCodes.NoMatches; }
        var impact = OperationImpact.ObservedRetention(OperationSubject.From(selected[0]), "backup");
        try
        {
            if (!snapshot.ConfiguredRootsComplete) throw new IOException("Configured agent/workspace root coverage is incomplete; resolve omitted or unavailable contexts before backup.");
            var store = new SkillView.Maintenance.MaintenanceBackupStore(SkillView.Maintenance.MaintenanceBackupStore.ResolveRoot(services.Runner), services.Runner);
            var record = await Task.Run(() => store.Capture(selected[0], token, snapshot.Skills,
                snapshot.ProtectedRoots.Where(scan => scan.AgentHint is not null).ToArray()), token).ConfigureAwait(false);
            impact = impact with
            {
                PayloadRetained = true,
                RetainedPayloadPath = record.Path,
                RetentionEvidence = "verified-backup-capture; original payload was not mutated"
            };
            if (json) WriteJson(w =>
            {
                w.WriteStartObject(); w.WriteNumber("schemaVersion", 1); w.WriteBoolean("succeeded", true);
                w.WriteString("id", record.Id); w.WriteString("installationId", record.InstallationId); w.WriteString("path", record.Path);
                w.WriteString("fingerprint", record.BeforeFingerprint); w.WriteString("archiveHash", record.ArchiveHash);
                w.WriteString("archivePath", record.ArchivePath); w.WriteString("status", record.Status);
                w.WriteString("metadataContract", "file bytes, entry Unix modes and native link bytes; in-place restore requires a reviewed plan");
                WriteImpact(w, impact);
                w.WriteEndObject();
            });
            else Console.WriteLine("Payload evidence captured: " + TerminalEscapeSanitizer.Sanitize(record.ArchivePath));
            if (!json) WriteImpactText(impact);
            return ExitCodes.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException || ex is OperationCanceledException && token.IsCancellationRequested)
        {
            if (json) WriteJson(w => { w.WriteStartObject(); w.WriteNumber("schemaVersion", 1); w.WriteBoolean("succeeded", false); w.WriteString("error", ex.Message); w.WriteBoolean("canceled", ex is OperationCanceledException); WriteImpact(w, impact); w.WriteEndObject(); });
            else Console.Error.WriteLine("skillview: " + TerminalEscapeSanitizer.Sanitize(ex.Message));
            if (!json) WriteImpactText(impact);
            return ex is OperationCanceledException ? ExitCodes.Cancelled : ExitCodes.EnvironmentError;
        }
    }
}
