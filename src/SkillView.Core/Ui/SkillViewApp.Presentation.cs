using System.Globalization;
using System.Text;
using SkillView.Gh;
using SkillView.Gh.Models;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace SkillView.Ui;

public sealed partial class SkillViewApp
{

    /// Sort modes for the Search tab results table. Mirrors winget-tui's
    /// app.sort_field cycle in src/app.rs. Off restores the natural ordering
    /// returned by `gh skill search` (which is itself relevance-ranked).
    internal enum SearchSort { Off, StarsDesc, NameAsc, NameDesc, RepoAsc }

    /// Sort the natural-order results into a new list per the active sort
    /// mode. Pure — extracted so the cycle behavior can be unit-tested.
    internal static List<SearchResultSkill> ApplySearchSort(
        IReadOnlyList<SearchResultSkill> source,
        SearchSort sort) => sort switch
        {
            SearchSort.StarsDesc => source
                .OrderByDescending(s => s.Stars ?? -1)
                .ThenBy(s => s.SkillName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            SearchSort.NameAsc => source
                .OrderBy(s => s.SkillName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            SearchSort.NameDesc => source
                .OrderByDescending(s => s.SkillName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            SearchSort.RepoAsc => source
                .OrderBy(s => s.Repo, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _ => source.ToList(),
        };

    internal static SearchSort CycleSearchSort(SearchSort current) => current switch
    {
        SearchSort.Off => SearchSort.StarsDesc,
        SearchSort.StarsDesc => SearchSort.NameAsc,
        SearchSort.NameAsc => SearchSort.NameDesc,
        SearchSort.NameDesc => SearchSort.RepoAsc,
        _ => SearchSort.Off,
    };

    internal static string DescribeSearchSort(SearchSort sort) => sort switch
    {
        SearchSort.StarsDesc => "sort: stars ↓",
        SearchSort.NameAsc => "sort: name ↑",
        SearchSort.NameDesc => "sort: name ↓",
        SearchSort.RepoAsc => "sort: repo ↑",
        _ => "sort: off (gh order)",
    };

    internal static string BuildRepoUrl(GhAuthStatus? auth, string? repo)
    {
        if (string.IsNullOrWhiteSpace(repo))
        {
            return string.Empty;
        }

        var host = GetRepoLinkHost(auth) ?? "github.com";
        return $"https://{host}/{repo.Trim()}";
    }

    internal static string RenderSearchMetadata(SearchResultSkill s, GhAuthStatus? auth)
    {
        var sb = new System.Text.StringBuilder();
        var repoUrl = BuildRepoUrl(auth, s.Repo);
        AppendSearchMetadataItem(sb, "Name", s.SkillName ?? "(unnamed)");
        AppendSearchMetadataItem(sb, "Repo", FormatRepoValue(s.Repo, repoUrl));
        if (s.Stars is { } st)
            AppendSearchMetadataItem(sb, "Stars", $"★ {st.ToString(CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrWhiteSpace(s.Path))
            AppendSearchMetadataItem(sb, "Path", s.Path);
        if (!string.IsNullOrWhiteSpace(s.Namespace))
            AppendSearchMetadataItem(sb, "Namespace", s.Namespace);
        return TerminalEscapeSanitizer.Sanitize(sb.ToString()) ?? string.Empty;
    }

    private static void AppendSearchMetadataItem(System.Text.StringBuilder sb, string label, string value)
    {
        sb.Append("- **");
        sb.Append(label);
        sb.Append(":** ");
        sb.AppendLine(value);
    }

    private static string BuildDiscoverPreviewBody(string? description, string previewText, bool includeDescription)
    {
        var trimmedPreview = previewText.Trim();
        var sanitizedDescription = TerminalEscapeSanitizer.Sanitize(description ?? string.Empty) ?? string.Empty;
        if (!includeDescription || string.IsNullOrWhiteSpace(sanitizedDescription))
        {
            return trimmedPreview;
        }

        var trimmedDescription = sanitizedDescription.Trim();
        if (trimmedPreview.Length == 0)
        {
            return trimmedDescription;
        }

        var sb = new StringBuilder(trimmedDescription.Length + trimmedPreview.Length + 8);
        sb.AppendLine(trimmedDescription);
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append(trimmedPreview);
        return sb.ToString();
    }

    private static string FormatRepoValue(string? repo, string repoUrl)
    {
        if (string.IsNullOrWhiteSpace(repo))
        {
            return "—";
        }

        var trimmedRepo = repo.Trim();
        if (string.IsNullOrEmpty(repoUrl))
        {
            return trimmedRepo;
        }

        return $"[{trimmedRepo}]({EscapeMarkdownLinkDestination(repoUrl)})";
    }

    private static string EscapeMarkdownLinkDestination(string value) =>
        value
            .Replace("%", "%25", StringComparison.Ordinal)
            .Replace(" ", "%20", StringComparison.Ordinal)
            .Replace("(", "%28", StringComparison.Ordinal)
            .Replace(")", "%29", StringComparison.Ordinal)
            .Replace("|", "%7C", StringComparison.Ordinal);

    internal static string DescribeSearchResults(int totalCount, int shownCount, string? requestedAgent)
    {
        var normalizedAgent = SearchAgentMetadataCache.NormalizeAgent(requestedAgent);
        if (normalizedAgent is null)
        {
            return shownCount == 0
                ? "no results"
                : $"{shownCount} result(s)";
        }

        return shownCount == 0
            ? $"no results for {normalizedAgent}"
            : $"{shownCount} of {totalCount} result(s) for {normalizedAgent}";
    }

    internal static bool ShouldAllowHiddenDirPreview(SearchResultSkill skill)
    {
        if (string.IsNullOrWhiteSpace(skill.Path))
        {
            return false;
        }

        return skill.Path
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.Length > 0 && segment[0] == '.');
    }

    internal static bool ShouldAllowHiddenDirs(SearchResultSkill skill, bool userEnabled) =>
        userEnabled || ShouldAllowHiddenDirPreview(skill);

    private static string? GetRepoLinkHost(GhAuthStatus? auth)
    {
        if (auth is not { LoggedIn: true })
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(auth.ActiveHost) ? null : auth.ActiveHost.Trim();
    }
}
