using System.Text.RegularExpressions;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Subprocess;
using SkillView.Ui;
using SkillView.Ui.Tabs;
using Terminal.Gui.Views;
using Xunit;

namespace SkillView.Tests.Gh;

[Trait("Category", "Contract")]
public sealed class GhUpdateOutcomeContractTests
{
    private static bool Enabled => string.Equals(System.Environment.GetEnvironmentVariable("SKILLVIEW_CONTRACT_TESTS"), "true", StringComparison.OrdinalIgnoreCase);
    private const string OldCommit = "33789149b9def6b66ddf247438a79fac94e22074";
    private CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RealGhPartialBatchRetainsFirstMutationAndBothFailureStreams()
    {
        if (!Enabled) return;
        using var fixture = await Fixture.CreateAsync(Token);
        var targets = fixture.RawTargets();
        var firstBefore = PayloadSnapshot.Capture(targets[0].ResolvedPath, Token);
        var secondBefore = PayloadSnapshot.Capture(targets[1].ResolvedPath, Token);
        var services = fixture.Services;
        var coordinator = new SkillUpdateCoordinator(services.InstallService, services.UpdateService, services.ListAdapter, services.Logger, services.Runner);
        var result = await coordinator.UpdateAsync(fixture.Gh, new(Targets: targets, Unpin: true), Token);
        Assert.False(result.Succeeded);
        Assert.Equal(2, result.ExitCode);
        Assert.True(result.MutationAttempted);
        var first = result.Outcomes.Single(row => row.InstallationId == targets[0].InstallationId);
        var second = result.Outcomes.Single(row => row.InstallationId == targets[1].InstallationId);
        Assert.Equal("changed", first.Status);
        Assert.True(first.ChangedFileCount > 0);
        Assert.Contains("SKILL.md", first.ChangedFiles);
        Assert.Equal("refused", second.Status);
        Assert.Contains("Updated gh-skill", result.StdOut);
        Assert.False(string.IsNullOrWhiteSpace(result.StdErr));
        Assert.NotEqual(firstBefore.Fingerprint, PayloadSnapshot.Capture(targets[0].ResolvedPath, Token).Fingerprint);
        Assert.Equal(secondBefore.Objects, PayloadSnapshot.Capture(targets[1].ResolvedPath, Token).Objects);
        Assert.Equal(fixture.RegistryBefore, File.ReadAllBytes(fixture.Registry));
    }

    [Fact]
    public async Task SavedExactUpdateReviewsReconcileBeforeAnotherCopyAndReportActualRemoteFailure()
    {
        if (!Enabled) return;
        using var fixture = await Fixture.CreateAsync(Token);
        var initial = await fixture.InventoryAsync(Token);
        var targets = fixture.RawTargets();
        initial = initial with { Skills = [.. targets.Select(target => initial.Skills.Single(skill => skill.InstallationId == target.InstallationId))] };
        Assert.False(initial.IsPartial);
        var firstBefore = PayloadSnapshot.Capture(targets[0].ResolvedPath, Token);
        var secondBefore = PayloadSnapshot.Capture(targets[1].ResolvedPath, Token);
        UpdateResult? applied = null, refused = null;
        using var view = new UpdatesTabView(action => { action(); return Task.CompletedTask; },
            (operation, _) => operation().GetAwaiter().GetResult(), _ => fixture.InventoryAsync(Token),
            async (_, options, token) =>
            {
                var result = await fixture.Services.SavedUpdates.PrepareAsync(fixture.Gh, options,
                    await fixture.InventoryAsync(token), token);
                if (!result.Succeeded) refused = result;
                return result;
            }, () => fixture.Gh, fixture.Services.Logger, () => { }, () => { },
            applyRunner: async (review, _, token) => applied = await fixture.Services.SavedUpdates.ApplyAsync(review,
                "retain-original", await fixture.InventoryAsync(token), fixture.InventoryAsync, token));
        await view.LoadForSubjectAsync(OperationSubject.From(initial.Skills.Single(skill => skill.InstallationId == targets[0].InstallationId)));
        view.UnpinBoxForTests.Value = CheckState.Checked;
        await view.ReviewUpdateAsync();
        Assert.Contains("review required", view.StatusTextForTests);
        Assert.True(view.SourceReviewBoxForTests.Enabled);
        Assert.Equal(CheckState.UnChecked, view.SourceReviewBoxForTests.Value);
        view.RetainBoxForTests.Value = CheckState.Checked;
        await view.ApplyReviewForTestsAsync();
        Assert.Null(applied);
        Assert.Equal(firstBefore.ObjectStateFingerprint, PayloadSnapshot.Capture(targets[0].ResolvedPath, Token).ObjectStateFingerprint);
        view.SourceReviewBoxForTests.Value = CheckState.Checked;
        await view.ApplyReviewForTestsAsync();
        Assert.NotNull(applied);
        Assert.True(applied.Succeeded, applied.ErrorMessage);
        Assert.Equal("changed", Assert.Single(applied.Outcomes).Status);
        var afterFirst = PayloadSnapshot.Capture(targets[0].ResolvedPath, Token);
        Assert.NotEqual(firstBefore.Fingerprint, afterFirst.Fingerprint);
        await view.LoadForSubjectAsync(OperationSubject.From(initial.Skills.Single(skill => skill.InstallationId == targets[1].InstallationId)));
        view.UnpinBoxForTests.Value = CheckState.Checked;
        await view.ReviewUpdateAsync();
        Assert.NotNull(refused);
        Assert.False(refused.Succeeded);
        Assert.Equal("refused", Assert.Single(refused.Outcomes).Status);
        Assert.Contains("404", refused.StdOut);
        Assert.Contains("404", refused.StdErr);
        view.ToggleDetailsForTests();
        Assert.Equal("changed", Assert.Single(applied.Outcomes).Status);
        Assert.True(File.Exists(applied.SavedPlan!.PlanPath));
        Assert.Contains(targets[1].ResolvedPath, view.PreviewTextForTests);
        Assert.Contains(refused.StdErr.Trim(), view.PreviewTextForTests);
        Assert.Contains("Not Found", view.PreviewTextForTests);
        Assert.Equal(afterFirst.Objects, PayloadSnapshot.Capture(targets[0].ResolvedPath, Token).Objects);
        Assert.Equal(secondBefore.Objects, PayloadSnapshot.Capture(targets[1].ResolvedPath, Token).Objects);
        Assert.Equal(fixture.RegistryBefore, File.ReadAllBytes(fixture.Registry));
    }

