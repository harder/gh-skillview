using System.Collections.Immutable;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.PackageChecking;
using SkillView.Ui;
using SkillView.Ui.Tabs;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class PackageResourceReplacementTests
{
    private const string Resource = "references/maintenance.md";
    private CancellationToken Token => TestContext.Current.CancellationToken;
    private static PackageCheckFinding Finding(string target = Resource) => new("reference", "reference-missing", "SKILL.md", 56, target, "message is never an action selector", "none");
    private static PackageCheckFact Fact(string path = "/skills/demo")
    {
        var copy = InstalledOverviewTests.Copy(path, "demo", "test/repo", "refs/tags/v1.0") with
        { FrontMatter = new() { Name = "demo", Description = "fixture", Upstream = "https://github.com/test/repo", GithubPath = "skills/demo", GithubRef = "refs/tags/v1.0", GithubTreeSha = new('a', 40) } };
        copy = copy with
        {
            ObservedSubject = OperationSubject.From(copy) with
            {
                NativeObject = new("observed", "inventory-observation", DateTimeOffset.UnixEpoch,
            "linux", "device", "full-id", path, "1", "2", "0", "0", null)
            }
        };
        var report = new PackageCheckReport(copy.InstallationId, path, "valid", "findings", new("complete", "partial", 1, 10, 1, 1,
            ["Unknown other references remain uninspected."]), new('b', 64), [Finding()], 1, 0, true)
        { RootMetadataSha256 = new('c', 64), DeclarationCoverage = "partial", DeclarationReasons = ["Unknown declarations"] };
        return new(new(InventorySnapshot.Empty with { Skills = [copy] }, copy, 1, "context", copy.InstallationId), report, DateTimeOffset.UtcNow);
    }
    private static ReplacementResult Plan(PackageCheckFact fact)
    {
        var target = fact.Request.Target;
        return ReviewedUpdatesTabTests.Review(target) with
        {
            Subject = new(target.InstallationId, target.Name, target.Scope, target.Agents, target.FrontMatter, []),
            Impact = OperationImpact.Payload(OperationSubject.From(target), "update-prepare"),
            Comparison = new(fact.Report.PayloadFingerprint!, new('d', 64), [new(Resource, null, new(Resource, "file", "hash", "hash", 10, 420, null))]),
            Revision = new(new(target.FrontMatter.Upstream!, target.FrontMatter.GithubRef!, new('a', 40), new('a', 40), "skills/demo"), target.FrontMatter, null, [])
        };
    }
    private static UpdateResult Result(ReplacementResult plan, bool applied = false) => new()
    { Succeeded = true, DryRun = false, ExitCode = 0, StdOut = "", StdErr = "", ErrorMessage = null, CommandLine = [], Entries = [], SavedPlan = plan, MutationAttempted = applied };

    [Theory]
    [InlineData("references/a.md", true)]
    [InlineData("references/a-b_c.d/e.1", true)]
    [InlineData("references/%61.md", false)]
    [InlineData("references/a#b", false)]
    [InlineData("references/a?b", false)]
    [InlineData("/references/a", false)]
    [InlineData("~/references/a", false)]
    [InlineData("C:/references/a", false)]
    [InlineData("references//a", false)]
    [InlineData("references/a/", false)]
    [InlineData("references/./a", false)]
    [InlineData("references/../a", false)]
    [InlineData("references/a..b", false)]
    [InlineData("references/.a", false)]
    [InlineData("references/a.", false)]
    [InlineData("references/a\\b", false)]
    [InlineData("references/a b", false)]
    [InlineData("references/é.md", false)]
    [InlineData("references/a\n", false)]
    [InlineData("References/a", false)]
    [InlineData("assets/a", false)]
    public void RawTargetGrammarIsStrict(string target, bool eligible) => Assert.Equal(eligible, PackageResourceReplacement.EligibleFinding(Finding(target)));

    [Fact]
    public void DuplicatesDeduplicateButMultipleEligibleTargetsHaveNoActionAndPartialReferencesAreAllowed()
    {
        var fact = Fact();
        Assert.True(fact.Incomplete);
        Assert.Equal(Resource, PackageResourceReplacement.Target(fact));
        Assert.Equal(Resource, PackageResourceReplacement.Target(fact with { Report = fact.Report with { Findings = [Finding(), Finding(), Finding("references/%bad")] } }));
        Assert.Null(PackageResourceReplacement.Target(fact with { Report = fact.Report with { Findings = [Finding(), Finding("references/other.md")] } }));
        foreach (var finding in new[] { Finding() with { Path = "other.md" }, Finding() with { Category = "Reference" }, Finding() with { Code = "reference-case-mismatch" }, Finding() with { Code = "dependency-file-missing" } })
            Assert.Null(PackageResourceReplacement.Target(fact with { Report = fact.Report with { Findings = [finding] } }));
    }

    [Fact]
    public void UnknownOrIncompletePayloadMetadataSourcePinsOwnersAndInventoryRefuse()
    {
        var fact = Fact();
        foreach (var report in new[] { fact.Report with { Coverage = fact.Report.Coverage with { Payload = "partial" } }, fact.Report with { PayloadFingerprint = null },
            fact.Report with { RootMetadataSha256 = null }, fact.Report with { FormatStatus = "unknown" }, fact.Report with { OmittedFindingCount = 1 },
            fact.Report with { Path = "/sibling/demo" }, fact.Report with { InstallationId = "other" } })
            Assert.Null(PackageResourceReplacement.Target(fact with { Report = report }));
        foreach (var metadata in new[] { fact.Request.Target.FrontMatter with { GithubTreeSha = "short" }, fact.Request.Target.FrontMatter with { GithubRef = "v1.0" },
            fact.Request.Target.FrontMatter with { GithubRef = "refs/heads/" }, fact.Request.Target.FrontMatter with { GithubPath = "../escape" },
            fact.Request.Target.FrontMatter with { GithubPath = null }, fact.Request.Target.FrontMatter with { Upstream = "https://evil.test/a/b" },
            fact.Request.Target.FrontMatter with { Pinned = true }, fact.Request.Target.FrontMatter with { PinnedRef = "" },
            fact.Request.Target.FrontMatter with { LocalPath = "/local" }, fact.Request.Target.FrontMatter with { Diagnostics = ["unknown"] } })
            Assert.False(PackageResourceReplacement.TrackedUnpinned(fact.Request.Target with { FrontMatter = metadata }));
        var owner = new SkillPackage("test/repo", "github", "https://github.com/test/repo", null, null)
        {
            Provider = "gh-vercel-global",
            IsExclusiveOwner = false,
            LockFilePath = "/home/.agents/.skill-lock.json",
            Selector = "demo",
            EntryFingerprint = new('e', 64),
            SchemaVersion = 3,
            SkillPath = "skills/demo/SKILL.md",
            ContentHash = new('a', 40)
        };
        Assert.True(PackageResourceReplacement.TrackedUnpinned(fact.Request.Target with { OwnershipClaims = [owner] }));
        foreach (var bad in new[] { owner with { Provider = "other" }, owner with { ReadOnly = true }, owner with { IsExclusiveOwner = true }, owner with { Diagnostic = "unknown" },
            owner with { PinnedRef = "v1" }, owner with { EntryFingerprint = null }, owner with { Selector = null } })
            Assert.False(PackageResourceReplacement.TrackedUnpinned(fact.Request.Target with { OwnershipClaims = [bad] }));
        Assert.False(PackageResourceReplacement.TrackedUnpinned(fact.Request.Target with { OwnershipClaims = [owner, owner with { EntryFingerprint = new('f', 64) }] }));
        Assert.False(PackageResourceReplacement.Complete(fact.Request.Snapshot with { ConfiguredRootsComplete = false }));
        Assert.False(PackageResourceReplacement.Complete(fact.Request.Snapshot with { Providers = [new("gh", false, [], DateTimeOffset.UtcNow, "truncated")] }));
        Assert.False(PackageResourceReplacement.Complete(fact.Request.Snapshot with { OwnershipRecords = [owner with { Diagnostic = "unknown" }] }));
    }

    [Fact]
    public void FreshCopyAndFullPlanComparisonBindExactAddressSourceNativeAndRawPayload()
    {
        var fact = Fact(); var plan = Plan(fact); var copy = fact.Request.Target;
        Assert.True(PackageResourceReplacement.FreshCopy(fact, fact.Request.Snapshot, copy));
        Assert.True(PackageResourceReplacement.Ready(fact, Resource, plan));
        foreach (var changed in new[] { copy with { ResolvedPath = "/other/demo" }, copy with { FrontMatter = copy.FrontMatter with { GithubTreeSha = new('f', 40) } },
            copy with { ObservedSubject = copy.ObservedSubject! with { NativeObject = copy.ObservedSubject.NativeObject with { ChangeTimeNanoseconds = "3" } } },
            copy with { ObservedSubject = null } }) Assert.False(PackageResourceReplacement.FreshCopy(fact, fact.Request.Snapshot, changed));
        foreach (var changed in new[] { plan with { Comparison = plan.Comparison! with { InstalledPayload = "different" } }, plan with { Comparison = plan.Comparison! with { Paths = [] } },
            plan with { Comparison = plan.Comparison! with { Paths = [new(Resource, null, new(Resource, "link", "h", "h", 1, null, "other"))] } },
            plan with { Comparison = plan.Comparison! with { Paths = [new(Resource, new(Resource, "file", "h", "h", 1, null, null), plan.Comparison.Paths[0].Candidate)] } },
            plan with { OriginalPath = "/other/demo" }, plan with { Impact = null }, plan with { Canceled = true }, plan with { MutationObserved = true } })
            Assert.False(PackageResourceReplacement.Ready(fact, Resource, changed));
        Assert.False(PackageResourceReplacement.Ready(fact, "references/other.md", plan));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedApplyPostcheckNoteSurvivesOnlyUntilUndoAndExceptionsKeepNativeSuccessAndUndo(bool checkerThrows)
    {
        var fact = Fact(); var plan = Plan(fact); var applied = plan with { Status = "installed", MutationObserved = true };
        var inspected = applied with { MutationObserved = false, ReviewFingerprint = new('f', 64) }; var checks = 0; var applies = 0;
        using var view = new UpdatesTabView(a => { a(); return Task.CompletedTask; }, (f, _) => f().GetAwaiter().GetResult(),
            _ => Task.FromResult(fact.Request.Snapshot), (_, _, _) => throw new InvalidOperationException("targeted preparation must use fresh-admission runner"),
            () => "/gh", new Logger(), () => { }, () => { }, applyRunner: (_, _, _) => { applies++; return Task.FromResult(Result(applied, true)); },
            inspectUndoRunner: (_, _, _) => Task.FromResult(inspected), undoRunner: (_, _, _) => Task.FromResult(Result(applied with { Status = "restored" }, true)),
            resourcePrepareRunner: (f, target, _, options, _) => { Assert.Same(fact, f); Assert.Equal(Resource, target); Assert.False(options.Unpin); return Task.FromResult(Result(plan)); },
            resourceCheckRunner: (_, _, _, _) => { checks++; return checkerThrows ? Task.FromException<string>(new IOException("postcheck failed")) : Task.FromResult(PackageResourceReplacement.Restored + "\n3 Reviews remain"); });
        view.ActivateExactReview(OperationSubject.From(fact.Request.Target), false, fact, Resource);
        Assert.Contains("Whole-package replacement", view.PreviewTextForTests);
        await view.ReviewUpdateAsync();
        Assert.Contains("saved full comparison", view.PreviewTextForTests);
        Assert.False(view.ApplyButtonForTests.Enabled);
        await view.ApplyReviewForTestsAsync(); Assert.Equal(0, applies);
        view.RetainBoxForTests.Value = CheckState.Checked;
        await view.ApplyReviewForTestsAsync();
        Assert.Equal(1, applies); Assert.Equal(1, checks); Assert.True(view.UndoButtonForTests.Enabled);
        Assert.Contains(checkerThrows ? PackageResourceReplacement.Incomplete : PackageResourceReplacement.Restored, view.PreviewTextForTests);
        Assert.DoesNotContain("Update failed", view.PreviewTextForTests);
        await view.InspectUndoForTestsAsync();
        Assert.DoesNotContain(PackageResourceReplacement.Restored, view.PreviewTextForTests);
        Assert.DoesNotContain(PackageResourceReplacement.Incomplete, view.PreviewTextForTests);
        view.CancelUndoForTests();
        Assert.DoesNotContain(PackageResourceReplacement.Restored, view.PreviewTextForTests);
        view.CancelPendingWork();
    }

    private static UpdatesTabView View(PackageCheckFact fact, ReplacementResult plan,
        Func<UpdateResult>? prepareResource = null, Func<UpdateResult>? ordinary = null,
        Func<ReplacementResult, Task<UpdateResult>>? apply = null, Func<Task<string>>? postcheck = null,
        Func<Action, Task>? dispatch = null, Action<Func<Task>, string>? run = null,
        Func<CancellationToken, Task<string>>? postcheckWithToken = null, Func<CancellationToken>? lifetime = null) => new(
            dispatch ?? (a => { a(); return Task.CompletedTask; }), run ?? ((f, _) => f().GetAwaiter().GetResult()),
            _ => Task.FromResult(fact.Request.Snapshot), (_, _, _) => Task.FromResult(ordinary?.Invoke() ?? Result(plan)),
            () => "/gh", new Logger(), () => { }, () => { }, chooseTag: _ => null, lifetimeToken: lifetime?.Invoke() ?? TestContext.Current.CancellationToken,
            applyRunner: (p, _, _) => apply?.Invoke(p) ?? Task.FromResult(Result(p with { Status = "installed", MutationObserved = true }, true)),
            inspectUndoRunner: (p, _, _) => Task.FromResult(p with { MutationObserved = false, ReviewFingerprint = new('f', 64) }),
            undoRunner: (p, _, _) => Task.FromResult(Result(p with { Status = "restored" }, true)),
            resourcePrepareRunner: (_, _, _, _, _) => Task.FromResult(prepareResource?.Invoke() ?? Result(plan)),
            resourceCheckRunner: (_, _, _, token) => postcheckWithToken?.Invoke(token) ?? postcheck?.Invoke() ?? Task.FromResult(PackageResourceReplacement.Restored));

    [Theory]
    [InlineData("candidate-without-target")]
    [InlineData("wrong-raw-payload")]
    [InlineData("auth")]
    [InlineData("404")]
    [InlineData("truncated")]
    [InlineData("cancel")]
    public async Task FailedOrUnboundPreparationKeepsOrdinaryReviewWithoutTargetedClaim(string kind)
    {
        var fact = Fact(); var plan = Plan(fact); var checks = 0;
        var result = kind switch
        {
            "candidate-without-target" => Result(plan with { Comparison = plan.Comparison! with { Paths = [] } }),
            "wrong-raw-payload" => Result(plan with { Comparison = plan.Comparison! with { InstalledPayload = "changed" } }),
            _ => Result(plan) with { SavedPlan = null, Succeeded = false, ErrorMessage = kind, Cancelled = kind == "cancel" }
        };
        using var view = View(fact, plan, prepareResource: () => result, postcheck: () => { checks++; return Task.FromResult(PackageResourceReplacement.Restored); });
        view.ActivateExactReview(OperationSubject.From(fact.Request.Target), false, fact, Resource);
        await view.ReviewUpdateAsync();
        Assert.DoesNotContain("saved full comparison", view.PreviewTextForTests);
        Assert.DoesNotContain(PackageResourceReplacement.Restored, view.PreviewTextForTests);
        if (result.SavedPlan is not null)
        {
            Assert.Contains(view.SubViews.OfType<Button>(), b => b.Visible && b.Text.ToString() == "Skip review");
            view.RetainBoxForTests.Value = CheckState.Checked;
            await view.ApplyReviewForTestsAsync();
            Assert.True(view.UndoButtonForTests.Enabled);
        }
        Assert.Equal(0, checks);
    }

    [Theory]
    [InlineData("skip")]
    [InlineData("cancel")]
    [InlineData("refresh")]
    [InlineData("version-cancel")]
    public async Task IntentDoesNotSurviveAnotherPreparationOrActivation(string boundary)
    {
        var fact = Fact(); var plan = Plan(fact); var targeted = 0; var ordinary = 0; var checks = 0;
        using var view = View(fact, plan, prepareResource: () => { targeted++; return Result(plan); },
            ordinary: () => { ordinary++; return Result(plan); }, postcheck: () => { checks++; return Task.FromResult(PackageResourceReplacement.Restored); });
        view.ActivateExactReview(OperationSubject.From(fact.Request.Target), false, fact, Resource);
        if (boundary == "skip") { await view.ReviewUpdateAsync(); await view.FinishReviewForTestsAsync(); }
        else if (boundary == "cancel") { view.CancelPendingWork(); await view.LoadForSubjectAsync(OperationSubject.From(fact.Request.Target)); }
        else if (boundary == "refresh") await view.LoadForSubjectAsync(OperationSubject.From(fact.Request.Target));
        else view.ChooseVersionForTests();
        await view.ReviewUpdateAsync();
        view.RetainBoxForTests.Value = CheckState.Checked;
        await view.ApplyReviewForTestsAsync();
        Assert.Equal(boundary == "skip" ? 1 : 0, targeted); Assert.Equal(1, ordinary); Assert.Equal(0, checks);
        Assert.DoesNotContain(PackageResourceReplacement.Restored, view.PreviewTextForTests);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("fingerprint")]
    [InlineData("candidate")]
    public async Task AppliedPlanCannotRetargetOrReplayRestorationIntent(string change)
    {
        var fact = Fact(); var plan = Plan(fact); var checks = 0;
        using var view = View(fact, plan, apply: p => Task.FromResult(Result(change switch
        {
            "id" => p with { Id = new('f', 32), Status = "installed", MutationObserved = true },
            "fingerprint" => p with { PlanFingerprint = new('f', 64), Status = "installed", MutationObserved = true },
            _ => p with { Comparison = p.Comparison! with { CandidatePayload = "other" }, Status = "installed", MutationObserved = true }
        }, true)), postcheck: () => { checks++; return Task.FromResult(PackageResourceReplacement.Restored); });
        view.ActivateExactReview(OperationSubject.From(fact.Request.Target), false, fact, Resource);
        await view.ReviewUpdateAsync(); view.RetainBoxForTests.Value = CheckState.Checked; await view.ApplyReviewForTestsAsync();
        Assert.Equal(0, checks); Assert.DoesNotContain(PackageResourceReplacement.Restored, view.PreviewTextForTests);
    }

    [Fact]
    public async Task LateQueuedApplyCompletionAfterDeactivationCannotPublishResourceNote()
    {
        var fact = Fact(); var plan = Plan(fact); Action? queued = null; var dispatchReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var defer = false;
        using var view = View(fact, plan, dispatch: action =>
        {
            if (!defer) { action(); return Task.CompletedTask; }
            queued = action; dispatchReady.TrySetResult(); return release.Task;
        });
        view.ActivateExactReview(OperationSubject.From(fact.Request.Target), false, fact, Resource);
        await view.ReviewUpdateAsync(); view.RetainBoxForTests.Value = CheckState.Checked;
        defer = true; var applying = view.ApplyReviewForTestsAsync(); await dispatchReady.Task.WaitAsync(Token);
        view.CancelPendingWork(); Assert.NotNull(queued); queued(); release.SetResult(); await applying;
        Assert.False(view.UndoButtonForTests.Enabled);
        Assert.DoesNotContain(PackageResourceReplacement.Restored, view.PreviewTextForTests);
        defer = false;
        view.ActivateExactReview(OperationSubject.From(fact.Request.Target), false);
        Assert.DoesNotContain("saved full comparison", view.PreviewTextForTests);
    }

    [Theory]
    [InlineData("deactivate")]
    [InlineData("undo-cancel")]
    [InlineData("reload")]
    public async Task SlowVerificationPublishesNativeSuccessAndUndoBeforeReleaseAndClearWins(string boundary)
    {
        var fact = Fact(); var plan = Plan(fact); var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var faults = new List<Exception>(); var tracker = new BackgroundTaskTracker(faults.Add);
        using var view = View(fact, plan, run: (work, _) => Assert.True(tracker.TryRun(work)), postcheckWithToken: token =>
        { entered.SetResult(token); return release.Task; });
        try
        {
            view.ActivateExactReview(OperationSubject.From(fact.Request.Target), false, fact, Resource);
            await view.ReviewUpdateAsync(); view.RetainBoxForTests.Value = CheckState.Checked;
            await view.ApplyReviewForTestsAsync().WaitAsync(TimeSpan.FromSeconds(10), Token);
            var checkToken = await entered.Task.WaitAsync(Token);
            Assert.False(release.Task.IsCompleted);
            Assert.True(view.UndoButtonForTests.Enabled);
            Assert.Contains("Update result", view.PreviewTextForTests);
            Assert.Contains("Verifying package", view.PreviewTextForTests);
            Assert.Contains("verifying package", view.StatusTextForTests);
            Assert.DoesNotContain("Update failed", view.PreviewTextForTests);
            if (boundary == "deactivate")
            {
                view.Visible = false; view.CancelPendingWork();
                Assert.True(view.UndoButtonForTests.Enabled); // already remembered native result survives
                Assert.Equal(CheckState.UnChecked, view.UndoApprovalForTests.Value);
                Assert.Contains("Update result", view.PreviewTextForTests);
            }
            else if (boundary == "undo-cancel")
            {
                await view.InspectUndoForTestsAsync(); view.CancelUndoForTests();
                Assert.True(view.UndoButtonForTests.Enabled); // the same applied plan remains
            }
            else await view.LoadForSubjectAsync(OperationSubject.From(fact.Request.Target));
            Assert.True(checkToken.IsCancellationRequested);
        }
        finally
        {
            release.TrySetResult(PackageResourceReplacement.Restored);
            tracker.StopAccepting(); await tracker.DrainAsync().WaitAsync(Token);
        }
        Assert.Empty(faults);
        Assert.DoesNotContain(PackageResourceReplacement.Restored, view.PreviewTextForTests);
        Assert.DoesNotContain("Verifying package", view.PreviewTextForTests);
    }

    [Fact]
    public async Task CompletedVerificationDoesNotBroadenOrdinaryDeactivationUndoLifetime()
    {
        var fact = Fact(); var plan = Plan(fact);
        using var view = View(fact, plan);
        view.ActivateExactReview(OperationSubject.From(fact.Request.Target), false, fact, Resource);
        await view.ReviewUpdateAsync(); view.RetainBoxForTests.Value = CheckState.Checked; await view.ApplyReviewForTestsAsync();
        Assert.Contains(PackageResourceReplacement.Restored, view.PreviewTextForTests);
        Assert.True(view.UndoButtonForTests.Enabled);
        view.CancelPendingWork();
        Assert.False(view.UndoButtonForTests.Enabled);
        Assert.DoesNotContain(PackageResourceReplacement.Restored, view.PreviewTextForTests);
    }

    [Fact]
    public async Task QueuedVerificationCannotResurrectNoteAfterUndoInspectionIsCanceledOnSamePlan()
    {
        var fact = Fact(); var plan = Plan(fact); var faults = new List<Exception>(); var tracker = new BackgroundTaskTracker(faults.Add);
        var checker = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var defer = false;
        using var view = View(fact, plan, run: (work, _) => Assert.True(tracker.TryRun(work)), postcheck: () => checker.Task,
            dispatch: action => { if (!defer) { action(); return Task.CompletedTask; } queued.SetResult(action); return delivered.Task; });
        try
        {
            view.ActivateExactReview(OperationSubject.From(fact.Request.Target), false, fact, Resource);
            await view.ReviewUpdateAsync(); view.RetainBoxForTests.Value = CheckState.Checked; await view.ApplyReviewForTestsAsync();
            Assert.True(view.UndoButtonForTests.Enabled);
            defer = true; checker.SetResult(PackageResourceReplacement.Restored);
            var completion = await queued.Task.WaitAsync(Token);
            defer = false; await view.InspectUndoForTestsAsync(); view.CancelUndoForTests();
            Assert.True(view.UndoButtonForTests.Enabled);
            completion(); delivered.SetResult();
        }
        finally
        {
            checker.TrySetResult(PackageResourceReplacement.Restored); delivered.TrySetResult();
            tracker.StopAccepting(); await tracker.DrainAsync().WaitAsync(Token);
        }
        Assert.Empty(faults);
        Assert.DoesNotContain(PackageResourceReplacement.Restored, view.PreviewTextForTests);
        Assert.DoesNotContain("Verifying package", view.PreviewTextForTests);
    }

    [Fact]
    public async Task ShutdownCancelsAndDrainsSeparateVerificationWithoutLosingPublishedResultOrDispatchingLate()
    {
        var fact = Fact(); var plan = Plan(fact); using var lifetime = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var faults = new List<Exception>(); var tracker = new BackgroundTaskTracker(faults.Add); var dispatches = 0;
        using var view = View(fact, plan, lifetime: () => lifetime.Token, run: (work, _) => Assert.True(tracker.TryRun(work)),
            dispatch: action => { dispatches++; action(); return Task.CompletedTask; }, postcheckWithToken: async token =>
            { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return PackageResourceReplacement.Restored; });
        view.ActivateExactReview(OperationSubject.From(fact.Request.Target), false, fact, Resource);
        await view.ReviewUpdateAsync(); view.RetainBoxForTests.Value = CheckState.Checked; await view.ApplyReviewForTestsAsync();
        await entered.Task.WaitAsync(Token);
        Assert.True(view.UndoButtonForTests.Enabled); var publishedDispatches = dispatches;
        tracker.StopAccepting(); lifetime.Cancel(); view.CancelPendingWork();
        await tracker.DrainAsync().WaitAsync(Token);
        Assert.Empty(faults); Assert.Equal(publishedDispatches, dispatches);
        Assert.True(view.UndoButtonForTests.Enabled);
        Assert.Contains("Update result", view.PreviewTextForTests);
        Assert.DoesNotContain("Verifying package", view.PreviewTextForTests);
        Assert.DoesNotContain(PackageResourceReplacement.Restored, view.PreviewTextForTests);
    }

    [Fact]
    public async Task LateVerificationUiFaultIsReportedByApplicationTrackerAndNeverReclassifiedAsNativeApplyFailure()
    {
        var fact = Fact(); var plan = Plan(fact); var faults = new List<Exception>(); var tracker = new BackgroundTaskTracker(faults.Add);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); var failDispatch = false;
        using var view = View(fact, plan, run: (work, _) => Assert.True(tracker.TryRun(work)), postcheck: () => release.Task,
            dispatch: action => { if (failDispatch) return Task.FromException(new InvalidOperationException("late UI delivery fault")); action(); return Task.CompletedTask; });
        view.ActivateExactReview(OperationSubject.From(fact.Request.Target), false, fact, Resource);
        await view.ReviewUpdateAsync(); view.RetainBoxForTests.Value = CheckState.Checked; await view.ApplyReviewForTestsAsync();
        Assert.True(view.UndoButtonForTests.Enabled); failDispatch = true; release.SetResult(PackageResourceReplacement.Restored);
        tracker.StopAccepting(); await tracker.DrainAsync().WaitAsync(Token);
        Assert.Equal("late UI delivery fault", Assert.Single(faults).Message);
        Assert.True(view.UndoButtonForTests.Enabled);
        Assert.Contains("Update result", view.PreviewTextForTests);
        Assert.DoesNotContain("operation failed", view.StatusTextForTests);
    }

    [Fact]
    public async Task AppliedCheckUsesFinalCaptureAtExactAppliedPathAndRefusesFingerprintTypeOrFindingMismatch()
    {
        var root = Directory.CreateTempSubdirectory("skillview-resource-postcheck-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "SKILL.md"), "---\nname: demo\ndescription: fixture\n---\n[resource](references/maintenance.md)\n");
            Directory.CreateDirectory(Path.Combine(root.FullName, "references"));
            File.WriteAllText(Path.Combine(root.FullName, Resource), "resource bytes\n");
            var snapshot = PayloadSnapshot.Capture(root.FullName, Token); var fact = Fact(snapshot.Path); var plan = Plan(fact) with { Status = "installed", MutationObserved = true };
            plan = plan with { Comparison = plan.Comparison! with { CandidatePayload = snapshot.Fingerprint } };
            var captures = 0;
            var restored = await PackageResourceReplacement.CheckAppliedAsync(fact.Request.Target, Resource, plan, Token, (path, sink, token) =>
            { Assert.Equal(plan.OriginalPath, path); captures++; return PayloadSnapshot.CaptureWithEntrySink(path, sink, token); });
            Assert.StartsWith(PackageResourceReplacement.Restored, restored); Assert.Equal(1, captures);
            var report = await new StaticPackageChecker().CheckAsync(fact.Request.Target, Token);
            Assert.False(PackageResourceReplacement.Verified(report with { Findings = [Finding()] }, snapshot, Resource, plan));
            Assert.False(PackageResourceReplacement.Verified(report with { OmittedFindingCount = 1 }, snapshot, Resource, plan));
            Assert.False(PackageResourceReplacement.Verified(report, snapshot with { Path = "/sibling/demo" }, Resource, plan));
            Assert.False(PackageResourceReplacement.Verified(report, snapshot with { RootIdentity = null }, Resource, plan));

            Assert.Equal(PackageResourceReplacement.Incomplete, await PackageResourceReplacement.CheckAppliedAsync(fact.Request.Target, Resource, plan with { OriginalPath = "/sibling/demo" }, Token));
            Assert.StartsWith(PackageResourceReplacement.Incomplete, await PackageResourceReplacement.CheckAppliedAsync(fact.Request.Target, Resource, plan with { Comparison = plan.Comparison with { CandidatePayload = new('f', 64) } }, Token));
            File.Delete(Path.Combine(root.FullName, Resource));
            snapshot = PayloadSnapshot.Capture(root.FullName, Token);
            Assert.StartsWith(PackageResourceReplacement.Incomplete, await PackageResourceReplacement.CheckAppliedAsync(fact.Request.Target, Resource, plan with { Comparison = plan.Comparison with { CandidatePayload = snapshot.Fingerprint } }, Token));
            Assert.StartsWith(PackageResourceReplacement.Incomplete, await PackageResourceReplacement.CheckAppliedAsync(fact.Request.Target, Resource, plan, Token,
                (path, sink, token) => { _ = PayloadSnapshot.CaptureWithEntrySink(path, sink, token); throw new IOException("final verification failed"); }));
            Directory.CreateDirectory(Path.Combine(root.FullName, Resource));
            snapshot = PayloadSnapshot.Capture(root.FullName, Token);
            Assert.StartsWith(PackageResourceReplacement.Incomplete, await PackageResourceReplacement.CheckAppliedAsync(fact.Request.Target, Resource,
                plan with { Comparison = plan.Comparison with { CandidatePayload = snapshot.Fingerprint } }, Token));
        }
        finally { root.Delete(true); }
    }
}
