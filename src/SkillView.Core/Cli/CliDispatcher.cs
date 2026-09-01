using System.Collections.Immutable;
using System.IO;
using System.Text;
using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Diagnostics;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Threading;
using SkillView.Ui;
using Terminal.Gui.Views;

namespace SkillView.Cli;

/// Non-interactive subcommand router. Feature-complete through Phase 7:
/// `doctor`, `list`, `rescan`, `search`, `preview`, `install`, `update`,
/// `remove`, `cleanup`. JSON rendering and argv parsing are factored into
/// `internal` helpers for snapshot testing.
public static class CliDispatcher
{
    public static async Task<int> RunAsync(
        AppOptions options,
        TuiServices services,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var timeout = TimeoutFor(options.SubcommandName);
        using var deadline = new CancellationSource(
            cancellationToken,
            timeout,
            ex => services.Logger.Error(
                "cancellation",
                $"CLI deadline callback failed: {ex.Message}"));

        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            return options.SubcommandName switch
            {
                "doctor" => await DoctorAsync(options, services, deadline.Token).ConfigureAwait(false),
                "list" => await ListAsync(options, services, deadline.Token).ConfigureAwait(false),
                "rescan" => await RescanAsync(options, services, deadline.Token).ConfigureAwait(false),
                "search" => await SearchAsync(options, services, deadline.Token).ConfigureAwait(false),
                "preview" => await PreviewAsync(options, services, deadline.Token).ConfigureAwait(false),
                "install" => await InstallAsync(options, services, deadline.Token).ConfigureAwait(false),
                "update" => await UpdateAsync(options, services, deadline.Token).ConfigureAwait(false),
                "remove" => await RemoveAsync(options, services, deadline.Token).ConfigureAwait(false),
                "cleanup" => await CleanupAsync(options, services, deadline.Token).ConfigureAwait(false),
                "--help" or "-h" or "help" => PrintHelp(options),
                "--version" or "-V" => PrintVersion(options),
                _ => UnknownSubcommand(options.SubcommandName ?? "<null>", services.Logger),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                $"skillview: {options.SubcommandName ?? "operation"} timed out after {timeout.TotalSeconds:F0}s");
            return ExitCodes.EnvironmentError;
        }
    }

    internal static TimeSpan TimeoutFor(string? subcommand) => subcommand switch
    {
        "doctor" or "preview" => TimeSpan.FromSeconds(30),
        "list" or "rescan" or "search" => TimeSpan.FromMinutes(2),
        "install" or "update" or "remove" or "cleanup" => TimeSpan.FromMinutes(10),
        _ => TimeSpan.FromSeconds(30),
    };

    private static async Task<int> DoctorAsync(
        AppOptions options,
        TuiServices services,
        CancellationToken cancellationToken)
    {
        if (options.SubcommandArgs.Contains("--clear-logs"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ClearLogs(services, cancellationToken);
        }

        var report = await services.EnvironmentProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        var json = options.SubcommandArgs.Contains("--json");
        if (json)
        {
            WriteDoctorJson(report, options);
        }
        else
        {
            WriteDoctorText(report, options);
        }

        if (!report.GhFound)
        {
            return ExitCodes.EnvironmentError;
        }
        if (!report.GhMeetsMinimum)
        {
            return ExitCodes.EnvironmentError;
        }
        if (!report.GhSkillAvailable)
        {
            return ExitCodes.EnvironmentError;
        }
        return ExitCodes.Success;
    }

    private static void WriteDoctorText(EnvironmentReport r, AppOptions options)
        => Console.Out.Write(RenderDoctorText(r, options));

    internal static string RenderDoctorText(EnvironmentReport r, AppOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"invocation    : {options.InvocationMode}");
        sb.AppendLine($"gh path       : {r.GhPath ?? "(not found)"}");
        sb.AppendLine($"gh version    : {r.GhVersionRaw ?? "(unknown)"}");
        sb.AppendLine($"gh minimum    : {GhBinaryLocator.MinimumVersion}{(r.GhMeetsMinimum ? " ✓" : " ✗ too old")}");
        sb.AppendLine($"gh auth       : {AuthSummary(r.Auth)}");
        sb.AppendLine($"gh skill      : {(r.GhSkillAvailable ? "present (gh ≥ 2.95 — full skill surface)" : "(not detected)")}");
        sb.AppendLine($"debug         : {options.Debug}");
        sb.AppendLine($"log directory : {r.LogDirectory ?? "(unset)"}");
        sb.AppendLine($"scan roots    : {(options.ScanRoots.Count == 0 ? "(default)" : string.Join(", ", options.ScanRoots))}");
        sb.AppendLine($"baseline      : {(r.BaselineOk ? "ok" : "degraded")}");
        return sb.ToString();
    }

    private static string AuthSummary(GhAuthStatus auth)
    {
        if (!auth.LoggedIn)
        {
            return "not logged in";
        }
        var host = auth.ActiveHost ?? "?";
        var acct = auth.Account ?? "?";
        var others = auth.Hosts.Length > 1 ? $" (+{auth.Hosts.Length - 1} other host{(auth.Hosts.Length == 2 ? "" : "s")})" : string.Empty;
        return $"{acct}@{host}{others}";
    }

    private static void WriteDoctorJson(EnvironmentReport r, AppOptions options)
        => WriteJson(w => WriteDoctorJson(w, r, options));

    internal static string RenderDoctorJson(EnvironmentReport r, AppOptions options)
        => RenderJson(w => WriteDoctorJson(w, r, options));

    private static void WriteDoctorJson(Utf8JsonWriter writer, EnvironmentReport r, AppOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("invocation", options.InvocationMode.ToString());
        writer.WriteString("ghPath", r.GhPath);
        writer.WriteString("ghVersion", r.GhVersionRaw);
        writer.WriteString("ghVersionParsed", r.GhVersion?.ToString());
        writer.WriteString("ghMinimum", GhBinaryLocator.MinimumVersion.ToString());
        writer.WriteBoolean("ghMeetsMinimum", r.GhMeetsMinimum);
        writer.WriteBoolean("debug", options.Debug);
        writer.WriteString("logDirectory", r.LogDirectory);
        writer.WriteBoolean("baselineOk", r.BaselineOk);

        writer.WriteStartObject("auth");
        writer.WriteBoolean("loggedIn", r.Auth.LoggedIn);
        writer.WriteString("activeHost", r.Auth.ActiveHost);
        writer.WriteString("account", r.Auth.Account);
        writer.WriteStartArray("hosts");
        foreach (var h in r.Auth.Hosts) writer.WriteStringValue(h);
        writer.WriteEndArray();
        writer.WriteEndObject();

        // gh ≥ 2.95 is required, so the full `gh skill` flag surface is
        // guaranteed once the command is present — a single availability
        // bool replaces the old per-flag capability probe.
        writer.WriteBoolean("ghSkillAvailable", r.GhSkillAvailable);

        writer.WriteStartArray("scanRoots");
        foreach (var root in options.ScanRoots) writer.WriteStringValue(root);
        writer.WriteEndArray();

        writer.WriteEndObject();
    }


    private static int ClearLogs(TuiServices services, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (services.FileLogSink is null)
        {
            Console.Error.WriteLine("skillview: log sink not initialized; nothing to clear");
            return ExitCodes.EnvironmentError;
        }
        var count = services.FileLogSink.ClearAll(cancellationToken);
        Console.Out.WriteLine($"cleared {count} log file(s) from {services.LogDirectory}");
        return ExitCodes.Success;
    }

    private static async Task<int> RescanAsync(
        AppOptions options,
        TuiServices services,
        CancellationToken cancellationToken)
    {
        services.ListAdapter.Invalidate();
        var snapshot = await CaptureInventoryAsync(options, services, cancellationToken: cancellationToken).ConfigureAwait(false);
        var d = snapshot.Diagnostics;
        Console.Out.WriteLine($"rescan: {snapshot.Skills.Length} skill(s) across {snapshot.ScannedRoots.Length} root(s)" +
                              (snapshot.UsedGhSkillList ? " (gh skill list used)" : " (filesystem only)"));
        Console.Out.WriteLine($"  scan: {d.FsScanDuration.TotalMilliseconds:F0}ms" +
            (snapshot.UsedGhSkillList ? $", gh list: {d.GhListDuration.TotalMilliseconds:F0}ms" : "") +
            (d.BrokenSymlinksFound > 0 ? $", {d.BrokenSymlinksFound} broken symlink(s)" : ""));
        return ExitCodes.Success;
    }

    private static async Task<int> ListAsync(
        AppOptions options,
        TuiServices services,
        CancellationToken cancellationToken)
    {
        var listOptions = ParseListArgs(options.SubcommandArgs, out var json);
        var snapshot = await CaptureInventoryAsync(options, services, listOptions, cancellationToken).ConfigureAwait(false);

        if (json)
        {
            WriteListJson(snapshot);
        }
        else
        {
            WriteListText(snapshot);
        }

        if (snapshot.Skills.Length == 0) return ExitCodes.NoMatches;
        return ExitCodes.Success;
    }

    internal static (string? scope, string? agent, string? path, List<string> scanRoots) ParseListArgs(
        IReadOnlyList<string> args, out bool json)
    {
        json = false;
        string? scope = null, agent = null, path = null;
        var scanRoots = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--json") { json = true; continue; }
            if (a.StartsWith("--scope=", StringComparison.Ordinal)) { scope = a["--scope=".Length..]; continue; }
            if (a == "--scope" && i + 1 < args.Count) { scope = args[++i]; continue; }
            if (a.StartsWith("--agent=", StringComparison.Ordinal)) { agent = a["--agent=".Length..]; continue; }
            if (a == "--agent" && i + 1 < args.Count) { agent = args[++i]; continue; }
            if (a.StartsWith("--dir=", StringComparison.Ordinal)) { path = a["--dir=".Length..]; continue; }
            if (a == "--dir" && i + 1 < args.Count) { path = args[++i]; continue; }
            if (a == "--allow-hidden-dirs") { /* handled via inventory.CaptureAsync */ continue; }
        }
        if (!string.IsNullOrEmpty(path)) scanRoots.Add(path!);
        return (scope, agent, path, scanRoots);
    }

    private static async Task<Inventory.Models.InventorySnapshot>
        CaptureInventoryAsync(
            AppOptions options,
            TuiServices services,
            (string? scope, string? agent, string? path, List<string> scanRoots)? listOptions = null,
            CancellationToken cancellationToken = default)
    {
        var report = await services.EnvironmentProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        var scanRoots = new List<string>(options.ScanRoots);
        string? scope = null, agent = null;
        var allowHidden = options.SubcommandArgs.Contains("--allow-hidden-dirs");
        if (listOptions is { } lo)
        {
            scope = lo.scope;
            agent = lo.agent;
            foreach (var extra in lo.scanRoots)
            {
                if (!scanRoots.Contains(extra)) scanRoots.Add(extra);
            }
        }
        return await services.InventoryService.CaptureAsync(
            report.GhPath,
            new Inventory.LocalInventoryService.Options(
                scanRoots,
                allowHidden,
                FilterScope: scope,
                FilterAgent: agent)
        , cancellationToken).ConfigureAwait(false);
    }

    private static void WriteListText(Inventory.Models.InventorySnapshot snapshot)
    {
        if (snapshot.Skills.Length == 0)
        {
            Console.Out.WriteLine("no skills found");
            return;
        }
        var nameWidth = Math.Max(4, snapshot.Skills.Max(s => s.Name.Length));
        Console.Out.WriteLine($"{"NAME".PadRight(nameWidth)}  SCOPE     PROV   FLAGS  PATH");
        foreach (var skill in snapshot.Skills)
        {
            var flags = FormatFlags(skill);
            Console.Out.WriteLine(
                $"{skill.Name.PadRight(nameWidth)}  {skill.Scope,-8}  {skill.Provenance,-5}  {flags,-5}  {skill.ResolvedPath}");
        }
    }

    private static string FormatFlags(InstalledSkill s)
    {
        Span<char> flags = stackalloc char[5];
        flags[0] = s.Pinned ? 'p' : '-';
        flags[1] = s.IsSymlinked ? 's' : '-';
        flags[2] = s.Ignored ? 'i' : '-';
        flags[3] = s.Validity == ValidityState.Valid ? '-' : '!';
        flags[4] = s.TreeSha is null ? '-' : 't';
        return new string(flags);
    }

    private static void WriteListJson(Inventory.Models.InventorySnapshot snapshot)
        => WriteJson(w => WriteListJson(w, snapshot));

    internal static string RenderListJson(Inventory.Models.InventorySnapshot snapshot)
        => RenderJson(w => WriteListJson(w, snapshot));

    private static void WriteListJson(Utf8JsonWriter w, Inventory.Models.InventorySnapshot snapshot)
    {
        w.WriteStartObject();
        w.WriteString("capturedAt", snapshot.CapturedAt.ToString("O"));
        w.WriteBoolean("usedGhSkillList", snapshot.UsedGhSkillList);

        w.WriteStartArray("scannedRoots");
        foreach (var r in snapshot.ScannedRoots)
        {
            w.WriteStartObject();
            w.WriteString("path", r.Path);
            w.WriteString("scope", r.Scope.ToString());
            w.WriteString("agentHint", r.AgentHint);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("skills");
        foreach (var skill in snapshot.Skills)
        {
            w.WriteStartObject();
            w.WriteString("name", skill.Name);
            w.WriteString("resolvedPath", skill.ResolvedPath);
            w.WriteString("scanRoot", skill.ScanRoot);
            w.WriteString("scope", skill.Scope.ToString());
            w.WriteString("provenance", skill.Provenance.ToString());
            w.WriteString("validity", skill.Validity.ToString());
            w.WriteBoolean("pinned", skill.Pinned);
            w.WriteBoolean("isSymlinked", skill.IsSymlinked);
            w.WriteBoolean("ignored", skill.Ignored);
            w.WriteString("githubTreeSha", skill.TreeSha);
            w.WriteString("version", skill.FrontMatter.Version);
            w.WriteString("description", skill.FrontMatter.Description);
            w.WriteStartArray("agents");
            foreach (var a in skill.Agents)
            {
                w.WriteStartObject();
                w.WriteString("id", a.AgentId);
                w.WriteString("path", a.Path);
                w.WriteBoolean("isSymlink", a.IsSymlink);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteEndObject();
    }

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
        if (!report.GhFound || !report.GhMeetsMinimum || !report.GhSkillAvailable)
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

    private static async Task<int> PreviewAsync(
        AppOptions options,
        TuiServices services,
        CancellationToken cancellationToken)
    {
        var parsed = ParsePreviewArgs(options.SubcommandArgs);
        if (parsed.Repo is null)
        {
            Console.Error.WriteLine("skillview: preview requires a repo (OWNER/REPO)");
            Console.Error.WriteLine("usage: skillview preview <owner/repo> [<skill-name>] [--version <ref>] [--allow-hidden-dirs] [--rendered] [--json]");
            return ExitCodes.InvalidUsage;
        }

        var report = await services.EnvironmentProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!report.GhFound || !report.GhMeetsMinimum || !report.GhSkillAvailable)
        {
            Console.Error.WriteLine("skillview: gh or gh skill not available (run `skillview doctor`)");
            return ExitCodes.EnvironmentError;
        }

        var preview = await services.PreviewService.PreviewAsync(
            report.GhPath!,
            parsed.Repo,
            parsed.SkillName,
            parsed.Version,
            parsed.AllowHiddenDirs,
            cancellationToken
        ).ConfigureAwait(false);

        if (!preview.Succeeded)
        {
            Console.Error.WriteLine($"skillview: preview failed (exit {preview.ExitCode}): {preview.ErrorMessage}");
            return ExitCodes.EnvironmentError;
        }

        if (parsed.Json) WritePreviewJson(preview);
        else Console.Out.WriteLine(RenderPreviewText(preview, parsed.Rendered));

        return ExitCodes.Success;
    }

    internal record ParsedPreviewArgs(string? Repo, string? SkillName, string? Version, bool AllowHiddenDirs, bool Rendered, bool Json);

    internal static ParsedPreviewArgs ParsePreviewArgs(IReadOnlyList<string> args)
    {
        string? repo = null, skill = null, version = null;
        var allowHiddenDirs = false;
        var rendered = false;
        var json = false;
        var positional = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--json") { json = true; continue; }
            if (a == "--allow-hidden-dirs") { allowHiddenDirs = true; continue; }
            if (a == "--rendered") { rendered = true; continue; }
            if (a.StartsWith("--version=", StringComparison.Ordinal)) { version = a["--version=".Length..]; continue; }
            if (a == "--version" && i + 1 < args.Count) { version = args[++i]; continue; }
            if (a.StartsWith("--", StringComparison.Ordinal)) continue;
            positional.Add(a);
        }
        if (positional.Count > 0) repo = positional[0];
        if (positional.Count > 1) skill = positional[1];
        // Allow `owner/repo@ref` shorthand in the first positional.
        if (repo is not null && version is null)
        {
            var at = repo.LastIndexOf('@');
            if (at > 0 && at < repo.Length - 1)
            {
                version = repo[(at + 1)..];
                repo = repo[..at];
            }
        }
        return new ParsedPreviewArgs(repo, skill, version, allowHiddenDirs, rendered, json);
    }

    private static void WritePreviewJson(PreviewResult p)
        => WriteJson(w => WritePreviewJson(w, p));

    internal static string RenderPreviewText(PreviewResult preview, bool rendered)
    {
        if (!rendered)
        {
            return preview.Body.TrimEnd();
        }

        var markdown = new Markdown();
        var body = markdown.RenderToAnsi(preview.MarkdownBody, 80).TrimEnd();
        if (preview.AssociatedFiles.Length == 0)
        {
            return body;
        }

        var sb = new StringBuilder(body.Length + 64);
        if (body.Length > 0)
        {
            sb.AppendLine(body);
            sb.AppendLine();
        }

        sb.AppendLine("Associated files:");
        foreach (var file in preview.AssociatedFiles)
        {
            sb.Append("- ");
            sb.AppendLine(file);
        }

        return sb.ToString().TrimEnd();
    }

    internal static string RenderPreviewJson(PreviewResult p)
        => RenderJson(w => WritePreviewJson(w, p));

    private static void WritePreviewJson(Utf8JsonWriter w, PreviewResult p)
    {
        w.WriteStartObject();
        w.WriteString("repo", p.Repo);
        w.WriteString("skillName", p.SkillName);
        w.WriteString("version", p.Version);
        w.WriteString("markdown", p.MarkdownBody);
        w.WriteStartArray("associatedFiles");
        foreach (var f in p.AssociatedFiles) w.WriteStringValue(f);
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static async Task<int> InstallAsync(
        AppOptions options,
        TuiServices services,
        CancellationToken cancellationToken)
    {
        var parsed = ParseInstallArgs(options.SubcommandArgs);
        if (parsed.Repo is null)
        {
            Console.Error.WriteLine("skillview: install requires a repo (OWNER/REPO)");
            Console.Error.WriteLine(
                "usage: skillview install <owner/repo>[@<ref>] [<skill>|--all] [--agent <id>]..." +
                " [--scope project|user|custom] [--path <dir>] [--version <ref>] [--pin]" +
                " [--force] [--upstream <url>] [--from-local]" +
                " [--allow-hidden-dirs] [--json]");
            return ExitCodes.InvalidUsage;
        }

        var report = await services.EnvironmentProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!report.GhFound || !report.GhMeetsMinimum || !report.GhSkillAvailable)
        {
            Console.Error.WriteLine("skillview: gh or gh skill not available (run `skillview doctor`)");
            return ExitCodes.EnvironmentError;
        }

        var installOptions = new GhSkillInstallService.Options(
            Agents: parsed.Agents,
            Scope: parsed.Scope,
            Path: parsed.Path,
            Version: parsed.Version,
            Pin: parsed.Pin,
            Overwrite: parsed.Force,
            Upstream: parsed.Upstream,
            AllowHiddenDirs: parsed.AllowHiddenDirs,
            FromLocal: parsed.FromLocal,
            All: parsed.All);

        // Snapshot pre-install so we can surface the post-install diff.
        var preSnapshot = await services.InventoryService.CaptureAsync(
            report.GhPath,
            new Inventory.LocalInventoryService.Options(
                ScanRoots: options.ScanRoots,
                AllowHiddenDirs: parsed.AllowHiddenDirs),
            cancellationToken
        ).ConfigureAwait(false);

        var result = await services.InstallService.InstallAsync(
            report.GhPath!,
            parsed.Repo,
            parsed.SkillName,
            installOptions,
            cancellationToken
        ).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            if (parsed.Json) WriteInstallJson(result, parsed, added: Array.Empty<InstalledSkill>());
            else
            {
                Console.Error.WriteLine($"skillview: install failed (exit {result.ExitCode}): {result.ErrorMessage}");
                if (!string.IsNullOrWhiteSpace(result.StdErr)) Console.Error.WriteLine(result.StdErr.TrimEnd());
            }
            return result.ExitCode == 0 ? ExitCodes.UserError : ExitCodes.EnvironmentError;
        }

        services.ListAdapter.Invalidate();
        var postSnapshot = await services.InventoryService.CaptureAsync(
            report.GhPath,
            new Inventory.LocalInventoryService.Options(
                ScanRoots: options.ScanRoots,
                AllowHiddenDirs: parsed.AllowHiddenDirs),
            cancellationToken
        ).ConfigureAwait(false);
        var added = InventoryDiff(preSnapshot, postSnapshot);

        if (parsed.Json) WriteInstallJson(result, parsed, added);
        else WriteInstallText(result, added);

        return ExitCodes.Success;
    }

    internal record ParsedInstallArgs(
        string? Repo,
        string? SkillName,
        string? Version,
        List<string> Agents,
        string? Scope,
        string? Path,
        bool Pin,
        bool Force,
        string? Upstream,
        bool FromLocal,
        bool AllowHiddenDirs,
        bool Json,
        bool All);

    internal static ParsedInstallArgs ParseInstallArgs(IReadOnlyList<string> args)
    {
        string? version = null, scope = null, path = null, upstream = null;
        var agents = new List<string>();
        var positional = new List<string>();
        bool pin = false, force = false, fromLocal = false, allowHidden = false, json = false, all = false;

        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--json") { json = true; continue; }
            if (a == "--all") { all = true; continue; }
            if (a == "--pin") { pin = true; continue; }
            if (a == "--force" || a == "--overwrite") { force = true; continue; }
            if (a == "--from-local") { fromLocal = true; continue; }
            if (a == "--allow-hidden-dirs") { allowHidden = true; continue; }
            if (a.StartsWith("--version=", StringComparison.Ordinal)) { version = a["--version=".Length..]; continue; }
            if (a == "--version" && i + 1 < args.Count) { version = args[++i]; continue; }
            if (a.StartsWith("--agent=", StringComparison.Ordinal)) { agents.Add(a["--agent=".Length..]); continue; }
            if (a == "--agent" && i + 1 < args.Count) { agents.Add(args[++i]); continue; }
            if (a.StartsWith("--scope=", StringComparison.Ordinal)) { scope = a["--scope=".Length..]; continue; }
            if (a == "--scope" && i + 1 < args.Count) { scope = args[++i]; continue; }
            if (a.StartsWith("--path=", StringComparison.Ordinal)) { path = a["--path=".Length..]; continue; }
            if (a == "--path" && i + 1 < args.Count) { path = args[++i]; continue; }
            if (a.StartsWith("--upstream=", StringComparison.Ordinal)) { upstream = a["--upstream=".Length..]; continue; }
            if (a == "--upstream" && i + 1 < args.Count) { upstream = args[++i]; continue; }
            if (a.StartsWith("--", StringComparison.Ordinal)) continue;
            positional.Add(a);
        }

        string? repo = positional.Count > 0 ? positional[0] : null;
        string? skill = positional.Count > 1 ? positional[1] : null;

        // `owner/repo@ref` shorthand in first positional.
        if (repo is not null && version is null)
        {
            var at = repo.LastIndexOf('@');
            if (at > 0 && at < repo.Length - 1)
            {
                version = repo[(at + 1)..];
                repo = repo[..at];
            }
        }

        return new ParsedInstallArgs(
            repo, skill, version, agents, scope, path, pin, force,
            upstream, fromLocal, allowHidden, json, all);
    }

    private static async Task<int> UpdateAsync(
        AppOptions options,
        TuiServices services,
        CancellationToken cancellationToken)
    {
        var parsed = ParseUpdateArgs(options.SubcommandArgs);
        var report = await services.EnvironmentProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!report.GhFound || !report.GhMeetsMinimum || !report.GhSkillAvailable)
        {
            Console.Error.WriteLine("skillview: gh or gh skill not available (run `skillview doctor`)");
            return ExitCodes.EnvironmentError;
        }

        if (!parsed.All && parsed.Skills.Count == 0 && !parsed.DryRun)
        {
            Console.Error.WriteLine("skillview: update requires at least one skill, --all, or --dry-run");
            Console.Error.WriteLine(
                "usage: skillview update [<skill>]... [--all] [--dry-run] [--force] [--unpin]" +
                " [--json]");
            return ExitCodes.InvalidUsage;
        }

        // Pre-snapshot for diffing: additions + version changes.
        var preSnapshot = await services.InventoryService.CaptureAsync(
            report.GhPath,
            new Inventory.LocalInventoryService.Options(
                ScanRoots: options.ScanRoots,
                AllowHiddenDirs: false),
            cancellationToken
        ).ConfigureAwait(false);

        var updateOptions = new GhSkillUpdateService.Options(
            Skills: parsed.Skills,
            All: parsed.All,
            DryRun: parsed.DryRun,
            Force: parsed.Force,
            Unpin: parsed.Unpin);

        var result = await services.UpdateService.UpdateAsync(
            report.GhPath!,
            updateOptions,
            cancellationToken
        ).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            if (parsed.Json) WriteUpdateJson(result, parsed, Array.Empty<InstalledSkill>(), Array.Empty<UpdateDiffEntry>());
            else
            {
                Console.Error.WriteLine($"skillview: update failed (exit {result.ExitCode}): {result.ErrorMessage}");
                if (!string.IsNullOrWhiteSpace(result.StdErr)) Console.Error.WriteLine(result.StdErr.TrimEnd());
            }
            return result.ExitCode == 0 ? ExitCodes.UserError : ExitCodes.EnvironmentError;
        }

        // For dry-run we don't re-scan (no mutation); skip the diff.
        IReadOnlyList<InstalledSkill> added;
        IReadOnlyList<UpdateDiffEntry> changed;
        if (parsed.DryRun)
        {
            added = Array.Empty<InstalledSkill>();
            changed = Array.Empty<UpdateDiffEntry>();
        }
        else
        {
            services.ListAdapter.Invalidate();
            var postSnapshot = await services.InventoryService.CaptureAsync(
                report.GhPath,
                new Inventory.LocalInventoryService.Options(
                    ScanRoots: options.ScanRoots,
                    AllowHiddenDirs: false),
                cancellationToken
            ).ConfigureAwait(false);
            added = InventoryDiff(preSnapshot, postSnapshot);
            changed = InventoryUpdateDiff(preSnapshot, postSnapshot);
        }

        if (parsed.Json) WriteUpdateJson(result, parsed, added, changed);
        else WriteUpdateText(result, parsed, added, changed);

        return ExitCodes.Success;
    }

    internal record ParsedUpdateArgs(
        List<string> Skills,
        bool All,
        bool DryRun,
        bool Force,
        bool Unpin,
        bool Json);

    internal static ParsedUpdateArgs ParseUpdateArgs(IReadOnlyList<string> args)
    {
        var skills = new List<string>();
        bool all = false, dryRun = false, force = false, unpin = false, json = false;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            // `--json` selects SkillView's own machine-readable output; it is
            // not passed to `gh skill update` (which has no --json flag).
            if (a == "--json") { json = true; continue; }
            if (a == "--all") { all = true; continue; }
            if (a == "--dry-run") { dryRun = true; continue; }
            if (a == "--force") { force = true; continue; }
            if (a == "--unpin") { unpin = true; continue; }
            if (a.StartsWith("--", StringComparison.Ordinal)) continue;
            skills.Add(a);
        }
        return new ParsedUpdateArgs(skills, all, dryRun, force, unpin, json);
    }

    /// Per-skill TreeSha delta: skills present at the same ResolvedPath in
    /// both snapshots where `TreeSha` changed. Captures the "updated" axis
    /// the install diff (additions only) doesn't — Phase 4 carry-forward.
    internal static IReadOnlyList<UpdateDiffEntry> InventoryUpdateDiff(
        Inventory.Models.InventorySnapshot before,
        Inventory.Models.InventorySnapshot after)
    {
        var beforeIndex = new Dictionary<string, InstalledSkill>(StringComparer.Ordinal);
        foreach (var s in before.Skills) beforeIndex[PathIdentity.NormalizeKey(s.ResolvedPath)] = s;

        var changed = new List<UpdateDiffEntry>();
        foreach (var a in after.Skills)
        {
            if (!beforeIndex.TryGetValue(PathIdentity.NormalizeKey(a.ResolvedPath), out var b)) continue;
            var fromSha = b.TreeSha;
            var toSha = a.TreeSha;
            var fromVer = b.FrontMatter.Version;
            var toVer = a.FrontMatter.Version;
            if (!string.Equals(fromSha, toSha, StringComparison.Ordinal) ||
                !string.Equals(fromVer, toVer, StringComparison.Ordinal))
            {
                changed.Add(new UpdateDiffEntry(a.Name, a.ResolvedPath, fromVer, toVer, fromSha, toSha));
            }
        }
        return changed;
    }

    internal sealed record UpdateDiffEntry(
        string Name,
        string ResolvedPath,
        string? FromVersion,
        string? ToVersion,
        string? FromSha,
        string? ToSha);

    private static void WriteUpdateText(
        UpdateResult r, ParsedUpdateArgs p,
        IReadOnlyList<InstalledSkill> added,
        IReadOnlyList<UpdateDiffEntry> changed)
    {
        var header = p.DryRun ? "dry-run" : "update";
        Console.Out.WriteLine($"{header}: exit {r.ExitCode}");
        if (!string.IsNullOrWhiteSpace(r.StdOut)) Console.Out.WriteLine(r.StdOut.TrimEnd());

        if (r.Entries.Length > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("parsed entries:");
            foreach (var e in r.Entries)
            {
                var from = e.FromVersion ?? "?";
                var to = e.ToVersion ?? "?";
                Console.Out.WriteLine($"  {e.Name,-30}  {e.Status,-11}  {from} → {to}");
            }
        }

        // gh 2.94 skips skills without GitHub metadata under --all / non-
        // interactive mode (cli/cli#13469). Surface the count so it's not
        // mistaken for silent success.
        var skipped = r.Entries.Count(e => e.Status == "skipped");
        if (skipped > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine($"note: {skipped} skill(s) skipped (no GitHub metadata — install via gh to enable updates)");
        }

        if (p.DryRun) return;

        if (added.Count > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine($"rescan: +{added.Count} new skill(s)");
            foreach (var s in added)
                Console.Out.WriteLine($"  +  {s.Name,-24}  {s.Scope,-7}  {s.ResolvedPath}");
        }
        if (changed.Count > 0)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine($"rescan: Δ{changed.Count} changed skill(s)");
            foreach (var c in changed)
            {
                var from = c.FromVersion ?? c.FromSha ?? "?";
                var to = c.ToVersion ?? c.ToSha ?? "?";
                Console.Out.WriteLine($"  Δ  {c.Name,-24}  {from} → {to}  {c.ResolvedPath}");
            }
        }
        if (added.Count == 0 && changed.Count == 0)
        {
            Console.Out.WriteLine("rescan: no inventory changes detected");
        }
    }

    private static void WriteUpdateJson(
        UpdateResult r, ParsedUpdateArgs p,
        IReadOnlyList<InstalledSkill> added,
        IReadOnlyList<UpdateDiffEntry> changed)
        => WriteJson(w => WriteUpdateJson(w, r, p, added, changed));

    internal static string RenderUpdateJson(
        UpdateResult r, ParsedUpdateArgs p,
        IReadOnlyList<InstalledSkill> added,
        IReadOnlyList<UpdateDiffEntry> changed)
        => RenderJson(w => WriteUpdateJson(w, r, p, added, changed));

    private static void WriteUpdateJson(
        Utf8JsonWriter w,
        UpdateResult r,
        ParsedUpdateArgs p,
        IReadOnlyList<InstalledSkill> added,
        IReadOnlyList<UpdateDiffEntry> changed)
    {
        w.WriteStartObject();
        w.WriteBoolean("dryRun", r.DryRun);
        w.WriteBoolean("succeeded", r.Succeeded);
        w.WriteNumber("exitCode", r.ExitCode);
        if (r.ErrorMessage is not null) w.WriteString("errorMessage", r.ErrorMessage);
        w.WriteBoolean("all", p.All);
        w.WriteBoolean("force", p.Force);
        w.WriteBoolean("unpin", p.Unpin);

        w.WriteStartArray("skills");
        foreach (var s in p.Skills) w.WriteStringValue(s);
        w.WriteEndArray();

        w.WriteStartArray("commandLine");
        foreach (var arg in r.CommandLine) w.WriteStringValue(arg);
        w.WriteEndArray();

        w.WriteStartArray("entries");
        foreach (var e in r.Entries)
        {
            w.WriteStartObject();
            w.WriteString("name", e.Name);
            w.WriteString("status", e.Status);
            w.WriteString("fromVersion", e.FromVersion);
            w.WriteString("toVersion", e.ToVersion);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("added");
        foreach (var s in added)
        {
            w.WriteStartObject();
            w.WriteString("name", s.Name);
            w.WriteString("resolvedPath", s.ResolvedPath);
            w.WriteString("scope", s.Scope.ToString());
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("changed");
        foreach (var c in changed)
        {
            w.WriteStartObject();
            w.WriteString("name", c.Name);
            w.WriteString("resolvedPath", c.ResolvedPath);
            w.WriteString("fromVersion", c.FromVersion);
            w.WriteString("toVersion", c.ToVersion);
            w.WriteString("fromSha", c.FromSha);
            w.WriteString("toSha", c.ToSha);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteEndObject();
    }

    internal static IReadOnlyList<InstalledSkill> InventoryDiff(
        Inventory.Models.InventorySnapshot before,
        Inventory.Models.InventorySnapshot after)
    {
        var beforeKeys = new HashSet<string>(
            before.Skills.Select(s => PathIdentity.NormalizeKey(s.ResolvedPath)),
            StringComparer.Ordinal);
        var added = new List<InstalledSkill>();
        foreach (var s in after.Skills)
        {
            if (!beforeKeys.Contains(PathIdentity.NormalizeKey(s.ResolvedPath))) added.Add(s);
        }
        return added;
    }

    private static void WriteInstallText(InstallResult r, IReadOnlyList<InstalledSkill> added)
    {
        Console.Out.WriteLine($"installed: {r.Repo}{(r.SkillName is null ? "" : "/" + r.SkillName)}" +
                              (r.Version is null ? "" : $"@{r.Version}"));
        if (!string.IsNullOrWhiteSpace(r.StdOut)) Console.Out.WriteLine(r.StdOut.TrimEnd());
        if (added.Count == 0)
        {
            Console.Out.WriteLine("rescan: no new inventory entries detected");
        }
        else
        {
            Console.Out.WriteLine($"rescan: +{added.Count} new skill(s):");
            foreach (var s in added)
            {
                Console.Out.WriteLine($"  {s.Name,-24}  {s.Scope,-7}  {s.ResolvedPath}");
            }
        }
    }

    private static void WriteInstallJson(InstallResult r, ParsedInstallArgs p, IReadOnlyList<InstalledSkill> added)
        => WriteJson(w => WriteInstallJson(w, r, p, added));

    internal static string RenderInstallJson(InstallResult r, ParsedInstallArgs p, IReadOnlyList<InstalledSkill> added)
        => RenderJson(w => WriteInstallJson(w, r, p, added));

    private static void WriteInstallJson(
        Utf8JsonWriter w,
        InstallResult r,
        ParsedInstallArgs p,
        IReadOnlyList<InstalledSkill> added)
    {
        w.WriteStartObject();
        w.WriteString("repo", r.Repo);
        w.WriteString("skillName", r.SkillName);
        w.WriteString("version", r.Version);
        w.WriteBoolean("succeeded", r.Succeeded);
        w.WriteNumber("exitCode", r.ExitCode);
        if (r.ErrorMessage is not null) w.WriteString("errorMessage", r.ErrorMessage);

        w.WriteStartArray("agents");
        foreach (var a in p.Agents) w.WriteStringValue(a);
        w.WriteEndArray();
        w.WriteString("scope", p.Scope);
        w.WriteString("path", p.Path);
        w.WriteBoolean("pin", p.Pin);
        w.WriteBoolean("force", p.Force);
        w.WriteBoolean("all", p.All);

        w.WriteStartArray("commandLine");
        foreach (var arg in r.CommandLine) w.WriteStringValue(arg);
        w.WriteEndArray();

        w.WriteStartArray("added");
        foreach (var s in added)
        {
            w.WriteStartObject();
            w.WriteString("name", s.Name);
            w.WriteString("resolvedPath", s.ResolvedPath);
            w.WriteString("scope", s.Scope.ToString());
            w.WriteStartArray("agents");
            foreach (var ag in s.Agents) w.WriteStringValue(ag.AgentId);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteEndObject();
    }

    private static async Task<int> RemoveAsync(
        AppOptions options,
        TuiServices services,
        CancellationToken cancellationToken)
    {
        var parsed = ParseRemoveArgs(options.SubcommandArgs);
        if (parsed.Name is null)
        {
            Console.Error.WriteLine("skillview: remove requires a skill name");
            Console.Error.WriteLine("usage: skillview remove <name> [--agent <id>] [--yes] [--json]");
            return ExitCodes.InvalidUsage;
        }

        var report = await services.EnvironmentProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await services.InventoryService.CaptureAsync(
            report.GhPath,
            new Inventory.LocalInventoryService.Options(
                ScanRoots: options.ScanRoots,
                AllowHiddenDirs: false),
            cancellationToken
        ).ConfigureAwait(false);

        var matches = snapshot.Skills
            .Where(s => string.Equals(s.Name, parsed.Name, StringComparison.OrdinalIgnoreCase))
            .Where(s => parsed.Agent is null || s.Agents.Any(a => string.Equals(a.AgentId, parsed.Agent, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (matches.Count == 0)
        {
            Console.Error.WriteLine($"skillview: no installed skill named '{parsed.Name}'" +
                (parsed.Agent is null ? "" : $" for agent '{parsed.Agent}'"));
            return ExitCodes.NoMatches;
        }
        if (matches.Count > 1)
        {
            Console.Error.WriteLine($"skillview: {matches.Count} skills match '{parsed.Name}' — narrow with --agent");
            foreach (var m in matches) Console.Error.WriteLine($"  · {m.ResolvedPath} ({string.Join(",", m.Agents.Select(a => a.AgentId))})");
            return ExitCodes.UserError;
        }

        var target = matches[0];
        var validation = parsed.Yes
            ? RemoveValidator.Validate(target, snapshot.ScannedRoots, snapshot.Skills)
            : RemoveValidator.ValidateForPreview(target, snapshot.ScannedRoots, snapshot.Skills);

        RemoveService.RemoveReport result;
        if (!validation.Allowed)
        {
            result = new RemoveService.RemoveReport(
                Succeeded: false,
                ResolvedPath: validation.ResolvedPath,
                FilesDeleted: 0,
                DirectoriesDeleted: 0,
                Errors: validation.Errors.Select(e => $"{e.Kind}: {e.Detail}").ToImmutableArray(),
                DryRun: false);
        }
        else if (!parsed.Yes)
        {
            result = await services.RemoveService.RemoveAsync(
                validation,
                new RemoveService.Options(DryRun: true),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            result = await services.RemoveService.RemoveAsync(
                validation,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
            {
                services.ListAdapter.Invalidate();
            }
        }

        if (parsed.Json) WriteRemoveJson(result, target, parsed, validation);
        else WriteRemoveText(result, target, parsed, validation);

        if (!validation.Allowed)
        {
            return IsEnvironmentRefusal(validation)
                ? ExitCodes.EnvironmentError
                : ExitCodes.UserError;
        }
        if (!result.Succeeded) return ExitCodes.EnvironmentError;
        return ExitCodes.Success;
    }

    internal record ParsedRemoveArgs(string? Name, string? Agent, bool Yes, bool Json);

    internal static ParsedRemoveArgs ParseRemoveArgs(IReadOnlyList<string> args)
    {
        string? name = null, agent = null;
        bool yes = false, json = false;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--json") { json = true; continue; }
            if (a == "--yes" || a == "-y") { yes = true; continue; }
            if (a.StartsWith("--agent=", StringComparison.Ordinal)) { agent = a["--agent=".Length..]; continue; }
            if (a == "--agent" && i + 1 < args.Count) { agent = args[++i]; continue; }
            if (a.StartsWith("--", StringComparison.Ordinal)) continue;
            if (name is null) name = a;
        }
        return new ParsedRemoveArgs(name, agent, yes, json);
    }

    private static void WriteRemoveText(
        RemoveService.RemoveReport r,
        InstalledSkill target,
        ParsedRemoveArgs p,
        RemoveValidator.RemoveValidation validation)
    {
        Console.Out.WriteLine($"{(r.DryRun ? "remove (dry-run)" : "remove")}: {target.Name}");
        Console.Out.WriteLine($"  path     : {target.ResolvedPath}");
        Console.Out.WriteLine($"  resolved : {validation.ResolvedPath}");
        Console.Out.WriteLine($"  scope    : {target.Scope}");
        if (validation.Errors.Length > 0)
        {
            Console.Error.WriteLine("REFUSED:");
            foreach (var e in validation.Errors) Console.Error.WriteLine($"  ✗ {e.Kind}: {e.Detail}");
            return;
        }
        if (validation.Warnings.Length > 0)
        {
            Console.Error.WriteLine("WARNINGS:");
            foreach (var w in validation.Warnings) Console.Error.WriteLine($"  ! {w.Kind}: {w.Detail}");
            if (!p.Yes)
            {
                Console.Error.WriteLine("hint: --yes is required to accept these warnings and execute");
            }
        }
        if (r.DryRun)
        {
            Console.Out.WriteLine($"  would remove: {r.FilesDeleted} file(s), {r.DirectoriesDeleted} dir(s)");
            Console.Out.WriteLine("  (dry-run; rerun with --yes to execute)");
        }
        else if (r.Succeeded)
        {
            Console.Out.WriteLine($"  removed: {r.FilesDeleted} file(s), {r.DirectoriesDeleted} dir(s)");
        }
        else
        {
            Console.Error.WriteLine($"  remove failed with {r.ErrorCount} error(s)");
            foreach (var e in r.Errors) Console.Error.WriteLine($"  · {e}");
        }
    }

    private static void WriteRemoveJson(
        RemoveService.RemoveReport r,
        InstalledSkill target,
        ParsedRemoveArgs p,
        RemoveValidator.RemoveValidation validation)
        => WriteJson(w => WriteRemoveJson(w, r, target, p, validation));

    internal static string RenderRemoveJson(
        RemoveService.RemoveReport r,
        InstalledSkill target,
        ParsedRemoveArgs p,
        RemoveValidator.RemoveValidation validation)
        => RenderJson(w => WriteRemoveJson(w, r, target, p, validation));

    private static void WriteRemoveJson(
        Utf8JsonWriter w,
        RemoveService.RemoveReport r,
        InstalledSkill target,
        ParsedRemoveArgs p,
        RemoveValidator.RemoveValidation validation)
    {
        var removalAttempted = validation.Allowed
            && (r.DryRun || !validation.RequiresSecondConfirm || p.Yes);
        w.WriteStartObject();
        w.WriteBoolean("dryRun", r.DryRun);
        w.WriteBoolean("succeeded", r.Succeeded);
        w.WriteBoolean("allowed", validation.Allowed);
        w.WriteString("name", target.Name);
        w.WriteString("resolvedPath", validation.ResolvedPath);
        w.WriteString("scope", target.Scope.ToString());
        w.WriteNumber("filesDeleted", r.FilesDeleted);
        w.WriteNumber("directoriesDeleted", r.DirectoriesDeleted);
        w.WriteNumber("runtimeErrorCount", removalAttempted ? r.ErrorCount : 0);
        w.WriteStartArray("errors");
        foreach (var e in validation.Errors)
        {
            w.WriteStartObject();
            w.WriteString("kind", e.Kind.ToString());
            w.WriteString("detail", e.Detail);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("warnings");
        foreach (var warn in validation.Warnings)
        {
            w.WriteStartObject();
            w.WriteString("kind", warn.Kind.ToString());
            w.WriteString("detail", warn.Detail);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("runtimeErrors");
        if (removalAttempted)
        {
            foreach (var e in r.Errors) w.WriteStringValue(e);
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static async Task<int> CleanupAsync(
        AppOptions options,
        TuiServices services,
        CancellationToken cancellationToken)
    {
        var parsed = ParseCleanupArgs(options.SubcommandArgs);
        var report = await services.EnvironmentProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await services.InventoryService.CaptureAsync(
            report.GhPath,
            new Inventory.LocalInventoryService.Options(
                ScanRoots: options.ScanRoots,
                AllowHiddenDirs: false),
            cancellationToken
        ).ConfigureAwait(false);

        var candidates = CleanupClassifier.ClassifyWithCancellation(
            snapshot,
            snapshot.ScannedRoots,
            options: null,
            cancellationToken);
        if (parsed.KindFilter is { Count: > 0 })
        {
            candidates = candidates.Where(c => parsed.KindFilter.Contains(c.Kind.ToString(), StringComparer.OrdinalIgnoreCase)).ToImmutableArray();
        }

        var applied = new List<(CleanupClassifier.Candidate C, RemoveService.RemoveReport R)>();
        var duplicateSkips = ImmutableArray<CleanupClassifier.Candidate>.Empty;
        var environmentRefusal = false;
        if (parsed.Apply)
        {
            if (!parsed.Yes)
            {
                Console.Error.WriteLine("skillview: cleanup --apply requires --yes");
                return ExitCodes.UserError;
            }
            // Resolve every key before the first removal. A candidate may be
            // emitted under multiple cleanup kinds; validating its second
            // occurrence after the first was deleted would falsely report an
            // identity/environment failure.
            var selection = CleanupClassifier.DeduplicateByPath(
                candidates,
                cancellationToken);
            duplicateSkips = selection.Duplicates;
            foreach (var c in selection.Unique)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var validation = ValidateCleanupCandidate(c, snapshot.ScannedRoots, snapshot.Skills);
                if (!validation.Allowed || validation.RequiresSecondConfirm)
                {
                    environmentRefusal |= IsEnvironmentRefusal(validation);
                    var reason = validation.Allowed
                        ? "requires second-confirm"
                        : string.Join("; ", validation.Errors.Select(error =>
                            $"{error.Kind}: {error.Detail}"));
                    applied.Add((c, RemoveService.RemoveReport.Refused(validation.ResolvedPath,
                        reason)));
                    continue;
                }
                var removal = await services.RemoveService.RemoveAsync(
                    validation,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                applied.Add((c, removal));
                if (removal.Succeeded)
                {
                    services.ListAdapter.Invalidate();
                }
            }
        }

        if (parsed.Json) WriteCleanupJson(candidates, applied, duplicateSkips, parsed);
        else WriteCleanupText(candidates, applied, duplicateSkips, parsed);

        if (parsed.Output is not null)
        {
            try
            {
                File.WriteAllText(parsed.Output, RenderCleanupReport(candidates));
                Console.Error.WriteLine($"wrote report to {parsed.Output}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"skillview: failed to write {parsed.Output}: {ex.Message}");
                return ExitCodes.EnvironmentError;
            }
        }

        if (candidates.Length == 0) return ExitCodes.Success;
        if (parsed.Apply && environmentRefusal) return ExitCodes.EnvironmentError;
        if (parsed.Apply && applied.Any(a => !a.R.Succeeded)) return ExitCodes.UserError;
        return ExitCodes.Success;
    }

    private static bool IsEnvironmentRefusal(
        RemoveValidator.RemoveValidation validation) =>
        validation.Errors.Any(error =>
            error.Kind == RemoveValidator.ErrorKind.FilesystemIdentityUnavailable);

    internal record ParsedCleanupArgs(
        IReadOnlyList<string>? KindFilter,
        bool Apply,
        bool Yes,
        bool Json,
        string? Output);

    internal static ParsedCleanupArgs ParseCleanupArgs(IReadOnlyList<string> args)
    {
        List<string>? kinds = null;
        bool apply = false, yes = false, json = false;
        string? output = null;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--apply") { apply = true; continue; }
            if (a == "--yes" || a == "-y") { yes = true; continue; }
            if (a == "--json") { json = true; continue; }
            if (a.StartsWith("--candidates=", StringComparison.Ordinal))
            {
                kinds = a["--candidates=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                continue;
            }
            if (a == "--candidates" && i + 1 < args.Count)
            {
                kinds = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                continue;
            }
            if (a.StartsWith("--output=", StringComparison.Ordinal)) { output = a["--output=".Length..]; continue; }
            if (a == "--output" && i + 1 < args.Count) { output = args[++i]; continue; }
        }
        return new ParsedCleanupArgs(kinds, apply, yes, json, output);
    }

    private static RemoveValidator.RemoveValidation ValidateCleanupCandidate(
        CleanupClassifier.Candidate candidate,
        IReadOnlyList<ScanRoot> scanRoots,
        IReadOnlyList<InstalledSkill> allSkills)
    {
        return candidate.Kind switch
        {
            CleanupClassifier.CandidateKind.BrokenSymlink =>
                RemoveValidator.ValidateBrokenSymlink(candidate.Path, scanRoots),
            CleanupClassifier.CandidateKind.EmptyDirectory =>
                RemoveValidator.ValidateEmptyDirectory(candidate.Path, scanRoots),
            _ when candidate.Skill is not null => RemoveValidator.Validate(candidate.Skill, scanRoots, allSkills),
            _ => RefuseUnsupportedCleanupCandidate(candidate),
        };
    }

    private static RemoveValidator.RemoveValidation RefuseUnsupportedCleanupCandidate(
        CleanupClassifier.Candidate candidate) =>
        new(
            ImmutableArray.Create(new RemoveValidator.Error(
                RemoveValidator.ErrorKind.NotASkillDirectory,
                $"cleanup candidate kind '{candidate.Kind}' requires installed-skill metadata")),
            ImmutableArray<RemoveValidator.Warning>.Empty,
            candidate.Path,
            ImmutableArray<string>.Empty);

    private static void WriteCleanupText(
        IReadOnlyList<CleanupClassifier.Candidate> candidates,
        IReadOnlyList<(CleanupClassifier.Candidate C, RemoveService.RemoveReport R)> applied,
        IReadOnlyList<CleanupClassifier.Candidate> duplicateSkips,
        ParsedCleanupArgs p)
    {
        Console.Out.WriteLine($"cleanup: {candidates.Count} candidate(s){(p.Apply ? " (--apply)" : "")}");
        foreach (var c in candidates)
        {
            Console.Out.WriteLine($"  {c.Kind,-22}  {c.Path}");
            Console.Out.WriteLine($"    why: {c.Reason}");
        }
        if (applied.Count > 0 || duplicateSkips.Count > 0)
        {
            var ok = applied.Count(a => a.R.Succeeded);
            Console.Out.WriteLine();
            Console.Out.WriteLine($"applied: {ok}/{applied.Count} succeeded");
            if (duplicateSkips.Count > 0)
            {
                Console.Out.WriteLine(
                    $"skipped: {duplicateSkips.Count} duplicate candidate path(s)");
            }
            foreach (var (c, r) in applied.Where(a => !a.R.Succeeded))
            {
                Console.Out.WriteLine($"  ✗ {c.Path}: {string.Join("; ", r.Errors)}");
            }
        }
    }

    private static void WriteCleanupJson(
        IReadOnlyList<CleanupClassifier.Candidate> candidates,
        IReadOnlyList<(CleanupClassifier.Candidate C, RemoveService.RemoveReport R)> applied,
        IReadOnlyList<CleanupClassifier.Candidate> duplicateSkips,
        ParsedCleanupArgs p)
        => WriteJson(w => WriteCleanupJson(w, candidates, applied, duplicateSkips, p));

    internal static string RenderCleanupJson(
        IReadOnlyList<CleanupClassifier.Candidate> candidates,
        IReadOnlyList<(CleanupClassifier.Candidate C, RemoveService.RemoveReport R)> applied,
        ParsedCleanupArgs p,
        IReadOnlyList<CleanupClassifier.Candidate>? duplicateSkips = null)
        => RenderJson(w => WriteCleanupJson(
            w,
            candidates,
            applied,
            duplicateSkips ?? [],
            p));

    private static void WriteCleanupJson(
        Utf8JsonWriter w,
        IReadOnlyList<CleanupClassifier.Candidate> candidates,
        IReadOnlyList<(CleanupClassifier.Candidate C, RemoveService.RemoveReport R)> applied,
        IReadOnlyList<CleanupClassifier.Candidate> duplicateSkips,
        ParsedCleanupArgs p)
    {
        w.WriteStartObject();
        w.WriteBoolean("apply", p.Apply);
        w.WriteNumber("candidates", candidates.Count);
        w.WriteStartArray("entries");
        foreach (var c in candidates)
        {
            w.WriteStartObject();
            w.WriteString("kind", c.Kind.ToString());
            w.WriteString("path", c.Path);
            w.WriteString("reason", c.Reason);
            w.WriteString("name", c.Skill?.Name);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        if (p.Apply)
        {
            w.WriteNumber("skippedCandidates", duplicateSkips.Count);
            w.WriteStartArray("applied");
            foreach (var (c, r) in applied)
            {
                w.WriteStartObject();
                w.WriteString("path", c.Path);
                w.WriteBoolean("succeeded", r.Succeeded);
                w.WriteNumber("filesDeleted", r.FilesDeleted);
                w.WriteNumber("directoriesDeleted", r.DirectoriesDeleted);
                w.WriteStartArray("errors");
                foreach (var e in r.Errors) w.WriteStringValue(e);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("skipped");
            foreach (var candidate in duplicateSkips)
            {
                w.WriteStartObject();
                w.WriteString("path", candidate.Path);
                w.WriteString("kind", candidate.Kind.ToString());
                w.WriteString("reason", "duplicate candidate path");
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    internal static string RenderCleanupReport(IReadOnlyList<CleanupClassifier.Candidate> candidates)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# SkillView cleanup report — {DateTimeOffset.UtcNow:O}");
        sb.AppendLine($"candidates: {candidates.Count}");
        foreach (var c in candidates)
        {
            sb.AppendLine();
            sb.AppendLine($"- kind : {c.Kind}");
            sb.AppendLine($"  path : {c.Path}");
            sb.AppendLine($"  why  : {c.Reason}");
        }
        return sb.ToString();
    }

    private static void WriteJson(Action<Utf8JsonWriter> write)
    {
        using var stream = new Utf8TextWriterStream(Console.Out);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        write(writer);
        writer.Flush();
        Console.Out.WriteLine();
    }

    private static string RenderJson(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private static int UnknownSubcommand(string name, Logger logger)
    {
        logger.Warn("cli", $"unknown subcommand '{name}'");
        Console.Error.WriteLine($"skillview: unknown subcommand '{name}'");
        Console.Error.WriteLine("try `skillview --help`");
        return ExitCodes.InvalidUsage;
    }

    internal static string RenderHelpMarkdown(AppOptions options)
    {
        var command = GetCommandName(options.InvocationMode);
        var alternateCommand = options.InvocationMode == InvocationMode.GhExtension ? "skillview" : "gh skillview";

        return $$"""
            # SkillView

            SkillView is a terminal UI and scriptable CLI for discovering, previewing, installing, updating, removing, and cleaning up AI agent skills on top of `gh skill`.

            SkillView complements `gh skill`: use SkillView for guided TUI flows and automation-friendly CLI wrappers, and reach for raw `gh skill` when you need a brand-new upstream preview flag before SkillView exposes it.

            You are running the `{{command}}` entrypoint. The alternate entrypoint is `{{alternateCommand}}`.

            ## Usage

            ```text
            {{command}} [global options]
            {{command}} <subcommand> [subcommand options]
            ```

            With no subcommand, SkillView launches the full-screen TUI.

            ## Quick start

            ```bash
            {{command}}
            {{command}} doctor
            {{command}} search terraform
            {{command}} list --json
            {{command}} update --dry-run
            {{command}} cleanup
            ```

            ## Global options

            | Global flag | What it does |
            | --- | --- |
            | `--help`, `-h` | Show this Markdown help view. |
            | `--version`, `-V` | Show the SkillView version. |
            | `--debug` | Enable debug logging. This flag works before or after the subcommand and streams logs to stderr in CLI mode. |
            | `--theme <name>` | Select the TUI theme. Supported values are `default` and `high-contrast`. You can also set `SKILLVIEW_THEME`. |
            | `--scan-root <path>` | Add a custom scan root. Repeat this flag to scan multiple roots. |

            ## Subcommands

            | Subcommand | Purpose | Key options |
            | --- | --- | --- |
            | `doctor` | Inspect `gh`, auth state, capability probes, log path, and scan roots. | `--json`, `--clear-logs` |
            | `list` | Show installed skills from the filesystem and, when supported, `gh skill list`. | `--json`, `--scope`, `--agent`, `--dir`, `--allow-hidden-dirs` |
            | `rescan` | Rebuild the local inventory snapshot and print a summary. | _none_ |
            | `search <query>` | Search public skill repositories. | `--owner`, `--limit`, `--page`, `--json` |
            | `preview OWNER/REPO [SKILL]` | Render a skill preview without installing it. | `--version`, `--allow-hidden-dirs`, `--rendered`, `--json` |
            | `install OWNER/REPO [SKILL]` | Install one skill, or every skill in the repo with `--all`, then rescan inventory. | `--all`, `--agent`, `--scope`, `--path`, `--version`, `--pin`, `--force`, `--upstream`, `--from-local`, `--allow-hidden-dirs`, `--json` |
            | `update [SKILL...]` | Dry-run or apply updates for one or many installed skills. `--all` is non-interactive and skips metadata-less skills. | `--all`, `--dry-run`, `--force`, `--unpin`, `--json` |
            | `remove <SKILL>` | Safely remove an installed skill. Defaults to dry-run until you pass confirmation. | `--agent`, `--yes`, `--json` |
            | `cleanup` | Find duplicates, residue, malformed installs, and other cleanup candidates. | `--candidates`, `--apply`, `--yes`, `--json`, `--output` |

            ## Common examples

            ```bash
            {{command}} doctor --json
            {{command}} list --scope user --json
            {{command}} search prompt --owner github
            {{command}} preview github/awesome-copilot documentation-writer --rendered
            {{command}} install github/awesome-copilot git-commit --agent claude-code --scope user
            {{command}} update --all --dry-run
            {{command}} remove git-commit --yes
            {{command}} cleanup --apply --yes
            ```

            ## Automation

            SkillView is automation-friendly when you want safer wrappers than raw `gh skill`.

            - Prefer `--json` on `doctor`, `list`, `search`, `preview`, `install`, `update`, `remove`, and `cleanup`.
            - Use exit codes in scripts: `0` success, `2` invalid usage, `10` environment/setup problems, `20` no matches, `130` canceled.
            - Put global flags like `--scan-root` and `--theme` before the subcommand; only `--debug` is accepted after the subcommand.

            ## Notes

            - SkillView only emits capability-gated flags when the installed `gh` build supports them.
            - `--debug` is the only global flag accepted after a subcommand. Put other global flags before the subcommand.
            - `SKILLVIEW_LOG=debug` is the environment-variable alternative to `--debug`.
            - Homebrew and WinGet scaffolding exists in the release workflow, but those channels are still dark-launch only and are not public install paths yet.

            ## Exit codes

            | Code | Meaning |
            | --- | --- |
            | `0` | Success or nothing to do |
            | `1` | User-level error |
            | `2` | Invalid usage |
            | `10` | Environment error |
            | `20` | No matches |
            | `130` | Canceled by the caller or Ctrl+C |
            """;
    }

    private static int PrintHelp(AppOptions options)
    {
        Console.Out.WriteLine(RenderHelpMarkdown(options));
        return ExitCodes.Success;
    }

    internal static string RenderVersionText(AppOptions options)
    {
        var version = typeof(CliDispatcher).Assembly.GetName().Version;
        var versionText = version is null
            ? "0.0.0"
            : version.Revision == 0
                ? $"{version.Major}.{version.Minor}.{version.Build}"
                : version.ToString();
        var terminalGuiVersion = typeof(Terminal.Gui.Views.Window).Assembly.GetName().Version?.ToString() ?? "unknown";

        return $"{GetCommandName(options.InvocationMode)} {versionText} (Terminal.Gui {terminalGuiVersion})";
    }

    private static int PrintVersion(AppOptions options)
    {
        Console.Out.WriteLine(RenderVersionText(options));
        return ExitCodes.Success;
    }

    private static string GetCommandName(InvocationMode invocationMode) =>
        invocationMode == InvocationMode.GhExtension ? "gh skillview" : "skillview";
}
