using System.Collections.Immutable;
using System.Text.Json;
using SkillView.Logging;
using SkillView.Subprocess;
using SkillView.Threading;

namespace SkillView.Evaluation;

internal sealed record EvaluationProcessCall(string Executable, ImmutableArray<string> Arguments,
    string WorkingDirectory, IReadOnlyDictionary<string, string> Environment);
internal sealed record EvaluationRunResult(string State, bool ExecutionAttempted, int? ExitCode,
    string StdOut, string StdErr, bool StdOutTruncated, bool StdErrTruncated,
    string? Error, string? CleanupError, string? ImageId, string? ImageOs,
    string? DaemonId, string? ContainerId, ImmutableArray<string> EntrypointArgv);

internal sealed class IsolatedEvaluationRunner
{
    private readonly Logger logger;
    private readonly Func<EvaluationProcessCall, CancellationToken, Task<ProcessResult>> invoke;

    internal IsolatedEvaluationRunner(Logger logger,
        Func<EvaluationProcessCall, CancellationToken, Task<ProcessResult>>? invokeForTests = null)
    {
        this.logger = logger;
        var runner = new ProcessRunner(logger, EvaluationReview.OutputCharacters);
        invoke = invokeForTests ?? ((call, token) => runner.WithIsolatedContext(call.WorkingDirectory, call.Environment).RunAsync(call.Executable, call.Arguments,
            cancellationToken: token, retainCancellationOutput: true));
    }

