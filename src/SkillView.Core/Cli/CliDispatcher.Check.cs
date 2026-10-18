using System.Text;
using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.PackageChecking;
using SkillView.Ui;

namespace SkillView.Cli;

public static partial class CliDispatcher
{
    internal sealed record CheckArguments(string? Path, string? InstallationId, bool Json, bool AllowHidden, string? Error)
    {
        internal string? Agent { get; init; }
        internal string? AgentVersion { get; init; }
    }

    internal static CheckArguments ParseCheckArgs(IReadOnlyList<string> arguments)
    {
        string? path = null, id = null, agent = null, agentVersion = null;
        var json = arguments.Contains("--json");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hidden = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (!seen.Add(argument)) return new(null, null, json, hidden, "Duplicate check option: " + argument);
            if (argument == "--json") continue;
            if (argument == "--allow-hidden-dirs") { hidden = true; continue; }
            if (argument is not ("--path" or "--install-id" or "--agent" or "--agent-version") || index + 1 >= arguments.Count)
                return new(null, null, json, hidden, "check requires exactly one --path PATH or --install-id ID; optional --json and --allow-hidden-dirs.");
            var value = arguments[++index];
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal) || value.Any(char.IsControl))
                return new(null, null, json, hidden, argument + " requires a nonblank value without control characters.");
            if (argument == "--path") path = value;
            else if (argument == "--install-id") id = value;
            else if (argument == "--agent") agent = value;
            else agentVersion = value;
        }
        if (agentVersion is not null && agent is null) return new(path, id, json, hidden, "--agent-version requires an explicit --agent.");
        return (path is null) == (id is null)
            ? new(null, null, json, hidden, "Select exactly one --path PATH or --install-id ID.")
            : new(path, id, json, hidden, null) { Agent = agent, AgentVersion = agentVersion };
    }

    internal static InstalledSkill? SelectCheckTarget(InventorySnapshot inventory, CheckArguments arguments, string cwd)
    {
        var path = arguments.Path is null ? null : System.IO.Path.GetFullPath(arguments.Path, cwd);
        var matches = inventory.Skills.Where(skill => arguments.InstallationId is { } id
            ? skill.InstallationId == id : PathIdentity.Equals(skill.ResolvedPath, path!)
                || skill.Agents.Any(exposure => PathIdentity.Equals(exposure.Path, path!))).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static async Task<int> CheckAsync(AppOptions options, TuiServices services, CancellationToken token)
    {
        var arguments = ParseCheckArgs(options.SubcommandArgs);
        if (arguments.Error is { } error) return Fail(error, ExitCodes.InvalidUsage);
        try
        {
            // A local inventory deliberately has no gh host: checking cannot
            // authenticate, resolve a remote source, install, or execute a skill.
            var inventory = await services.InventoryService.CaptureAsync(null,
                new(options.ScanRoots, arguments.AllowHidden, WorkspaceDirectories: options.WorkspaceDirectories,
                    Profiles: options.Profiles), token).ConfigureAwait(false);
            var target = SelectCheckTarget(inventory, arguments, services.Runner.EffectiveWorkingDirectory);
            if (target is null) return Fail("Select exactly one observed installation; put --scan-root before check.", ExitCodes.NoMatches);
            var report = await new StaticPackageChecker(agent: arguments.Agent is { } agent
                ? new(agent, arguments.AgentVersion) : null).CheckAsync(target, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (arguments.Json) WriteJson(writer => WritePackageCheckJson(writer, report));
            else Console.Write(RenderPackageCheckText(report));
            return PackageCheckExitCode(report);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        { return Fail(exception.Message, exception is ArgumentException ? ExitCodes.InvalidUsage : ExitCodes.EnvironmentError); }

        int Fail(string message, int code)
        {
            if (arguments.Json) WriteJson(writer =>
            {
                writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 2);
                writer.WriteBoolean("succeeded", false); writer.WriteString("error", message); writer.WriteEndObject();
            });
            else Console.Error.WriteLine("skillview: " + TerminalEscapeSanitizer.Sanitize(message));
            return code;
        }
    }

    internal static int PackageCheckExitCode(PackageCheckReport report) => report.HasErrors
        || report.Coverage.Payload != "complete" || report.Coverage.References != "limited"
        || report.DeclarationCoverage != "limited" || report.AgentAssessment.State == "incompatible"
        || report.FormatStatus != "valid" || report.ReferenceStatus is not ("none-observed" or "findings") ? ExitCodes.UserError : ExitCodes.Success;

    internal static string RenderPackageCheckJson(PackageCheckReport report) => RenderJson(writer => WritePackageCheckJson(writer, report));

    private static void WritePackageCheckJson(Utf8JsonWriter writer, PackageCheckReport report)
    {
        writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 2);
        writer.WriteString("evidenceKind", "bounded-local-static-package-check");
        writer.WriteString("execution", report.Execution); writer.WriteString("agentCompatibility", report.AgentCompatibility);
        writer.WriteString("sourceReview", report.SourceReview); writer.WriteString("referenceScope", PackageCheckReport.ReferenceScope);
        writer.WriteString("installationId", report.InstallationId); writer.WriteString("path", report.Path);
        writer.WriteString("formatStatus", report.FormatStatus); writer.WriteString("referenceStatus", report.ReferenceStatus);
        writer.WriteString("payloadFingerprint", report.PayloadFingerprint);
        writer.WriteString("rootMetadataSha256", report.RootMetadataSha256);
        writer.WriteStartObject("assessments");
        Assessment("format", report.Format); Assessment("agentCompatibility", report.AgentAssessment);
        Assessment("payloadIntact", report.PayloadIntact); Assessment("sourceReviewed", report.SourceReviewed);
        writer.WriteEndObject();
        writer.WriteString("declarationCoverage", report.DeclarationCoverage);
        writer.WriteString("declarationScope", PackageCheckReport.DeclarationScope);
        writer.WriteStartArray("declarationReasons"); foreach (var reason in report.DeclarationReasons) writer.WriteStringValue(reason); writer.WriteEndArray();
        writer.WriteStartArray("observations");
        foreach (var observation in report.Observations)
        {
            writer.WriteStartObject(); writer.WriteString("category", observation.Category); writer.WriteString("kind", observation.Kind);
            writer.WriteString("path", observation.Path); if (observation.Line is { } line) writer.WriteNumber("line", line); else writer.WriteNull("line");
            writer.WriteString("value", observation.Value); writer.WriteString("state", observation.State); writer.WriteString("repair", observation.Repair); writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteNumber("observationCount", report.ObservationCount); writer.WriteNumber("omittedObservationCount", report.OmittedObservationCount);
        writer.WriteStartObject("coverage"); var coverage = report.Coverage;
        writer.WriteString("payload", coverage.Payload); writer.WriteString("references", coverage.References);
        writer.WriteNumber("observedEntries", coverage.ObservedEntries); writer.WriteNumber("observedBytes", coverage.ObservedBytes);
        writer.WriteNumber("analyzedMarkdownFiles", coverage.AnalyzedMarkdownFiles); writer.WriteNumber("analyzedReferences", coverage.AnalyzedReferences);
        writer.WriteStartArray("reasons"); foreach (var reason in coverage.Reasons) writer.WriteStringValue(reason); writer.WriteEndArray(); writer.WriteEndObject();
        writer.WriteStartArray("findings");
        foreach (var finding in report.Findings)
        {
            writer.WriteStartObject(); writer.WriteString("category", finding.Category); writer.WriteString("code", finding.Code);
            writer.WriteString("path", finding.Path); if (finding.Line is { } line) writer.WriteNumber("line", line); else writer.WriteNull("line");
            writer.WriteString("target", finding.Target); writer.WriteString("message", finding.Message); writer.WriteString("repair", finding.Repair); writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteNumber("findingCount", report.FindingCount); writer.WriteNumber("omittedFindingCount", report.OmittedFindingCount);
        writer.WriteBoolean("hasErrors", report.HasErrors); writer.WriteEndObject();

        void Assessment(string name, PackageAssessment assessment)
        {
            writer.WriteStartObject(name); writer.WriteString("state", assessment.State); writer.WriteString("scope", assessment.Scope);
            writer.WriteString("fingerprint", assessment.Fingerprint);
            writer.WriteString("observedCondition", assessment.ObservedCondition);
            writer.WriteStartArray("reasons"); foreach (var reason in assessment.Reasons) writer.WriteStringValue(reason); writer.WriteEndArray();
            writer.WriteStartArray("sources");
            foreach (var source in assessment.Sources)
            {
                writer.WriteStartObject(); writer.WriteString("url", source.Url); writer.WriteString("revision", source.Revision);
                writer.WriteString("evidence", source.Evidence); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
    }

    internal static string RenderPackageCheckText(PackageCheckReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("Static package check: " + report.Path);
        text.AppendLine($"Metadata: {report.FormatStatus}; references: {report.ReferenceStatus}");
        text.AppendLine($"Format: {report.Format.State}; agent compatibility: {report.AgentAssessment.State}; payload intact: {report.PayloadIntact.State}; source reviewed: {report.SourceReviewed.State}");
        text.AppendLine($"Coverage: payload={report.Coverage.Payload}; references={report.Coverage.References}; {report.Coverage.ObservedEntries} entries, {report.Coverage.ObservedBytes} bytes");
        text.AppendLine("Reference scope: " + PackageCheckReport.ReferenceScope);
        foreach (var assessment in new[] { report.Format, report.AgentAssessment, report.PayloadIntact, report.SourceReviewed })
        {
            text.AppendLine("Scope: " + assessment.Scope);
            if (assessment.ObservedCondition is { } condition) text.AppendLine("Observed condition: " + condition);
            foreach (var reason in assessment.Reasons) text.AppendLine("  " + reason);
            foreach (var source in assessment.Sources) text.AppendLine($"  Rule source: {source.Url} ({source.Revision}; {source.Evidence})");
        }
        text.AppendLine("Execution: none. Declarations do not establish installed dependencies, effective permissions or runtime network access.");
        text.AppendLine("Declaration coverage: " + report.DeclarationCoverage);
        text.AppendLine("Declaration scope: " + PackageCheckReport.DeclarationScope);
        foreach (var observation in report.Observations)
            text.AppendLine($"[{observation.Category}/{observation.Kind}; {observation.State}] {observation.Path}{(observation.Line is { } line ? ":" + line : "")}: {observation.Value}\n  Guidance: {observation.Repair}");
        if (report.OmittedObservationCount > 0) text.AppendLine($"{report.OmittedObservationCount} further observations omitted.");
        foreach (var reason in report.DeclarationReasons) text.AppendLine("Declaration coverage: " + reason);
        foreach (var finding in report.Findings)
        {
            text.AppendLine($"[{finding.Category}/{finding.Code}] {finding.Path}{(finding.Line is { } line ? ":" + line : "")}: {finding.Message}");
            if (finding.Target is { } target) text.AppendLine("  Target: " + target);
            text.AppendLine("  Repair: " + finding.Repair);
        }
        if (report.OmittedFindingCount > 0) text.AppendLine($"{report.OmittedFindingCount} further findings omitted from this bounded report.");
        foreach (var reason in report.Coverage.Reasons) text.AppendLine("Coverage: " + reason);
        return TerminalEscapeSanitizer.Sanitize(text.ToString())!;
    }
}
