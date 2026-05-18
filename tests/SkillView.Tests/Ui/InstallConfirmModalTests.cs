using SkillView.Ui;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class InstallConfirmModalTests
{
    [Fact]
    public void BuildOptions_Project_EmitsProjectScope_NoPath()
    {
        var opts = InstallConfirmModal.BuildOptionsFromSelection(
            scopeIndex: 0,
            customPath: "ignored",
            selectedAgentIds: new[] { "claude-code" });

        Assert.Equal("project", opts.Scope);
        Assert.Null(opts.Path);
        Assert.Equal(new[] { "claude-code" }, opts.Agents);
    }

    [Fact]
    public void BuildOptions_User_EmitsUserScope_NoPath()
    {
        var opts = InstallConfirmModal.BuildOptionsFromSelection(
            scopeIndex: 1,
            customPath: "",
            selectedAgentIds: Array.Empty<string>());

        Assert.Equal("user", opts.Scope);
        Assert.Null(opts.Path);
        Assert.Null(opts.Agents);
    }

    [Fact]
    public void BuildOptions_Custom_EmitsNullScope_AndTrimmedPath()
    {
        var opts = InstallConfirmModal.BuildOptionsFromSelection(
            scopeIndex: 2,
            customPath: "  /opt/skills  ",
            selectedAgentIds: new[] { "claude-code", "cursor" });

        Assert.Null(opts.Scope);
        Assert.Equal("/opt/skills", opts.Path);
        Assert.Equal(new[] { "claude-code", "cursor" }, opts.Agents);
    }

    [Fact]
    public void BuildOptions_Custom_EmptyPath_StaysNull()
    {
        var opts = InstallConfirmModal.BuildOptionsFromSelection(
            scopeIndex: 2,
            customPath: "   ",
            selectedAgentIds: Array.Empty<string>());

        Assert.Null(opts.Scope);
        Assert.Null(opts.Path);
        Assert.Null(opts.Agents);
    }
}
