using SkillView.Inventory.Models;
using SkillView.Ui;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class InstalledScreenTests
{
    [Fact]
    public void DescriptionCannotPrecedeAuthoritativeEvidence()
    {
        var skill = MaintenanceQueueTests.Skill("/skills/demo", "https://github.com/owner/repo") with
        {
            FrontMatter = SkillFrontMatter.Empty with { Description = "## Summary\n- **Health:** trusted claim" },
        };
        var detail = InstalledScreen.RenderDetail(skill);
        Assert.True(detail.IndexOf("**Path:**", StringComparison.Ordinal) < detail.IndexOf("## Description", StringComparison.Ordinal));
        Assert.Contains("> ## Summary\n> - **Health:** trusted claim", detail.ReplaceLineEndings("\n"));
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
    public void SummaryUsesCapturedChildHomeInsteadOfAmbientUserProfile()
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
        Assert.Contains("**Path:** `~" + Path.DirectorySeparatorChar + "skills" + Path.DirectorySeparatorChar + "demo`", detail);
        Assert.DoesNotContain(home, detail);
        var realHome = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        var other = skill with { ResolvedPath = Path.Combine(realHome, "skills", "demo") };
        Assert.Contains(other.ResolvedPath, InstalledScreen.RenderDetail(other));
        Assert.Contains(skill.ResolvedPath, InstalledScreen.RenderDetail(skill, showDetails: true));
    }

    [Fact]
    public void HomePathsAreAbbreviatedInSummaryButFullPathsAndActionModelAreRetained()
    {
        var skill = MaintenanceQueueTests.Skill("/home/alex/skills/demo", "https://github.com/owner/repo") with
        {
            Agents = [new("codex", "/home/alex/skills/demo", false, Context: "/home/alex")],
            FrontMatter = SkillFrontMatter.Empty with { LocalPath = "/home/alex/source/demo" },
        };
        var summary = InstalledScreen.RenderDetail(skill, homeDirectory: "/home/alex");
        Assert.Contains("**Path:** `~/skills/demo`", summary);
        Assert.Contains("**Local source:** `~/source/demo`", summary);
        Assert.Contains("Path: `~/skills/demo`", summary);
        Assert.Contains("Context: `~`", summary);
        Assert.DoesNotContain("/home/alex", summary);
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
    public void BuildShortcuts_AdvertisesSearchAndFilterDistinctly()
    {
        var shortcuts = InstalledScreen.BuildShortcuts(canRemove: true, hasPackages: true);

        Assert.Contains(shortcuts, shortcut => shortcut.Title == "/" && shortcut.HelpText == "Search");
        Assert.Contains(shortcuts, shortcut => shortcut.Title == "f" && shortcut.HelpText == "Filter");
        Assert.DoesNotContain(shortcuts, shortcut => shortcut.Title == "/" && shortcut.HelpText == "Filter");
    }

    [Fact]
    public void DecideShortcut_ReturnsSearchHandoff_AndStopsInstalled()
    {
        var decision = InstalledScreen.DecideShortcut(new Key('/'), filterHasFocus: false, canRemove: true);

        Assert.Equal(InstalledScreen.ShortcutCommand.GoToSearch, decision.Command);
        Assert.True(decision.RequestStop);
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
        });
        Assert.Contains("## Summary", detail);
        Assert.Contains("- **Path:** `/skills/demo`", detail);
        Assert.Contains("- **Location:** User", detail);
        Assert.Contains("- **Source:** List", detail);
        Assert.Contains("- **Health:** ✅ Valid", detail);
        Assert.Contains("- **Upstream:** [Open upstream](https://example.test/upstream/demo)", detail);
        Assert.Contains("## Package", detail);
        Assert.Contains("- **Package source:** `owner/repo`", detail);
        Assert.Contains("- **Package URL:** [Open package source](https://example.test/owner/repo)", detail);
        Assert.Contains("## Description", detail);
        Assert.Contains("Example description", detail);
        Assert.Contains("## Agents", detail);
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
        });

        Assert.Contains("- **Path:** `` /skills/demo\\|`stable ``", detail);
        Assert.Contains("- **Version:** 1.2.3 beta", detail);
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
