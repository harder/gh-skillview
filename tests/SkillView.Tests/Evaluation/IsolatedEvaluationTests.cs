using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using SkillView.Cli;
using SkillView.Bootstrapping;
using SkillView.Ui;
using SkillView.Tests.Cli;
using SkillView.Evaluation;
using SkillView.Inventory;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Evaluation;

/// Injected Docker responses prove admission/protocol boundaries only. These
/// tests never run a container, client, skill script, model or API.
public sealed class IsolatedEvaluationTests : IDisposable
{
    private readonly DirectoryInfo temporary = Directory.CreateTempSubdirectory("skillview-evaluation-tests-");
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private string Payload => Path.Combine(temporary.FullName, "skill");
    private string Config => Path.Combine(temporary.FullName, "evaluation.json");
    private const string Digest = "local/evaluator@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ImageId = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private readonly List<EvaluationProcessCall> calls = [];
    private EvaluationReview? review;
    private string? inputPath;
    private string? containerId;
    private Func<EvaluationProcessCall, CancellationToken, ProcessResult?>? responseOverride;

    public IsolatedEvaluationTests()
    {
        Directory.CreateDirectory(Payload);
        File.WriteAllText(Path.Combine(Payload, "SKILL.md"), "---\nname: sample\ndescription: Boundary fixture only\n---\n");
        Directory.CreateDirectory(Path.Combine(Payload, "scripts"));
        File.WriteAllText(Path.Combine(Payload, "scripts", "never-run.py"), "raise Exception('must not execute')\n");
        WriteConfig();
    }
    public void Dispose() => temporary.Delete(true);

