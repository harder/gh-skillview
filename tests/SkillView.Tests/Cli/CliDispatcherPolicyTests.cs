using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Cli;
using SkillView.Logging;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Cli;

public sealed class CliDispatcherPolicyTests : IDisposable
{
    private readonly DirectoryInfo temporary = Directory.CreateTempSubdirectory("skillview-policy-cli-");
    private string Skills => Path.Combine(temporary.FullName, "skills");
    private string Selected => Path.Combine(Skills, "selected");
    private string Peer => Path.Combine(Skills, "peer");
    private string State => Path.Combine(temporary.FullName, "state");

    public CliDispatcherPolicyTests()
    {
        foreach (var path in new[] { Selected, Peer })
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "SKILL.md"), "---\nname: same-name\ndescription: Fixture\nmetadata:\n  github-repo: https://github.com/fixture/"
                + Path.GetFileName(path) + "\n  github-ref: refs/heads/main\n  github-pinned: v1\n---\nPrivate fixture instructions\n");
        }
    }

    [Fact]
    public async Task ExactSelectedIntentRoundTripsWithoutChangingPayloadActualPinOrPeer()
    {
        var original = File.ReadAllBytes(Path.Combine(Selected, "SKILL.md"));
        var inspected = await Run("policy-inspect", "--path", Selected);
        Assert.Equal(ExitCodes.Success, inspected.ExitCode);
        Assert.Equal(1, inspected.Json.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("absent", inspected.Json.GetProperty("status").GetString());
        Assert.False(Directory.Exists(State));
        var id = inspected.Json.GetProperty("subject").GetProperty("installationId").GetString()!;
        var fingerprint = inspected.Json.GetProperty("fingerprint").GetString()!;
        var set = await Run("policy-set", "--install-id", id, "--mode", "manual-fork", "--expect", fingerprint, "--yes");
        Assert.True(set.ExitCode == ExitCodes.Success, set.Json.ToString());
        Assert.Equal("manual-fork", set.Json.GetProperty("desiredPolicy").GetProperty("mode").GetString());
        Assert.True(set.Json.GetProperty("actualPinned").GetBoolean());
        Assert.False(set.Json.GetProperty("payloadMutation").GetBoolean());
        Assert.False(set.Json.GetProperty("runtimeMetadataChanged").GetBoolean());
        Assert.Equal("unknown", set.Json.GetProperty("sessionState").GetString());
        Assert.Equal(id, set.Json.GetProperty("subject").GetProperty("installationId").GetString());
        var peer = await Run("policy-inspect", "--path", Peer);
        Assert.Equal("absent", peer.Json.GetProperty("status").GetString());
        var stale = await Run("policy-clear", "--path", Selected, "--expect", fingerprint, "--yes");
        Assert.Equal(ExitCodes.UserError, stale.ExitCode);
        Assert.Equal("refused", stale.Json.GetProperty("status").GetString());
        var clear = await Run("policy-clear", "--install-id", id, "--expect", set.Json.GetProperty("fingerprint").GetString()!, "--yes");
        Assert.Equal(ExitCodes.Success, clear.ExitCode);
        Assert.Equal("absent", clear.Json.GetProperty("status").GetString());
        Assert.True(clear.Json.GetProperty("actualPinned").GetBoolean());
        Assert.NotEqual(fingerprint, clear.Json.GetProperty("fingerprint").GetString());
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(Selected, "SKILL.md")));
    }

    [Theory]
    [InlineData("--mode", "manual-fork")]
    [InlineData("--mode", "approved-immutable", "--expect", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "--yes")]
    [InlineData("--mode", "manual-fork", "--mode", "exact-pin", "--yes")]
    public async Task MissingReviewDuplicateOptionsAndModeOnlyApprovalCannotPublish(params string[] arguments)
    {
        var result = await Run("policy-set", ["--path", Selected, .. arguments]);
        Assert.Equal(ExitCodes.InvalidUsage, result.ExitCode);
        Assert.Equal("refused", result.Json.GetProperty("status").GetString());
        Assert.False(result.Json.GetProperty("payloadMutation").GetBoolean());
        Assert.False(Directory.Exists(State));
    }

    private async Task<(int ExitCode, JsonElement Json)> Run(string command, params string[] arguments)
    {
        await CliConsoleCapture.Gate.WaitAsync(TestContext.Current.CancellationToken);
        var priorOut = Console.Out;
        var priorError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output); Console.SetError(error);
            var home = Directory.CreateDirectory(Path.Combine(temporary.FullName, "home")).FullName;
            var services = TuiServices.Build(new Logger(), processEnvironment: new Dictionary<string, string>
            {
                ["HOME"] = home,
                ["USERPROFILE"] = home,
                ["XDG_CONFIG_HOME"] = Path.Combine(home, "config"),
                ["XDG_STATE_HOME"] = Path.Combine(home, "xdg-state"),
                ["SKILLVIEW_STATE_HOME"] = State,
                ["CLAUDE_CONFIG_DIR"] = Path.Combine(home, "claude"),
                ["CODEX_HOME"] = Path.Combine(home, "codex"),
                ["PI_CODING_AGENT_DIR"] = Path.Combine(home, "pi"),
                ["TMPDIR"] = "/var/tmp",
                ["GIT_DIR"] = Path.Combine(temporary.FullName, ".git"),
                ["GIT_WORK_TREE"] = temporary.FullName,
                ["GIT_COMMON_DIR"] = Path.Combine(temporary.FullName, ".git"),
                ["GIT_CONFIG_COUNT"] = "0"
            }, workingDirectory: temporary.FullName);
            var options = new AppOptions(InvocationMode.Standalone, DispatchMode.Cli, false, AppTheme.Default,
                [Skills], command, [.. arguments, "--json"]);
            var code = await CliDispatcher.RunAsync(options, services, TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(output.ToString());
            return (code, document.RootElement.Clone());
        }
        finally { Console.SetOut(priorOut); Console.SetError(priorError); CliConsoleCapture.Gate.Release(); }
    }

    public void Dispose() => temporary.Delete(true);
}
