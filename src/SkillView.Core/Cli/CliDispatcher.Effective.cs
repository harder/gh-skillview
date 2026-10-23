using System.Text;
using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Inventory;
using SkillView.Loading;
using SkillView.Ui;

namespace SkillView.Cli;

public static partial class CliDispatcher
{
    internal sealed record EffectiveArguments(LoadingCaptureRequest? Request, bool Json, string? Error);

    internal static EffectiveArguments ParseEffectiveArgs(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var json = false;
        var known = new HashSet<string>(["--agent", "--profile", "--project", "--cwd", "--config-dir", "--client-version", "--agent-name", "--trust"], StringComparer.Ordinal);
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument == "--json") { json = true; continue; }
            var equals = argument.IndexOf('=');
            var flag = equals < 0 ? argument : argument[..equals];
            if (!known.Contains(flag)) return new(null, json, "Unknown effective option: " + flag);
            var value = equals < 0 ? ++index < args.Count ? args[index] : "" : argument[(equals + 1)..];
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal) || value.Any(char.IsControl))
                return new(null, json, flag + " requires a nonblank value without control characters.");
            if (!values.TryAdd(flag, value)) return new(null, json, flag + " may be supplied only once.");
        }
        if (!values.TryGetValue("--agent", out var agent)) return new(null, json, "effective requires --agent <id>.");
        if (agent.Length > 64 || agent.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))
            return new(null, json, "Agent IDs must use ASCII letters, digits, hyphens or underscores.");
        var trust = values.GetValueOrDefault("--trust") ?? "unknown";
        if (trust is not ("unknown" or "trusted" or "untrusted")) return new(null, json, "--trust must be unknown, trusted or untrusted; it is an analysis assumption.");
        return new(new(agent, values.GetValueOrDefault("--profile"), values.GetValueOrDefault("--project"),
            values.GetValueOrDefault("--cwd"), values.GetValueOrDefault("--config-dir"), values.GetValueOrDefault("--client-version"),
            values.GetValueOrDefault("--agent-name"), trust), json, null);
    }

    private static async Task<int> EffectiveAsync(AppOptions options, TuiServices services, CancellationToken token)
    {
        var parsed = ParseEffectiveArgs(options.SubcommandArgs);
        if (parsed.Error is not null)
        { Console.Error.WriteLine("skillview: " + TerminalEscapeSanitizer.Sanitize(parsed.Error)); return ExitCodes.InvalidUsage; }
        try
        {
            var analysis = await new EffectiveSkillLoader(services.Runner).CaptureInventoryAsync(services.InventoryService, services.GhLocator.FindOnPath(),
                parsed.Request!, options.ScanRoots, options.WorkspaceDirectories, options.Profiles, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (parsed.Json) WriteJson(writer => WriteEffectiveJson(writer, analysis));
            else Console.Out.Write(RenderEffectiveText(analysis));
            return ExitCodes.Success;
        }
        catch (ArgumentException exception)
        { Console.Error.WriteLine("skillview: " + TerminalEscapeSanitizer.Sanitize(exception.Message)); return ExitCodes.InvalidUsage; }
    }

    internal static string RenderEffectiveJson(EffectiveSkillAnalysis analysis) => RenderJson(writer => WriteEffectiveJson(writer, analysis));

    private static void WriteEffectiveJson(Utf8JsonWriter writer, EffectiveSkillAnalysis analysis)
    {
        writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 1);
        writer.WriteString("evidenceKind", "conditional-local-file-policy-preview");
        writer.WriteString("capturedAt", analysis.CapturedAt); writer.WriteBoolean("analysisInputsComplete", analysis.Complete);
        var context = analysis.Context;
        writer.WriteStartObject("context");
        writer.WriteString("id", context.ContextId); writer.WriteString("agent", context.AgentId);
        writer.WriteString("profile", context.Profile); writer.WriteString("project", context.ProjectDirectory);
        writer.WriteString("cwd", context.WorkingDirectory); writer.WriteString("home", context.HomeDirectory);
        writer.WriteString("configDirectory", context.ConfigDirectory); writer.WriteString("clientVersion", context.ClientVersion);
        writer.WriteString("agentName", context.AgentName); writer.WriteString("projectTrustAssumption", context.ProjectTrust);
        writer.WriteString("configurationFingerprint", context.ConfigurationFingerprint); writer.WriteEndObject();
        writer.WriteStartObject("rules"); writer.WriteString("revision", analysis.Rules?.Revision);
        writer.WriteString("applicability", analysis.RuleApplicability); writer.WriteString("nativeConformance", analysis.Rules?.NativeConformance ?? "not-run");
        writer.WriteStartArray("sources");
        if (analysis.Rules is { } rules) foreach (var source in rules.Sources)
            {
                writer.WriteStartObject(); writer.WriteString("url", source.Url); writer.WriteString("revision", source.Revision);
                writer.WriteString("evidence", source.Evidence); writer.WriteEndObject();
            }
        writer.WriteEndArray(); writer.WriteEndObject();
        writer.WriteStartArray("candidates");
        foreach (var candidate in analysis.Candidates)
        {
            writer.WriteStartObject(); writer.WriteString("loadingKey", candidate.LoadingKey);
            writer.WriteString("entryPath", candidate.EntryPath); writer.WriteString("invocationName", candidate.InvocationName);
            Strings("invocationAliases", candidate.InvocationAliases); writer.WriteString("placement", candidate.Placement);
            writer.WriteStartArray("geminiExtensions");
            foreach (var extension in candidate.GeminiExtensions)
            {
                writer.WriteStartObject(); writer.WriteString("name", extension.Name); writer.WriteString("version", extension.Version);
                writer.WriteString("installedDirectory", extension.InstalledDirectory); writer.WriteString("payloadDirectory", extension.PayloadDirectory);
                if (extension.Enabled is { } enabled) writer.WriteBoolean("savedEnablement", enabled); else writer.WriteNull("savedEnablement");
                Strings("limitations", extension.Reasons); writer.WriteEndObject();
            }
            writer.WriteEndArray();
            Strings("selectionPeers", candidate.SelectionPeers); writer.WriteString("selectionPeerCoverage", candidate.SelectionPeerCoverage);
            WriteSubject(writer, candidate.Subject); WriteExposures(writer, "candidateExposures", candidate.Exposures);
            writer.WriteStartObject("expected"); var expected = candidate.Expected;
            writer.WriteString("state", expected.State); writer.WriteString("discoverability", expected.Discoverability);
            writer.WriteString("manualInvocation", expected.ManualInvocation); writer.WriteString("automaticInvocation", expected.AutomaticInvocation);
            writer.WriteString("slashInvocation", expected.SlashInvocation); writer.WriteString("approval", expected.Approval);
            writer.WriteString("selection", expected.Selection); writer.WriteString("compatibility", expected.Compatibility);
            writer.WriteString("reload", expected.Reload); Strings("reasons", expected.Reasons); writer.WriteEndObject();
            writer.WriteString("activeSessionState", candidate.SessionState);
            Strings("disagreements", candidate.Disagreements); Strings("portableDiagnostics", candidate.PortableDiagnostics);
            writer.WriteStartArray("nativeObservations"); foreach (var entry in candidate.NativeObservations) NativeEntry(entry); writer.WriteEndArray();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("discoveryRoots");
        foreach (var root in analysis.DiscoveryRoots)
        {
            writer.WriteStartObject(); writer.WriteString("path", root.Path); writer.WriteString("placement", root.Placement);
            writer.WriteString("pattern", "**/SKILL.md"); writer.WriteBoolean("includeHidden", root.IncludeHidden);
            writer.WriteString("applicability", root.Applicability); writer.WriteString("reason", root.Reason); writer.WriteString("sourceEvidence", root.SourceEvidence); writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("configurationEvidence");
        foreach (var file in analysis.Configuration)
        {
            writer.WriteStartObject(); writer.WriteString("path", file.Path); writer.WriteString("state", file.State);
            writer.WriteString("sha256", file.Sha256); writer.WriteString("purpose", file.Purpose); writer.WriteString("diagnostic", file.Diagnostic); writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartObject("native"); var native = analysis.Native;
        writer.WriteString("provider", native.Provider); writer.WriteString("state", native.State); writer.WriteString("contextId", native.ContextId);
        writer.WriteString("clientVersion", native.ClientVersion); writer.WriteString("schema", native.Schema);
        if (native.CapturedAt is { } at) writer.WriteString("capturedAt", at); else writer.WriteNull("capturedAt");
        writer.WriteString("coverage", native.Coverage); Strings("diagnostics", native.Diagnostics);
        writer.WriteStartArray("entries"); foreach (var entry in native.Entries) NativeEntry(entry); writer.WriteEndArray(); writer.WriteEndObject();
        Strings("diagnostics", analysis.Diagnostics); writer.WriteEndObject();

        void Strings(string name, IEnumerable<string> values)
        { writer.WriteStartArray(name); foreach (var value in values) writer.WriteStringValue(value); writer.WriteEndArray(); }
        void NativeEntry(NativeSkillEntry entry)
        {
            writer.WriteStartObject(); writer.WriteString("path", entry.Path); writer.WriteString("name", entry.Name);
            if (entry.Enabled is { } enabled) writer.WriteBoolean("enabled", enabled); else writer.WriteNull("enabled");
            writer.WriteString("invocationName", entry.InvocationName); writer.WriteString("diagnostic", entry.Diagnostic); writer.WriteEndObject();
        }
    }

    internal static string RenderEffectiveText(EffectiveSkillAnalysis analysis)
    {
        var result = new StringBuilder();
        var byKey = analysis.Candidates.GroupBy(candidate => candidate.LoadingKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        result.AppendLine($"Agent: {analysis.Context.AgentId}  Profile: {analysis.Context.Profile ?? "default"}");
        result.AppendLine($"Project: {analysis.Context.ProjectDirectory}  CWD: {analysis.Context.WorkingDirectory}");
        result.AppendLine($"Config: {analysis.Context.ConfigDirectory}  Client version: {analysis.Context.ClientVersion ?? "unknown"}  Trust assumption: {analysis.Context.ProjectTrust}");
        result.AppendLine($"Conditional local-file policy preview: {analysis.Rules?.Revision ?? "unsupported"} ({analysis.RuleApplicability})");
        result.AppendLine($"Native inventory: {analysis.Native.State}; active-session state: unknown");
        foreach (var candidate in analysis.Candidates)
        {
            var expected = candidate.Expected;
            result.AppendLine($"{candidate.InvocationName ?? "(unknown name)"}: {expected.State}; selection={expected.Selection}; manual={expected.ManualInvocation}; automatic={expected.AutomaticInvocation}; approval={expected.Approval}");
            result.AppendLine($"  discovery={expected.Discoverability}; slash={expected.SlashInvocation}; compatibility={expected.Compatibility}; reload={expected.Reload}");
            result.AppendLine("  " + candidate.EntryPath);
            foreach (var extension in candidate.GeminiExtensions.Take(16))
                result.AppendLine($"  Gemini extension: {extension.Name ?? "unknown"} {extension.Version ?? "unknown"}; installed at {extension.InstalledDirectory}; payload at {extension.PayloadDirectory}; saved enablement={extension.Enabled?.ToString() ?? "unknown"} (session selection unobserved)");
            if (candidate.GeminiExtensions.Length > 16) result.AppendLine("  Additional extension deliveries are retained in JSON.");
            if (!candidate.InvocationAliases.IsEmpty) result.AppendLine("  Other observed command names (selection may differ): " + string.Join(", ", candidate.InvocationAliases));
            foreach (var reason in expected.Reasons) result.AppendLine("  " + reason);
            foreach (var observed in candidate.NativeObservations.Take(16))
                result.AppendLine($"  Native observation: {observed.Path}; name={observed.Name ?? "unknown"}; enabled={observed.Enabled?.ToString() ?? "unknown"}; {observed.Diagnostic}");
            if (candidate.NativeObservations.Length > 16) result.AppendLine("  Additional native observations are retained in JSON.");
            foreach (var disagreement in candidate.Disagreements.Take(16)) result.AppendLine("  Disagreement: " + disagreement);
            foreach (var diagnostic in candidate.PortableDiagnostics.Take(16)) result.AppendLine("  Portable metadata diagnosis (separate from native acceptance): " + diagnostic);
            foreach (var peer in candidate.SelectionPeers)
            {
                var competing = byKey.GetValueOrDefault(peer);
                if (competing is not null) result.AppendLine("  Competing/coexisting command: " + competing.InvocationName + " at " + competing.EntryPath + " (" + competing.Expected.Selection + ")");
            }
            if (candidate.SelectionPeerCoverage != "complete") result.AppendLine("  Competitor references are bounded; all physical candidates remain in JSON.");
        }
        foreach (var root in analysis.DiscoveryRoots)
            result.AppendLine($"Discovery root: {root.Path}; {root.Applicability}; hidden={root.IncludeHidden}; {root.Placement}; {root.Reason}");
        result.AppendLine("Observed local configuration evidence:");
        foreach (var file in analysis.Configuration.Where(file => file.State != "missing" && file.Purpose != "bounded skill frontmatter"))
            result.AppendLine($"  {file.Path}: {file.State}; {file.Purpose}{(file.Diagnostic is null ? "" : "; " + file.Diagnostic)}");
        if (analysis.Rules is { } rules) foreach (var source in rules.Sources) result.AppendLine("Rule source: " + source.Url + " (" + source.Revision + ")");
        foreach (var diagnostic in analysis.Native.Diagnostics) result.AppendLine("Native: " + diagnostic);
        foreach (var diagnostic in analysis.Diagnostics) result.AppendLine("Note: " + diagnostic);
        return TerminalEscapeSanitizer.Sanitize(result.ToString())!;
    }
}
