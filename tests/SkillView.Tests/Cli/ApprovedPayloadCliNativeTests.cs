using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Cli;
using SkillView.Inventory;
using SkillView.Logging;
using SkillView.Maintenance;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Cli;

public sealed class ApprovedPayloadCliNativeTests
{
    [Fact]
    public async Task ExistingTargetCliUsesActualArchivesReceiptsReferenceAndSourceFreeUndo()
    {
        // The child supports only a fixed list/version protocol. No real gh,
        // network, installer, client, package payload or model is executed.
        if (OperatingSystem.IsWindows()) { Assert.Skip("POSIX inert list-only protocol child; Windows native consumer proof remains required."); return; }
        var token = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("skillview-approved-cli-native-");
        var home = Directory.CreateDirectory(Path.Combine(root.FullName, "home")).FullName;
        var cwd = Directory.CreateDirectory(Path.Combine(root.FullName, "cwd")).FullName;
        var skills = Directory.CreateDirectory(Path.Combine(home, "skills")).FullName;
        var payload = Directory.CreateDirectory(Path.Combine(skills, "demo")).FullName;
        var source = Directory.CreateDirectory(Path.Combine(home, "source")).FullName;
        var bin = Directory.CreateDirectory(Path.Combine(home, "bin")).FullName;
        var gh = Path.Combine(bin, "gh");
        Directory.CreateDirectory(Path.Combine(cwd, ".git"));
        File.WriteAllText(gh, "#!/bin/sh\nif [ \"$1\" = \"--version\" ]; then printf 'gh version 2.102.0\\n'; exit 0; fi\nif [ \"$1\" = skill ] && [ \"$2\" = list ]; then printf '[]\\n'; exit 0; fi\nprintf 'unsupported protocol request\\n' >&2\nexit 113\n");
        File.SetUnixFileMode(gh, (UnixFileMode)448);
        var skill = Path.Combine(payload, "SKILL.md");
        File.WriteAllText(skill, "---\nname: demo\ndescription: actual custom I\nversion: unchanged\nmetadata:\n  local-path: " + source + "\n---\nApproved customized instructions.\n");
        File.WriteAllText(Path.Combine(payload, "extra"), "authored installed resource\n");
        var values = System.Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToDictionary(key => key, _ => "");
        foreach (var pair in new Dictionary<string, string>
        {
            ["HOME"] = home,
            ["USERPROFILE"] = home,
            ["PATH"] = bin + ":/usr/bin:/bin",
            ["GH_EXTENSION"] = "0",
            ["GH_PATH"] = "",
            ["SKILLVIEW_STATE_HOME"] = Path.Combine(home, "state"),
            ["XDG_STATE_HOME"] = Path.Combine(home, "xdg-state"),
            ["XDG_CONFIG_HOME"] = Path.Combine(home, "config"),
            ["APPDATA"] = Path.Combine(home, "appdata"),
            ["LOCALAPPDATA"] = Path.Combine(home, "local"),
            ["CODEX_HOME"] = Path.Combine(home, "codex"),
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(home, "claude"),
            ["PI_CODING_AGENT_DIR"] = Path.Combine(home, "pi"),
            ["CLAUDE_CODE_PLUGIN_CACHE_DIR"] = Path.Combine(home, "plugins"),
            ["CLAUDE_CODE_PLUGIN_DIRS"] = "",
            ["CLAUDE_CODE_PLUGIN_SEED_DIR"] = "",
            ["GIT_DIR"] = Path.Combine(cwd, ".git"),
            ["GIT_COMMON_DIR"] = Path.Combine(cwd, ".git"),
            ["GIT_WORK_TREE"] = cwd,
            ["GIT_CONFIG_COUNT"] = "0",
            ["TMPDIR"] = root.FullName
        }) values[pair.Key] = pair.Value;
        var services = TuiServices.Build(new Logger(), processEnvironment: values, workingDirectory: cwd);
        try
        {
            var approved = PayloadSnapshot.Capture(payload, token);
            using var preview = await Success("approval-inspect", "--path", payload);
            Assert.Equal(approved.Fingerprint, preview.RootElement.GetProperty("payloadFingerprint").GetString());
            var captureResult = await Run("approval-capture", "--path", payload, "--expect-payload", approved.Fingerprint, "--yes");
            if (captureResult.Code != ExitCodes.Success)
            {
                using var refused = JsonDocument.Parse(captureResult.Output);
                var reason = refused.RootElement.GetProperty("reason").GetString()!;
                if (OperatingSystem.IsLinux() && reason.Contains("CAP_SYS_ADMIN", StringComparison.Ordinal))
                {
                    Assert.Equal(approved.ObjectStateFingerprint, PayloadSnapshot.Capture(payload, token).ObjectStateFingerprint);
                    Assert.Skip("Real CLI completed archive/restore needs admitted full metadata authority; no synthetic success substitutes for this native gap.");
                }
                Assert.Fail(captureResult.Output);
            }
            using var capture = JsonDocument.Parse(captureResult.Output);
            var reference = capture.RootElement.GetProperty("approvalReference");
            var id = reference.GetProperty("id").GetString()!; var fingerprint = reference.GetProperty("fingerprint").GetString()!;
            var storeAddress = reference.GetProperty("storeAddress").GetString()!;
            Assert.False(capture.RootElement.GetProperty("desiredPolicyChanged").GetBoolean());
            using var inspectedApproval = await Success("approval-inspect", "--path", payload, "--id", id, "--expect", fingerprint);
            var policyFingerprint = inspectedApproval.RootElement.GetProperty("policyFingerprint").GetString()!;
            using var intent = await Success("policy-set", "--path", payload, "--mode", "approved-immutable", "--approval-id", id, "--approval-expect", fingerprint, "--expect", policyFingerprint, "--yes");
            Assert.Equal("approved-immutable", intent.RootElement.GetProperty("desiredPolicy").GetProperty("mode").GetString());
            File.AppendAllText(skill, "Later body; same authored version and source.\n");
            File.WriteAllText(Path.Combine(payload, "later-only"), "retain me exactly on undo\n");
            var beforeRestore = PayloadSnapshot.Capture(payload, token);
            using var prepared = await Success("plan", "--purpose", "restore-approved", "--path", payload, "--approval-id", id, "--approval-expect", fingerprint);
            var operation = prepared.RootElement.GetProperty("operation");
            var planId = operation.GetProperty("id").GetString()!; var planFingerprint = operation.GetProperty("planFingerprint").GetString()!;
            var sourceFingerprint = operation.GetProperty("sourceReview").GetProperty("fingerprint").GetString()!;
            var retainedPath = operation.GetProperty("retainedPath").GetString()!;
            var planFile = Path.Combine(root.FullName, "saved-reference.json"); File.WriteAllText(planFile, prepared.RootElement.GetRawText());
            var referenceBytes = File.ReadAllBytes(planFile);
            var refusedApply = await Run("apply", "--plan-file", planFile, "--yes", "--expect", planFingerprint, "--disposition", ReplacementPlanStore.ApprovedRestoreDisposition);
            Assert.Equal(ExitCodes.UserError, refusedApply.Code); Assert.Contains("source-review acknowledgement", refusedApply.Output);
            Assert.False(Directory.Exists(retainedPath)); Assert.Equal(beforeRestore.ObjectStateFingerprint, PayloadSnapshot.Capture(payload, token).ObjectStateFingerprint);
            using var acknowledged = await Success("approved-ack-source", "--id", planId, "--expect", planFingerprint, "--review", sourceFingerprint, "--yes");
            Assert.Equal("not-performed; source-review acknowledgement only", acknowledged.RootElement.GetProperty("diskVerification").GetString());
            using var applied = await Success("apply", "--plan-file", planFile, "--yes", "--expect", planFingerprint, "--disposition", ReplacementPlanStore.ApprovedRestoreDisposition);
            Assert.Equal("installed", applied.RootElement.GetProperty("status").GetString());
            Assert.Equal(approved.Fingerprint, PayloadSnapshot.Capture(payload, token).Fingerprint);
            Directory.Move(storeAddress, storeAddress + "-unavailable"); Directory.Delete(source);
            using var installed = await Success("inspect", "--plan-file", planFile);
            var undoFingerprint = installed.RootElement.GetProperty("operation").GetProperty("reviewFingerprint").GetString()!;
            using var undone = await Success("undo", "--plan-file", planFile, "--yes", "--expect", undoFingerprint);
            Assert.Equal(beforeRestore.ObjectStateFingerprint, PayloadSnapshot.Capture(payload, token).ObjectStateFingerprint);
            Assert.Equal(beforeRestore.Fingerprint, PayloadSnapshot.Capture(payload, token).Fingerprint);
            Assert.Equal(referenceBytes, File.ReadAllBytes(planFile));
        }
        finally { root.Delete(true); }

        async Task<JsonDocument> Success(params string[] arguments)
        {
            var result = await Run(arguments); Assert.True(result.Code == ExitCodes.Success, result.Output);
            return JsonDocument.Parse(result.Output);
        }
        async Task<(int Code, string Output)> Run(params string[] arguments)
        {
            await CliConsoleCapture.Gate.WaitAsync(token); var prior = Console.Out; var priorError = Console.Error;
            using var output = new StringWriter(); using var error = new StringWriter(); Console.SetOut(output); Console.SetError(error);
            try { return (await CliDispatcher.RunAsync(ArgParser.Parse("skillview", ["--scan-root", skills, .. arguments, "--allow-hidden-dirs", "--json"]), services, token), output.ToString()); }
            finally { Console.SetOut(prior); Console.SetError(priorError); CliConsoleCapture.Gate.Release(); }
        }
    }
}
