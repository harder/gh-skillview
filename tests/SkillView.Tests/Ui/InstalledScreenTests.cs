using SkillView.Inventory.Models;
using SkillView.Ui;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class InstalledScreenTests
{
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
        Assert.Contains("- **Upstream:** [https://example.test/upstream/demo](https://example.test/upstream/demo)", detail);
        Assert.Contains("## Package", detail);
        Assert.Contains("- **Package source:** `owner/repo`", detail);
        Assert.Contains("- **Package URL:** [https://example.test/owner/repo](https://example.test/owner/repo)", detail);
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
            "- **Upstream:** [https://example.test/upstream\\|`demo](https://example.test/upstream%7C`demo)",
            detail);
        Assert.Contains("- **Package source:** `` owner/repo\\|`fork ``", detail);
        Assert.Contains(
            "- **Package URL:** [https://example.test/owner/repo\\|`fork](https://example.test/owner/repo%7C`fork)",
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
