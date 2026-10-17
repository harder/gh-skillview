using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Cli;
using SkillView.Logging;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Cli;

public sealed class ApprovedPayloadCliTests : IDisposable
{
    private readonly DirectoryInfo temporary = Directory.CreateTempSubdirectory("skillview-approval-cli-");
    private readonly TuiServices services;
    private string Payload => Path.Combine(temporary.FullName, "skills", "demo");
    public ApprovedPayloadCliTests()
    {
        Directory.CreateDirectory(Payload); File.WriteAllText(Path.Combine(Payload, "SKILL.md"), "---\nname: demo\ndescription: private fixture\n---\nInstalled custom bytes\n");
        var home = Directory.CreateDirectory(Path.Combine(temporary.FullName, "home")).FullName;
        var environment = System.Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToDictionary(key => key, _ => "");
        foreach (var pair in new Dictionary<string, string>
        {
            ["HOME"] = home,
            ["USERPROFILE"] = home,
            ["PATH"] = "",
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
            ["GIT_DIR"] = Path.Combine(home, "private.git"),
            ["GIT_COMMON_DIR"] = Path.Combine(home, "private.git"),
            ["GIT_WORK_TREE"] = temporary.FullName,
            ["GIT_CONFIG_COUNT"] = "0",
            ["TMPDIR"] = temporary.FullName
        }) environment[pair.Key] = pair.Value;
        services = TuiServices.Build(new Logger(), processEnvironment: environment, workingDirectory: temporary.FullName);
    }

    [Theory]
    [InlineData("approval-capture", "--yes")]
    [InlineData("approval-capture", "--expect-payload", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("approval-inspect", "--id", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("policy-set", "--mode", "approved-immutable", "--yes", "--expect", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("plan", "--purpose", "restore-approved")]
    [InlineData("plan", "--purpose", "selected-revision")]
    [InlineData("plan", "--purpose", "selected-revision", "--revision-kind", "branch", "--revision", "main")]
    [InlineData("plan", "--purpose", "selected-revision", "--revision-kind", "commit", "--revision", "short")]
    [InlineData("plan", "--purpose", "restore-approved", "--approval-id", "wrong", "--approval-expect", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("plan", "--purpose", "restore-recorded", "--approval-id", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public async Task IndependentMissingConsentOrReferenceRefusesWithoutPrivateCapture(params string[] arguments)
    {
        var original = File.ReadAllBytes(Path.Combine(Payload, "SKILL.md"));
        var result = await Run([.. arguments, "--path", Payload]);
        Assert.Equal(ExitCodes.InvalidUsage, result.Code);
        using var json = JsonDocument.Parse(result.Output); Assert.Equal("refused", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(Payload, "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(temporary.FullName, "home", "state")));
    }

    [Fact]
    public async Task OfflinePreviewReportsIncompleteCoverageWithoutInventingApproval()
    {
        var original = File.ReadAllBytes(Path.Combine(Payload, "SKILL.md"));
        var result = await Run(["approval-inspect", "--path", Payload]);
        Assert.Equal(ExitCodes.UserError, result.Code);
        using var json = JsonDocument.Parse(result.Output); Assert.Equal("refused", json.RootElement.GetProperty("status").GetString());
        Assert.Contains("complete inventory", json.RootElement.GetProperty("reason").GetString());
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(Payload, "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(temporary.FullName, "home", "state")));
    }

    private async Task<(int Code, string Output)> Run(string[] arguments)
    {
        var token = TestContext.Current.CancellationToken;
        await CliConsoleCapture.Gate.WaitAsync(token); var prior = Console.Out; var priorError = Console.Error;
        using var output = new StringWriter(); using var error = new StringWriter(); Console.SetOut(output); Console.SetError(error);
        try { return (await CliDispatcher.RunAsync(ArgParser.Parse("skillview", ["--scan-root", Path.GetDirectoryName(Payload)!, .. arguments, "--allow-hidden-dirs", "--json"]), services, token), output.ToString()); }
        finally { Console.SetOut(prior); Console.SetError(priorError); CliConsoleCapture.Gate.Release(); }
    }
    public void Dispose() => temporary.Delete(true);
}
