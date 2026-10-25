using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using SkillView.Inventory.Models;
using SkillView.Ui;
using Terminal.Gui.Text;
using Xunit;

namespace SkillView.Tests.Ui;

public sealed class InstalledDetailReadabilityTests
{
    internal const string OrdinaryDescription = "Manage agent skills with gh skill. Use this skill to discover, preview, install, update, and publish Agent Skills so an agent can self-manage the skills available in its environment.";
    internal static InstalledSkill SharedSkill() => InstalledOverviewTests.Copy("/home/alex/projects/acme/.agents/skills/gh-skill", "gh-skill", "cli/cli", "refs/tags/v2.103.2") with
    {
        Scope = Scope.Project,
        FrontMatter = InstalledOverviewTests.Copy("/unused", "gh-skill", "cli/cli", "refs/tags/v2.103.2").FrontMatter with { Description = OrdinaryDescription },
        Agents = [.. new[] { "universal", "github-copilot", "cursor", "codex", "gemini-cli", "antigravity", "antigravity-cli", "antigravity2.0", "amp", "cline", "deepagents", "firebender", "kimi-cli", "opencode", "replit", "warp" }
            .Select(id => new AgentMembership(id, "/home/alex/projects/acme/.agents/skills/gh-skill", false, Scope.Project, id == "universal" ? "/home/alex/projects/acme" : null))]
    };

