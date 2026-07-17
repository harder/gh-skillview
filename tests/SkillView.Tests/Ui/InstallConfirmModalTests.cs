using SkillView.Ui;
using Terminal.Gui.Views;
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
        Assert.Equal(new[] { "universal" }, opts.Agents);
    }

    [Fact]
    public void BuildOptions_NoAgentsSelected_NoCustomPath_DefaultsToUniversal()
    {
        var opts = InstallConfirmModal.BuildOptionsFromSelection(
            scopeIndex: 0,
            customPath: "",
            selectedAgentIds: Array.Empty<string>());

        Assert.Equal(new[] { "universal" }, opts.Agents);
    }

    [Fact]
    public void BuildOptions_NoAgentsSelected_WithCustomPath_LeavesAgentsNull()
    {
        // --dir overrides --agent entirely, so a default agent would be
        // meaningless (and misleading in the command preview) once a custom
        // install path is set.
        var opts = InstallConfirmModal.BuildOptionsFromSelection(
            scopeIndex: 2,
            customPath: "/opt/skills",
            selectedAgentIds: Array.Empty<string>());

        Assert.Equal("/opt/skills", opts.Path);
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
        // Path never resolved (blank), so the universal-agent default still
        // applies — same as any other no-path, no-selection case.
        Assert.Equal(new[] { "universal" }, opts.Agents);
    }

    [Fact]
    public void BuildOptions_DefaultsAllToFalse()
    {
        var opts = InstallConfirmModal.BuildOptionsFromSelection(
            scopeIndex: 1,
            customPath: "",
            selectedAgentIds: Array.Empty<string>());

        Assert.False(opts.All);
    }

    [Fact]
    public void BuildOptions_InstallAll_SetsAllFlagAndKeepsScopeAgents()
    {
        var opts = InstallConfirmModal.BuildOptionsFromSelection(
            scopeIndex: 1,
            customPath: "",
            selectedAgentIds: new[] { "claude-code" },
            installAll: true);

        Assert.True(opts.All);
        Assert.Equal("user", opts.Scope);
        Assert.Equal(new[] { "claude-code" }, opts.Agents);
    }

    [Fact]
    public void ValidateSelection_Custom_EmptyPath_ReturnsError()
    {
        var error = InstallConfirmModal.ValidateSelection(scopeIndex: 2, customPath: "   ");

        Assert.Equal("enter a custom install path", error);
    }

    [Fact]
    public void ValidateSelection_ProjectScope_IgnoresCustomPath()
    {
        var error = InstallConfirmModal.ValidateSelection(scopeIndex: 0, customPath: "   ");

        Assert.Null(error);
    }

    [Fact]
    public void WireValidation_DisablesInstall_ForBlankCustomPath_AndReenablesOnInput()
    {
        var scopeSelector = new OptionSelector
        {
            Labels = new List<string> { "Project", "User", "Custom" },
            Value = 0,
        };
        var customPathLabel = new Label { Visible = false };
        var customPathField = new TextField { Text = string.Empty, Visible = false };
        var installButton = new Button { Enabled = true };
        var status = new Label();
        var spinner = new SpinnerView { Visible = false };

        InstallConfirmModal.WireValidation(
            scopeSelector,
            customPathLabel,
            customPathField,
            installButton,
            status,
            spinner);

        Assert.True(installButton.Enabled);
        Assert.Equal(" ready", status.Text.ToString());

        scopeSelector.Value = 2;

        Assert.True(customPathLabel.Visible);
        Assert.True(customPathField.Visible);
        Assert.False(installButton.Enabled);
        Assert.Equal(" enter a custom install path", status.Text.ToString());

        customPathField.Text = "/tmp/skills";

        Assert.True(installButton.Enabled);
        Assert.Equal(" ready", status.Text.ToString());
    }
}
