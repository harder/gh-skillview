using System.Text.Json;
using SkillView.Cli;
using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using Xunit;

namespace SkillView.Tests.Cli;

public sealed class CliDispatcherInstallTests
{
    [Theory]
    [InlineData(InstallAttemptOutcome.RefusedPreflight, false, false)]
    [InlineData(InstallAttemptOutcome.CanceledBeforeStart, false, true)]
    [InlineData(InstallAttemptOutcome.StartFailed, false, false)]
    [InlineData(InstallAttemptOutcome.ChildFailed, true, false)]
    [InlineData(InstallAttemptOutcome.ChildCanceled, true, true)]
    [InlineData(InstallAttemptOutcome.Unknown, true, false)]
    public void JsonRetainsFailureAttemptAndBothStreamsWithoutInventingNewEntries(
        InstallAttemptOutcome attempt, bool mayHaveChanged, bool canceled)
    {
        var result = InstallResult.Failure("fixture/source", "demo", null, canceled ? 130 : 1, "stderr", []) with
        { Attempt = attempt, ChildDrained = attempt is InstallAttemptOutcome.ChildFailed or InstallAttemptOutcome.ChildCanceled, StdOut = "partial stdout" };
        var args = CliDispatcher.ParseInstallArgs(["fixture/source", "demo", "--json"]);
        using var document = JsonDocument.Parse(CliDispatcher.RenderInstallJson(result, args, added: null));
        var root = document.RootElement;
        Assert.Equal(attempt.ToString(), root.GetProperty("attempt").GetString());
        Assert.Equal(mayHaveChanged, root.GetProperty("mayHaveChangedFiles").GetBoolean());
        Assert.Equal(canceled, root.GetProperty("canceled").GetBoolean());
        Assert.Equal("partial stdout", root.GetProperty("stdout").GetString());
        Assert.Equal("stderr", root.GetProperty("stderr").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("added").ValueKind);
        Assert.False(root.GetProperty("addedKnown").GetBoolean());
        Assert.Equal(0, root.GetProperty("observed").GetArrayLength());
        Assert.NotEqual("completed", root.GetProperty("refresh").GetString());
    }
    [Fact]
    public void JsonKnownEmptyAdditionsRemainDistinctFromUnknown()
    {
        var result = Success();
        var args = CliDispatcher.ParseInstallArgs(["fixture/source", "demo", "--json"]);
        using var known = JsonDocument.Parse(CliDispatcher.RenderInstallJson(result, args, []));
        using var unknown = JsonDocument.Parse(CliDispatcher.RenderInstallJson(result, args, added: null));
        Assert.True(known.RootElement.GetProperty("addedKnown").GetBoolean());
        Assert.Equal(0, known.RootElement.GetProperty("added").GetArrayLength());
        Assert.False(unknown.RootElement.GetProperty("addedKnown").GetBoolean());
        Assert.Equal(JsonValueKind.Null, unknown.RootElement.GetProperty("added").ValueKind);
    }

    [Theory]
    [InlineData("demo", "v7", "installed: fixture/source/demo@v7")]
    [InlineData("demo", null, "installed: fixture/source/demo")]
    [InlineData(null, "v7", "installed: fixture/source@v7")]
    public void TextCleanSuccessConfirmsExactSelectionAndCompleteObservation(string? skill, string? version, string expected)
    {
        var result = Success() with { SkillName = skill, Version = version };
        using var output = new StringWriter();
        using var error = new StringWriter();
        CliDispatcher.WriteInstallText(result, InventorySnapshot.Empty, null, output, error);
        Assert.Equal(new[] { expected, "gh stdout", "rescan: 0 skill(s) observed" },
            output.ToString().Split(System.Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal("gh diagnostic" + System.Environment.NewLine, error.ToString());
        Assert.DoesNotContain("may have changed files", output.ToString() + error.ToString());
        Assert.DoesNotContain("before retrying", output.ToString() + error.ToString());
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("partial")]
    [InlineData("failed")]
    [InlineData("writer-unverified")]
    public void TextSuccessWithUnknownRefreshWarnsWithoutSuccessfulConfirmationOrRescan(string refresh)
    {
        var result = Success() with { ChildDrained = refresh != "writer-unverified" };
        var observed = refresh is "failed" or "writer-unverified" ? null
            : refresh == "incomplete" ? InventorySnapshot.Empty with { ConfiguredRootsComplete = false }
            : InventorySnapshot.Empty with { Diagnostics = new() { InaccessiblePaths = ["/private/unknown"] } };
        using var output = new StringWriter();
        using var error = new StringWriter();
        CliDispatcher.WriteInstallText(result, observed, refresh == "failed" ? "capture failed" : null, output, error);
        Assert.Contains("gh stdout", output.ToString());
        Assert.Contains("may have changed files", error.ToString());
        Assert.DoesNotContain("installed:", output.ToString());
        Assert.DoesNotContain("rescan:", output.ToString());
    }

    [Theory]
    [InlineData(InstallAttemptOutcome.ChildFailed)]
    [InlineData(InstallAttemptOutcome.ChildCanceled)]
    public void TextFailedOrCanceledAttemptKeepsWarningAndStreamsWithACompleteObservation(InstallAttemptOutcome attempt)
    {
        var result = InstallResult.Failure("fixture/source", "demo", null, attempt == InstallAttemptOutcome.ChildCanceled ? 130 : 1,
            "child stderr", []) with
        { Attempt = attempt, ChildDrained = true, StdOut = "partial stdout" };
        using var output = new StringWriter();
        using var error = new StringWriter();
        CliDispatcher.WriteInstallText(result, InventorySnapshot.Empty, null, output, error);
        Assert.Contains("partial stdout", output.ToString());
        Assert.Contains("rescan: 0 skill(s) observed", output.ToString());
        Assert.Contains("child stderr", error.ToString());
        Assert.Contains(result.Canceled ? "install canceled" : "install failed", error.ToString());
        Assert.Contains("may have changed files", error.ToString());
        Assert.DoesNotContain("installed:", output.ToString());
    }

    private static InstallResult Success() => new()
    {
        Repo = "fixture/source",
        SkillName = "demo",
        Version = null,
        Succeeded = true,
        ExitCode = 0,
        StdOut = "gh stdout\n",
        StdErr = "gh diagnostic\n",
        ErrorMessage = null,
        CommandLine = [],
        Attempt = InstallAttemptOutcome.Succeeded,
        ChildDrained = true,
    };

}
