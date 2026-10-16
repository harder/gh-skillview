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
    internal sealed record CheckArguments(string? Path, string? InstallationId, bool Json, bool AllowHidden, string? Error);

    internal static CheckArguments ParseCheckArgs(IReadOnlyList<string> arguments)
    {
        string? path = null, id = null;
        var json = arguments.Contains("--json");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hidden = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (!seen.Add(argument)) return new(null, null, json, hidden, "Duplicate check option: " + argument);
            if (argument == "--json") continue;
            if (argument == "--allow-hidden-dirs") { hidden = true; continue; }
            if (argument is not ("--path" or "--install-id") || index + 1 >= arguments.Count)
                return new(null, null, json, hidden, "check requires exactly one --path PATH or --install-id ID; optional --json and --allow-hidden-dirs.");
            var value = arguments[++index];
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal) || value.Any(char.IsControl))
                return new(null, null, json, hidden, argument + " requires a nonblank value without control characters.");
            if (argument == "--path") path = value; else id = value;
        }
        return (path is null) == (id is null)
            ? new(null, null, json, hidden, "Select exactly one --path PATH or --install-id ID.")
            : new(path, id, json, hidden, null);
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
            var report = await new StaticPackageChecker().CheckAsync(target, token).ConfigureAwait(false);
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
                writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 1);
                writer.WriteBoolean("succeeded", false); writer.WriteString("error", message); writer.WriteEndObject();
            });
            else Console.Error.WriteLine("skillview: " + TerminalEscapeSanitizer.Sanitize(message));
            return code;
        }
    }

    internal static int PackageCheckExitCode(PackageCheckReport report) => report.HasErrors
        || report.Coverage.Payload != "complete" || report.Coverage.References != "limited"
        || report.FormatStatus != "valid" || report.ReferenceStatus is not ("none-observed" or "findings") ? ExitCodes.UserError : ExitCodes.Success;

    internal static string RenderPackageCheckJson(PackageCheckReport report) => RenderJson(writer => WritePackageCheckJson(writer, report));

    private static void WritePackageCheckJson(Utf8JsonWriter writer, PackageCheckReport report)
    {
        writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 1);
        writer.WriteString("evidenceKind", "bounded-local-static-package-check");
        writer.WriteString("execution", report.Execution); writer.WriteString("agentCompatibility", report.AgentCompatibility);
        writer.WriteString("sourceReview", report.SourceReview); writer.WriteString("referenceScope", PackageCheckReport.ReferenceScope);
        writer.WriteString("installationId", report.InstallationId); writer.WriteString("path", report.Path);
        writer.WriteString("formatStatus", report.FormatStatus); writer.WriteString("referenceStatus", report.ReferenceStatus);
        writer.WriteString("payloadFingerprint", report.PayloadFingerprint);
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
    }

    internal static string RenderPackageCheckText(PackageCheckReport report)
    {
        var text = new StringBuilder();
        text.AppendLine("Static package check: " + report.Path);
        text.AppendLine($"Metadata: {report.FormatStatus}; references: {report.ReferenceStatus}");
        text.AppendLine($"Coverage: payload={report.Coverage.Payload}; references={report.Coverage.References}; {report.Coverage.ObservedEntries} entries, {report.Coverage.ObservedBytes} bytes");
        text.AppendLine("Reference scope: " + PackageCheckReport.ReferenceScope);
        text.AppendLine("Agent compatibility: not assessed; source review: not assessed; execution: none.");
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
