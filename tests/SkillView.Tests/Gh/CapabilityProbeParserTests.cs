using SkillView.Gh;
using Xunit;

namespace SkillView.Tests.Gh;

public class CapabilityProbeParserTests
{
    [Fact]
    public void ContainsTokenAtBoundary_matches_exact_flag()
    {
        const string help = "  --agent stringArray   Agent(s) to install for\n";
        Assert.True(CapabilityProbeParser.ContainsTokenAtBoundary(help, "--agent"));
    }

    [Fact]
    public void ContainsTokenAtBoundary_rejects_partial()
    {
        // `--agents` must not match the probed token `--agent`
        const string help = "  --agents stringArray\n";
        Assert.False(CapabilityProbeParser.ContainsTokenAtBoundary(help, "--agent"));
    }

    [Fact]
    public void ScanTokens_returns_only_present_flags()
    {
        const string installHelp = """
            Usage: gh skill install [flags]

            Flags:
              --agent stringArray     Agents
              --allow-hidden-dirs     Scan hidden
              --upstream string       Upstream choice
              -h, --help              help for install
            """;
        var tokens = CapabilityProbeParser.ProbedTokens["install"];
        var present = CapabilityProbeParser.ScanTokens(installHelp, tokens);
        Assert.Contains("--agent", present);
        Assert.Contains("--allow-hidden-dirs", present);
        Assert.Contains("--upstream", present);
        Assert.DoesNotContain("--repo-path", present);
        Assert.DoesNotContain("--from-local", present);
    }

    [Fact]
    public void ScanTokens_is_boundary_safe_for_non_flag_prefixes()
    {
        // Only `--json-lines` appears — the `--json` token must not match.
        const string help = "Comment mentioning --json-lines and nothing else.";
        var hits = CapabilityProbeParser.ScanTokens(help, new[] { "--json" });
        Assert.Empty(hits);
    }

    [Fact]
    public void LooksLikeHelpOutput_accepts_realistic_gh_help()
    {
        const string help = """
            Work with gh skill.

            Usage:
              gh skill <command>

            Flags:
              -h, --help
            """;
        Assert.True(CapabilityProbeParser.LooksLikeHelpOutput(help));
    }

    [Fact]
    public void LooksLikeHelpOutput_rejects_noise()
    {
        Assert.False(CapabilityProbeParser.LooksLikeHelpOutput(""));
        Assert.False(CapabilityProbeParser.LooksLikeHelpOutput("unknown command \"skill\""));
    }

    [Fact]
    public void ProbedTokens_match_prd_11_3()
    {
        // Sanity-check the static table against the PRD enumeration.
        Assert.Contains("--allow-hidden-dirs", CapabilityProbeParser.ProbedTokens["install"]);
        Assert.Contains("--repo-path", CapabilityProbeParser.ProbedTokens["install"]);
        Assert.Contains("--dry-run", CapabilityProbeParser.ProbedTokens["update"]);
        Assert.Contains("--unpin", CapabilityProbeParser.ProbedTokens["update"]);
        Assert.Contains("--json", CapabilityProbeParser.ProbedTokens["list"]);
        Assert.Contains("--owner", CapabilityProbeParser.ProbedTokens["search"]);
    }
}
