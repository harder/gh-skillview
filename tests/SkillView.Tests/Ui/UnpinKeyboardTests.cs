using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Ui.Tabs;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Ui;

[Collection(TestCollections.TerminalGuiStaticState)]
public sealed class UnpinKeyboardTests
{
    [Theory]
    [InlineData(80, 20)]
    [InlineData(100, 26)]
    public async Task KeyboardRoutesSelectedRowThroughPinReviewAndExplicitRetentionBeforeApply(int width, int height)
    {
        using var app = Application.Create().Init("ansi");
        app.Driver!.SetScreenSize(width, height);
        var target = new InstalledSkill
        {
            Name = "demo",
            ResolvedPath = "/skills/demo",
            ScanRoot = "/skills",
            Scope = Scope.Custom,
            Agents = [new("claude", "/claude/demo", true), new("codex", "/codex/demo", true)],
            FrontMatter = SkillFrontMatter.Empty with { Pinned = true, PinnedRef = "release-1" },
            Validity = ValidityState.Valid,
            Provenance = Provenance.FsScan,
            Ignored = false,
            IsSymlinked = false,
            InstalledAt = null
        };
        var plan = new ReplacementResult(new('a', 32), "planned", true, false, false,
            target.ResolvedPath, "/state/original", "/state/candidate", "/state/displaced", new('b', 64), new('c', 64),
            ["SKILL.md"], "Pin-only review", new(target.InstallationId, target.Name, target.Scope, target.Agents, target.FrontMatter, []),
            "/state/plan.json", 448, 448,
            PinPolicy: new(target.FrontMatter with { Pinned = false, PinnedRef = null }, new('d', 64), "native metadata", []));
        var planned = 0; var applied = 0; var shellReceivedShortcut = false;
        UpdateResult Result() => new()
        {
            DryRun = false,
            Succeeded = true,
            ExitCode = 0,
            StdOut = "",
            StdErr = "",
            ErrorMessage = null,
            CommandLine = [],
            Entries = [],
            SavedPlan = plan
        };
        using var window = new Window { Width = Dim.Fill(), Height = Dim.Fill() };
        var view = new UpdatesTabView(action => { action(); return Task.CompletedTask; },
            (work, _) => work().GetAwaiter().GetResult(),
            _ => Task.FromResult(InventorySnapshot.Empty with { Skills = [target] }),
            (_, _, _) => throw new InvalidOperationException("Pin-only keyboard action cannot update"), () => null,
            new Logger(), () => { }, () => { },
            applyRunner: (review, _, _) => { Assert.Same(plan, review); applied++; return Task.FromResult(Result()); },
            unpinRunner: (selected, _, _) => { Assert.Same(target, selected); planned++; return Task.FromResult(Result()); })
        { Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(view);
        window.KeyDown += (_, key) => { if (!key.Handled && key.AsRune.Value == 'p') shellReceivedShortcut = true; };
        await view.LoadForSubjectAsync(OperationSubject.From(target));
        var session = app.Begin(window);
        try
        {
            var preview = view.SubViews.OfType<Markdown>().Single();
            preview.SetFocus(); Assert.True(preview.HasFocus);
            Press(new Key('p'));
            Assert.Equal(1, planned); Assert.Equal(0, applied); Assert.False(shellReceivedShortcut);
            Assert.False(view.ApplyButtonForTests.Enabled);
            view.RetainBoxForTests.SetFocus(); Assert.True(view.RetainBoxForTests.HasFocus);
            Press(new Key(KeyCode.Space));
            Assert.Equal(CheckState.Checked, view.RetainBoxForTests.Value);
            Assert.True(view.ApplyButtonForTests.Enabled);
            view.ApplyButtonForTests.SetFocus(); Assert.True(view.ApplyButtonForTests.HasFocus);
            Press(new Key(KeyCode.Enter));
            Assert.Equal(1, applied);
        }
        finally { app.End(session!); }

        void Press(Key key)
        {
            app.Keyboard.RaiseKeyDownEvent(key);
            window.Draw(new DrawContext());
        }
    }
}
