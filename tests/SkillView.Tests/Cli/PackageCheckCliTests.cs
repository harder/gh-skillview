using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Cli;
using SkillView.Inventory.Models;
using SkillView.PackageChecking;
using Xunit;

namespace SkillView.Tests.Cli;

public sealed class PackageCheckCliTests
{
    [Theory]
    [InlineData("")]
    [InlineData("--path")]
    [InlineData("--path /one --path /two")]
    [InlineData("--install-id one --install-id two")]
    [InlineData("--path /one --install-id id")]
    [InlineData("--path --json")]
    [InlineData("--path /one --execute")]
    [InlineData("--path /one --json --json")]
    [InlineData("--path /one --agent-version 0.46.0")]
    [InlineData("--path /one --agent gemini --agent codex")]
    public void MissingDuplicateAndUnsupportedSelectorsRefuse(string arguments)
    {
        var parsed = CliDispatcher.ParseCheckArgs(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.NotNull(parsed.Error);
    }

    [Fact]
    public void ExactSelectorsKeepPathsAndInstallationIdsDistinctAndAmbiguityRefuses()
    {
        var skill = Target();
        var snapshot = InventorySnapshot.Empty with { Skills = [skill] };
        var path = CliDispatcher.ParseCheckArgs(["--path", "demo", "--json", "--allow-hidden-dirs"]);
        Assert.Null(path.Error); Assert.True(path.Json); Assert.True(path.AllowHidden);
        Assert.Same(skill, CliDispatcher.SelectCheckTarget(snapshot, path, Path.GetTempPath()));
        var id = CliDispatcher.ParseCheckArgs(["--install-id", skill.InstallationId]);
        Assert.Same(skill, CliDispatcher.SelectCheckTarget(snapshot, id, Path.GetTempPath()));
        Assert.Null(CliDispatcher.SelectCheckTarget(snapshot with { Skills = [skill, skill] }, id, Path.GetTempPath()));
        Assert.Null(CliDispatcher.SelectCheckTarget(InventorySnapshot.Empty, id, Path.GetTempPath()));
    }

    [Fact]
    public void ExposurePathSelectsItsPhysicalInstallationAndCompetingRowsRefuse()
    {
        var alias = Path.Combine(Path.GetTempPath(), "agent", "demo");
        var target = Target() with { Agents = [new("codex", alias, true), new("claude", alias, true)] };
        var arguments = CliDispatcher.ParseCheckArgs(["--path", alias]);
        var inventory = InventorySnapshot.Empty with { Skills = [target] };
        Assert.Same(target, CliDispatcher.SelectCheckTarget(inventory, arguments, Path.GetTempPath()));
        Assert.Null(CliDispatcher.SelectCheckTarget(inventory with { Skills = [target, Target() with { ResolvedPath = alias }] }, arguments, Path.GetTempPath()));
    }

    [Fact]
    public void JsonSeparatesEvidenceDimensionsAndRetainsTypedFindingsAndCoverage()
    {
        var report = Report() with
        {
            ReferenceStatus = "findings",
            HasErrors = true,
            FindingCount = 3,
            OmittedFindingCount = 2,
            Findings = [new("reference", "missing-target", "SKILL.md", 8, "scripts/missing.sh", "Missing local target", "Restore scripts/missing.sh or correct SKILL.md:8.")]
        };
        using var json = JsonDocument.Parse(CliDispatcher.RenderPackageCheckJson(report));
        var root = json.RootElement;
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("none", root.GetProperty("execution").GetString());
        Assert.Equal("not-assessed", root.GetProperty("sourceReview").GetString());
        Assert.Equal("not-assessed", root.GetProperty("agentCompatibility").GetString());
        Assert.Equal("valid", root.GetProperty("formatStatus").GetString());
        var assessments = root.GetProperty("assessments");
        Assert.Equal("valid", assessments.GetProperty("format").GetProperty("state").GetString());
        Assert.Equal("unknown", assessments.GetProperty("agentCompatibility").GetProperty("state").GetString());
        Assert.Equal("unknown", assessments.GetProperty("payloadIntact").GetProperty("state").GetString());
        Assert.Equal("local-defects-observed", assessments.GetProperty("payloadIntact").GetProperty("observedCondition").GetString());
        Assert.Equal("not-reviewed", assessments.GetProperty("sourceReviewed").GetProperty("state").GetString());
        Assert.Equal("limited", root.GetProperty("coverage").GetProperty("references").GetString());
        Assert.Equal("scripts/missing.sh", Assert.Single(root.GetProperty("findings").EnumerateArray()).GetProperty("target").GetString());
        Assert.Equal(3, root.GetProperty("findingCount").GetInt32());
        Assert.Equal(2, root.GetProperty("omittedFindingCount").GetInt32());
        Assert.Equal(ExitCodes.UserError, CliDispatcher.PackageCheckExitCode(report));
    }

    [Fact]
    public void IncompleteCoverageCannotProduceSuccessfulCheckAndHumanOutputIsSanitized()
    {
        var complete = Report();
        Assert.Equal(ExitCodes.Success, CliDispatcher.PackageCheckExitCode(complete));
        Assert.Equal(ExitCodes.UserError, CliDispatcher.PackageCheckExitCode(complete with { Coverage = complete.Coverage with { Payload = "partial" } }));
        Assert.Equal(ExitCodes.UserError, CliDispatcher.PackageCheckExitCode(complete with { Coverage = complete.Coverage with { References = "partial" } }));
        Assert.Equal(ExitCodes.UserError, CliDispatcher.PackageCheckExitCode(complete with { FormatStatus = "unknown" }));
        Assert.Equal(ExitCodes.UserError, CliDispatcher.PackageCheckExitCode(complete with { ReferenceStatus = "unknown" }));
        Assert.Equal(ExitCodes.UserError, CliDispatcher.PackageCheckExitCode(complete with { DeclarationCoverage = "partial" }));
        Assert.Equal(ExitCodes.UserError, CliDispatcher.PackageCheckExitCode(complete with { AgentAssessment = complete.AgentAssessment with { State = "incompatible" } }));
        Assert.DoesNotContain('\x1b', CliDispatcher.RenderPackageCheckText(complete with { Path = "bad\x1b[31m" }));
    }

    [Fact]
    public void ExplicitAgentVersionRetainsSelectionWithoutProbingAndUnknownDimensionsDoNotBecomePassed()
    {
        var parsed = CliDispatcher.ParseCheckArgs(["--path", "demo", "--agent", "gemini-cli", "--agent-version", "0.46.0"]);
        Assert.Null(parsed.Error);
        Assert.Equal("gemini-cli", parsed.Agent);
        Assert.Equal("0.46.0", parsed.AgentVersion);
        var report = Report() with
        {
            Observations = [new("dependency", "dependencies", "package.json", null, "name@^2\x1b[31m", "availability-unknown", "Provide it in a selected environment")],
            ObservationCount = 1
        };
        using var json = JsonDocument.Parse(CliDispatcher.RenderPackageCheckJson(report));
        var observation = Assert.Single(json.RootElement.GetProperty("observations").EnumerateArray());
        Assert.Equal("availability-unknown", observation.GetProperty("state").GetString());
        Assert.Contains('\x1b', observation.GetProperty("value").GetString()!);
        var rendered = CliDispatcher.RenderPackageCheckText(report);
        Assert.Contains("agent compatibility: unknown", rendered);
        Assert.Contains("source reviewed: not-reviewed", rendered);
        Assert.Contains("availability-unknown", rendered);
        Assert.Contains("expected payload baseline", rendered);
        Assert.Equal("unknown", json.RootElement.GetProperty("assessments").GetProperty("payloadIntact").GetProperty("state").GetString());
        Assert.Equal("no-supported-reference-defects-observed", json.RootElement.GetProperty("assessments").GetProperty("payloadIntact").GetProperty("observedCondition").GetString());
        Assert.DoesNotContain('\x1b', rendered);
        Assert.Equal(ExitCodes.Success, CliDispatcher.PackageCheckExitCode(report));
    }

    internal static PackageCheckReport Report() => new("fixture", "/skills/demo", "valid", "none-observed",
        new("complete", "limited", 2, 123, 1, 1, []), "fingerprint", [], 0, 0, false)
    { DeclarationCoverage = "limited" };

    private static InstalledSkill Target() => new()
    {
        Name = "demo",
        ResolvedPath = Path.Combine(Path.GetTempPath(), "demo"),
        ScanRoot = Path.GetTempPath(),
        Scope = Scope.Custom,
        Agents = [],
        FrontMatter = SkillFrontMatter.Empty,
        Validity = ValidityState.Valid,
        Provenance = Provenance.FsScan,
        Ignored = false,
        IsSymlinked = false,
        InstalledAt = null
    };
}
