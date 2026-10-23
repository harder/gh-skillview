using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Ui;

namespace SkillView.Cli;

public static partial class CliDispatcher
{

    private static async Task<int> SearchAsync(
        AppOptions options,
        TuiServices services,
        CancellationToken cancellationToken)
    {
        var parsed = ParseSearchArgs(options.SubcommandArgs);
        if (parsed.Query is null)
        {
            Console.Error.WriteLine("skillview: search requires a query");
            Console.Error.WriteLine("usage: skillview search <query> [--owner <o>] [--limit <n>] [--json]");
            return ExitCodes.InvalidUsage;
        }

        var report = await services.EnvironmentProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!report.BaselineOk)
        {
            Console.Error.WriteLine("skillview: gh or gh skill not available (run `skillview doctor`)");
            return ExitCodes.EnvironmentError;
        }

        var response = await services.SearchService.SearchAsync(
            report.GhPath!,
            parsed.Query,
            new GhSkillSearchService.Options(
                Owner: parsed.Owner,
                Limit: parsed.Limit ?? GhSkillSearchService.DefaultLimit,
                Page: parsed.Page ?? 1),
            cancellationToken
        ).ConfigureAwait(false);

        if (!response.Succeeded)
        {
            Console.Error.WriteLine($"skillview: search failed (exit {response.ExitCode}): {response.ErrorMessage}");
            return ExitCodes.EnvironmentError;
        }

        if (parsed.Json) WriteSearchJson(response.Results, parsed);
        else WriteSearchText(response.Results);

        return response.Results.Count == 0 ? ExitCodes.NoMatches : ExitCodes.Success;
    }

    internal record ParsedSearchArgs(string? Query, string? Owner, int? Limit, int? Page, bool Json);

    internal static ParsedSearchArgs ParseSearchArgs(IReadOnlyList<string> args)
    {
        string? query = null, owner = null;
        int? limit = null, page = null;
        var json = false;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--json") { json = true; continue; }
            if (a.StartsWith("--owner=", StringComparison.Ordinal)) { owner = a["--owner=".Length..]; continue; }
            if (a == "--owner" && i + 1 < args.Count) { owner = args[++i]; continue; }
            if (a.StartsWith("--limit=", StringComparison.Ordinal) && int.TryParse(a["--limit=".Length..], out var l1)) { limit = l1; continue; }
            if (a == "--limit" && i + 1 < args.Count && int.TryParse(args[i + 1], out var l2)) { limit = l2; i++; continue; }
            if (a.StartsWith("--page=", StringComparison.Ordinal) && int.TryParse(a["--page=".Length..], out var p1)) { page = p1; continue; }
            if (a == "--page" && i + 1 < args.Count && int.TryParse(args[i + 1], out var p2)) { page = p2; i++; continue; }
            if (a.StartsWith("--", StringComparison.Ordinal)) continue;
            if (query is null) query = a;
        }
        return new ParsedSearchArgs(query, owner, limit, page, json);
    }

    private static void WriteSearchText(IReadOnlyList<SearchResultSkill> rows)
    {
        if (rows.Count == 0)
        {
            Console.Out.WriteLine("no matches");
            return;
        }
        var nameWidth = Math.Max(5, rows.Max(r => (r.SkillName ?? "").Length));
        var repoWidth = Math.Max(4, rows.Max(r => (r.Repo ?? "").Length));
        Console.Out.WriteLine($"{"SKILL".PadRight(nameWidth)}  {"REPO".PadRight(repoWidth)}  ★       DESCRIPTION");
        foreach (var r in rows)
        {
            var stars = r.Stars?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            Console.Out.WriteLine(
                $"{(r.SkillName ?? "").PadRight(nameWidth)}  {(r.Repo ?? "").PadRight(repoWidth)}  {stars,-6}  {r.Description ?? ""}");
        }
    }

    private static void WriteSearchJson(IReadOnlyList<SearchResultSkill> rows, ParsedSearchArgs parsed)
        => WriteJson(w => WriteSearchJson(w, rows, parsed));

    internal static string RenderSearchJson(IReadOnlyList<SearchResultSkill> rows, ParsedSearchArgs parsed)
        => RenderJson(w => WriteSearchJson(w, rows, parsed));

    private static void WriteSearchJson(
        Utf8JsonWriter w,
        IReadOnlyList<SearchResultSkill> rows,
        ParsedSearchArgs parsed)
    {
        w.WriteStartObject();
        w.WriteString("query", parsed.Query);
        w.WriteString("owner", parsed.Owner);
        w.WriteNumber("limit", parsed.Limit ?? GhSkillSearchService.DefaultLimit);
        if (parsed.Page is int pg) w.WriteNumber("page", pg);
        w.WriteStartArray("results");
        foreach (var r in rows)
        {
            w.WriteStartObject();
            w.WriteString("skillName", r.SkillName);
            w.WriteString("repo", r.Repo);
            w.WriteString("namespace", r.Namespace);
            w.WriteString("path", r.Path);
            w.WriteString("description", r.Description);
            if (r.Stars is int s) w.WriteNumber("stars", s);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }
}
