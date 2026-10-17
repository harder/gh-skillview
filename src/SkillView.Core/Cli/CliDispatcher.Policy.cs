using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using SkillView.Preferences;
using SkillView.Ui;

namespace SkillView.Cli;

public static partial class CliDispatcher
{
    private static async Task<int> DesiredPolicyAsync(AppOptions options, TuiServices services, CancellationToken token)
    {
        var command = options.SubcommandName!;
        var arguments = options.SubcommandArgs;
        var json = arguments.Contains("--json");
        string? mode = null, reference = null, expected = null;
        var confirmed = false;
        var selectors = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            for (var index = 0; index < arguments.Count; index++)
            {
                var argument = arguments[index];
                if (!seen.Add(argument)) throw new ArgumentException("Duplicate desired-policy option: " + argument);
                if (argument is "--json" or "--allow-hidden-dirs") { selectors.Add(argument); continue; }
                if (argument == "--yes" && command != "policy-inspect") { confirmed = true; continue; }
                if (argument is not ("--path" or "--install-id" or "--mode" or "--ref" or "--expect")
                    || ++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index])
                    || arguments[index].StartsWith("--", StringComparison.Ordinal) || arguments[index].Any(char.IsControl))
                    throw new ArgumentException("Unsupported or incomplete desired-policy option: " + argument);
                var value = arguments[index];
                switch (argument)
                {
                    case "--path" or "--install-id": selectors.AddRange([argument, value]); break;
                    case "--mode" when command == "policy-set": mode = value; break;
                    case "--ref" when command == "policy-set": reference = value; break;
                    case "--expect" when command != "policy-inspect": expected = value; break;
                    default: throw new ArgumentException("Unsupported desired-policy option: " + argument);
                }
            }
            var parsed = ParseCheckArgs(selectors);
            if (parsed.Error is not null) throw new ArgumentException(parsed.Error);
            if (command != "policy-inspect" && (!confirmed || !QuarantineStore.ValidHash(expected!)))
                throw new ArgumentException("Desired-policy publication requires --yes --expect <policy inspection fingerprint>.");
            DesiredRevisionPolicy? desired = null;
            if (command == "policy-set")
            {
                if (mode is null) throw new ArgumentException("policy-set requires --mode.");
                if (mode == "approved-immutable") throw new ArgumentException("Immutable approval requires its native capture workflow; a mode/ref alone cannot approve bytes.");
                desired = new(mode, reference);
                DesiredRevisionPolicyStore.ValidatePolicy(desired);
            }
            // Read-only inventory only. No gh/API/client or payload execution.
            var inventory = await services.InventoryService.CaptureAsync(null,
                new(options.ScanRoots, parsed.AllowHidden, WorkspaceDirectories: options.WorkspaceDirectories, Profiles: options.Profiles), token).ConfigureAwait(false);
            var target = SelectCheckTarget(inventory, parsed, services.Runner.EffectiveWorkingDirectory)
                ?? throw new IOException("Select one exact physical installation; use --scan-root before the command.");
            var exclusions = inventory.ProtectedRoots.Select(root => root.Path).Concat(inventory.Skills.Select(skill => skill.ResolvedPath)).ToArray();
            var store = DesiredRevisionPolicyStore.For(services.Runner);
            var observation = store.Observe(target, exclusions, token);
            if (command != "policy-inspect")
            {
                // This explicitly offline intent operation never requested a
                // gh catalog. Retain its unavailable observation, while requiring
                // complete physical, managed-owner and lockfile coverage.
                if (!inventory.Diagnostics.InaccessiblePaths.IsEmpty || !inventory.ConfiguredRootsComplete
                    || inventory.Providers.Any(provider => !provider.Succeeded && provider.Provider != "gh-default"))
                    throw new IOException("Desired-policy publication requires complete inventory/root coverage.");
                if (observation.Status == "unknown") throw new IOException(observation.Reason);
                using var writer = store.Acquire(exclusions, token);
                observation = writer.Set(target, desired, expected!, token);
            }
            if (json) WriteJson(writer =>
            {
                writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 1);
                writer.WriteString("operation", command); writer.WriteString("status", observation.Status);
                writer.WriteString("reason", observation.Reason); writer.WriteString("fingerprint", observation.Fingerprint);
                if (observation.Sequence is { } sequence) writer.WriteNumber("sequence", sequence); else writer.WriteNull("sequence");
                writer.WritePropertyName("desiredPolicy"); JsonSerializer.Serialize(writer, observation.Policy, DesiredPolicyJsonContext.Default.DesiredRevisionPolicy);
                writer.WriteBoolean("actualPinned", target.Pinned || target.Revision.PinnedRef is not null);
                writer.WriteString("actualPinnedRef", target.Revision.PinnedRef);
                writer.WriteBoolean("payloadMutation", false); writer.WriteBoolean("runtimeMetadataChanged", false);
                writer.WriteString("sessionState", "unknown");
                WriteSubject(writer, OperationSubject.From(target)); writer.WriteEndObject();
            });
            else Console.WriteLine(TerminalEscapeSanitizer.Sanitize($"{target.InstallationId}  {target.ResolvedPath}\nDesired policy: {observation.Policy?.Mode ?? observation.Status}\n{observation.Reason}\nReview: {observation.Fingerprint}\nActual pin: {target.Revision.PinnedRef ?? (target.Pinned ? "recorded" : "none observed")}\nDesired intent is separate from actual pin/tracking and active-session state. No payload or runtime metadata changed."));
            return observation.Known ? ExitCodes.Success : ExitCodes.UserError;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            if (json) WriteJson(writer =>
            {
                writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 1); writer.WriteString("status", "refused");
                writer.WriteString("reason", exception.Message); writer.WriteBoolean("payloadMutation", false);
                writer.WriteString("sessionState", "unknown"); writer.WriteEndObject();
            });
            else Console.Error.WriteLine("skillview: " + TerminalEscapeSanitizer.Sanitize(exception.Message));
            return exception is ArgumentException ? ExitCodes.InvalidUsage : ExitCodes.UserError;
        }
    }
}
