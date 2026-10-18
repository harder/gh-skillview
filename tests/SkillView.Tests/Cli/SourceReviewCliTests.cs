using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Cli;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Cli;

public sealed class SourceReviewCliTests
{
    [Fact]
    public async Task ProductFactoryAndOfflineCliAcknowledgementPreserveInstalledBytes()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(OperatingSystem.IsWindows() ? Path.GetTempPath() : "/var/tmp", "skillview-source-cli-" + Guid.NewGuid().ToString("N"));
        var skills = Path.Combine(root, "skills"); var payload = Path.Combine(skills, "demo");
        var home = Path.Combine(root, "home"); var bin = Path.Combine(root, "empty-bin");
        Directory.CreateDirectory(payload); Directory.CreateDirectory(home); Directory.CreateDirectory(bin);
        const string metadata = "---\nname: demo\ndescription: Fixture\nmetadata:\n  github-repo: https://github.com/fixture/repo\n  github-ref: refs/heads/main\n  github-tree-sha: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n---\n";
        File.WriteAllText(Path.Combine(payload, "SKILL.md"), metadata + "Installed instructions\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(payload, (UnixFileMode)448);
        try
        {
            var services = TuiServices.Build(new Logger(), processEnvironment: new Dictionary<string, string>
            {
                ["HOME"] = home,
                ["USERPROFILE"] = home,
                ["PATH"] = bin,
                ["GH_EXTENSION"] = "0",
                ["XDG_STATE_HOME"] = Path.Combine(home, "state"),
                ["XDG_CONFIG_HOME"] = Path.Combine(home, "config"),
                ["SKILLVIEW_STATE_HOME"] = Path.Combine(home, "maintenance-state"),
                ["CODEX_HOME"] = Path.Combine(home, "codex"),
                ["CLAUDE_CONFIG_DIR"] = Path.Combine(home, "claude"),
                ["PI_CODING_AGENT_DIR"] = Path.Combine(home, "pi"),
                ["APPDATA"] = Path.Combine(home, "appdata"),
                ["LOCALAPPDATA"] = Path.Combine(home, "localappdata"),
                ["GIT_DIR"] = Path.Combine(root, "private.git"),
                ["GIT_WORK_TREE"] = root,
                ["GIT_COMMON_DIR"] = Path.Combine(root, "private.git"),
                ["GIT_CONFIG_COUNT"] = "0"
            }, workingDirectory: root);
            var inventory = await services.InventoryService.CaptureAsync(null,
                new LocalInventoryService.Options([skills], true), token);
            var target = Assert.Single(inventory.Skills.Where(skill => skill.ResolvedPath == payload));
            var before = PayloadSnapshot.Capture(payload, token);
            // A deliberate fixture inventory supplies preparation authority.
            // Runtime CLI inventory stays offline/partial; it never promotes a
            // missing gh provider into complete discovery or write authority.
            var fixtureInventory = InventorySnapshot.Empty with
            { Skills = [target], ScannedRoots = inventory.ScannedRoots, ConfiguredRoots = inventory.ConfiguredRoots };
            var plan = await services.SavedUpdatePlans.PlanAsync(target, fixtureInventory, (destination, _) =>
            { File.WriteAllText(Path.Combine(destination.CanonicalPath, "SKILL.md"), metadata + "Frozen replacement\n"); return Task.CompletedTask; }, token);
            Assert.NotNull(plan.SourceReview); // The actual product factory opts in.
            var wrongPurpose = await Run("source-switch-ack-source", "--id", plan.Id, "--expect", plan.PlanFingerprint,
                "--review", plan.SourceReview.Fingerprint, "--yes");
            Assert.Equal(ExitCodes.UserError, wrongPurpose.ExitCode);
            using (var refused = JsonDocument.Parse(wrongPurpose.Output))
                Assert.Contains("purpose", refused.RootElement.GetProperty("error").GetString()!, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(plan.PlanPath)!, "source-review-" + plan.PlanFingerprint + ".json")));
            Assert.Equal(before.Fingerprint, PayloadSnapshot.Capture(payload, token).Fingerprint);
            var acknowledged = await Run("restore-ack-source", "--id", plan.Id, "--expect", plan.PlanFingerprint,
                "--review", plan.SourceReview.Fingerprint, "--yes");
            Assert.Equal(ExitCodes.Success, acknowledged.ExitCode);
            using var json = JsonDocument.Parse(acknowledged.Output);
            var operation = json.RootElement.GetProperty("operation");
            Assert.True(operation.GetProperty("sourceReviewAcknowledged").GetBoolean());
            Assert.False(operation.GetProperty("mutationObserved").GetBoolean());
            Assert.Equal(plan.PlanFingerprint, operation.GetProperty("planFingerprint").GetString());
            Assert.Equal(before.ObjectStateFingerprint, PayloadSnapshot.Capture(payload, token).ObjectStateFingerprint);

            async Task<(int ExitCode, string Output)> Run(params string[] arguments)
            {
                await CliConsoleCapture.Gate.WaitAsync(token);
                var originalOut = Console.Out; var originalError = Console.Error;
                using var output = new StringWriter(); using var error = new StringWriter();
                Console.SetOut(output); Console.SetError(error);
                try
                {
                    var options = ArgParser.Parse("skillview", ["--scan-root", skills, .. arguments, "--allow-hidden-dirs", "--json"]);
                    return (await CliDispatcher.RunAsync(options, services, token), output.ToString());
                }
                finally { Console.SetOut(originalOut); Console.SetError(originalError); CliConsoleCapture.Gate.Release(); }
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
