using System.Collections.Immutable;
using System.Text.Json;
using SkillView.Cli;
using SkillView.Inventory.Models;
using SkillView.Loading;
using Xunit;

namespace SkillView.Tests.Loading;

public sealed class EffectiveCliTests : IDisposable
{
    private readonly DirectoryInfo _temporary = Directory.CreateTempSubdirectory("skillview-effective-cli-");
    public void Dispose() => _temporary.Delete(true);

    [Fact]
    public void ExactContextOptionsAreDistinctFromGlobalInventoryProfiles()
    {
        var parsed = CliDispatcher.ParseEffectiveArgs(["--agent", "claude", "--profile", "work", "--project=/project", "--cwd", "/project/src",
            "--config-dir", "/profile", "--client-version", "2.1.197", "--agent-name", "plan", "--trust", "unknown", "--json"]);
        Assert.Null(parsed.Error); Assert.True(parsed.Json);
        var request = Assert.IsType<LoadingCaptureRequest>(parsed.Request);
        Assert.Equal("claude", request.AgentId); Assert.Equal("work", request.Profile); Assert.Equal("/project", request.ProjectDirectory);
        Assert.Equal("/project/src", request.WorkingDirectory); Assert.Equal("/profile", request.ConfigDirectory);
        Assert.Equal("2.1.197", request.ClientVersion); Assert.Equal("unknown", request.ProjectTrust);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--agent")]
    [InlineData("--agent codex --agent gemini")]
    [InlineData("--agent codex --trust yes")]
    [InlineData("--agent codex --execute")]
    [InlineData("--agent codex --config-dir --json")]
    public void MissingDuplicateAndUnsupportedContextFlagsFailBeforeCapture(string arguments)
    {
        var parsed = CliDispatcher.ParseEffectiveArgs(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.NotNull(parsed.Error); Assert.Null(parsed.Request);
    }

    private EffectiveSkillAnalysis Analysis()
    {
        var context = SkillLoadingContext.Capture("codex", "work", _temporary.FullName, _temporary.FullName, _temporary.FullName,
            _temporary.FullName, "unverified-fixture-version", null, "unknown", "fixture", "fixture-rule");
        var metadata = new SkillFrontMatter { Name = "demo", Description = "fixture" };
        var subject = OperationSubject.FromRecorded("physical-id", Path.Combine(_temporary.FullName, "demo"), "demo", Scope.User, metadata,
            [new("codex", "/observed-entry", true, Scope.User)], []);
        var expected = new ExpectedSkillLoading("manual-only", "expected", "yes", "no", "unknown", "coexists", "not-proven-incompatible", "restart", ["fixture reason"]);
        var candidate = new EffectiveSkillCandidate("contextual-key", subject, subject.Exposures, "demo", "user-or-system", expected, [], [], ["portable warning"])
        { EntryPath = "/observed-entry", InvocationAliases = ["other-name"] };
        return new(context, AgentLoadingRules.Find("codex"), "client-version-unverified", DateTimeOffset.UtcNow, false, [candidate],
            [new("/config.toml", "observed", "digest", "fixture evidence", null)], NativeLoadingObservation.Unavailable(context, "No client was launched."), ["fixture limitation"]);
    }

    [Fact]
    public void VersionedJsonRetainsSubjectsAliasesDimensionsAndUnknownNativeSessionFacts()
    {
        using var json = JsonDocument.Parse(CliDispatcher.RenderEffectiveJson(Analysis()));
        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("conditional-local-file-policy-preview", root.GetProperty("evidenceKind").GetString());
        var candidate = Assert.Single(root.GetProperty("candidates").EnumerateArray());
        Assert.Equal("physical-id", candidate.GetProperty("subject").GetProperty("installationId").GetString());
        Assert.Equal("contextual-key", candidate.GetProperty("loadingKey").GetString());
        Assert.Equal("other-name", Assert.Single(candidate.GetProperty("invocationAliases").EnumerateArray()).GetString());
        Assert.Equal("yes", candidate.GetProperty("expected").GetProperty("manualInvocation").GetString());
        Assert.Equal("no", candidate.GetProperty("expected").GetProperty("automaticInvocation").GetString());
        Assert.Equal("unknown", candidate.GetProperty("activeSessionState").GetString());
        Assert.Equal("unavailable", root.GetProperty("native").GetProperty("state").GetString());
        Assert.Equal("client-version-unverified", root.GetProperty("rules").GetProperty("applicability").GetString());
        Assert.Equal("not-run", root.GetProperty("rules").GetProperty("nativeConformance").GetString());
        Assert.NotEmpty(root.GetProperty("rules").GetProperty("sources").EnumerateArray());
        Assert.False(root.GetProperty("analysisInputsComplete").GetBoolean());
    }

    [Fact]
    public void RenderingUsesFrozenEvidenceAndSanitizesUntrustedText()
    {
        var analysis = Analysis();
        var candidate = analysis.Candidates[0];
        analysis = analysis with
        {
            Candidates = [candidate with { InvocationName = "demo\x1b[31m", EntryPath = "unavailable\0path" }],
            DiscoveryRoots = [new("unavailable\0root\x1b[31m", "configured-path", false, SourceEvidence: "frozen source")]
        };
        // These invalid addresses would throw if either renderer probed them.
        var text = CliDispatcher.RenderEffectiveText(analysis);
        Assert.DoesNotContain('\x1b', text);
        Assert.Contains("manual=yes; automatic=no", text);
        Assert.Contains("active-session state: unknown", text);
        using var json = JsonDocument.Parse(CliDispatcher.RenderEffectiveJson(analysis));
        Assert.Single(json.RootElement.GetProperty("candidates").EnumerateArray());
        var root = Assert.Single(json.RootElement.GetProperty("discoveryRoots").EnumerateArray());
        Assert.Equal("configured-path", root.GetProperty("placement").GetString());
        Assert.Equal("frozen source", root.GetProperty("sourceEvidence").GetString());
    }

    [Fact]
    public void SharedTextViewRetainsSlashNativeDisagreementAndPortableEvidence()
    {
        var analysis = Analysis();
        var candidate = analysis.Candidates[0] with
        {
            Expected = analysis.Candidates[0].Expected with { SlashInvocation = "no" },
            NativeObservations = [new("/observed-entry", "demo", false, "demo", "fixture observation")],
            Disagreements = ["fixture disagreement"]
        };
        var text = CliDispatcher.RenderEffectiveText(analysis with { Candidates = [candidate] });
        Assert.Contains("slash=no", text); Assert.Contains("discovery=expected", text);
        Assert.Contains("Native observation:", text); Assert.Contains("fixture disagreement", text);
        Assert.Contains("portable warning", text); Assert.Contains("reload=restart", text);
    }
}
