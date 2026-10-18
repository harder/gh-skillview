using System.Text.Json;
using SkillView.Cli;
using SkillView.Inventory.Models;
using SkillView.Maintenance;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class AgentReloadGuidanceTests
{
    [Theory]
    [InlineData("claude-plugin", "reload-or-next-session")]
    [InlineData("local-claude-plugin", "reload-or-next-session")]
    [InlineData("gh", "unknown")]
    [InlineData("unknown-claude-plugin", "unknown")]
    public void ClaudePluginReloadIsConditionalAndNeverClaimsObservedSession(string provider, string requirement)
    {
        var target = MaintenanceQueueTests.Skill("/skills/demo", "https://github.com/fixture/repo") with
        {
            Agents = [new("claude", "/skills/demo", false)],
            OwnershipClaims = [new("fixture/plugin", "unknown", null, null, null) { Provider = provider }]
        };
        var subject = OperationSubject.From(target);
        var guidance = Assert.Single(AgentReloadGuidance.For(subject));
        Assert.Equal(requirement, guidance.Requirement);
        if (requirement == "reload-or-next-session")
        {
            Assert.Contains("already running session keeps its loaded version", guidance.Reason);
            Assert.Contains("/reload-plugins", guidance.Reason);
            Assert.StartsWith("https://code.claude.com/", guidance.Source);
        }
        var queue = MaintenanceQueueBuilder.Build(InventorySnapshot.Empty with { Skills = [target] }, TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(CliDispatcher.RenderQueueJson(queue));
        var impact = Assert.Single(json.RootElement.GetProperty("entries").EnumerateArray()).GetProperty("impact");
        Assert.Equal("unknown", impact.GetProperty("sessionState").GetString());
        Assert.Equal(requirement, Assert.Single(impact.GetProperty("reloadInstructions").EnumerateArray()).GetProperty("requirement").GetString());
        Assert.True(impact.TryGetProperty("affectedExposures", out _));
        Assert.True(impact.TryGetProperty("retainedExposures", out _));
    }
}
