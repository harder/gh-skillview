using System.Text;
using System.Text.Json;
using SkillView.Authoring;
using SkillView.Bootstrapping;
using SkillView.Ui;

namespace SkillView.Cli;

public static partial class CliDispatcher
{
    internal sealed record AuthoringArguments(string? Repository, bool Json, string? Error);

    internal static AuthoringArguments ParseAuthoringArgs(IReadOnlyList<string> arguments)
    {
        string? repository = null;
        var dryRun = false;
        var json = arguments.Contains("--json");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (!seen.Add(argument)) return new(null, json, "Duplicate authoring option: " + argument);
            if (argument == "--json") continue;
            if (argument == "--dry-run") { dryRun = true; continue; }
            if (argument != "--repository" || ++index >= arguments.Count)
                return new(null, json, "Use authoring-validate --repository PATH --dry-run [--json]. No other publish flags are accepted.");
            repository = arguments[index];
            if (string.IsNullOrWhiteSpace(repository) || repository.StartsWith("--", StringComparison.Ordinal) || repository.Any(char.IsControl))
                return new(null, json, "--repository requires an explicit path without control characters.");
        }
        return repository is null || !dryRun
            ? new(null, json, "Explicit --repository PATH and --dry-run are both required. Dry-run may contact GitHub and write cache state.")
            : new(repository, json, null);
    }

    private static async Task<int> AuthoringAsync(AppOptions options, TuiServices services, CancellationToken token)
    {
        var arguments = ParseAuthoringArgs(options.SubcommandArgs);
        if (arguments.Error is { } error)
        {
            if (arguments.Json) WriteJson(writer =>
            {
                writer.WriteStartObject(); writer.WriteNumber("schemaVersion", AuthoringValidationReport.SchemaVersion);
                writer.WriteString("status", "refused"); writer.WriteString("execution", "none"); writer.WriteString("error", error); writer.WriteEndObject();
            });
            else Console.Error.WriteLine("skillview: " + TerminalEscapeSanitizer.Sanitize(error));
            return ExitCodes.InvalidUsage;
        }
        try
        {
            var report = await new AuthoringValidationService(services.Runner, services.Logger).ValidateAsync(
                arguments.Repository!, services.GhLocator.FindOnPath(token), token).ConfigureAwait(false);
            if (arguments.Json) WriteJson(writer => WriteAuthoringJson(writer, report));
            else Console.Write(RenderAuthoringText(report));
            return report.Diagnostics.Any(item => item.Code == "canceled") ? ExitCodes.Cancelled
                : report.Status == "completed" ? ExitCodes.Success
                : report.Status == "refused" || report.Diagnostics.Any(item => item.Code == "deadline") ? ExitCodes.EnvironmentError
                : report.Status == "skipped" ? ExitCodes.NoMatches : ExitCodes.UserError;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            if (arguments.Json) WriteJson(writer =>
            {
                writer.WriteStartObject(); writer.WriteNumber("schemaVersion", AuthoringValidationReport.SchemaVersion);
                writer.WriteString("status", "refused"); writer.WriteString("execution", "none"); writer.WriteString("error", exception.Message); writer.WriteEndObject();
            });
            else Console.Error.WriteLine("skillview: " + TerminalEscapeSanitizer.Sanitize(exception.Message));
            return ExitCodes.InvalidUsage;
        }
    }

    internal static string RenderAuthoringJson(AuthoringValidationReport report) => RenderJson(writer => WriteAuthoringJson(writer, report));

    private static void WriteAuthoringJson(Utf8JsonWriter writer, AuthoringValidationReport report)
    {
        writer.WriteStartObject(); writer.WriteNumber("schemaVersion", AuthoringValidationReport.SchemaVersion);
        writer.WriteString("evidenceKind", "explicit-authoring-publish-dry-run");
        writer.WriteString("repository", report.Repository); writer.WriteString("status", report.Status); writer.WriteString("execution", report.Execution);
        writer.WriteString("scope", AuthoringValidationReport.Scope); writer.WriteString("limitations", AuthoringValidationReport.Limitations);
        writer.WriteString("ghPath", report.GhPath); writer.WriteString("ghVersion", report.GhVersion);
        if (report.CommandExitCode is { } exit) writer.WriteNumber("commandExitCode", exit); else writer.WriteNull("commandExitCode");
        writer.WriteString("stdout", report.StdOut); writer.WriteString("stderr", report.StdErr); writer.WriteBoolean("outputTruncated", report.OutputTruncated);
        writer.WriteString("discoveryCoverage", report.DiscoveryCoverage); writer.WriteString("authoredBytes", report.AuthoredBytes);
        writer.WriteStartObject("assessments");
        writer.WriteString("format", report.Execution == "gh-skill-publish-dry-run" && report.CommandExitCode is not null ? "gh-diagnostics-only" : "unavailable"); writer.WriteString("agentCompatibility", "unknown"); writer.WriteString("payloadIntact", "unknown");
        writer.WriteString("safety", "not-assessed"); writer.WriteString("sourceReviewed", "not-reviewed"); writer.WriteEndObject();
        writer.WriteStartArray("candidatePayloads");
        foreach (var payload in report.Payloads)
        {
            writer.WriteStartObject(); writer.WriteString("path", payload.Path); writer.WriteString("beforeFingerprint", payload.BeforeFingerprint);
            writer.WriteString("afterFingerprint", payload.AfterFingerprint); writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteStartArray("diagnostics");
        foreach (var diagnostic in report.Diagnostics)
        {
            writer.WriteStartObject(); writer.WriteString("code", diagnostic.Code); writer.WriteString("path", diagnostic.Path);
            writer.WriteString("message", diagnostic.Message); writer.WriteString("guidance", diagnostic.Guidance); writer.WriteEndObject();
        }
        writer.WriteEndArray(); writer.WriteEndObject();
    }

    internal static string RenderAuthoringText(AuthoringValidationReport report)
    {
        var text = new StringBuilder();
        text.AppendLine($"Authoring validation: {report.Status} ({report.Repository})");
        text.AppendLine($"Execution: {report.Execution}; gh {report.GhVersion ?? "unknown"}; exit {report.CommandExitCode?.ToString() ?? "none"}");
        text.AppendLine($"Discovery: {report.DiscoveryCoverage}; authored payloads: {report.AuthoredBytes}");
        foreach (var payload in report.Payloads) text.AppendLine("Candidate: " + payload.Path);
        foreach (var diagnostic in report.Diagnostics) text.AppendLine($"[{diagnostic.Code}] {diagnostic.Path}: {diagnostic.Message}\n  {diagnostic.Guidance}");
        if (report.StdOut.Length > 0) text.AppendLine("gh stdout:\n" + report.StdOut);
        if (report.StdErr.Length > 0) text.AppendLine("gh stderr:\n" + report.StdErr);
        text.AppendLine(AuthoringValidationReport.Scope); text.AppendLine(AuthoringValidationReport.Limitations);
        text.AppendLine("Agent compatibility and intact source: unknown. Safety: not assessed. Source: not reviewed.");
        return TerminalEscapeSanitizer.Sanitize(text.ToString())!;
    }
}
