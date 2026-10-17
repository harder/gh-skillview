using System.Collections.Immutable;
using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Maintenance;
using SkillView.Ui;

namespace SkillView.Cli;

public static partial class CliDispatcher
{
    private static async Task<int> MaintenanceAsync(AppOptions options, TuiServices services, CancellationToken token)
    {
        services = services.CaptureContext();
        var command = options.SubcommandName!;
        var arguments = options.SubcommandArgs.ToList();
        var json = arguments.Contains("--json");
        try
        {
            if (command == "inspect" && !arguments.Contains("--plan-file"))
            {
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
            MaintenancePlanReference? reference = null;
            string purpose;
            ValidateMaintenanceArguments(command, arguments);
            if (command == "plan")
            {
                var index = arguments.IndexOf("--purpose");
                if (index < 0 || index + 1 >= arguments.Count) throw new ArgumentException("plan requires --purpose update-revision, restore-recorded, unpin-only, local-reimport or switch-local-source and one exact --path/--install-id.");
                purpose = arguments[index + 1];
                arguments.RemoveRange(index, 2);
                if (arguments.Contains("--path") == arguments.Contains("--install-id"))
                    throw new ArgumentException("Plan requires exactly one --path or --install-id selector.");
            }
            else
            {
                var index = arguments.IndexOf("--plan-file");
                if (index < 0 || index + 1 >= arguments.Count || arguments.Contains("--id"))
                    throw new ArgumentException(command + " requires one --plan-file FILE; --id cannot override its local locator.");
                var path = Path.GetFullPath(arguments[index + 1], services.Runner.EffectiveWorkingDirectory);
                reference = MaintenancePlanReference.Read(path, token);
                arguments.RemoveRange(index, 2);
                arguments.AddRange(["--id", reference.Id]);
                purpose = reference.Purpose;
            }
            var prefix = purpose switch
            {
                "update-revision" => "update",
                "restore-recorded" => "restore",
                "unpin-only" => "unpin",
                "local-reimport" => "reimport",
                "switch-local-source" => "source-switch",
                "restore-approved" => "approved",
                "selected-revision" => "selected",
                _ => throw new ArgumentException("Unsupported plan purpose; no owner transition was selected.")
            };
            if (command == "plan" && purpose != "selected-revision" && (arguments.Contains("--revision") || arguments.Contains("--revision-kind") || arguments.Contains("--pin")))
                throw new ArgumentException("Typed revision options require selected-revision purpose.");
            if (command == "plan" && purpose == "selected-revision" && (!arguments.Contains("--revision") || !arguments.Contains("--revision-kind")))
                throw new ArgumentException("Selected revision requires --revision-kind tag|commit --revision VALUE and optional explicit --pin.");
            if (command == "plan" && arguments.Contains("--unpin") && purpose != "update-revision")
                throw new ArgumentException("--unpin is supported only by an update-revision plan; standalone unpin uses purpose unpin-only.");
            if (command == "plan" && arguments.Contains("--new-source") && purpose != "switch-local-source")
                throw new ArgumentException("--new-source is supported only by a switch-local-source plan.");
            if (command == "plan" && purpose != "restore-approved" && (arguments.Contains("--approval-id") || arguments.Contains("--approval-expect")))
                throw new ArgumentException("Approval options require restore-approved purpose.");
            if (command == "plan" && purpose == "restore-approved" && (!arguments.Contains("--approval-id") || !arguments.Contains("--approval-expect")))
                throw new ArgumentException("Approved restore requires --approval-id --approval-expect separate from later source and retention consent.");
            if (command == "plan" && purpose == "restore-approved")
            {
                if (!QuarantineStore.ValidId(arguments[arguments.IndexOf("--approval-id") + 1])
                    || !QuarantineStore.ValidHash(arguments[arguments.IndexOf("--approval-expect") + 1]))
                    throw new ArgumentException("Approved restore requires an exact archive ID and separate fingerprint.");
            }
            if (command == "plan" && purpose == "selected-revision")
            {
                var kind = arguments[arguments.IndexOf("--revision-kind") + 1];
                var value = arguments[arguments.IndexOf("--revision") + 1];
                try { new SelectedRevisionRequest(kind, value, arguments.Contains("--pin")).Validate(); }
                catch (IOException ex) { throw new ArgumentException(ex.Message, ex); }
            }
            if (command == "plan" && purpose == "switch-local-source")
            {
                var sourceIndex = arguments.IndexOf("--new-source");
                if (sourceIndex < 0 || sourceIndex + 1 >= arguments.Count || !Path.IsPathFullyQualified(arguments[sourceIndex + 1]))
                    throw new ArgumentException("A switch-local-source plan requires --new-source <absolute directory>.");
            }
            var action = command switch
            {
                "plan" => "plan",
                "inspect" or "verify" => "inspect",
                "apply" => "apply",
                "undo" => "undo",
                "repair" => "recover",
                _ => throw new ArgumentException("Unsupported maintenance operation.")
            };
            if (action is "apply" or "recover" or "undo")
            {
                var expectedIndex = arguments.IndexOf("--expect");
                if (!arguments.Contains("--yes") || expectedIndex < 0 || expectedIndex + 1 >= arguments.Count
                    || !QuarantineStore.ValidHash(arguments[expectedIndex + 1]))
                    throw new ArgumentException("Execution requires --yes and a separately supplied --expect reviewed fingerprint. The plan file cannot supply approval.");
                if (action == "apply")
                {
                    var dispositionIndex = arguments.IndexOf("--disposition");
                    if (dispositionIndex < 0 || dispositionIndex + 1 >= arguments.Count)
                        throw new ArgumentException("Apply requires explicit --disposition retention consent matching the saved specialist plan.");
                    var disposition = arguments[dispositionIndex + 1];
                    var accepted = purpose switch
                    {
                        "local-reimport" => disposition == "replace-local-retain-original",
                        "switch-local-source" => disposition == ReplacementPlanStore.LocalSourceSwitchDisposition,
                        "restore-approved" => disposition == ReplacementPlanStore.ApprovedRestoreDisposition,
                        "selected-revision" => disposition == ReplacementPlanStore.SelectedRevisionDisposition,
                        "unpin-only" => disposition == "unpin-only",
                        "update-revision" => disposition is "retain-original" or "merge-reviewed",
                        _ => disposition == "retain-original"
                    };
                    if (!accepted) throw new ArgumentException("The explicit retention disposition does not match this plan purpose; review the specialist plan.");
                }
            }
            // Repair resumes only this backend's recognized retained transaction.
            // Registry-image repair remains the explicit specialist command.
            return await RestoreAsync(options with { SubcommandName = prefix + "-" + action, SubcommandArgs = arguments },
                services, token, reference).ConfigureAwait(false);
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
                writer.WriteString("operation", command); writer.WriteString("reason", reason);
                writer.WriteBoolean("mutationObserved", false); writer.WriteString("sessionState", "unknown"); writer.WriteEndObject();
            });
            else Console.Error.WriteLine("skillview: " + TerminalEscapeSanitizer.Sanitize(reason));
            return code;
        }
    }

    private static void ValidateMaintenanceArguments(string command, IReadOnlyList<string> arguments)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (!seen.Add(argument)) throw new ArgumentException("Duplicate maintenance option: " + argument);
            if (argument is "--json" or "--allow-hidden-dirs") continue;
            if (argument == "--yes" && command is "apply" or "repair" or "undo") continue;
            if (argument is "--unpin" or "--pin" && command == "plan") continue;
            var valued = command == "plan" ? argument is "--purpose" or "--path" or "--install-id" or "--new-source" or "--approval-id" or "--approval-expect" or "--revision-kind" or "--revision"
                : argument == "--plan-file" || argument == "--expect" && command is "apply" or "repair" or "undo"
                    || argument == "--disposition" && command == "apply";
            if (!valued || ++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index])
                || arguments[index].StartsWith("--", StringComparison.Ordinal) || arguments[index].Any(char.IsControl))
                throw new ArgumentException("Unsupported or incomplete maintenance option: " + argument);
        }
    }

    private static void WritePlanReference(Utf8JsonWriter writer, ReplacementResult result)
    {
        writer.WritePropertyName("planReference");
        JsonSerializer.Serialize(writer, result.Reference, MaintenancePlanReferenceJsonContext.Default.MaintenancePlanReference);
        writer.WritePropertyName("comparison");
        JsonSerializer.Serialize(writer, result.Comparison, MaintenanceComparisonJsonContext.Default.MaintenancePlanComparison);
    }
}