    private void WriteConfig(string prompt = "Representative task", string context = "Frozen context", string entrypoint = "/opt/evaluator",
        string image = Digest, string endpoint = "unix:///var/run/docker.sock", string[]? arguments = null, string executable = "/explicit/docker", string imageId = ImageId)
    {
        File.WriteAllText(Config, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            dockerExecutable = executable,
            dockerEndpoint = endpoint,
            image,
            imageId,
            entrypoint,
            arguments = arguments ?? [],
            evaluator = "Offline model/evaluator chosen by user",
            context,
            prompts = new[] { new { id = "case-1", text = prompt } },
            timeoutSeconds = 60,
            memoryMiB = 128
        }));
    }
    private EvaluationReview Capture() => review = EvaluationReview.Capture(Payload, Config, Token);
    private Task<EvaluationRunResult> Run(CancellationToken? token = null)
    {
        var frozen = review ?? Capture();
        return new IsolatedEvaluationRunner(new Logger(), Invoke).RunAsync(frozen, frozen.Fingerprint, true, token ?? Token);
    }
    private Task<ProcessResult> Invoke(EvaluationProcessCall call, CancellationToken token)
    {
        calls.Add(call);
        Assert.Equal("/explicit/docker", call.Executable);
        Assert.Equal("--host", call.Arguments[0]); Assert.Equal("unix:///var/run/docker.sock", call.Arguments[1]);
        Assert.Equal("--config", call.Arguments[2]);
        Assert.All(call.Environment.Keys, key => Assert.DoesNotContain(key, new[] { "GH_TOKEN", "GITHUB_TOKEN", "OPENAI_API_KEY", "SSH_AUTH_SOCK", "DOCKER_HOST", "GIT_DIR" }));
        Assert.Equal(call.WorkingDirectory, call.Environment["HOME"]);
        Assert.StartsWith("/var/tmp/skillview-evaluation-", call.WorkingDirectory);
        Assert.Equal(call.Arguments[3], call.Environment["DOCKER_CONFIG"]);
        var overridden = responseOverride?.Invoke(call, token);
        if (overridden is not null) return Task.FromResult(overridden);
        var args = call.Arguments.Skip(4).ToArray();
        if (args[0] == "info") return Task.FromResult(Result(call, JsonSerializer.Serialize(new
        {
            OSType = "linux",
            DefaultRuntime = "runc",
            MemoryLimit = true,
            SwapLimit = true,
            PidsLimit = true,
            CpuCfsQuota = true,
            ID = "fake-daemon",
            SecurityOptions = new[] { "name=seccomp,profile=builtin" }
        })));
        if (args.Take(2).SequenceEqual(["image", "inspect"])) return Task.FromResult(Result(call, JsonSerializer.Serialize(new
        { Id = ImageId, Os = "linux", RepoDigests = new[] { Digest }, Config = new { Volumes = (object?)null } })));
        if (args.Take(2).SequenceEqual(["container", "create"]))
        {
            // Independent primary-contract gate: do not blindly accept emitted
            // arguments and manufacture a successful Docker response.
            if (!DockerPidModeValid(ParsePidMode(args))) return Task.FromResult(Result(call, exit: 125, stderr: "--pid: invalid PID mode"));
            var mount = args[Array.IndexOf(args, "--mount") + 1]; inputPath = mount.Split(',')[1][4..];
            Assert.Equal(File.ReadAllBytes(Path.Combine(Payload, "SKILL.md")), File.ReadAllBytes(Path.Combine(inputPath, "skill", "SKILL.md")));
            using var request = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(inputPath, "request.json")));
            Assert.Equal(review!.Environment.Prompts[0].Text, request.RootElement.GetProperty("prompts")[0].GetProperty("text").GetString());
            Assert.Equal("/input/request.json", args[^1]); Assert.Equal(Digest, args[^2]); // Empty args never adopt image CMD.
            containerId = new string('c', 64); return Task.FromResult(Result(call, containerId));
        }
        if (args.Take(2).SequenceEqual(["container", "inspect"])) return Task.FromResult(Result(call, ContainerJson()));
        if (args.Take(2).SequenceEqual(["container", "start"])) return Task.FromResult(Result(call, "Untrusted model observation\u001b[31m"));
        if (args.Take(2).SequenceEqual(["container", "wait"])) return Task.FromResult(Result(call, "0\n"));
        Assert.Equal(new[] { "container", "rm", "--force", "--volumes" }, args.Take(4));
        Assert.False(token.IsCancellationRequested); return Task.FromResult(Result(call, containerId ?? ""));
    }
    private static ProcessResult Result(EvaluationProcessCall call, string stdout = "", int exit = 0, string stderr = "") => new(call.Executable, call.Arguments, exit, stdout, stderr, TimeSpan.Zero);
    private string ContainerJson(string? pidMode = "", string[]? healthcheckTest = null) => JsonSerializer.Serialize(new
    {
        Id = containerId,
        Image = ImageId,
        Config = new { User = "65534:65534", WorkingDir = "/work", Entrypoint = new[] { "/opt/evaluator" }, Cmd = new[] { "/input/request.json" }, Env = EvaluationReview.ContainerEnvironment, Healthcheck = new { Test = healthcheckTest ?? ["NONE"] } },
        HostConfig = new
        {
            ReadonlyRootfs = true,
            Privileged = false,
            NetworkMode = "none",
            Runtime = "runc",
            PidMode = pidMode,
            IpcMode = "private",
            CgroupnsMode = "private",
            Memory = 128L * 1024 * 1024,
            MemorySwap = 128L * 1024 * 1024,
            NanoCpus = 1_000_000_000L,
            PidsLimit = 64,
            CapDrop = new[] { "ALL" },
            SecurityOpt = new[] { "no-new-privileges=true" },
            LogConfig = new { Type = "none" },
            Tmpfs = new Dictionary<string, string> { ["/work"] = "rw,nosuid,nodev,noexec,size=268435456,mode=1777", ["/tmp"] = "rw,nosuid,nodev,noexec,size=67108864,mode=1777" }
        },
        Mounts = new[] { new { Type = "bind", Source = inputPath, Destination = "/input", RW = false, Propagation = "rprivate" } }
    });
    // Primary contract, checked 2026-10-16:
    // https://docs.docker.com/reference/cli/docker/container/run/#pid-settings---pid
    // https://github.com/moby/moby/blob/master/api/types/container/hostconfig.go
    // Moby permits only empty/default, host or container:<nonempty-name-or-id>.
    // This oracle deliberately rejects the once-emitted invented "private".
    private static bool DockerPidModeValid(string value) => value is "" or "host"
        || value.StartsWith("container:", StringComparison.Ordinal) && value.Length > "container:".Length;

    private static string ParsePidMode(IReadOnlyList<string> arguments)
    {
        var mode = "";
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index].StartsWith("--pid=", StringComparison.Ordinal)) mode = arguments[index][6..];
            else if (arguments[index] == "--pid") mode = ++index < arguments.Count ? arguments[index] : "invalid-missing-value";
        }
        return mode;
    }

    [Fact]
    public void PrimaryDockerPidContractRejectsPrivateAndCreateKeepsDefaultIsolation()
    {
        Assert.True(DockerPidModeValid("")); Assert.True(DockerPidModeValid("host"));
        Assert.True(DockerPidModeValid("container:peer"));
        Assert.False(DockerPidModeValid(ParsePidMode(["--pid=private"])));
        Assert.False(DockerPidModeValid(ParsePidMode(["--pid", "private"])));
        Assert.False(DockerPidModeValid("container:")); Assert.False(DockerPidModeValid("default"));
        var config = EvaluationReview.Parse(File.ReadAllBytes(Config));
        var arguments = IsolatedEvaluationRunner.CreateArguments(config, "/private/input", "fixture");
        Assert.Equal("", ParsePidMode(arguments));
        Assert.Contains("--no-healthcheck", arguments);
        Assert.DoesNotContain(arguments, value => value == "--pid" || value.StartsWith("--pid=", StringComparison.Ordinal));
        inputPath = "/private/input"; containerId = new string('c', 64);
        using var container = JsonDocument.Parse(ContainerJson());
        IsolatedEvaluationRunner.ValidateContainer(container.RootElement, config, inputPath, containerId);
    }

    [Fact]
    public async Task FakeCreateRejectsInvalidPrimaryPidTokenBeforeManufacturingContainer()
    {
        const string context = "/var/tmp/skillview-evaluation-primary-contract";
        var call = new EvaluationProcessCall("/explicit/docker",
            ["--host", "unix:///var/run/docker.sock", "--config", context + "/docker", "container", "create", "--pid=private"],
            context, new Dictionary<string, string> { ["HOME"] = context, ["DOCKER_CONFIG"] = context + "/docker" });
        var result = await Invoke(call, Token);
        Assert.False(result.Succeeded); Assert.Equal(125, result.ExitCode);
        Assert.Equal("--pid: invalid PID mode", result.StdErr);
        Assert.Null(containerId); Assert.Null(inputPath);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("container:peer")]
    [InlineData("private")]
    [InlineData("default")]
    [InlineData(null)]
    public void ObservedPidModeRequiresEmptyDefaultInsteadOfSharedOrInventedValues(string? mode)
    {
        inputPath = "/private/input"; containerId = new string('c', 64);
        using var container = JsonDocument.Parse(ContainerJson(mode));
        var config = EvaluationReview.Parse(File.ReadAllBytes(Config));
        Assert.Throws<IOException>(() => IsolatedEvaluationRunner.ValidateContainer(container.RootElement, config, inputPath, containerId));
    }

    private static bool NativeRunSupported => OperatingSystem.IsLinux() && SecureRemovalBackend.IsSupported;

    [Theory]
    [InlineData("/usr/bin/docker")]
    [InlineData("/explicit/docker")]
    [InlineData("/opt/offline tools/docker")]
    public void LinuxExecutableDeclarationIsPortableAndRetainsExactText(string executable)
    {
        WriteConfig(executable: executable);
        var environment = EvaluationReview.Parse(File.ReadAllBytes(Config));
        Assert.Equal(executable, environment.DockerExecutable);
        Assert.Empty(calls);
    }

    [Theory]
    [InlineData("docker")]
    [InlineData("usr/bin/docker")]
    [InlineData("C:/Docker/docker.exe")]
    [InlineData(@"C:\Docker\docker.exe")]
    [InlineData(@"\usr\bin\docker")]
    [InlineData(@"\\server\share\docker.exe")]
    public void NonLinuxRootedAndRelativeExecutableDeclarationsRemainRefused(string executable)
    {
        WriteConfig(executable: executable);
        var refused = Assert.Throws<ArgumentException>(() => EvaluationReview.Parse(File.ReadAllBytes(Config)));
        Assert.Contains("explicit absolute Linux path", refused.Message);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task NonLinuxRunRefusesValidReviewedLinuxPathBeforeStagingOrProcess()
    {
        Assert.SkipUnless(!OperatingSystem.IsLinux(), "Non-Linux runner admission regression; portable parsing runs on every host.");
        WriteConfig(executable: "/usr/bin/docker");
        var frozen = Capture();
        var refused = await Assert.ThrowsAsync<IOException>(() => new IsolatedEvaluationRunner(new Logger(), Invoke)
            .RunAsync(frozen, frozen.Fingerprint, true, Token));
        Assert.Contains("local Linux host only", refused.Message);
        Assert.Empty(calls);
    }

    [Fact]
    public void ReviewHasNoProcessBoundaryAndPreservesRawPayload()
    {
        var before = PayloadSnapshot.CaptureEvidence(Payload, Token); var frozen = Capture();
        Assert.Equal(before.Fingerprint, frozen.Payload.Fingerprint); Assert.Empty(calls);
        var again = Capture(); Assert.Equal(frozen.Fingerprint, again.Fingerprint);
        Assert.Equal(before.ObjectStateFingerprint, again.Payload.ObjectStateFingerprint);
        Assert.Equal("/input/skill", JsonDocument.Parse(frozen.Request).RootElement.GetProperty("skillPath").GetString());
    }

    [Theory]
    [InlineData("prompt")]
    [InlineData("context")]
    [InlineData("entrypoint")]
    [InlineData("image")]
    [InlineData("endpoint")]
    [InlineData("executable")]
    [InlineData("imageId")]
    [InlineData("arguments")]
    [InlineData("payload")]
    [InlineData("whitespace")]
    public async Task ExactReviewInvalidatesEveryExecutableInputBeforeAnyProcess(string changed)
    {
        var old = Capture().Fingerprint;
        switch (changed)
        {
            case "prompt": WriteConfig(prompt: "New task"); break;
            case "context": WriteConfig(context: "New context"); break;
            case "entrypoint": WriteConfig(entrypoint: "/other/evaluator"); break;
            case "image": WriteConfig(image: Digest[..Digest.LastIndexOf(':')] + ":" + new string('d', 64)); break;
            case "endpoint": WriteConfig(endpoint: "unix:///run/private-docker.sock"); break;
            case "arguments": WriteConfig(arguments: ["--other"]); break;
            case "executable": WriteConfig(executable: "/explicit/other-docker"); break;
            case "imageId": WriteConfig(imageId: "sha256:" + new string('d', 64)); break;
            case "payload": File.AppendAllText(Path.Combine(Payload, "SKILL.md"), "Changed instruction"); break;
            case "whitespace": File.AppendAllText(Config, "\n"); break;
        }
        var fresh = Capture(); Assert.NotEqual(old, fresh.Fingerprint);
        await Assert.ThrowsAsync<ArgumentException>(() => new IsolatedEvaluationRunner(new Logger(), Invoke).RunAsync(fresh, old, true, Token));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task ConsentAndExactFingerprintAreRequiredBeforeStagingOrRuntime()
    {
        var frozen = Capture();
        await Assert.ThrowsAsync<ArgumentException>(() => new IsolatedEvaluationRunner(new Logger(), Invoke).RunAsync(frozen, frozen.Fingerprint, false, Token));
        await Assert.ThrowsAsync<ArgumentException>(() => new IsolatedEvaluationRunner(new Logger(), Invoke).RunAsync(frozen, null, true, Token));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task FakeCompletedEvaluationBindsActualFactsWithoutInventingQualityVerdicts()
    {
        Assert.SkipUnless(NativeRunSupported, "Requires Linux native staging/cleanup; injected Docker boundary only.");
        var frozen = Capture(); var result = await Run();
        Assert.Equal("completed", result.State); Assert.True(result.ExecutionAttempted); Assert.Equal(ImageId, result.ImageId);
        Assert.Null(result.CleanupError); Assert.False(Directory.Exists(calls[0].WorkingDirectory));
        var create = calls.Single(call => call.Arguments.Contains("create")).Arguments;
        foreach (var flag in new[] { "--pull=never", "--network=none", "--read-only", "--no-healthcheck", "--cap-drop=ALL", "--security-opt=no-new-privileges=true", "--pids-limit=64", "--log-driver=none" }) Assert.Contains(flag, create);
        Assert.DoesNotContain(create, value => value.Contains("docker.sock", StringComparison.Ordinal) && value != frozen.Environment.DockerEndpoint);
        Assert.DoesNotContain("\u001b", CliDispatcher.RenderEvaluationText(frozen, result));
        using var json = JsonDocument.Parse(CliDispatcher.RenderEvaluationJson(frozen, result));
        Assert.Contains("\u001b", json.RootElement.GetProperty("stdout").GetString());
        Assert.False(json.RootElement.TryGetProperty("score", out _));
        Assert.Contains("no SkillView quality score or safety verdict", json.RootElement.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task AlreadyCanceledCallerCannotAdmitRuntime()
    {
        var frozen = Capture(); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new IsolatedEvaluationRunner(new Logger(), Invoke).RunAsync(frozen, frozen.Fingerprint, true, canceled.Token));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task CancelAfterCreateAlwaysRequestsIndependentContainerAndNativeInputCleanup()
    {
        Assert.SkipUnless(NativeRunSupported, "Requires Linux native staging/cleanup; injected Docker boundary only.");
        using var canceled = new CancellationTokenSource(); Capture();
        responseOverride = (call, _) =>
        {
            if (!call.Arguments.Contains("start")) return null;
            canceled.Cancel(); return Result(call, "partial", 130) with { Cancelled = true };
        };
        var result = await Run(canceled.Token);
        Assert.Equal("canceled", result.State); Assert.Equal("partial", result.StdOut);
        Assert.Contains(calls, call => call.Arguments.Contains("rm")); Assert.Null(result.CleanupError);
        Assert.False(Directory.Exists(calls[0].WorkingDirectory));
    }

    [Fact]
    public async Task FailedRuntimeStartIsAnHonestRefusalAndDoesNotTryPullOrCreate()
    {
        Assert.SkipUnless(NativeRunSupported, "Requires Linux native staging/cleanup; injected Docker boundary only.");
        Capture(); responseOverride = (call, _) => Result(call, exit: -1, stderr: "Executable missing");
        var result = await Run();
        Assert.Equal("refused", result.State); Assert.False(result.ExecutionAttempted); Assert.Contains("Executable missing", result.Error);
        Assert.Single(calls); Assert.False(Directory.Exists(calls[0].WorkingDirectory));
    }

    [Fact]
    public async Task FailedCreateStillCleansUpBecauseDaemonMayHaveAcceptedIt()
    {
        Assert.SkipUnless(NativeRunSupported, "Requires Linux native staging/cleanup; injected Docker boundary only.");
        Capture(); responseOverride = (call, _) => call.Arguments.Contains("create") ? Result(call, exit: -1, stderr: "Lost response") : null;
        var result = await Run();
        Assert.Equal("refused", result.State); Assert.False(result.ExecutionAttempted);
        Assert.Contains(calls, call => call.Arguments.Contains("rm")); Assert.Null(result.CleanupError);
    }

    [Fact]
    public async Task ChangedCreatedIsolationRefusesBeforeStartAndCleansContainer()
    {
        Assert.SkipUnless(NativeRunSupported, "Requires Linux native staging/cleanup; injected Docker boundary only.");
        Capture(); responseOverride = (call, _) => call.Arguments.Contains("container") && call.Arguments.Contains("inspect")
            ? Result(call, ContainerJson().Replace("\"ReadonlyRootfs\":true", "\"ReadonlyRootfs\":false", StringComparison.Ordinal)) : null;
        var result = await Run();
        Assert.Equal("refused", result.State); Assert.DoesNotContain(calls, call => call.Arguments.Contains("start"));
        Assert.Contains(calls, call => call.Arguments.Contains("rm"));
    }

    [Fact]
    public async Task UnexpectedImageHealthcheckRefusesBeforeStartAndCleansContainer()
    {
        Assert.SkipUnless(NativeRunSupported, "Requires Linux native staging/cleanup; injected Docker boundary only.");
        Capture();
        responseOverride = (call, _) => call.Arguments.Contains("container") && call.Arguments.Contains("inspect")
            ? Result(call, ContainerJson(healthcheckTest: ["CMD-SHELL", "unexpected auxiliary command"])) : null;
        var result = await Run();
        Assert.Equal("refused", result.State); Assert.False(result.ExecutionAttempted);
        Assert.DoesNotContain(calls, call => call.Arguments.Contains("start"));
        Assert.Contains(calls, call => call.Arguments.Contains("rm")); Assert.Null(result.CleanupError);
    }

    [Theory]
    [InlineData("CMD")]
    [InlineData("CMD-SHELL")]
    [InlineData("")]
    [InlineData("NONE", "unexpected extra value")]
    public void InspectedHealthcheckRequiresExactlyTheDockerDisabledTest(params string[] healthcheckTest)
    {
        // docker/cli opts.go constructs HealthConfig.Test=["NONE"] for
        // --no-healthcheck. No image health command may survive that override.
        inputPath = "/private/input"; containerId = new string('c', 64);
        using var container = JsonDocument.Parse(ContainerJson(healthcheckTest: healthcheckTest));
        var config = EvaluationReview.Parse(File.ReadAllBytes(Config));
        Assert.Throws<IOException>(() => IsolatedEvaluationRunner.ValidateContainer(container.RootElement, config, inputPath, containerId));
    }

    [Fact]
    public async Task PrivateInputMutationAfterCreateRefusesBeforeStart()
    {
        Assert.SkipUnless(NativeRunSupported, "Requires Linux native staging/cleanup; injected Docker boundary only.");
        Capture();
        responseOverride = (call, _) =>
        {
            if (!call.Arguments.Contains("container") || !call.Arguments.Contains("inspect")) return null;
            var path = Path.Combine(inputPath!, "skill", "SKILL.md");
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.AppendAllText(path, "Tampered private input");
            return null;
        };
        var result = await Run();
        Assert.Equal("refused", result.State); Assert.Contains("changed", result.Error);
        Assert.DoesNotContain(calls, call => call.Arguments.Contains("start"));
        Assert.Contains(calls, call => call.Arguments.Contains("rm"));
    }

    [Fact]
    public async Task UncertainContainerCleanupCannotBecomeCompletedSuccess()
    {
        Assert.SkipUnless(NativeRunSupported, "Requires Linux native staging/cleanup; injected Docker boundary only.");
        Capture(); responseOverride = (call, _) => call.Arguments.Contains("rm") ? Result(call, exit: 1, stderr: "daemon unavailable") : null;
        var result = await Run(); Assert.Equal("failed", result.State);
        Assert.Contains("Container cleanup not verified", result.CleanupError); Assert.Contains("unix:///var/run/docker.sock", result.CleanupError);
    }

    [Fact]
    public async Task UntrustedOutputTruncationIsRetainedExplicitly()
    {
        Assert.SkipUnless(NativeRunSupported, "Requires Linux native staging/cleanup; injected Docker boundary only.");
        Capture(); responseOverride = (call, _) => call.Arguments.Contains("start")
            ? Result(call, "bounded prefix") with { StdOutTruncated = true, StdErrTruncated = true } : null;
        var result = await Run(); Assert.True(result.StdOutTruncated); Assert.True(result.StdErrTruncated);
    }

    [Theory]
    [InlineData("\"image\":\"local/evaluator@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"", "\"image\":\"latest\"")]
    [InlineData("unix:///var/run/docker.sock", "tcp://remote:2375")]
    [InlineData("\"entrypoint\":\"/opt/evaluator\"", "\"entrypoint\":\"relative\"")]
    [InlineData("\"timeoutSeconds\":60", "\"timeoutSeconds\":301")]
    [InlineData("\"memoryMiB\":128", "\"memoryMiB\":99999")]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1")]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":1,\"credential\":\"forbidden\"")]
    public void InvalidOrAmbientEnvironmentCannotBeReviewed(string before, string after)
    {
        var config = File.ReadAllText(Config); Assert.Contains(before, config);
        File.WriteAllText(Config, config.Replace(before, after, StringComparison.Ordinal));
        Assert.Throws<ArgumentException>(() => Capture()); Assert.Empty(calls);
    }

    [Fact]
    public void NativeCaptureRejectsLinksAndGitMetadataWithoutFollowingThem()
    {
        File.WriteAllText(Path.Combine(Payload, ".git"), "gitdir: /host/private/repository");
        Assert.Throws<IOException>(() => Capture()); File.Delete(Path.Combine(Payload, ".git"));
        File.CreateSymbolicLink(Path.Combine(Payload, "outside"), Config);
        Assert.Throws<IOException>(() => Capture()); Assert.Empty(calls);
    }

    [Fact]
    public void ChangedSecondReadAndFailedFinalCaptureCannotPublishReviewEvidence()
    {
        Assert.Throws<IOException>(() => EvaluationReview.Capture(Payload, Config, Token, (path, sink, token) =>
            PayloadSnapshot.CaptureWithEntrySink(path, (item, entry) =>
            {
                if (entry.Kind != "file") { sink(item, entry); return; }
                using var changed = new MemoryStream(new byte[checked((int)entry.Bytes)]);
                sink(item with { Content = changed }, entry);
            }, token)));
        Assert.Throws<IOException>(() => EvaluationReview.Capture(Payload, Config, Token, (path, sink, token) =>
        { _ = PayloadSnapshot.CaptureWithEntrySink(path, sink, token); throw new IOException("Final capture failure"); }));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task CliReviewUsesChildCwdWithoutNeedingDockerAndRunRefusesUnapprovedInputs()
    {
        await CliConsoleCapture.Gate.WaitAsync(Token);
        var savedOut = Console.Out; var savedError = Console.Error;
        try
        {
            var services = TuiServices.Build(new Logger(), logDirectory: temporary.FullName, workingDirectory: temporary.FullName,
                processEnvironment: new Dictionary<string, string>
                {
                    ["HOME"] = temporary.FullName,
                    ["USERPROFILE"] = temporary.FullName,
                    ["XDG_STATE_HOME"] = temporary.FullName,
                    ["TMPDIR"] = "/var/tmp"
                });
            var (code, stdout) = await InvokeCli("evaluate-review", "--path", "skill", "--config", "evaluation.json", "--json");
            Assert.Equal(ExitCodes.Success, code);
            using (var json = JsonDocument.Parse(stdout))
            {
                Assert.Equal("review-only", json.RootElement.GetProperty("state").GetString());
                Assert.False(json.RootElement.GetProperty("executionAttempted").GetBoolean());
                Assert.Equal("not-verified", json.RootElement.GetProperty("environment").GetProperty("runtimeVerification").GetString());
            }
            (code, stdout) = await InvokeCli("evaluate-run", "--path", "skill", "--config", "evaluation.json", "--json");
            Assert.Equal(ExitCodes.InvalidUsage, code);
            (code, stdout) = await InvokeCli("evaluate-run", "--path", "skill", "--config", "evaluation.json", "--json", "--review", "never-approved", "--consent-run");
            Assert.Equal(ExitCodes.InvalidUsage, code);
            using var refusal = JsonDocument.Parse(stdout);
            Assert.False(refusal.RootElement.GetProperty("succeeded").GetBoolean());
            Assert.Contains("exact current evaluate-review fingerprint", refusal.RootElement.GetProperty("error").GetString());

            async Task<(int Code, string Stdout)> InvokeCli(params string[] arguments)
            {
                using var output = new StringWriter(); using var error = new StringWriter();
                Console.SetOut(output); Console.SetError(error);
                var result = await CliDispatcher.RunAsync(ArgParser.Parse("skillview", arguments), services, Token);
                return (result, output.ToString());
            }
        }
        finally { Console.SetOut(savedOut); Console.SetError(savedError); CliConsoleCapture.Gate.Release(); }
    }

    [Fact]
    public void ArgumentParserKeepsReviewNonExecutingAndRunExplicit()
    {
        Assert.Null(CliDispatcher.ParseEvaluationArgs(["--path", Payload, "--config", Config], false).Error);
        Assert.NotNull(CliDispatcher.ParseEvaluationArgs(["--path", Payload, "--config", Config, "--consent-run"], false).Error);
        Assert.NotNull(CliDispatcher.ParseEvaluationArgs(["--path", Payload, "--config", Config, "--review", "hash"], true).Error);
        Assert.NotNull(CliDispatcher.ParseEvaluationArgs(["--path", Payload, "--config", Config, "--path", Payload], false).Error);
        Assert.Null(CliDispatcher.ParseEvaluationArgs(["--path", Payload, "--config", Config, "--review", "hash", "--consent-run", "--json"], true).Error);
    }
}