    internal async Task<EvaluationRunResult> RunAsync(EvaluationReview review, string? acknowledgedFingerprint,
        bool consentRun, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!consentRun || !string.Equals(acknowledgedFingerprint, review.Fingerprint, StringComparison.Ordinal))
            throw new ArgumentException("Run requires --consent-run and --review matching the exact current evaluate-review fingerprint.");
        if (!OperatingSystem.IsLinux()) throw new IOException("Offline Docker evaluation currently supports a local Linux host only.");
        var config = review.Environment;
        var argv = ImmutableArray.Create(config.Entrypoint).AddRange(config.Arguments).Add("/input/request.json");
        EvaluationWorkspace? workspace = null;
        var name = "skillview-evaluation-" + Guid.NewGuid().ToString("N");
        var creationAdmitted = false; var executionAttempted = false;
        string? cleanup = null, error = null, imageId = null, imageOs = null, daemonId = null, containerId = null;
        ProcessResult? output = null; int? evaluationExitCode = null; var state = "refused";
        using var deadline = new CancellationSource(token, TimeSpan.FromSeconds(config.TimeoutSeconds),
            CancellationCallbackReporter.For(logger, "isolated evaluation deadline"));
        try
        {
            workspace = EvaluationWorkspace.Create(review, logger, deadline.Token);
            var daemon = await Call(["info", "--format", "{{json .}}"], deadline.Token).ConfigureAwait(false);
            using (var document = ParseSuccessful(daemon, "Docker daemon inspection"))
            {
                var root = document.RootElement;
                if (root.GetProperty("OSType").GetString() != "linux" || root.GetProperty("DefaultRuntime").GetString() != "runc"
                    || !root.GetProperty("MemoryLimit").GetBoolean() || !root.GetProperty("SwapLimit").GetBoolean()
                    || !root.GetProperty("PidsLimit").GetBoolean() || !root.GetProperty("CpuCfsQuota").GetBoolean()
                    || !root.GetProperty("SecurityOptions").EnumerateArray().Any(value => value.GetString() == "name=seccomp,profile=builtin"))
                    throw new IOException("Local Linux runc, builtin seccomp and enforced memory/swap/PID/CPU limits are required; this daemon is unsupported.");
                daemonId = root.GetProperty("ID").GetString();
                if (string.IsNullOrWhiteSpace(daemonId)) throw new IOException("Docker daemon identity is unavailable.");
            }
            var image = await Call(["image", "inspect", config.Image, "--format", "{{json .}}"], deadline.Token).ConfigureAwait(false);
            using (var document = ParseSuccessful(image, "Local image inspection (no image pull is attempted)"))
            {
                var root = document.RootElement; imageId = root.GetProperty("Id").GetString(); imageOs = root.GetProperty("Os").GetString();
                var volumes = root.GetProperty("Config").GetProperty("Volumes");
                if (imageId != config.ImageId || imageOs != "linux"
                    || !root.GetProperty("RepoDigests").EnumerateArray().Any(value => value.GetString() == config.Image)
                    || volumes.ValueKind != JsonValueKind.Null && (volumes.ValueKind != JsonValueKind.Object || volumes.EnumerateObject().Any()))
                    throw new IOException("Local image must match the reviewed ID/digest, be Linux, and declare no anonymous VOLUME mounts.");
            }
            workspace.Validate(deadline.Token);
            // Admission precedes await: create can succeed even if the client is
            // canceled before it receives the daemon's response.
            creationAdmitted = true;
            var created = await Call(CreateArguments(config, workspace.InputPath, name), deadline.Token).ConfigureAwait(false);
            RequireSuccessful(created, "Container creation");
            containerId = created.StdOut.Trim();
            if (containerId.Length != 64 || containerId.Any(character => !char.IsAsciiHexDigit(character)))
                throw new IOException("Docker did not return an unambiguous container ID.");
            var inspected = await Call(["container", "inspect", name, "--format", "{{json .}}"], deadline.Token).ConfigureAwait(false);
            using (var document = ParseSuccessful(inspected, "Created-container inspection"))
                ValidateContainer(document.RootElement, config, workspace.InputPath, containerId);
            workspace.Validate(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested(); executionAttempted = true;
            output = await Call(["container", "start", "--attach", name], deadline.Token).ConfigureAwait(false);
            evaluationExitCode = output.ExitCode;
            if (!output.Cancelled && !deadline.IsCancellationRequested && output.Succeeded)
            {
                var waited = await Call(["container", "wait", name], deadline.Token).ConfigureAwait(false);
                RequireSuccessful(waited, "Container completion");
                if (!int.TryParse(waited.StdOut.Trim(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var actualExit) || actualExit is < 0 or > 255)
                    throw new IOException("Docker did not return an unambiguous evaluator exit status.");
                evaluationExitCode = actualExit;
            }
            state = output.Cancelled || deadline.IsCancellationRequested ? "canceled" : evaluationExitCode == 0 ? "completed" : "failed";
            if (state == "failed") error = "Evaluator or Docker attach exited unsuccessfully; no quality verdict is inferred.";
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { state = "canceled"; error = "Evaluation canceled or its reviewed deadline expired."; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or JsonException or InvalidOperationException or KeyNotFoundException)
        { state = deadline.IsCancellationRequested ? "canceled" : executionAttempted ? "failed" : "refused"; error = exception.Message; }
        finally
        {
            if (creationAdmitted)
            {
                // Killing `docker start` alone does not stop the container.
                // Independent bounded cleanup runs even after caller cancellation.
                using var cleanupDeadline = new CancellationSource(TimeSpan.FromSeconds(10),
                    CancellationCallbackReporter.For(logger, "evaluation container cleanup"));
                try
                {
                    var removed = await Call(["container", "rm", "--force", "--volumes", name], cleanupDeadline.Token).ConfigureAwait(false);
                    RequireSuccessful(removed, "Container cleanup");
                }
                catch (Exception exception) when (exception is OperationCanceledException or IOException or NotSupportedException)
                { cleanup = "Container cleanup not verified for " + name + " at " + config.DockerEndpoint + ": " + exception.Message; }
            }
            if (workspace is not null)
            {
                workspace.Dispose();
                if (workspace.CleanupError is { } problem) cleanup = cleanup is null ? problem : cleanup + "\n" + problem;
            }
        }
        if (cleanup is not null && state == "completed") state = "failed";
        return new(state, executionAttempted, evaluationExitCode, output?.StdOut ?? "", output?.StdErr ?? "",
            output?.StdOutTruncated ?? false, output?.StdErrTruncated ?? false, error, cleanup,
            imageId, imageOs, daemonId, containerId, argv);

        Task<ProcessResult> Call(IReadOnlyList<string> arguments, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (workspace is null) throw new IOException("Private Docker context unavailable.");
            var full = ImmutableArray.Create("--host", config.DockerEndpoint, "--config", workspace.DockerConfigPath).AddRange(arguments);
            return invoke(new(config.DockerExecutable, full, workspace.Path,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["HOME"] = workspace.Path,
                    ["DOCKER_CONFIG"] = workspace.DockerConfigPath,
                    ["PATH"] = "/usr/bin:/bin",
                    ["LANG"] = "C.UTF-8",
                    ["TMPDIR"] = workspace.Path,
                    ["XDG_CONFIG_HOME"] = workspace.Path,
                    ["XDG_CACHE_HOME"] = workspace.Path,
                    ["XDG_DATA_HOME"] = workspace.Path,
                    ["XDG_STATE_HOME"] = workspace.Path
                }), cancellation);
        }
    }

    internal static ImmutableArray<string> CreateArguments(EvaluationEnvironment config, string inputPath, string name)
    {
        if (inputPath.Contains(',') || inputPath.Any(char.IsControl)) throw new IOException("Unsupported private input mount path.");
        // Docker isolates PID namespaces by default; --pid=private is invalid.
        // HostConfig.PidMode must remain the valid empty/default value.
        var args = ImmutableArray.Create("container", "create", "--name", name, "--pull=never", "--network=none",
            "--read-only", "--no-healthcheck", "--cap-drop=ALL", "--security-opt=no-new-privileges=true", "--runtime=runc",
            "--user=65534:65534", "--workdir=/work", "--hostname=skillview-evaluation", "--ipc=private", "--cgroupns=private",
            "--cpus=1", "--memory=" + config.MemoryMiB + "m", "--memory-swap=" + config.MemoryMiB + "m", "--pids-limit=64",
            "--ulimit=nofile=256:256", "--ulimit=fsize=16777216:16777216", "--ulimit=core=0:0",
            "--tmpfs=/work:rw,nosuid,nodev,noexec,size=268435456,mode=1777",
            "--tmpfs=/tmp:rw,nosuid,nodev,noexec,size=67108864,mode=1777", "--shm-size=16m",
            "--log-driver=none", "--restart=no", "--stop-timeout=1", "--mount",
            "type=bind,src=" + inputPath + ",dst=/input,readonly,bind-propagation=rprivate");
        foreach (var variable in EvaluationReview.ContainerEnvironment) args = args.Add("--env").Add(variable);
        return args.Add("--entrypoint").Add(config.Entrypoint).Add(config.Image).AddRange(config.Arguments).Add("/input/request.json");
    }

    internal static void ValidateContainer(JsonElement root, EvaluationEnvironment config, string inputPath, string id)
    {
        var host = root.GetProperty("HostConfig"); var settings = root.GetProperty("Config");
        var expectedArgs = config.Arguments.Add("/input/request.json");
        if (root.GetProperty("Id").GetString() != id || root.GetProperty("Image").GetString() != config.ImageId
            || settings.GetProperty("User").GetString() != "65534:65534" || settings.GetProperty("WorkingDir").GetString() != "/work"
            || !settings.GetProperty("Entrypoint").EnumerateArray().Select(value => value.GetString()).SequenceEqual([config.Entrypoint])
            || !settings.GetProperty("Cmd").EnumerateArray().Select(value => value.GetString()).SequenceEqual(expectedArgs)
            || !settings.GetProperty("Healthcheck").GetProperty("Test").EnumerateArray().Select(value => value.GetString()).SequenceEqual(["NONE"])
            || !host.GetProperty("ReadonlyRootfs").GetBoolean() || host.GetProperty("Privileged").GetBoolean()
            || host.GetProperty("NetworkMode").GetString() != "none" || host.GetProperty("Runtime").GetString() != "runc"
            || host.GetProperty("PidMode").GetString() != "" || host.GetProperty("IpcMode").GetString() != "private"
            || host.GetProperty("CgroupnsMode").GetString() != "private"
            || host.GetProperty("Memory").GetInt64() != (long)config.MemoryMiB * 1024 * 1024
            || host.GetProperty("MemorySwap").GetInt64() != (long)config.MemoryMiB * 1024 * 1024
            || host.GetProperty("NanoCpus").GetInt64() != 1_000_000_000 || host.GetProperty("PidsLimit").GetInt64() != 64
            || !host.GetProperty("CapDrop").EnumerateArray().Any(value => value.GetString() == "ALL")
            || !host.GetProperty("SecurityOpt").EnumerateArray().Any(value => value.GetString() == "no-new-privileges=true")
            || host.GetProperty("LogConfig").GetProperty("Type").GetString() != "none")
            throw new IOException("Created container does not match the reviewed isolation contract; it will not be started.");
        var environment = settings.GetProperty("Env").EnumerateArray().Select(value => value.GetString()).ToArray();
        foreach (var variable in EvaluationReview.ContainerEnvironment)
            if (environment.Count(value => value?.Split('=', 2)[0] == variable.Split('=', 2)[0]) != 1 || !environment.Contains(variable))
                throw new IOException("Created container changed a required private environment value.");
        var mounts = root.GetProperty("Mounts").EnumerateArray().ToArray();
        // Docker reports tmpfs in HostConfig.Tmpfs, not as host bind mounts.
        if (mounts.Length != 1 || mounts[0].GetProperty("Type").GetString() != "bind"
            || mounts[0].GetProperty("Source").GetString() != inputPath || mounts[0].GetProperty("Destination").GetString() != "/input"
            || mounts[0].GetProperty("RW").GetBoolean() || mounts[0].GetProperty("Propagation").GetString() != "rprivate")
            throw new IOException("Created container has unexpected mounts; it will not be started.");
        var tmpfs = host.GetProperty("Tmpfs");
        if (tmpfs.EnumerateObject().Count() != 2
            || tmpfs.GetProperty("/work").GetString() != "rw,nosuid,nodev,noexec,size=268435456,mode=1777"
            || tmpfs.GetProperty("/tmp").GetString() != "rw,nosuid,nodev,noexec,size=67108864,mode=1777")
            throw new IOException("Created container changed the bounded temporary filesystems.");
    }

    private static JsonDocument ParseSuccessful(ProcessResult result, string operation)
    { RequireSuccessful(result, operation); return JsonDocument.Parse(result.StdOut, new() { MaxDepth = 32 }); }
    private static void RequireSuccessful(ProcessResult result, string operation)
    {
        if (!result.Succeeded || result.Cancelled || result.StdOutTruncated || result.StdErrTruncated)
            throw new IOException(operation + " failed, was canceled, or exceeded output bounds: " + result.StdErr);
    }
}
