using System.Collections.Immutable;

namespace SkillView.Gh;

/// Pure flag-token scanner split out from the subprocess orchestrator so unit
/// tests can feed in fixed help-text fixtures. §11.3 mandates "flag-token
/// membership scans, not structural parsing of help text".
public static class CapabilityProbeParser
{
    /// Known flag tokens to probe per subcommand. Exactly the tokens listed in
    /// §11.3 — extend only when the PRD does.
    public static readonly ImmutableDictionary<string, ImmutableArray<string>> ProbedTokens =
        ImmutableDictionary<string, ImmutableArray<string>>.Empty
            .Add("install", ImmutableArray.Create(
                "--allow-hidden-dirs", "--upstream", "--agent", "--repo-path", "--from-local"))
            .Add("update", ImmutableArray.Create(
                "--dry-run", "--all", "--force", "--unpin", "--yes", "--non-interactive", "--json"))
            .Add("list", ImmutableArray.Create(
                "--json", "--agent", "--scope"))
            .Add("search", ImmutableArray.Create(
                "--json", "--owner", "--limit"))
            .Add("preview", ImmutableArray<string>.Empty);

    /// Scan `helpText` for each exact token in `candidates`. Tokens are matched
    /// at word boundaries so `--agent` does not accidentally match `--agents`.
    public static ImmutableHashSet<string> ScanTokens(string helpText, IEnumerable<string> candidates)
    {
        if (string.IsNullOrEmpty(helpText))
        {
            return ImmutableHashSet<string>.Empty;
        }

        var builder = ImmutableHashSet.CreateBuilder<string>();
        foreach (var token in candidates)
        {
            if (ContainsTokenAtBoundary(helpText, token))
            {
                builder.Add(token);
            }
        }
        return builder.ToImmutable();
    }

    /// True when `haystack` contains `token` bordered by non-flag characters on
    /// both sides (so `--agent` in `--agent stringArray` matches but `--agents`
    /// does not).
    public static bool ContainsTokenAtBoundary(string haystack, string token)
    {
        var index = 0;
        while (true)
        {
            var hit = haystack.IndexOf(token, index, StringComparison.Ordinal);
            if (hit < 0) return false;

            var end = hit + token.Length;
            var leftOk = hit == 0 || !IsFlagChar(haystack[hit - 1]);
            var rightOk = end == haystack.Length || !IsFlagChar(haystack[end]);
            if (leftOk && rightOk)
            {
                return true;
            }
            index = hit + 1;
        }
    }

    private static bool IsFlagChar(char c) =>
        char.IsLetterOrDigit(c) || c == '-' || c == '_';

    /// True when `helpText` looks like real help output (we treat non-empty
    /// help with the subcommand name or "Usage:" header as present). Keeps the
    /// probe tolerant to the exact help banner wording.
    public static bool LooksLikeHelpOutput(string helpText) =>
        !string.IsNullOrWhiteSpace(helpText) &&
        (helpText.Contains("Usage:", StringComparison.OrdinalIgnoreCase) ||
         helpText.Contains("Flags:", StringComparison.OrdinalIgnoreCase) ||
         helpText.Contains("Available commands", StringComparison.OrdinalIgnoreCase) ||
         helpText.Contains("--help", StringComparison.Ordinal));
}
