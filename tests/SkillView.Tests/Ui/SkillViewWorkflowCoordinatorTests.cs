using System.Collections.Immutable;
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
