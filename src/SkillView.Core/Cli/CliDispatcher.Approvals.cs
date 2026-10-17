using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Maintenance;
using SkillView.Inventory.Models;
using SkillView.Preferences;
using SkillView.Ui;

namespace SkillView.Cli;

public static partial class CliDispatcher
{
    private static async Task<int> ApprovalAsync(AppOptions options, TuiServices services, CancellationToken token)
    {
        services = services.CaptureContext();
        var command = options.SubcommandName!;
        var json = options.SubcommandArgs.Contains("--json");
        try
        {
            string? id = null, expected = null, payload = null, selectedFile = null;
            var yes = false; var selectors = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            var args = options.SubcommandArgs;
            for (var i = 0; i < args.Count; i++)
            {
                var arg = args[i];
                if (!seen.Add(arg)) throw new ArgumentException("Duplicate approval option: " + arg);
                if (arg is "--json" or "--allow-hidden-dirs") { selectors.Add(arg); continue; }
                if (arg == "--yes" && command == "approval-capture") { yes = true; continue; }
                if (arg is not ("--path" or "--install-id" or "--id" or "--expect" or "--expect-payload" or "--selected-plan-file")
                    || ++i >= args.Count || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal) || args[i].Any(char.IsControl))
                    throw new ArgumentException("Unsupported or incomplete approval option: " + arg);
                switch (arg)
                {
                    case "--path" or "--install-id": selectors.AddRange([arg, args[i]]); break;
                    case "--id": id = args[i]; break;
                    case "--expect": expected = args[i]; break;
                    case "--expect-payload": payload = args[i]; break;
                    case "--selected-plan-file": selectedFile = args[i]; break;
                }
            }
            var parsed = ParseCheckArgs(selectors);
            if (parsed.Error is not null) throw new ArgumentException(parsed.Error);
            if (command == "approval-capture" && (!yes || !QuarantineStore.ValidHash(payload!) || id is not null || expected is not null)
                || command != "approval-capture" && (payload is not null || selectedFile is not null)
                || (id is null) != (expected is null) || id is not null && (!QuarantineStore.ValidId(id) || !QuarantineStore.ValidHash(expected!)))
                throw new ArgumentException("Capture requires separate --yes --expect-payload; saved inspection requires --id and --expect. Inspection alone never captures or sets policy.");
            var inventory = await services.InventoryService.CaptureAsync(services.GhLocator.FindOnPath(),
                new(options.ScanRoots, parsed.AllowHidden, WorkspaceDirectories: options.WorkspaceDirectories, Profiles: options.Profiles), token).ConfigureAwait(false);
            var target = SelectCheckTarget(inventory, parsed, services.Runner.EffectiveWorkingDirectory)
                ?? throw new IOException("Select one exact physical installation.");
            var exclusions = inventory.ProtectedRoots.Select(root => root.Path).Concat(inventory.Skills.Select(skill => skill.ResolvedPath)).ToArray();
            var store = ApprovedPayloadStore.For(services.Runner, services.Logger);
            ApprovedPayloadReference? reference = id is null ? null : new(store.Root, id, expected!);
            ApprovedPayloadHeader? header = null;
            ReplacementEvidence? preview = null;
            if (command == "approval-capture")
            {
                if (selectedFile is null) reference = await Task.Run(() => store.CaptureCustom(target, inventory, payload!, token), token).ConfigureAwait(false);
                else
                {
                    var selected = MaintenancePlanReference.Read(Path.GetFullPath(selectedFile, services.Runner.EffectiveWorkingDirectory), token);
                    if (selected.Purpose != ReplacementPlanStore.SelectedRevisionPurpose || selected.InstallationId != target.InstallationId || !SkillView.Inventory.PathIdentity.Equals(selected.OriginalPath, target.ResolvedPath))
                        throw new IOException("Selected approval needs the exact completed selected-result plan for this installation.");
                    reference = await Task.Run(() => store.CaptureSelected(target, inventory, payload!, services.SavedUpdatePlans.Root, selected.Id, selected.PlanFingerprint, token), token).ConfigureAwait(false);
                }
            }
            if (reference is not null)
            {
                using var held = store.Open(reference, exclusions, token);
                held.RequireTarget(target, services.Runner, token); header = held.Header;
            }
            else preview = await Task.Run(() => services.SavedUpdatePlans.PreviewApprovedPayload(target, inventory, token), token).ConfigureAwait(false);
            var policy = DesiredRevisionPolicyStore.For(services.Runner).Observe(target, exclusions, token);
            if (json) WriteJson(w =>
            {
                w.WriteStartObject(); w.WriteNumber("schemaVersion", 1); w.WriteString("status", header is null ? "preview" : "captured");
                WriteSubject(w, OperationSubject.From(target));
                w.WriteString("payloadFingerprint", header?.Captured.Payload ?? preview!.Payload);
                w.WriteString("policyFingerprint", policy.Fingerprint);
                w.WritePropertyName("approvalReference"); JsonSerializer.Serialize(w, reference, ApprovedPayloadJsonContext.Default.ApprovedPayloadReference);
                w.WritePropertyName("approval"); JsonSerializer.Serialize(w, header, ApprovedPayloadJsonContext.Default.ApprovedPayloadHeader);
                w.WriteBoolean("payloadMutation", false); w.WriteBoolean("desiredPolicyChanged", false);
                w.WriteString("sourceReview", "not-acknowledged; restore plan requires separate frozen source review");
                w.WriteString("provenance", header?.Selected is null ? "Captured installed/custom bytes; pristine upstream status unproved." : "Completed selected result, not publisher authenticity.");
                w.WriteString("sessionState", "unknown"); w.WriteEndObject();
            });
            else Console.WriteLine(TerminalEscapeSanitizer.Sanitize($"{target.InstallationId}  {target.ResolvedPath}\nPayload: {header?.Captured.Payload ?? preview!.Payload}\nPolicy review: {policy.Fingerprint}\nApproval: {reference?.Id ?? "none"} {reference?.Fingerprint}\nPrivate capture does not set desired policy or acknowledge source review. Session state unknown."));
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or NotSupportedException)
        {
            if (json) WriteJson(w => { w.WriteStartObject(); w.WriteNumber("schemaVersion", 1); w.WriteString("status", "refused"); w.WriteString("reason", ex.Message); w.WriteString("sessionState", "unknown"); w.WriteEndObject(); });
            else Console.Error.WriteLine("skillview: " + TerminalEscapeSanitizer.Sanitize(ex.Message));
            return ex is ArgumentException ? ExitCodes.InvalidUsage : ExitCodes.UserError;
        }
    }
}
