using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class ContextBarViewTests
{
    [Fact]
    public void FormatForTests_ContainsLocationsWording()
    {
        var state = new ContextBarState(
            Workspace: "Discover",
            AgentLabel: "copilot",
            LocationLabel: "/home/user/.config/skills",
            ProvenanceLabel: "github",
            HealthLabel: "ok",
            FilterLabel: null);

        var text = ContextBarView.FormatForTests(state);

        Assert.Contains("Locations", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatForTests_ContainsHealthText()
    {
        var state = new ContextBarState(
            Workspace: "Discover",
            AgentLabel: "claude",
            LocationLabel: "/skills",
            ProvenanceLabel: null,
            HealthLabel: "2 warnings",
            FilterLabel: null);

        var text = ContextBarView.FormatForTests(state);

        Assert.Contains("2 warnings", text);
    }

    [Fact]
    public void FormatForTests_DoesNotExposeRootsWording()
    {
        var state = new ContextBarState(
            Workspace: "Discover",
            AgentLabel: null,
            LocationLabel: "/some/root",
            ProvenanceLabel: null,
            HealthLabel: null,
            FilterLabel: null);

        var text = ContextBarView.FormatForTests(state);

        Assert.DoesNotContain("roots", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatForTests_EmptyHealth_OmitsHealthSegment()
    {
        var state = new ContextBarState(
            Workspace: "Discover",
            AgentLabel: null,
            LocationLabel: null,
            ProvenanceLabel: null,
            HealthLabel: null,
            FilterLabel: null);

        var text = ContextBarView.FormatForTests(state);

        Assert.DoesNotContain("health", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatForTests_FilterLabel_AppendedWhenPresent()
    {
        var state = new ContextBarState(
            Workspace: "Changes",
            AgentLabel: null,
            LocationLabel: null,
            ProvenanceLabel: null,
            HealthLabel: null,
            FilterLabel: "pinned");

        var text = ContextBarView.FormatForTests(state);

        Assert.Contains("pinned", text, StringComparison.OrdinalIgnoreCase);
    }
}
