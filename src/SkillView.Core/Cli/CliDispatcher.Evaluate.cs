using System.Text;
using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Evaluation;
using SkillView.Inventory;
using SkillView.Ui;

namespace SkillView.Cli;

public static partial class CliDispatcher
{
    internal sealed record EvaluationArguments(string? Path, string? Config, string? Review, bool ConsentRun, bool Json, string? Error);

    internal static EvaluationArguments ParseEvaluationArgs(IReadOnlyList<string> arguments, bool run)
    {
        string? path = null, config = null, review = null;
        var consent = false; var json = arguments.Contains("--json"); var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (!seen.Add(argument)) return Fail("Duplicate evaluation option: " + argument);
            if (argument == "--json") continue;
            if (argument == "--consent-run" && run) { consent = true; continue; }
            if (argument is not ("--path" or "--config" or "--review") || argument == "--review" && !run || ++index >= arguments.Count)
                return Fail("Evaluation requires --path PATH --config FILE; run also requires --review FINGERPRINT --consent-run.");
            var value = arguments[index];
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal) || value.Any(char.IsControl))
                return Fail("Evaluation option requires a nonblank value without control characters.");
            if (argument == "--path") path = value;
            else if (argument == "--config") config = value;
            else review = value;
        }
        if (path is null || config is null || run && (review is null || !consent))
            return Fail("Review first; evaluation run requires --path PATH --config FILE --review FINGERPRINT --consent-run.");
        return new(path, config, review, consent, json, null);
        EvaluationArguments Fail(string error) => new(null, null, null, false, json, error);
    }

    private static async Task<int> EvaluateAsync(AppOptions options, TuiServices services, CancellationToken token)
    {
        var run = options.SubcommandName == "evaluate-run";
        var arguments = ParseEvaluationArgs(options.SubcommandArgs, run);
        if (arguments.Error is { } error) return Fail(error, ExitCodes.InvalidUsage);
        try
        {
            var cwd = services.Runner.EffectiveWorkingDirectory;
            var review = await Task.Run(() => EvaluationReview.Capture(Path.GetFullPath(arguments.Path!, cwd),
                Path.GetFullPath(arguments.Config!, cwd), token), token).ConfigureAwait(false);
            EvaluationRunResult? result = null;
            if (run) result = await new IsolatedEvaluationRunner(services.Logger).RunAsync(review, arguments.Review, arguments.ConsentRun, token).ConfigureAwait(false);
            if (arguments.Json) WriteJson(writer => WriteEvaluationJson(writer, review, result));
            else Console.Write(RenderEvaluationText(review, result));
            return result is null || result.State == "completed" ? ExitCodes.Success
                : result.State == "canceled" ? ExitCodes.Cancelled : ExitCodes.EnvironmentError;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
            or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { return Fail(exception.Message, exception is ArgumentException or JsonException or FormatException ? ExitCodes.InvalidUsage : ExitCodes.EnvironmentError); }

        int Fail(string message, int code)
        {
            if (arguments.Json) WriteJson(writer =>
            {
                writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 1); writer.WriteString("evidenceKind", "opt-in-offline-evaluation");
                writer.WriteBoolean("succeeded", false); writer.WriteString("error", message); writer.WriteEndObject();
            });
            else Console.Error.WriteLine("skillview: " + TerminalEscapeSanitizer.Sanitize(message));
            return code;
        }
    }

    internal static string RenderEvaluationJson(EvaluationReview review, EvaluationRunResult? result) => RenderJson(writer => WriteEvaluationJson(writer, review, result));

    private static void WriteEvaluationJson(Utf8JsonWriter writer, EvaluationReview review, EvaluationRunResult? result)
    {
        var config = review.Environment;
        writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 1); writer.WriteString("evidenceKind", "opt-in-offline-evaluation");
        writer.WriteString("policy", EvaluationReview.Policy); writer.WriteString("scope", EvaluationReview.Scope);
        writer.WriteString("state", result?.State ?? "review-only"); writer.WriteBoolean("executionAttempted", result?.ExecutionAttempted ?? false);
        writer.WriteString("reviewFingerprint", review.Fingerprint); writer.WriteString("path", review.Payload.Path);
        writer.WriteString("payloadFingerprint", review.Payload.Fingerprint); writer.WriteString("nativeObjectFingerprint", review.Payload.ObjectStateFingerprint);
        writer.WriteString("configSha256", review.ConfigSha256); writer.WriteString("requestSha256", EvaluationReview.Hash(review.Request));
        writer.WriteStartObject("environment"); writer.WriteString("dockerExecutable", config.DockerExecutable); writer.WriteString("dockerEndpoint", config.DockerEndpoint);
        writer.WriteString("image", config.Image); writer.WriteString("expectedImageId", config.ImageId); writer.WriteString("evaluator", config.Evaluator);
        writer.WriteString("context", config.Context); writer.WriteString("entrypoint", config.Entrypoint);
        writer.WriteStartArray("arguments"); foreach (var argument in config.Arguments) writer.WriteStringValue(argument); writer.WriteStringValue("/input/request.json"); writer.WriteEndArray();
        writer.WriteStartArray("containerEnvironment"); foreach (var variable in EvaluationReview.ContainerEnvironment) writer.WriteStringValue(variable); writer.WriteEndArray();
        writer.WriteNumber("timeoutSeconds", config.TimeoutSeconds); writer.WriteNumber("memoryMiB", config.MemoryMiB);
        writer.WriteNumber("outputCharactersPerStream", EvaluationReview.OutputCharacters);
        writer.WriteString("isolation", "network none; read-only root/input; UID 65534; no capabilities/new privileges; runc builtin seccomp; CPU 1; PIDs 64; private namespaces; bounded tmpfs; no host configuration/environment/socket mounts");
        writer.WriteString("runtimeVerification", result?.ImageId is null ? "not-verified" : "see observed runtime facts; not an independent isolation proof");
        writer.WriteEndObject();
        writer.WriteStartArray("prompts");
        foreach (var prompt in config.Prompts)
        { writer.WriteStartObject(); writer.WriteString("id", prompt.Id); writer.WriteString("text", prompt.Text); writer.WriteEndObject(); }
        writer.WriteEndArray();
        writer.WriteStartArray("payloadEntries");
        foreach (var entry in review.Payload.Entries)
        {
            writer.WriteStartObject(); writer.WriteString("path", entry.Path); writer.WriteString("kind", entry.Kind); writer.WriteString("contentHash", entry.ContentHash);
            writer.WriteNumber("bytes", entry.Bytes); if (entry.UnixMode is { } mode) writer.WriteNumber("sourceUnixMode", mode); else writer.WriteNull("sourceUnixMode"); writer.WriteEndObject();
        }
        writer.WriteEndArray();
        if (result is not null)
        {
            writer.WriteStartObject("observedRuntime"); writer.WriteString("imageId", result.ImageId); writer.WriteString("imageOs", result.ImageOs);
            writer.WriteString("daemonId", result.DaemonId); writer.WriteString("containerId", result.ContainerId);
            writer.WriteStartArray("entrypointArgv"); foreach (var value in result.EntrypointArgv) writer.WriteStringValue(value); writer.WriteEndArray(); writer.WriteEndObject();
            if (result.ExitCode is { } code) writer.WriteNumber("exitCode", code); else writer.WriteNull("exitCode");
            writer.WriteString("stdout", result.StdOut); writer.WriteString("stderr", result.StdErr);
            writer.WriteBoolean("stdoutTruncated", result.StdOutTruncated); writer.WriteBoolean("stderrTruncated", result.StdErrTruncated);
            writer.WriteString("error", result.Error); writer.WriteString("cleanupError", result.CleanupError);
        }
        writer.WriteEndObject();
    }

    internal static string RenderEvaluationText(EvaluationReview review, EvaluationRunResult? result)
    {
        var config = review.Environment; var text = new StringBuilder();
        text.AppendLine("Offline isolated evaluation: " + (result?.State ?? "review-only; no Docker or evaluator launched"));
        text.AppendLine(EvaluationReview.Scope);
        text.AppendLine("Payload: " + review.Payload.Path + " (" + review.Payload.Fingerprint + ")");
        text.AppendLine("Review fingerprint: " + review.Fingerprint);
        text.AppendLine("Docker executable: " + config.DockerExecutable + "; endpoint: " + config.DockerEndpoint);
        text.AppendLine("Declared image: " + config.Image + "; expected ID: " + config.ImageId);
        text.AppendLine("Evaluator: " + config.Evaluator);
        text.AppendLine("Entrypoint argv: " + RenderJson(writer => { writer.WriteStartArray(); writer.WriteStringValue(config.Entrypoint); foreach (var arg in config.Arguments) writer.WriteStringValue(arg); writer.WriteStringValue("/input/request.json"); writer.WriteEndArray(); }));
        text.AppendLine($"Limits: {config.TimeoutSeconds}s execution deadline plus bounded termination/cleanup; {config.MemoryMiB} MiB RAM/no extra swap; CPU 1; PIDs 64; {EvaluationReview.OutputCharacters} captured characters per stream.");
        text.AppendLine("Network none; read-only root/input; nonroot; no capabilities/new privileges; private HOME/CWD/config/tmpfs. Docker daemon and image must already be trusted.");
        text.AppendLine("Context: " + config.Context);
        foreach (var prompt in config.Prompts) text.AppendLine("Prompt " + prompt.Id + ": " + prompt.Text);
        foreach (var entry in review.Payload.Entries) text.AppendLine("Payload " + entry.Kind + " " + entry.Path + " " + entry.ContentHash);
        if (result is not null)
        {
            text.AppendLine("Observed image: " + result.ImageId + "; daemon: " + result.DaemonId + "; container: " + result.ContainerId);
            text.AppendLine("Exit: " + result.ExitCode + "; stdout truncated: " + result.StdOutTruncated + "; stderr truncated: " + result.StdErrTruncated);
            text.AppendLine("Untrusted evaluator stdout:\n" + result.StdOut); text.AppendLine("Untrusted evaluator stderr:\n" + result.StdErr);
            if (result.Error is { } error) text.AppendLine(error);
            if (result.CleanupError is { } cleanup) text.AppendLine(cleanup);
        }
        return TerminalEscapeSanitizer.Sanitize(text.ToString())!;
    }
}
