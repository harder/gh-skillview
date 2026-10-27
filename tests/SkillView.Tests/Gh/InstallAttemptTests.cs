using System.Diagnostics;
using SkillView.Tests.Subprocess;
using SkillView.Tests.Ui;
using System.Collections.Immutable;
using SkillView.Bootstrapping;
using SkillView.Diagnostics;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Logging;
using SkillView.Subprocess;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Gh;

[Collection(TestCollections.ResourceStress)]
public sealed class InstallAttemptTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealChildPartialWriteFailureAndSuccessRetainStreamsAndReconcileOnce(bool success)
    {
        using var fixture = new Fixture(success ? "ok" : "fail");
        var coordinator = fixture.Coordinator();
        var generation = fixture.Services.ListAdapter.Generation;
        var result = await fixture.Install("demo", TestContext.Current.CancellationToken);
        Assert.Equal(success ? InstallAttemptOutcome.Succeeded : InstallAttemptOutcome.ChildFailed, result.Attempt);
        Assert.Equal(success, result.Succeeded);
        Assert.True(result.MayHaveChangedFiles);
        Assert.True(result.ChildDrained);
        Assert.Contains("child stdout", result.StdOut);
        Assert.Contains("child stderr", result.StdErr);
        Assert.True(File.Exists(fixture.Payload("demo")));
        Assert.Equal(0, fixture.Captures);
        var receipt = await coordinator.CreateInstallAttemptRun(fixture.Report).CompleteAsync(InstallAttemptSummary.Single(result));
        Assert.Equal(generation + 1, fixture.Services.ListAdapter.Generation);
        Assert.Equal(InstallRefreshState.Completed, receipt.Refresh);
        Assert.Equal(1, fixture.Captures);
        Assert.Contains(receipt.Snapshot!.Skills, skill => skill.Name == "demo");
        Assert.Same(receipt.Snapshot, await coordinator.CaptureInventorySnapshotAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, fixture.Captures);
        Assert.Equal("unrelated", File.ReadAllText(Path.Combine(fixture.Root, "unrelated")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealChildCancelAndDeadlineAfterWriteRetainAttemptAndRefreshAfterExit(bool deadline)
    {
        using var fixture = new Fixture("wait");
        using var cancellation = new CancellationTokenSource();
        var install = fixture.Install("demo", cancellation.Token);
        await WaitFor(() => File.Exists(fixture.Payload("demo")));
        if (deadline) cancellation.CancelAfter(20); else cancellation.Cancel();
        var result = await install;
        Assert.Equal(InstallAttemptOutcome.ChildCanceled, result.Attempt);
        Assert.Equal(130, result.ExitCode);
        Assert.True(result.ChildDrained);
        Assert.True(result.MayHaveChangedFiles);
        Assert.Contains("child stdout", result.StdOut);
        Assert.Contains("child stderr", result.StdErr);
        var receipt = await fixture.Coordinator().CreateInstallAttemptRun(fixture.Report).CompleteAsync(InstallAttemptSummary.Single(result));
        Assert.Equal(InstallRefreshState.Completed, receipt.Refresh);
        Assert.Contains(receipt.Snapshot!.Skills, skill => skill.Name == "demo");
        Assert.Equal(1, fixture.Captures);
        Assert.Contains("may have changed files", receipt.Guidance);
    }

    [Theory]
    [InlineData("cancel", InstallAttemptOutcome.CanceledBeforeStart)]
    [InlineData("refuse", InstallAttemptOutcome.RefusedPreflight)]
    [InlineData("missing", InstallAttemptOutcome.StartFailed)]
    public async Task NoWriterDoesNotInvalidateCaptureOrHash(string kind, InstallAttemptOutcome expected)
    {
        using var fixture = new Fixture("ok");
        using var cancellation = new CancellationTokenSource();
        if (kind == "cancel") cancellation.Cancel();
        var destination = Path.Combine(fixture.Root, "occupied");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "unrelated"), "protected");
        var result = await fixture.Services.InstallService.InstallAsync(kind == "missing" ? Path.Combine(fixture.Root, "absent") : fixture.Script,
            "fixture/source", "demo", kind == "refuse" ? new(Path: destination, Overwrite: true) : fixture.Options, cancellation.Token);
        Assert.Equal(expected, result.Attempt);
        Assert.False(result.MayHaveChangedFiles);
        var generation = fixture.Services.ListAdapter.Generation;
        var receipt = await fixture.Coordinator().CreateInstallAttemptRun(fixture.Report).CompleteAsync(InstallAttemptSummary.Single(result));
        Assert.Equal(InstallRefreshState.NotNeeded, receipt.Refresh);
        Assert.Equal(generation, fixture.Services.ListAdapter.Generation);
        Assert.Equal(0, fixture.Captures);
        Assert.False(File.Exists(fixture.Payload("demo")));
        Assert.Equal("protected", File.ReadAllText(Path.Combine(destination, "unrelated")));
    }

    [Fact]
    public async Task CancellationAtRunnerStartAdmissionIsProvenBeforeStart()
    {
        using var fixture = new Fixture("ok");
        using var cancellation = new CancellationTokenSource();
        using var subscription = fixture.Logger.Subscribe(entry =>
        {
            if (entry.Category == "subprocess" && entry.Message.Contains("skill install", StringComparison.Ordinal)) cancellation.Cancel();
        });
        var result = await fixture.Install("demo", cancellation.Token);
        Assert.Equal(InstallAttemptOutcome.CanceledBeforeStart, result.Attempt);
        Assert.False(result.MayHaveChangedFiles);
        Assert.False(File.Exists(fixture.Payload("demo")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedPickerRetainsExactResultsAndUnsubmittedSelectorsWithOneCapture(bool cancel)
    {
        using var fixture = new Fixture(cancel ? "mixed-wait" : "mixed");
        using var cancellation = new CancellationTokenSource();
        var plan = new GhSkillInstallService.InstallPlan(false, ["one", "two", "three"]);
        var operation = fixture.Services.InstallService.InstallBatchAsync(fixture.Script, "fixture/source", plan, fixture.Options, cancellation.Token);
        if (cancel)
        {
            await WaitFor(() => File.Exists(fixture.Payload("two")));
            cancellation.Cancel();
        }
        var result = await operation;
        Assert.False(result.Succeeded);
        Assert.Equal(["one", "two"], result.Results.Select(item => item.SkillName));
        Assert.True(result.Results[0].Succeeded);
        Assert.Equal(cancel ? InstallAttemptOutcome.ChildCanceled : InstallAttemptOutcome.ChildFailed, result.Results[1].Attempt);
        Assert.Equal(["three"], result.NotAttempted);
        Assert.False(File.Exists(fixture.Payload("three")));
        var receipt = await fixture.Coordinator().CreateInstallAttemptRun(fixture.Report).CompleteAsync(result);
        Assert.Equal(1, fixture.Captures);
        var text = InstallFailureDetails.Render(receipt);
        Assert.Contains("one: Succeeded", text);
        Assert.Contains(cancel ? "two: ChildCanceled" : "two: ChildFailed", text);
        Assert.Contains("Not attempted:\nthree", text);
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("bad-list")]
    public async Task IncompleteCaptureNeverClaimsCompleteRefresh(string mode)
    {
        using var fixture = new Fixture(mode);
        var result = await fixture.Install("demo", TestContext.Current.CancellationToken);
        var receipt = await fixture.Coordinator().CreateInstallAttemptRun(fixture.Report).CompleteAsync(InstallAttemptSummary.Single(result));
        Assert.Equal(InstallRefreshState.Partial, receipt.Refresh);
        Assert.Contains("incomplete", receipt.Guidance);
        Assert.Contains("unknown files remain protected", receipt.Guidance);
    }

    [Fact]
    public async Task OldGenerationCaptureCannotPublishAndNextFreshCaptureSeesDisk()
    {
        using var fixture = new Fixture("ok");
        var coordinator = fixture.Coordinator();
        var result = await fixture.Install("demo", TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(fixture.Root, "block-list"), "");
        var reconcile = coordinator.CreateInstallAttemptRun(fixture.Report).CompleteAsync(InstallAttemptSummary.Single(result));
        await WaitFor(() => File.Exists(Path.Combine(fixture.Root, "list-entered")));
        fixture.Services.ListAdapter.Invalidate();
        File.Delete(Path.Combine(fixture.Root, "block-list"));
        var receipt = await reconcile;
        Assert.NotEqual(receipt.CatalogGeneration, fixture.Services.ListAdapter.Generation);
        Assert.Equal(InstallRefreshState.Failed, receipt.Refresh);
        // A stale receipt cannot create an exact navigation handoff.
        var evidence = result.Navigation;
        Assert.Null(evidence); // This request made no source-navigation assertion.
        var captures = fixture.Captures;
        var fresh = await coordinator.CaptureInventoryAsync(fixture.Report, TestContext.Current.CancellationToken, bypassGhCache: true);
        Assert.NotSame(receipt.Snapshot, fresh);
        Assert.Equal(captures + 1, fixture.Captures);
    }

    [Fact]
    public async Task UnknownLegacyFailureInvalidatesButDoesNotCaptureWhileWriterIsUnverified()
    {
        using var fixture = new Fixture("ok");
        var result = InstallResult.Failure("fixture/source", "demo", null, -1, "transport unknown", []);
        Assert.Equal(InstallAttemptOutcome.Unknown, result.Attempt);
        Assert.True(result.MayHaveChangedFiles);
        var generation = fixture.Services.ListAdapter.Generation;
        var receipt = await fixture.Coordinator().CreateInstallAttemptRun(fixture.Report).CompleteAsync(InstallAttemptSummary.Single(result));
        Assert.Equal(InstallRefreshState.WriterUnverified, receipt.Refresh);
        Assert.Equal(generation + 1, fixture.Services.ListAdapter.Generation);
        Assert.Equal(0, fixture.Captures);
    }

    [Fact]
    public async Task ChangedContextCannotInvalidateAnotherContextsFacts()
    {
        using var fixture = new Fixture("ok");
        var coordinator = fixture.Coordinator();
        var result = await fixture.Install("demo", TestContext.Current.CancellationToken);
        var generation = fixture.Services.ListAdapter.Generation;
        var receipt = await coordinator.ReconcileInstallAttemptAsync(InstallAttemptSummary.Single(result), fixture.Report, "another-context");
        Assert.Equal(InstallRefreshState.Failed, receipt.Refresh);
        Assert.Equal(generation, fixture.Services.ListAdapter.Generation);
        Assert.Equal(0, fixture.Captures);
    }

    [Fact]
    public async Task PreCanceledBatchRetainsEveryUnsubmittedSelectorAndDoesNoIo()
    {
        using var fixture = new Fixture("ok");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await fixture.Services.InstallService.InstallBatchAsync(fixture.Script, "fixture/source",
            new(false, ["one", "two"]), fixture.Options, cancellation.Token);
        Assert.Empty(result.Results);
        Assert.Equal(["one", "two"], result.NotAttempted);
        Assert.True(result.Canceled);
        Assert.False(result.MayHaveChangedFiles);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "calls")));
    }

    [Fact]
    public void BatchOutputBudgetPreservesTypedOutcomesAndExplicitTruncation()
    {
        var remaining = 10;
        var first = InstallResult.Failure("fixture/source", "one", null, 1, "stderr", []) with
        { Attempt = InstallAttemptOutcome.ChildFailed, ChildDrained = true, StdOut = "stdout" };
        var retained = InstallAttemptSummary.Retain(first, ref remaining);
        Assert.Equal(0, remaining);
        Assert.Equal("stdout", retained.StdOut);
        Assert.Equal("stde", retained.StdErr);
        Assert.True(retained.StdErrTruncated);
        var next = InstallAttemptSummary.Retain(first with { SkillName = "two" }, ref remaining);
        Assert.Empty(next.StdOut);
        Assert.True(next.StdOutTruncated);
        Assert.Equal(InstallAttemptOutcome.ChildFailed, next.Attempt);
        Assert.Equal("two", next.SkillName);
        Assert.Contains("[stdout truncated]", InstallFailureDetails.Render(next));
        Assert.Equal("stderr", first.StdErr);
    }

    [Fact]
    public async Task StartExceptionCannotProveNoChildFromItsTypeOrExitCode()
    {
        using var fixture = new Fixture("ok");
        // The file exists, so this reaches Process.Start, whose failure is
        // deliberately unknown rather than inferred from Win32Exception.
        var executable = Path.Combine(fixture.Root, "invalid-executable");
        File.WriteAllText(executable, "not an executable");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var result = await fixture.Services.InstallService.InstallAsync(executable, "fixture/source", "demo", fixture.Options,
            TestContext.Current.CancellationToken);
        Assert.False(result.Succeeded);
        Assert.Equal(InstallAttemptOutcome.Unknown, result.Attempt);
        Assert.True(result.MayHaveChangedFiles);
        Assert.False(result.ChildDrained);
        var receipt = await fixture.Coordinator().CreateInstallAttemptRun(fixture.Report).CompleteAsync(InstallAttemptSummary.Single(result));
        Assert.Equal(InstallRefreshState.WriterUnverified, receipt.Refresh);
        Assert.Equal(0, fixture.Captures);
    }

    [Fact]
    public async Task RefusedTerminationRetainsStartedCancellationInvalidatesFactsAndSkipsCapture()
    {
        using var child = new RefusingTerminationChild();
        using var fixture = new Fixture("wait", child.Runner);
        using var cancellation = new CancellationTokenSource();
        var coordinator = fixture.Coordinator();
        var generation = fixture.Services.ListAdapter.Generation;
        var execution = fixture.Install("demo", cancellation.Token);
        try
        {
            await WaitFor(() => File.Exists(fixture.Payload("demo")));
            var elapsed = Stopwatch.StartNew();
            cancellation.Cancel();
            var result = await execution.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3));
            Assert.True(child.IsAlive);
            Assert.Equal(InstallAttemptOutcome.ChildCanceled, result.Attempt);
            Assert.True(result.Canceled);
            Assert.True(result.MayHaveChangedFiles);
            Assert.False(result.ChildDrained);
            Assert.Contains("child stdout", result.StdOut);
            Assert.Contains("child stderr", result.StdErr);
            InstallAttemptReceipt? initial = null;
            var receipt = await coordinator.CreateInstallAttemptRun(fixture.Report).CompleteAsync(InstallAttemptSummary.Single(result),
                pending => initial = pending);
            Assert.Equal(InstallRefreshState.WriterUnverified, initial!.Refresh);
            Assert.DoesNotContain("Installed is refreshing", initial.Guidance);
            Assert.Equal(generation + 1, fixture.Services.ListAdapter.Generation);
            Assert.Equal(InstallRefreshState.WriterUnverified, receipt.Refresh);
            Assert.Null(receipt.Snapshot);
            Assert.Equal(0, fixture.Captures);
            Assert.Contains("after it stops", receipt.Guidance);
            Assert.Contains("ChildCanceled", InstallFailureDetails.Render(receipt));
            Assert.DoesNotContain("No install started", receipt.Guidance);
            Assert.True(child.IsAlive); // No hidden reaper or automatic retry.
        }
        finally
        {
            cancellation.Cancel();
            if (!execution.IsCompleted && !child.IsAlive)
                await Task.WhenAny(execution, child.Observed).WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
            child.Dispose();
            await execution.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }

    internal sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("skillview-install-attempt-").FullName;
        internal string Script => Path.Combine(Root, "gh");
        internal Logger Logger { get; } = new(LogLevel.Debug);
        internal TuiServices Services { get; }
        internal GhSkillInstallService.Options Options { get; } = new(Agents: ["universal"], Scope: "user");
        internal EnvironmentReport Report { get; }
        internal int Captures => File.Exists(Path.Combine(Root, "calls"))
            ? File.ReadAllLines(Path.Combine(Root, "calls")).Count(line => line.StartsWith("skill list --json", StringComparison.Ordinal) && !line.Contains("--dir", StringComparison.Ordinal)) : 0;
        internal string Payload(string name) => Path.Combine(Root, "home", ".agents", "skills", name, "SKILL.md");
        internal Fixture(string mode, Func<Logger, ProcessRunner>? createRunner = null)
        {
            Assert.SkipUnless(OperatingSystem.IsLinux(), "Isolated POSIX fake child.");
            var home = Directory.CreateDirectory(Path.Combine(Root, "home")).FullName;
            var work = Directory.CreateDirectory(Path.Combine(Root, "work")).FullName;
            File.WriteAllText(Path.Combine(Root, "unrelated"), "unrelated");
            File.WriteAllText(Script, """
                #!/bin/sh
                printf '%s\n' "$*" >> "$FIXTURE_ROOT/calls"
                case "$*" in
                  'skill list '*)
                    touch "$FIXTURE_ROOT/list-entered"
                    while [ -f "$FIXTURE_ROOT/block-list" ]; do /bin/sleep 0.01; done
                    if [ "$MODE" = partial ]; then echo failure >&2; exit 1; fi
                    if [ "$MODE" = bad-list ]; then echo invalid; exit 0; fi
                    echo '[]'; exit 0 ;;
                esac
                for name do :; done
                path="$HOME/.agents/skills/$name"
                /bin/mkdir -p "$path"
                printf '%s\n' '---' "name: $name" '---' 'private owned partial payload' > "$path/SKILL.md"
                echo 'child stdout'; echo 'child stderr' >&2
                case "$MODE:$name" in
                  ok:*|mixed:one|mixed-wait:one) exit 0 ;;
                  wait:*|mixed-wait:two) exec /bin/sleep 30 ;;
                  *) exit 1 ;;
                esac
                """);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var environment = new Dictionary<string, string>
            {
                ["HOME"] = home,
                ["USERPROFILE"] = home,
                ["PATH"] = "/usr/bin:/bin",
                ["FIXTURE_ROOT"] = Root,
                ["MODE"] = mode,
                ["XDG_CONFIG_HOME"] = Path.Combine(Root, "config"),
                ["XDG_STATE_HOME"] = Path.Combine(Root, "state"),
                ["XDG_DATA_HOME"] = Path.Combine(Root, "data"),
                ["XDG_CACHE_HOME"] = Path.Combine(Root, "cache"),
                ["CODEX_HOME"] = Path.Combine(home, ".codex"),
                ["CLAUDE_CONFIG_DIR"] = Path.Combine(home, ".claude"),
                ["CLAUDE_CODE_PLUGIN_CACHE_DIR"] = Path.Combine(home, ".claude", "plugins"),
                ["CLAUDE_CODE_PLUGIN_DIRS"] = "",
                ["CLAUDE_CODE_PLUGIN_SEED_DIR"] = "",
                ["PI_CODING_AGENT_DIR"] = Path.Combine(home, ".pi"),
                ["GIT_DIR"] = Path.Combine(work, ".git"),
                ["GIT_WORK_TREE"] = work,
                ["GIT_COMMON_DIR"] = Path.Combine(work, ".git"),
                ["GIT_CONFIG_COUNT"] = "0",
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_CONFIG_GLOBAL"] = "/dev/null"
            };
            var runner = (createRunner?.Invoke(Logger) ?? new ProcessRunner(Logger)).WithIsolatedContext(work, environment);
            var locator = new GhBinaryLocator(runner, Logger);
            var auth = new GhAuthService(runner, Logger);
            var list = new GhSkillListAdapter(runner, Logger);
            var resolver = new SkillView.Inventory.ScanRootResolver();
            var scanner = new SkillView.Inventory.LocalSkillScanner(Logger);
            Services = new()
            {
                Logger = Logger,
                Runner = runner,
                GhLocator = locator,
                AuthService = auth,
                EnvironmentProbe = new(locator, auth, runner, Logger, Root),
                SearchService = new(runner, Logger),
                PreviewService = new(runner, Logger),
                InstallService = new(runner, Logger),
                UpdateService = new(runner, Logger),
                ListAdapter = list,
                ScanRootResolver = resolver,
                Scanner = scanner,
                InventoryService = new(resolver, scanner, list, Logger, runner),
                RemoveService = new(Logger),
                FileLogSink = null,
                LogDirectory = Root,
            };
            Report = new()
            {
                GhPath = Script,
                GhVersionRaw = "2.103.2",
                GhVersion = null,
                GhMeetsMinimum = true,
                GhSkillAvailable = true,
                Auth = GhAuthStatus.Unknown,
                LogDirectory = null
            };
        }
        internal Task<InstallResult> Install(string name, CancellationToken token = default) =>
            Services.InstallService.InstallAsync(Script, "fixture/source", name, Options, token);
        internal SkillViewWorkflowCoordinator Coordinator() => new(Services,
            new(InvocationMode.Standalone, DispatchMode.Tui, false, AppTheme.Default, [], null, []),
            () => null, () => Script, () => Report, _ => Task.FromResult(Report), _ => { }, () => { }, _ => { }, (_, _) => { },
            action => action(), (action, _) => { action(); return Task.FromResult(true); }, (_, _) => false, () => { }, () => { });
        public void Dispose() => Directory.Delete(Root, true);
    }
}