    private sealed class Fixture(DirectoryInfo root, TuiServices services, string gh) : IDisposable
    {
        internal TuiServices Services => services;
        internal string Gh => gh;
        private string[] Roots => [Path.Combine(root.FullName, "first"), Path.Combine(root.FullName, "second")];
        internal string Registry => Path.Combine(services.Runner.GetEffectiveEnvironmentVariable("HOME")!, ".agents", ".skill-lock.json");
        internal byte[] RegistryBefore { get; private set; } = [];
        internal InstalledSkill[] RawTargets() => Roots.Select(directory => Assert.Single(services.Scanner.Scan([new(directory, Scope.Custom, null)]))).ToArray();
        internal Task<InventorySnapshot> InventoryAsync(CancellationToken token) => services.InventoryService.CaptureAsync(gh, new(Roots, true), token);
        public void Dispose() => root.Delete(true);

        internal static async Task<Fixture> CreateAsync(CancellationToken token)
        {
            var logger = new Logger();
            var parent = new ProcessRunner(logger);
            var gh = new GhBinaryLocator(parent, logger).FindOnPath(token);
            Assert.NotNull(gh);
            var auth = System.Environment.GetEnvironmentVariable("GH_TOKEN") ?? System.Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (string.IsNullOrWhiteSpace(auth))
            {
                var credentials = await parent.RunAsync(gh!, ["auth", "token", "--hostname", "github.com"], cancellationToken: token);
                Assert.True(credentials.Succeeded && !credentials.StdOutTruncated, "Configured GitHub credentials are required.");
                auth = credentials.StdOut.Trim();
            }
            var temporary = Directory.CreateTempSubdirectory("skillview-outcome-contract-");
            try
            {
                var environment = new Dictionary<string, string>(SkillUpdateCoordinator.CreateStagingEnvironment(Path.Combine(temporary.FullName, "fixture")));
                environment["GH_TOKEN"] = auth!;
                environment["GH_CONFIG_DIR"] = Path.Combine(temporary.FullName, "gh-config");
                environment["SKILLVIEW_STATE_HOME"] = Path.Combine(temporary.FullName, "state");
                environment["CLAUDE_CONFIG_DIR"] = Path.Combine(environment["HOME"], ".claude");
                environment["PI_CODING_AGENT_DIR"] = Path.Combine(environment["HOME"], ".pi", "agent");
                foreach (var key in System.Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)))
                    environment[key] = null!;
                environment["GIT_CONFIG_COUNT"] = "0";
                var fixture = new Fixture(temporary, TuiServices.Build(logger, processEnvironment: environment, workingDirectory: temporary.FullName), gh!);
                foreach (var destination in fixture.Roots)
                {
                    var installed = await fixture.Services.Runner.RunAsync(gh!, ["skill", "install", "--dir", destination,
                        "--pin=" + OldCommit, "--", "cli/cli", "skills/gh-skill"], cancellationToken: token);
                    Assert.True(installed.Succeeded, installed.StdErr);
                }
                // Real gh created the second payload. Its deliberately stale
                // source now identifies an absent repository, not local edits.
                var markdown = Path.Combine(fixture.Roots[1], "gh-skill", "SKILL.md");
                var contents = File.ReadAllText(markdown);
                contents = Regex.Replace(contents, @"(?m)^([ \t]+github-repo:).*$", "$1 https://github.com/harder/skillview-missing-contract-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(markdown, contents);
                fixture.RegistryBefore = File.ReadAllBytes(fixture.Registry);
                return fixture;
            }
            catch { temporary.Delete(true); throw; }
        }
    }
}
