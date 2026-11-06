using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Cli;
using SkillView.Logging;
using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Cli;

public sealed class ReplacementHistoryCliTests
{
    [Fact]
    public async Task ListingCreatesNoStateAndLaunchesNoProcesses()
    {
        var temporary = Directory.CreateTempSubdirectory("skillview-history-cli-");
        var logger = new Logger(LogLevel.Debug);
        var state = Path.Combine(temporary.FullName, "state");
        var services = TuiServices.Build(logger, logDirectory: temporary.FullName,
            processEnvironment: new Dictionary<string, string>
            {
                ["HOME"] = temporary.FullName,
                ["USERPROFILE"] = temporary.FullName,
                ["SKILLVIEW_STATE_HOME"] = state,
                ["PATH"] = Path.Combine(temporary.FullName, "no-tools")
            }, workingDirectory: temporary.FullName);
        await CliConsoleCapture.Gate.WaitAsync(TestContext.Current.CancellationToken);
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            var result = await CliDispatcher.RunAsync(ArgParser.Parse("skillview", ["replacement-history", "--json"]), services, TestContext.Current.CancellationToken);
            Assert.Equal(ExitCodes.Success, result);
            using var json = JsonDocument.Parse(output.ToString());
            Assert.True(json.RootElement.GetProperty("readOnly").GetBoolean());
            Assert.Empty(json.RootElement.GetProperty("entries").EnumerateArray());
            Assert.Equal(0, json.RootElement.GetProperty("observedCount").GetInt32());
            Assert.False(Directory.Exists(state));
            Assert.Empty(logger.Snapshot());
        }
        finally { Console.SetOut(original); CliConsoleCapture.Gate.Release(); temporary.Delete(true); }
    }

    [Theory]
    [InlineData("--yes")]
    [InlineData("--id")]
    [InlineData("--inspect")]
    [InlineData("--expect")]
    public async Task ListingAcceptsNoMutationOrFacadeArguments(string argument)
    {
        await CliConsoleCapture.Gate.WaitAsync(TestContext.Current.CancellationToken);
        var original = Console.Error;
        using var error = new StringWriter();
        try
        {
            Console.SetError(error);
            var result = await CliDispatcher.RunAsync(ArgParser.Parse("skillview", ["replacement-history", argument]),
                TuiServices.Build(new Logger()), TestContext.Current.CancellationToken);
            Assert.Equal(ExitCodes.InvalidUsage, result);
            Assert.Contains("specialist inspect", error.ToString());
        }
        finally { Console.SetError(original); CliConsoleCapture.Gate.Release(); }
    }
}
