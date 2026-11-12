using System.Collections.Immutable;
using System.IO;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Ui;
using SkillView.Logging;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Editor;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class RemoveConfirmModalTests
{
    [Theory]
    [InlineData(80, 24)]
    [InlineData(120, 36)]
    public void LongReviewKeepsAllEvidenceAboveControlsAndEnterCancels(int width, int height)
    {
        var root = NewTempRoot();
        try
        {
            var skill = MakeSkill(root, withGit: false) with { Name = new string('n', 180) };
            var original = File.ReadAllBytes(Path.Combine(skill.ResolvedPath, "SKILL.md"));
            var target = new RemoveTarget(RemoveTargetKind.CurrentInstall, "Remove", "—", [skill]);
            var evaluation = RemoveTargetResolver.Evaluate(target, Snapshot(root, skill));
            using var app = Application.Create().Init("ansi");
            app.Driver!.SetScreenSize(width, height);
            Dialog? modal = null;
            var inspected = false;
            var timedOut = false;
            app.AddTimeout(TimeSpan.FromSeconds(6), () => { timedOut = true; app.RequestStop(); return false; });
            app.AddTimeout(TimeSpan.FromMilliseconds(10), () =>
            {
                if (modal is null) return true;
                modal.Layout(); modal.Draw(new DrawContext());
                var review = Assert.Single(modal.SubViews.OfType<Editor>());
                var status = Assert.Single(modal.SubViews.OfType<Label>());
                var buttons = modal.SubViews.OfType<Button>().ToArray();
                var cancel = Assert.Single(buttons, button => button.Text == "No");
                Assert.Contains(skill.Name, review.Text);
                Assert.Contains(skill.ResolvedPath, review.Text);
                Assert.Contains("known exposure(s)", review.Text);
                Assert.True(review.ReadOnly);
                Assert.True(review.Frame.Height > 3);
                Assert.True(review.Frame.Bottom <= status.Frame.Y);
                Assert.All(buttons, button =>
                {
                    Assert.Equal(1, button.Frame.Height);
                    Assert.True(status.Frame.Bottom <= button.Frame.Y);
                    Assert.True(button.Frame.X >= 0 && button.Frame.Right <= modal.Viewport.Width);
                });
                Assert.True(cancel.IsDefault);
                cancel.SetFocus(); inspected = true;
                app.Keyboard.RaiseKeyDownEvent(new Key(KeyCode.Enter));
                return false;
            });
            var logger = new Logger();
            var result = new RemoveConfirmModal(app, new RemoveService(logger), logger, skill, evaluation)
                .Show(dialog => modal = dialog);
            Assert.True(inspected); Assert.False(timedOut);
            Assert.Equal(RemoveConfirmModal.Outcome.Cancelled, result.Outcome);
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(skill.ResolvedPath, "SKILL.md")));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void ClassifyOperation_TreatsCompletedWorkerAsOwnedUntilUiCompletion()
    {
        Assert.Equal(
            RemoveConfirmModal.OperationOwnership.AwaitingUiCompletion,
            RemoveConfirmModal.ClassifyOperation(Task.CompletedTask));
    }

    [Fact]
    public void ClassifyOperation_DistinguishesIdleAndRunningWorkers()
    {
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.Equal(
            RemoveConfirmModal.OperationOwnership.None,
            RemoveConfirmModal.ClassifyOperation(null));
        Assert.Equal(
            RemoveConfirmModal.OperationOwnership.Running,
            RemoveConfirmModal.ClassifyOperation(running.Task));
    }

    [Fact]
    public void CanRunCompact_TrueForSimpleSingleSkillEval()
    {
        var root = NewTempRoot();
        try
        {
            var skill = MakeSkill(root, withGit: false);
            var target = new RemoveTarget(
                RemoveTargetKind.CurrentInstall,
                "Remove this skill",
                "—",
                [skill]);

            var eval = RemoveTargetResolver.Evaluate(target, Snapshot(root, skill));

            Assert.True(RemoveConfirmModal.CanRunCompact(eval));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void CanRunCompact_FalseWhenValidationBlocks()
    {
        var root = NewTempRoot();
        try
        {
            // A .git directory inside the skill blocks remove (containment check),
            // surfacing as a validation Error.
            var skill = MakeSkill(root, withGit: true);
            var target = new RemoveTarget(
                RemoveTargetKind.CurrentInstall,
                "Remove this skill",
                "—",
                [skill]);

            var eval = RemoveTargetResolver.Evaluate(target, Snapshot(root, skill));

            Assert.False(RemoveConfirmModal.CanRunCompact(eval));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void CanRunCompact_FalseForAgentSymlinkTarget()
    {
        var root = NewTempRoot();
        try
        {
            var skill = MakeSkill(root, withGit: false);
            // AgentSymlink targets are unlinks, not full removes — they go to
            // the wizard so the user sees what they're detaching from.
            var target = new RemoveTarget(
                RemoveTargetKind.AgentSymlink,
                "Unlink",
                "—",
                [skill]);

            var eval = RemoveTargetResolver.Evaluate(target, Snapshot(root, skill));

            Assert.False(RemoveConfirmModal.CanRunCompact(eval));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string NewTempRoot() =>
        Path.Combine(Path.GetTempPath(), "skillview-remove-confirm-" + Guid.NewGuid().ToString("N"));

    private static void TryDelete(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private static InventorySnapshot Snapshot(string root, params InstalledSkill[] skills) => new()
    {
        Skills = skills.ToImmutableArray(),
        ScannedRoots = ImmutableArray.Create(new ScanRoot(root, Scope.User, "claude")),
        UsedGhSkillList = false,
        CapturedAt = DateTimeOffset.UtcNow,
    };

    private static InstalledSkill MakeSkill(string root, bool withGit)
    {
        Directory.CreateDirectory(root);
        var dir = Path.Combine(root, "demo");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), "---\nname: demo\n---\nbody");
        if (withGit)
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
        }
        return new()
        {
            Name = "demo",
            ResolvedPath = dir,
            ScanRoot = root,
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
}
