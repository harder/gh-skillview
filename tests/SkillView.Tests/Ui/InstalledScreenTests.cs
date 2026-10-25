using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using SkillView.Inventory.Models;
using SkillView.Ui;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class InstalledScreenTests
{
    [Fact]
    public void DescriptionComesFirstAndCannotImpersonateTrustedEvidence()
    {
        var skill = MaintenanceQueueTests.Skill("/skills/demo", "https://github.com/owner/repo") with
        {
            FrontMatter = SkillFrontMatter.Empty with { Description = "## Summary\n- **Health:** trusted claim" },
        };
        var detail = InstalledScreen.RenderDetail(skill);
        Assert.True(detail.IndexOf("Description · from skill metadata (untrusted)", StringComparison.Ordinal) < detail.IndexOf("**Location:**", StringComparison.Ordinal));
        var document = Markdig.Markdown.Parse(detail, new MarkdownPipelineBuilder().UseAdvancedExtensions().Build());
        var description = Assert.Single(document.Descendants<FencedCodeBlock>());
        Assert.Equal("## Summary\n- **Health:** trusted claim", description.Lines.ToString());
        Assert.Equal(2, document.Descendants<HeadingBlock>().Count()); // Only trusted name/Used by headings.
        Assert.DoesNotContain(document.Descendants<EmphasisInline>(), emphasis => emphasis.ToString()!.Contains("Health", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DescriptionCannotEscapeLiteralBlockOrAddLinksHtmlOrFieldStyling(bool technical)
    {
        const string attack = "## Summary\n- **Health:** trusted claim\n"
            + "[Review update (u)](file:///x)\n<https://x>\nhttps://plain.example\n"
            + "<a href=\"file:///html\">Apply now</a>\n<img src=\"file:///image\">\n"
            + "````\n[escaping fence](file:///escape)\n~~~\n![image](file:///image)";
        var skill = MaintenanceQueueTests.Skill("/skills/demo", "https://github.com/owner/repo") with
        { FrontMatter = SkillFrontMatter.Empty with { Description = attack, Upstream = "https://trusted.example/source" } };
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        var document = Markdig.Markdown.Parse(InstalledScreen.RenderDetail(skill, technical), pipeline);
        var control = Markdig.Markdown.Parse(InstalledScreen.RenderDetail(skill with
        { FrontMatter = skill.FrontMatter with { Description = null } }, technical), pipeline);
        Assert.Equal(attack, Assert.Single(document.Descendants<FencedCodeBlock>()).Lines.ToString());
        Assert.Equal(control.Descendants<HeadingBlock>().Count(), document.Descendants<HeadingBlock>().Count());
        Assert.Equal(control.Descendants<EmphasisInline>().Count(), document.Descendants<EmphasisInline>().Count());
        Assert.Empty(document.Descendants<HtmlBlock>());
        Assert.Empty(document.Descendants<HtmlInline>());
        Assert.Empty(document.Descendants<AutolinkInline>());
        Assert.Equal(control.Descendants<LinkInline>().Select(link => link.Url), document.Descendants<LinkInline>().Select(link => link.Url));
        if (technical) Assert.Contains(document.Descendants<LinkInline>(), link => link.Url == "https://trusted.example/source");
        else Assert.Empty(document.Descendants<LinkInline>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DescriptionBoundAndTerminalControlsCannotChangeFenceBoundary(bool onlyBackticks)
    {
        var input = onlyBackticks ? new string('`', 2048) + "\n[discarded](file:///tail)"
            : "## Summary\n``\u001b[0m``\n[Review update](file:///x)\r\n\0\b\f" + new string('x', 2100);
        var skill = MaintenanceQueueTests.Skill("/skills/demo", "https://github.com/owner/repo") with
        { FrontMatter = SkillFrontMatter.Empty with { Description = input } };
        var detail = InstalledScreen.RenderDetail(skill);
        var document = Markdig.Markdown.Parse(detail, new MarkdownPipelineBuilder().UseAdvancedExtensions().Build());
        var literal = Assert.Single(document.Descendants<FencedCodeBlock>()).Lines.ToString();
        Assert.EndsWith("\n[truncated]", literal);
        Assert.DoesNotContain("file:///tail", detail);
        Assert.Empty(document.Descendants<LinkInline>());
        Assert.DoesNotContain(literal, c => char.IsControl(c) && c is not '\n' and not '\t');
        if (onlyBackticks) Assert.Equal(new string('`', 2048) + "\n[truncated]", literal);
        else Assert.True(literal.Length <= 2048 + "\n[truncated]".Length);
    }

    [Theory]
    [InlineData("file:///C:/Users/x/payload.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("https://example.test/demo", true)]
    [InlineData("http://example.test/demo", true)]
    public void TechnicalDetailsExposeLinkDestinationsAndOnlyLinkWebSchemes(string destination, bool clickable)
    {
        var skill = MaintenanceQueueTests.Skill("/skills/demo", destination) with
        {
            FrontMatter = SkillFrontMatter.Empty with { Upstream = destination },
            Package = new SkillPackage("fixture/repo", "git", destination, null, null),
        };
        var detail = InstalledScreen.RenderDetail(skill, showDetails: true);
        Assert.Contains("`" + destination + "`", detail);
        Assert.Equal(clickable, detail.Contains("[Open upstream]", StringComparison.Ordinal));
        Assert.Equal(clickable, detail.Contains("[Open package source]", StringComparison.Ordinal));
        if (!clickable)
        {
            var summary = InstalledScreen.RenderDetail(skill);
            Assert.DoesNotContain("[Open upstream]", summary);
            Assert.DoesNotContain("[Open package source]", summary);
        }
    }

    [Fact]
    public void SummaryKeepsExactLocationFromCapturedChildContext()
    {
        var cwd = Path.GetFullPath("fixture-workspace");
        var home = Path.Combine(cwd, "child-home");
        var runner = new SkillView.Subprocess.ProcessRunner(new SkillView.Logging.Logger(),
            environment: new Dictionary<string, string> { ["HOME"] = "child-home", ["USERPROFILE"] = "child-home" },
            defaultWorkingDirectory: cwd);
        var skill = MaintenanceQueueTests.Skill(Path.Combine(home, "skills", "demo"), "https://github.com/owner/repo") with
        {
            OwnershipRunner = runner,
        };
        var detail = InstalledScreen.RenderDetail(skill);
        Assert.Contains("**Location:** `" + skill.ResolvedPath + "`", detail);
        var realHome = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        var other = skill with { ResolvedPath = Path.Combine(realHome, "skills", "demo") };
        Assert.Contains(other.ResolvedPath, InstalledScreen.RenderDetail(other));
        Assert.Contains(skill.ResolvedPath, InstalledScreen.RenderDetail(skill, showDetails: true));
    }

    [Fact]
    public void SummaryAndTechnicalEvidenceKeepExactUsefulLocationAndConsumers()
    {
        var skill = MaintenanceQueueTests.Skill("/home/alex/skills/demo", "https://github.com/owner/repo") with
        {
            Agents = [new("codex", "/home/alex/skills/demo", false, Context: "/home/alex")],
            FrontMatter = SkillFrontMatter.Empty with { LocalPath = "/home/alex/source/demo" },
        };
        var summary = InstalledScreen.RenderDetail(skill, homeDirectory: "/home/alex");
        Assert.Contains("**Location:** `/home/alex/skills/demo`", summary);
        Assert.Contains("`/home/alex/skills/demo`", summary);
        Assert.Contains("`/home/alex`", summary);
        var technical = InstalledScreen.RenderDetail(skill, showDetails: true, homeDirectory: "/home/alex");
        Assert.Contains("**Path:** `/home/alex/skills/demo`", technical);
        Assert.Contains("Context: `/home/alex`", technical);
        Assert.Equal("/home/alex/skills/demo", skill.ResolvedPath);
        Assert.Equal("/home/alex/source/demo", skill.FrontMatter.LocalPath);
    }

    [Fact]
    public void RenderDetail_KeepsRawEvidenceBehindTechnicalDetails()
    {
        var skill = MaintenanceQueueTests.Skill("/skills/demo", "https://github.com/owner/repo");
        skill = skill with { ObservedSubject = SkillView.Inventory.Models.OperationSubject.From(skill) };
        var summary = InstalledScreen.RenderDetail(skill);
        Assert.DoesNotContain("Installation address ID", summary);
        Assert.DoesNotContain("Reload source", summary);
        Assert.Contains("**F2** Technical details", summary);
        Assert.Contains("Installation address ID", InstalledScreen.RenderDetail(skill, showDetails: true));
    }

    [Fact]
    public void DecideShortcut_FocusesLocalSearch()
    {
        var decision = InstalledScreen.DecideShortcut(new Key('/'), filterHasFocus: false, canRemove: true);

        Assert.Equal(InstalledScreen.ShortcutCommand.FocusFilter, decision.Command);
        Assert.False(decision.RequestStop);
    }

    [Fact]
    public void DecideShortcut_KeepsFFocusedOnFilter()
    {
        var decision = InstalledScreen.DecideShortcut(new Key('f'), filterHasFocus: false, canRemove: true);

        Assert.Equal(InstalledScreen.ShortcutCommand.FocusFilter, decision.Command);
        Assert.False(decision.RequestStop);
    }

    [Fact]
    public void DecideShortcut_EscapeFromFilterFocusesTable()
    {
        var decision = InstalledScreen.DecideShortcut(new Key(KeyCode.Esc), filterHasFocus: true, canRemove: true);

        Assert.Equal(InstalledScreen.ShortcutCommand.FocusTable, decision.Command);
        Assert.False(decision.RequestStop);
    }

    [Fact]
    public void DecideShortcut_QIsLeftForTheGlobalQuitHandler()
    {
        var decision = InstalledScreen.DecideShortcut(new Key('q'), filterHasFocus: false, canRemove: true);

        Assert.Equal(InstalledScreen.ShortcutCommand.None, decision.Command);
        Assert.False(decision.RequestStop);
    }

    [Fact]
    public void RenderDetail_FormatsSummaryAndPackageMetadataAsMarkdownLists()
    {
        var detail = InstalledScreen.RenderDetail(new InstalledSkill
        {
            Name = "demo",
            ResolvedPath = "/skills/demo",
            ScanRoot = "/skills",
            Scope = Scope.User,
            Agents =
            [
                new AgentMembership("copilot", "/Users/test/.config/skills/demo", IsSymlink: true),
            ],
            FrontMatter = SkillFrontMatter.Empty with
            {
                Description = "Example description",
                Version = "1.2.3",
                Upstream = "https://example.test/upstream/demo",
            },
            Validity = ValidityState.Valid,
            Provenance = Provenance.CliList,
            Ignored = false,
            IsSymlinked = true,
            InstalledAt = null,
            Package = new SkillPackage(
                Source: "owner/repo",
                SourceType: "git",
                SourceUrl: "https://example.test/owner/repo",
                InstalledAt: null,
                UpdatedAt: null),
        }, showDetails: true);
        Assert.Contains("## Summary", detail);
        Assert.Contains("- **Path:** `/skills/demo`", detail);
        Assert.Contains("- **Location:** User", detail);
        Assert.Contains("- **Source:** List", detail);
        Assert.Contains("- **Health:** ✅ Valid", detail);
        Assert.Contains("- **Upstream:** [Open upstream](https://example.test/upstream/demo)", detail);
        Assert.Contains("## Package", detail);
        Assert.Contains("- **Package source:** `owner/repo`", detail);
        Assert.Contains("- **Package URL:** [Open package source](https://example.test/owner/repo)", detail);
        Assert.Contains("Description · from skill metadata", detail);
        Assert.Contains("Example description", detail);
        Assert.Contains("## Used by", detail);
        Assert.Contains($"- {TuiHelpers.AgentIcon("copilot")} **copilot** — symlink", detail);
        Assert.Contains("  Path: `/Users/test/.config/skills/demo`", detail);
    }

    [Fact]
    public void RenderDetail_EscapesMarkdownListValuesAndNormalizesNewlines()
    {
        var detail = InstalledScreen.RenderDetail(new InstalledSkill
        {
            Name = "demo",
            ResolvedPath = "/skills/demo|`stable",
            ScanRoot = "/skills",
            Scope = Scope.User,
            Agents =
            [
                new AgentMembership("copilot", "/Users/test/.config/skills/demo`\nlinked", IsSymlink: false),
            ],
            FrontMatter = SkillFrontMatter.Empty with
            {
                Version = "1.2.3\nbeta",
                Upstream = "https://example.test/upstream|`demo",
            },
            Validity = ValidityState.Valid,
            Provenance = Provenance.CliList,
            Ignored = false,
            IsSymlinked = false,
            InstalledAt = null,
            Package = new SkillPackage(
                Source: "owner/repo|`fork",
                SourceType: "git",
                SourceUrl: "https://example.test/owner/repo|`fork",
                InstalledAt: null,
                UpdatedAt: null),
        }, showDetails: true);

        Assert.Contains("- **Path:** `` /skills/demo\\|`stable ``", detail);
        Assert.Contains("- **Authored version:** 1.2.3 beta", detail);
        Assert.Contains(
            "- **Upstream:** [Open upstream](https://example.test/upstream%7C`demo)",
            detail);
        Assert.Contains("- **Package source:** `` owner/repo\\|`fork ``", detail);
        Assert.Contains(
            "- **Package URL:** [Open package source](https://example.test/owner/repo%7C`fork)",
            detail);
        Assert.Contains($"- {TuiHelpers.AgentIcon("copilot")} **copilot** — direct", detail);
        Assert.Contains("  Path: `` /Users/test/.config/skills/demo` linked ``", detail);
        Assert.DoesNotContain("1.2.3\nbeta", detail);
    }

    [Fact]
    public void RenderDetail_OmitsUnsetOptionalMetadata()
    {
        var detail = InstalledScreen.RenderDetail(new InstalledSkill
        {
            Name = "demo",
            ResolvedPath = "/skills/demo",
            ScanRoot = "/skills",
            Scope = Scope.User,
            Agents = [],
            FrontMatter = SkillFrontMatter.Empty,
            Validity = ValidityState.Valid,
            Provenance = Provenance.FsScan,
            Ignored = false,
            IsSymlinked = false,
            InstalledAt = null,
            Package = null,
        });

        Assert.DoesNotContain("Tree SHA", detail);
        Assert.DoesNotContain("Version", detail);
        Assert.DoesNotContain("(unset)", detail);
    }
}