    [Theory]
    [InlineData(48)]
    [InlineData(73)]
    public void OrdinaryDescriptionAndAllSixteenPlacementsStayReadable(int width)
    {
        var skill = SharedSkill();
        var detail = InstalledScreen.RenderDetail(skill, descriptionWidth: width);
        var document = Parse(detail);
        var literal = Assert.Single(document.Descendants<FencedCodeBlock>());
        Assert.Equal(OrdinaryDescription, string.Join(" ", literal.Lines.ToString().Split('\n')));
        Assert.All(literal.Lines.ToString().Split('\n'), line => Assert.InRange(line.GetColumns(), 1, width));
        Assert.Contains("1 physical copy · 16 recorded placements", detail);
        Assert.Contains("Git tag `v2.103.2`", detail);
        Assert.Contains("Pin: `no pin recorded`", detail);
        Assert.Contains("Project — project root not recorded:", detail);
        Assert.Contains("Project · Context: `/home/alex/projects/acme`: `universal`", detail);
        Assert.Equal(1, detail.Split(skill.ResolvedPath).Length - 1);
        foreach (var membership in skill.Agents) Assert.Contains("`" + membership.AgentId + "`", detail);
        Assert.True(detail.IndexOf("Source:", StringComparison.Ordinal) < detail.IndexOf("1 physical copy", StringComparison.Ordinal));
        Assert.True(detail.IndexOf("1 physical copy", StringComparison.Ordinal) < detail.IndexOf("**Location:**", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(2046, false)] // The complete pair fits exactly.
    [InlineData(2047, true)] // The boundary would split the pair.
    [InlineData(2048, true)] // The pair is entirely beyond the boundary.
    [InlineData(2045, false)] // The pair precedes the boundary.
    public void DescriptionBoundaryNeverSplitsASurrogatePair(int prefixLength, bool pairOmitted)
    {
        var input = new string('a', prefixLength) + "😀" + "tail";
        var skill = SharedSkill() with { FrontMatter = SkillFrontMatter.Empty with { Description = input } };
        var detail = InstalledScreen.RenderDetail(skill, descriptionWidth: 39);
        var literal = Assert.Single(Parse(detail).Descendants<FencedCodeBlock>()).Lines.ToString();
        Assert.Equal(!pairOmitted, literal.Contains("😀", StringComparison.Ordinal));
        Assert.DoesNotContain('\uFFFD', literal);
        Assert.DoesNotContain("[truncated]", literal);
        Assert.Contains("\n\n[truncated]\n", detail);
        Assert.Equal(input, skill.FrontMatter.Description);
        // Enumerating runes substitutes U+FFFD for an unpaired surrogate.
        Assert.DoesNotContain(literal.EnumerateRunes(), rune => rune.Value == 0xfffd);
    }

    [Fact]
    public void DescriptionExactlyAtBoundIsNotTruncated()
    {
        var description = new string('x', 2046) + "😀";
        var detail = InstalledScreen.RenderDetail(SharedSkill() with { FrontMatter = SkillFrontMatter.Empty with { Description = description } }, descriptionWidth: 35);
        Assert.DoesNotContain("[truncated]", detail);
        Assert.EndsWith("😀", Assert.Single(Parse(detail).Descendants<FencedCodeBlock>()).Lines.ToString());
    }

    [Theory]
    [InlineData("abc ``` [click](file:///attack) <https://evil> **styled**", 4)]
    [InlineData("``\u001b[0m``\r\n## heading\n\n\0\b\f\tend", 12)]
    [InlineData("長い説明文の単語を幅に合わせて折り返します。éééé\tend", 8)]
    [InlineData("averylongtokenwithoutanyspaceswhatsoever", 9)]
    public void FinalWrappedDescriptionHasOneInertFence(string input, int width)
    {
        var skill = SharedSkill() with { FrontMatter = SkillFrontMatter.Empty with { Description = input, Upstream = "https://trusted.example/source" } };
        foreach (var technical in new[] { false, true })
        {
            var detail = InstalledScreen.RenderDetail(skill, technical, descriptionWidth: width);
            var document = Parse(detail);
            var block = Assert.Single(document.Descendants<FencedCodeBlock>());
            Assert.True(string.IsNullOrEmpty(block.Info));
            Assert.All(block.Lines.ToString().Split('\n'), line => Assert.True(line.GetColumns() <= width, line));
            Assert.DoesNotContain(block.Lines.ToString(), c => char.IsControl(c) && c != '\n');
            Assert.Empty(document.Descendants<HtmlBlock>());
            Assert.Empty(document.Descendants<HtmlInline>());
            Assert.Empty(document.Descendants<AutolinkInline>());
            Assert.All(document.Descendants<LinkInline>(), link => Assert.Equal("https://trusted.example/source", link.Url));
        }
    }

    [Fact]
    public void EveryNewLiteralIsEncodedBeforeTrustedLayoutWithoutChangingInputs()
    {
        const string attack = "[click](file:///attack) **bold** `x` <a href='x'>html</a> <https://evil>\n\t\0\u001b[31m";
        var member = new AgentMembership(attack, "/other/" + attack, true, Scope.Custom, attack, Profile: attack);
        var skill = SharedSkill() with { ResolvedPath = "/physical/" + attack, Agents = [member] };
        var detail = InstalledScreen.RenderDetail(skill);
        var document = Parse(detail);
        Assert.Empty(document.Descendants<LinkInline>());
        Assert.Empty(document.Descendants<HtmlInline>());
        Assert.Empty(document.Descendants<HtmlBlock>());
        Assert.Empty(document.Descendants<AutolinkInline>());
        Assert.Equal(2, document.Descendants<HeadingBlock>().Count());
        Assert.Contains("\uFFFD", detail);
        Assert.DoesNotContain('\u001b', detail);
        Assert.DoesNotContain('\t', detail);
        Assert.Contains("Profile:", detail);
        Assert.Equal(attack, member.AgentId);
        Assert.Equal(attack, member.Profile);
        Assert.Equal(attack, member.Context);
        Assert.Equal("/other/" + attack, member.Path);
        Assert.Equal("/physical/" + attack, skill.ResolvedPath);
    }

    [Fact]
    public void ContextsUseOnlyCapturedFactsAndOnlyOrdinalPathsSaySameLocation()
    {
        var skill = SharedSkill() with
        {
            ResolvedPath = "/Case/demo",
            Scope = Scope.Project,
            Agents = [new("a", "/Case/demo", false, Scope.User), new("b", "/Case/demo", false, Scope.Project),
                new("c", "/Case/demo", false, Scope.Custom), new("d", "/case/demo", false, Scope.User, "/captured"),
                new("e", "/Case/demo", false, Context: "/known"), new("f", "/Case/demo", false, Scope.Custom, "/custom")]
        };
        var text = InstalledScreen.RenderPlacements(skill);
        Assert.Contains("User: `a`", text);
        Assert.DoesNotContain("User —", text);
        Assert.Contains("Project — project root not recorded: `b`", text);
        Assert.Contains("Project · Context: `/known`: `e`", text);
        Assert.Contains("Custom: `c`", text);
        Assert.Contains("Custom · Context: `/custom`: `f`", text);
        Assert.Contains("Location: `/case/demo`", text);
        Assert.Equal(1, text.Split("Same location").Length - 1);
        Assert.True(text.IndexOf("Context: `/known`", StringComparison.Ordinal) < text.IndexOf("project root not recorded", StringComparison.Ordinal));
    }

    [Fact]
    public void StructuralMembershipsCompressVisibleLabelsButRetainProfilesAndEveryHiddenDifference()
    {
        var member = new AgentMembership("codex", "/physical", false, Scope.User);
        var skill = SharedSkill() with
        {
            Agents = [member, member, member with { IsSymlink = true }, member with { Scope = null },
                member with { DiscoveryRoot = "/root" }, member with { AgentVersion = "1" }, member with { Enabled = false },
                member with { Enabled = true }, member with { LoadingRule = "rule" },
                member with { Profile = "one" }, member with { Profile = "two" }]
        };
        // Null scope falls back to this copy's User scope, but stays distinct.
        skill = skill with { Scope = Scope.User };
        var text = InstalledScreen.RenderDetail(skill);
        Assert.Contains("1 physical copy · 10 recorded placements", text);
        Assert.Contains("`codex` ×8", text);
        Assert.Contains("`codex` (Profile: `one`)", text);
        Assert.Contains("`codex` (Profile: `two`)", text);
        Assert.DoesNotContain("LoadingRule", text);
        var snapshot = InventorySnapshot.Empty with { Skills = [skill] };
        Assert.Equal(10, InstalledOverview.Build(snapshot, TestContext.Current.CancellationToken).Placements);
    }

    [Fact]
    public void WholeMemberLimitRecomputesMultipliersAndKeepsTheTrueTotal()
    {
        var skill = SharedSkill() with { Agents = [.. Enumerable.Range(0, 130).Select(i => new AgentMembership("codex", "/physical", false, AgentVersion: i.ToString("D3")))] };
        var text = InstalledScreen.RenderDetail(skill);
        Assert.Contains("130 recorded placements", text);
        Assert.Contains("`codex` ×128", text);
        Assert.Contains("2 more recorded placements omitted", text);
        var reverse = skill with { Agents = [.. skill.Agents.Reverse()] };
        Assert.Equal(text, InstalledScreen.RenderDetail(reverse));
        var oneOmitted = skill with { Agents = [.. skill.Agents.Take(129)] };
        Assert.Contains("1 more recorded placement omitted", InstalledScreen.RenderDetail(oneOmitted));
    }

    [Fact]
    public void CharacterBudgetStopsAtAWholeEncodedMemberAndIncludesHeadersAndNotice()
    {
        var skill = SharedSkill() with { Agents = [.. Enumerable.Range(0, 40).Select(i => new AgentMembership(i.ToString("D2") + new string('`', 600), "/physical", false))] };
        var text = InstalledScreen.RenderPlacements(skill);
        Assert.True(text.Length <= InstalledScreen.MaxPlacementCharacters);
        var document = Parse(text);
        var included = document.Descendants<CodeInline>().Count() - 1; // One path header.
        Assert.InRange(included, 1, 39);
        Assert.Contains($"{40 - included} more recorded placements omitted", text);
        Assert.Equal(text, InstalledScreen.RenderPlacements(skill with { Agents = [.. skill.Agents.Reverse()] }));
        Assert.Empty(document.Descendants<LinkInline>());
        // An oversized first member is omitted completely, even if later ones fit.
        skill = skill with { Agents = [new(new string('a', 40000), "/physical", false), new("z", "/physical", false)] };
        text = InstalledScreen.RenderPlacements(skill);
        Assert.Contains("2 more recorded placements omitted", text);
        Assert.DoesNotContain("`z`", text);
        Assert.True(text.Length <= InstalledScreen.MaxPlacementCharacters);
    }

    [Fact]
    public void UnknownWidthDefersReflowAndOnlyTagRefsUseTheTagLabel()
    {
        var skill = SharedSkill();
        foreach (var width in new int?[] { null, 0, -1 })
            Assert.Equal(OrdinaryDescription, Assert.Single(Parse(InstalledScreen.RenderDetail(skill, descriptionWidth: width)).Descendants<FencedCodeBlock>()).Lines.ToString());
        var detail = InstalledScreen.RenderDetail(skill with { FrontMatter = skill.FrontMatter with { GithubRef = "refs/heads/main" } });
        Assert.Contains("Recorded ref `refs/heads/main`", detail);
        Assert.DoesNotContain("Git tag", detail);
    }

    [Fact]
    public void NormalizedLabelsCompressWithoutDeduplicatingStructuralObservations()
    {
        var skill = SharedSkill() with
        {
            Agents = [new("a\tb", "/p", false, Profile: "one\ttwo"),
            new("a b", "/p", false, Profile: "one two"), new("a b\u001b[0m", "/p", false, Profile: "one two")]
        };
        var text = InstalledScreen.RenderDetail(skill);
        Assert.Contains("3 recorded placements", text);
        Assert.Contains("`a b` (Profile: `one two`) ×3", text);
        Assert.Equal(text, InstalledScreen.RenderDetail(skill with { Agents = [.. skill.Agents.Reverse()] }));
    }

    private static MarkdownDocument Parse(string text) => Markdig.Markdown.Parse(text, new MarkdownPipelineBuilder().UseAdvancedExtensions().Build());
}
