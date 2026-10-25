using System.Collections.Immutable;
using SkillView.Bootstrapping;
using SkillView.Diagnostics;
using SkillView.Gh;
using SkillView.Logging;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.ResourceStress)]
public sealed class SkillViewWorkflowCoordinatorTests
{
    [Fact]
    public async Task BuildRemoveDialogPlanAsync_RunsFilesystemEvaluationOffCallerThread()
    {
        var skill = MakeSkill();
        var snapshot = Snapshot(skill);
        var callerThread = System.Environment.CurrentManagedThreadId;
        var evaluatorThread = callerThread;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var operation = SkillViewWorkflowCoordinator.BuildRemoveDialogPlanAsync(
            skill,
            snapshot,
            TestContext.Current.CancellationToken,
            (target, _, cancellationToken) =>
            {
                evaluatorThread = System.Environment.CurrentManagedThreadId;
                entered.Set();
                release.Wait(cancellationToken);
                return new RemoveTargetEvaluation(
                    target,
                    ImmutableArray<RemoveTargetItem>.Empty);
            });

        try
        {
            Assert.True(entered.Wait(
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken));
            Assert.NotEqual(callerThread, evaluatorThread);
            Assert.False(operation.IsCompleted);
        }
        finally
        {
            release.Set();
        }

        var plan = await operation;
        Assert.Single(plan.Targets);
        Assert.NotNull(plan.PrimaryEvaluation);
    }

    [Fact]
    public async Task BuildRemoveDialogPlanAsync_DoesNotEvaluateWhenAlreadyCanceled()
    {
        var skill = MakeSkill();
        var snapshot = Snapshot(skill);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var evaluated = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SkillViewWorkflowCoordinator.BuildRemoveDialogPlanAsync(
                skill,
                snapshot,
                cancellation.Token,
                (target, _, _) =>
                {
                    evaluated = true;
                    return new RemoveTargetEvaluation(
                        target,
                        ImmutableArray<RemoveTargetItem>.Empty);
                }));

        Assert.False(evaluated);
    }

    [Fact]
    public async Task RemovalPlanResolvesFreshConsumersAndSubjectBeforeEvaluation()
    {
        var selected = MakeSkill();
        var fresh = selected with { Agents = [new("claude", "/agents/demo", true, Scope: Scope.User)] };
        fresh = fresh with { ObservedSubject = OperationSubject.From(fresh) };
        var snapshot = Snapshot(fresh);
        var plan = await SkillViewWorkflowCoordinator.BuildRemoveDialogPlanAsync(selected, snapshot,
            TestContext.Current.CancellationToken, (target, _, _) => new(target, []));
        Assert.Same(fresh, plan.Target);
        Assert.Contains(plan.Targets, target => target.Kind == RemoveTargetKind.AgentSymlink);
        Assert.Contains(plan.Target.Agents, agent => agent.AgentId == "claude");
        Assert.Same(fresh.ObservedSubject, plan.Target.ObservedSubject);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovalPlanRefusesMissingOrAmbiguousFreshSelection(bool ambiguous)
    {
        var selected = MakeSkill();
        var snapshot = Snapshot(selected) with { Skills = ambiguous ? [selected, selected] : [] };
        var evaluated = false;
        await Assert.ThrowsAsync<IOException>(() => SkillViewWorkflowCoordinator.BuildRemoveDialogPlanAsync(
            selected, snapshot, TestContext.Current.CancellationToken, (target, _, _) =>
            {
                evaluated = true;
                return new(target, []);
            }));
        Assert.False(evaluated);
    }

    [Fact]
    public async Task InstallCaptureBypassesCatalogCacheAndOlderRequestsCannotPublish()
    {
        using var fixture = new HandoffFixture();
        var coordinator = fixture.Coordinator();
        var first = await coordinator.CaptureInstallHandoffAsync(fixture.Evidence, fixture.Report, TestContext.Current.CancellationToken);
        Assert.True(coordinator.IsInstallHandoffCurrent(first));
        var defaultCalls = fixture.DefaultCaptures;
        var second = await coordinator.CaptureInstallHandoffAsync(fixture.Evidence, fixture.Report, TestContext.Current.CancellationToken);
        Assert.Equal(defaultCalls + 1, fixture.DefaultCaptures);
        Assert.False(coordinator.IsInstallHandoffCurrent(first));
        Assert.True(coordinator.IsInstallHandoffCurrent(second));
        fixture.Services.ListAdapter.Invalidate();
        Assert.False(coordinator.IsInstallHandoffCurrent(second));
        var changed = fixture.Evidence with { ContextKey = "changed-context" };
        await Assert.ThrowsAsync<IOException>(() => coordinator.CaptureInstallHandoffAsync(changed, fixture.Report, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallHandoffPublishesTheSameSnapshotForDisplayAndIssuesWithoutRecapture(bool alternatives)
    {
        using var fixture = new HandoffFixture(alternatives ? ["codex"] : ["universal"]);
        var coordinator = fixture.Coordinator();
        var previousGeneration = fixture.Services.ListAdapter.Generation;
        var handoff = await coordinator.CaptureInstallHandoffAsync(fixture.Evidence, fixture.Report, TestContext.Current.CancellationToken);
        var generation = fixture.Services.ListAdapter.Generation;
        Assert.Equal(previousGeneration + 1, generation);
        Assert.Equal(generation, handoff.CatalogGeneration);
        Assert.Equal(alternatives, handoff.BrowseOnly);
        var commands = File.ReadAllLines(Path.Combine(fixture.Root, "calls"));
        Assert.Equal(1, fixture.DefaultCaptures);
        // This fixture refuses background display captures, so these calls
        // must find the exact operation snapshot already stored at generation G.
        Assert.Same(handoff.Snapshot, await coordinator.CaptureDisplayInventoryAsync(fixture.Report, TestContext.Current.CancellationToken));
        Assert.Same(handoff.Snapshot, await coordinator.CaptureInventorySnapshotAsync(TestContext.Current.CancellationToken));
        Assert.Equal(commands, File.ReadAllLines(Path.Combine(fixture.Root, "calls")));
        Assert.Equal(generation, fixture.Services.ListAdapter.Generation);
        Assert.True(coordinator.IsInstallHandoffCurrent(handoff));
    }

    [Fact]
    public async Task PendingInstallCaptureCancellationSettlesWithoutPublishingOrStalling()
    {
        using var fixture = new HandoffFixture();
        File.WriteAllText(Path.Combine(fixture.Root, "block"), "block");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var capture = fixture.Coordinator().CaptureInstallHandoffAsync(fixture.Evidence, fixture.Report, cancellation.Token);
        await WaitForAsync(() => File.Exists(Path.Combine(fixture.Root, "entered")));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task CodexUserCaptureDerivesFolderAlternativesAndRejectsChangedPlacement()
    {
        using var fixture = new HandoffFixture(["codex"]);
        var coordinator = fixture.Coordinator();
        var result = await coordinator.CaptureInstallHandoffAsync(fixture.Evidence, fixture.Report, TestContext.Current.CancellationToken);
        Assert.True(result.BrowseOnly);
        Assert.Single(result.CopyKeys);
        Assert.True(coordinator.IsInstallHandoffCurrent(result));
        var changedContext = fixture.Evidence with { ContextKey = "changed-context" };
        await Assert.ThrowsAsync<IOException>(() => coordinator.CaptureInstallHandoffAsync(changedContext, fixture.Report, TestContext.Current.CancellationToken));
        var changedRoots = fixture.Evidence with { Roots = [fixture.Evidence.Roots[0]] };
        await Assert.ThrowsAsync<IOException>(() => coordinator.CaptureInstallHandoffAsync(changedRoots, fixture.Report, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<IOException>(() => SkillViewWorkflowCoordinator.CaptureInstallAgentFoldersAsync(changedRoots, fixture.Services.Runner, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CodexAdmissionKeepsExistingSourceListingWithoutExtraPlacementChildren()
    {
        using var fixture = new HandoffFixture(["codex"]);
        // Even vacant Codex folders retain the existing .system boundary,
        // whose admission requires exactly one source listing before install.
        var seeded = Path.Combine(fixture.Evidence.Roots[0], "different-leaf");
        File.Delete(Path.Combine(seeded, "SKILL.md"));
        Directory.Delete(seeded);
        var result = await fixture.Services.InstallService.InstallAsync(fixture.Report.GhPath!, "owner/repo", "demo",
            new(Agents: ["codex"], Scope: "user") { NavigationSource = fixture.Evidence.Source }, TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.False(result.Navigation!.ExactPlacement);
        Assert.Equal(["skill install -- owner/repo", "skill install --agent codex --scope user -- owner/repo demo"],
            File.ReadAllLines(Path.Combine(fixture.Root, "calls")));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < end, "Timed out waiting for the owned capture child.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private sealed class HandoffFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "skillview-handoff-capture-" + Guid.NewGuid().ToString("N"));
        internal TuiServices Services { get; }
        internal EnvironmentReport Report { get; }
        internal InstallNavigationEvidence Evidence { get; }
        internal int DefaultCaptures => File.ReadAllLines(Path.Combine(Root, "calls")).Count(line => line.StartsWith("skill list --json ", StringComparison.Ordinal) && !line.Contains("--dir", StringComparison.Ordinal));
        internal HandoffFixture(string[]? agents = null)
        {
            Assert.SkipWhen(OperatingSystem.IsWindows(), "Inert POSIX subprocess fixture.");
            var work = Directory.CreateDirectory(Path.Combine(Root, "work")).FullName;
            var home = Directory.CreateDirectory(Path.Combine(Root, "home")).FullName;
            var skills = Path.Combine(home, ".agents", "skills");
            var payload = Directory.CreateDirectory(Path.Combine(skills, "different-leaf")).FullName;
            File.WriteAllText(Path.Combine(payload, "SKILL.md"), "---\nname: demo\nmetadata:\n  github-repo: https://github.com/owner/repo\n  github-path: skills/source\n---\nInert fixture.\n");
            var script = Path.Combine(Root, "gh");
            File.WriteAllText(script, "#!/bin/sh\nprintf '%s\\n' \"$*\" >> \"$FIXTURE_ROOT/calls\"\ntouch \"$FIXTURE_ROOT/entered\"\nwhile [ -f \"$FIXTURE_ROOT/block\" ]; do sleep 0.05; done\ncase \"$*\" in\n  'skill install -- owner/repo') printf 'demo\\tInert fixture\\n' ;;\n  *) printf '[]\\n' ;;\nesac\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var environment = new Dictionary<string, string>
            {
                ["HOME"] = home,
                ["USERPROFILE"] = home,
                ["PATH"] = "/usr/bin:/bin",
                ["FIXTURE_ROOT"] = Root,
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
            Services = TuiServices.Build(new Logger(LogLevel.Debug), processEnvironment: environment, workingDirectory: work);
            var options = new GhSkillInstallService.Options(Agents: agents ?? ["universal"], Scope: "user", WorkingDirectory: work)
            { NavigationSource = InstallNavigationEvidence.SourceAnchor("owner/repo", "skills/source/SKILL.md") };
            var roots = GhPlacementCatalog.DestinationsAsync(options, Services.Runner, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            Evidence = InstallNavigationEvidence.Capture(options, roots, Services.Runner)!;
            Report = new()
            {
                GhPath = script,
                GhVersionRaw = "2.103.2",
                GhVersion = null,
                GhMeetsMinimum = true,
                Auth = GhAuthStatus.Unknown,
                GhSkillAvailable = true,
                LogDirectory = null
            };
        }
        internal SkillViewWorkflowCoordinator Coordinator() => new(Services,
            new(InvocationMode.Standalone, DispatchMode.Tui, false, AppTheme.Default, [], null, []),
            () => null, () => Report.GhPath, () => Report, _ => Task.FromResult(Report),
            _ => { }, () => { }, _ => { }, (_, _) => { }, action => action(),
            (action, _) => { action(); return Task.FromResult(true); }, (_, _) => false, () => { }, () => { });
        public void Dispose() => Directory.Delete(Root, true);
    }

    private static InventorySnapshot Snapshot(InstalledSkill skill) => new()
    {
        Skills = [skill],
        ScannedRoots = [new ScanRoot("/skills", Scope.User, "test")],
        UsedGhSkillList = false,
        CapturedAt = DateTimeOffset.UtcNow,
    };

    private static InstalledSkill MakeSkill() => new()
    {
        Name = "demo",
        ResolvedPath = "/skills/demo",
        ScanRoot = "/skills",
        Scope = Scope.User,
        Agents = ImmutableArray<AgentMembership>.Empty,
        FrontMatter = new SkillFrontMatter { Name = "demo" },
        Validity = ValidityState.Valid,
        Provenance = Provenance.FsScan,
        Ignored = false,
        IsSymlinked = false,
        InstalledAt = null,
    };
}
