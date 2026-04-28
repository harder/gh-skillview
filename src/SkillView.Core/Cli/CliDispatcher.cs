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
using SkillView.Ui;

namespace SkillView.Cli;

/// Non-interactive subcommand router. Feature-complete through Phase 7:
/// `doctor`, `list`, `rescan`, `search`, `preview`, `install`, `update`,
/// `remove`, `cleanup`. JSON rendering and argv parsing are factored into
/// `internal` helpers for snapshot testing.
public static class CliDispatcher
{
    public static async Task<int> RunAsync(AppOptions options, TuiServices services)
    {
        return options.SubcommandName switch
        {
            "doctor" => await DoctorAsync(options, services).ConfigureAwait(false),
            "list" => await ListAsync(options, services).ConfigureAwait(false),
            "rescan" => await RescanAsync(options, services).ConfigureAwait(false),
            "search" => await SearchAsync(options, services).ConfigureAwait(false),
            "preview" => await PreviewAsync(options, services).ConfigureAwait(false),
            "install" => await InstallAsync(options, services).ConfigureAwait(false),
            "update" => await UpdateAsync(options, services).ConfigureAwait(false),
            "remove" => await RemoveAsync(options, services).ConfigureAwait(false),
            "cleanup" => await CleanupAsync(options, services).ConfigureAwait(false),
            "--help" or "-h" or "help" => PrintHelp(),
            "--version" or "-V" => PrintVersion(),
            _ => UnknownSubcommand(options.SubcommandName ?? "<null>", services.Logger),
        };
    }

    private static async Task<int> DoctorAsync(AppOptions options, TuiServices services)
    {
        if (options.SubcommandArgs.Contains("--clear-logs"))
        {
            return ClearLogs(services);
        }

        var report = await services.EnvironmentProbe.ProbeAsync().ConfigureAwait(false);
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
        if (!report.Capabilities.SkillSubcommandPresent)
        {
            return ExitCodes.EnvironmentError;
        }
        return ExitCodes.Success;
    }

    private static void WriteDoctorText(EnvironmentReport r, AppOptions options)
    {
        Console.Out.WriteLine($"invocation    : {options.InvocationMode}");
        Console.Out.WriteLine($"gh path       : {r.GhPath ?? "(not found)"}");
        Console.Out.WriteLine($"gh version    : {r.GhVersionRaw ?? "(unknown)"}");
        Console.Out.WriteLine($"gh minimum    : {GhBinaryLocator.MinimumVersion}{(r.GhMeetsMinimum ? " ✓" : " ✗ too old")}");
        Console.Out.WriteLine($"gh auth       : {AuthSummary(r.Auth)}");
        Console.Out.WriteLine($"gh skill      : {(r.Capabilities.SkillSubcommandPresent ? "present" : "(not detected)")}");
        Console.Out.WriteLine($"gh skill list : {(r.Capabilities.HasSkillList ? "present (--json supported)" : "(not detected — filesystem fallback in use)")}");
        Console.Out.WriteLine($"capabilities  : {CapabilitiesSummary(r.Capabilities)}");
        Console.Out.WriteLine($"debug         : {options.Debug}");
        Console.Out.WriteLine($"log directory : {r.LogDirectory ?? "(unset)"}");
        Console.Out.WriteLine($"scan roots    : {(options.ScanRoots.Count == 0 ? "(default)" : string.Join(", ", options.ScanRoots))}");
        Console.Out.WriteLine($"baseline      : {(r.BaselineOk ? "ok" : "degraded")}");
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

    private static string CapabilitiesSummary(CapabilityProfile c)
    {
        if (!c.SkillSubcommandPresent) return "(gh skill not detected)";
        var bits = new List<string>();
        if (c.SupportsAllowHiddenDirs) bits.Add("allow-hidden-dirs");
        if (c.SupportsUpstream) bits.Add("upstream");
        if (c.SupportsRepoPath) bits.Add("repo-path");
        if (c.SupportsFromLocal) bits.Add("from-local");
        if (c.SupportsUpdateJson) bits.Add("update-json");
        if (c.SupportsUpdateYes) bits.Add("update-yes");
        if (c.HasSkillList) bits.Add("list-json");
        return bits.Count == 0 ? "(no probed flags found)" : string.Join(", ", bits);
    }

    private static void WriteDoctorJson(EnvironmentReport r, AppOptions options)
        => Console.Out.WriteLine(RenderDoctorJson(r, options));

    internal static string RenderDoctorJson(EnvironmentReport r, AppOptions options)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
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

            writer.WriteStartObject("capabilities");
            writer.WriteBoolean("skillSubcommandPresent", r.Capabilities.SkillSubcommandPresent);
            writer.WriteBoolean("listSubcommandPresent", r.Capabilities.ListSubcommandPresent);
            writer.WriteBoolean("hasSkillList", r.Capabilities.HasSkillList);
            writer.WriteBoolean("supportsAllowHiddenDirs", r.Capabilities.SupportsAllowHiddenDirs);
            writer.WriteBoolean("supportsUpstream", r.Capabilities.SupportsUpstream);
            writer.WriteBoolean("supportsRepoPath", r.Capabilities.SupportsRepoPath);
            writer.WriteBoolean("supportsFromLocal", r.Capabilities.SupportsFromLocal);
            writer.WriteBoolean("supportsUpdateJson", r.Capabilities.SupportsUpdateJson);
            writer.WriteBoolean("supportsUpdateYes", r.Capabilities.SupportsUpdateYes);
            WriteFlagArray(writer, "installFlags", r.Capabilities.InstallFlags);
            WriteFlagArray(writer, "updateFlags", r.Capabilities.UpdateFlags);
            WriteFlagArray(writer, "listFlags", r.Capabilities.ListFlags);
            WriteFlagArray(writer, "searchFlags", r.Capabilities.SearchFlags);
            writer.WriteEndObject();

            writer.WriteStartArray("scanRoots");
            foreach (var root in options.ScanRoots) writer.WriteStringValue(root);
            writer.WriteEndArray();

            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteFlagArray(Utf8JsonWriter writer, string name, IEnumerable<string> flags)
    {
        writer.WriteStartArray(name);
        foreach (var f in flags.OrderBy(x => x, StringComparer.Ordinal))
        {
            writer.WriteStringValue(f);
        }
        writer.WriteEndArray();
    }

    private static int ClearLogs(TuiServices services)
    {
        if (services.FileLogSink is null)
        {
            Console.Error.WriteLine("skillview: log sink not initialized; nothing to clear");
            return ExitCodes.EnvironmentError;
        }
        var count = services.FileLogSink.ClearAll();
        Console.Out.WriteLine($"cleared {count} log file(s) from {services.LogDirectory}");
        return ExitCodes.Success;
    }

    private static async Task<int> RescanAsync(AppOptions options, TuiServices services)
    {
        var (snapshot, _) = await CaptureInventoryAsync(options, services).ConfigureAwait(false);
        var d = snapshot.Diagnostics;
        Console.Out.WriteLine($"rescan: {snapshot.Skills.Length} skill(s) across {snapshot.ScannedRoots.Length} root(s)" +
                              (snapshot.UsedGhSkillList ? " (gh skill list used)" : " (filesystem only)"));
        Console.Out.WriteLine($"  scan: {d.FsScanDuration.TotalMilliseconds:F0}ms" +
            (snapshot.UsedGhSkillList ? $", gh list: {d.GhListDuration.TotalMilliseconds:F0}ms" : "") +
            (d.BrokenSymlinksFound > 0 ? $", {d.BrokenSymlinksFound} broken symlink(s)" : ""));
        return ExitCodes.Success;
    }

    private static async Task<int> ListAsync(AppOptions options, TuiServices services)
    {
        var listOptions = ParseListArgs(options.SubcommandArgs, out var json);
        var (snapshot, capabilities) = await CaptureInventoryAsync(options, services, listOptions).ConfigureAwait(false);

        if (json)
        {
            WriteListJson(snapshot, capabilities);
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
            if (a.StartsWith("--path=", StringComparison.Ordinal)) { path = a["--path=".Length..]; continue; }
            if (a == "--path" && i + 1 < args.Count) { path = args[++i]; continue; }
            if (a == "--allow-hidden-dirs") { /* handled via inventory.CaptureAsync */ continue; }
        }
        if (!string.IsNullOrEmpty(path)) scanRoots.Add(path!);
        return (scope, agent, path, scanRoots);
    }

    private static async Task<(Inventory.Models.InventorySnapshot Snapshot, CapabilityProfile Capabilities)>
        CaptureInventoryAsync(
            AppOptions options,
            TuiServices services,
            (string? scope, string? agent, string? path, List<string> scanRoots)? listOptions = null)
    {
        var report = await services.EnvironmentProbe.ProbeAsync().ConfigureAwait(false);
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
        var snapshot = await services.InventoryService.CaptureAsync(
            report.GhPath,
            report.Capabilities,
            new Inventory.LocalInventoryService.Options(
                scanRoots,
                allowHidden,
                FilterScope: scope,
                FilterAgent: agent)
        ).ConfigureAwait(false);
        return (snapshot, report.Capabilities);
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

    private static void WriteListJson(
        Inventory.Models.InventorySnapshot snapshot,
        CapabilityProfile capabilities)
        => Console.Out.WriteLine(RenderListJson(snapshot, capabilities));

    internal static string RenderListJson(
        Inventory.Models.InventorySnapshot snapshot,
        CapabilityProfile capabilities)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("capturedAt", snapshot.CapturedAt.ToString("O"));
            w.WriteBoolean("usedGhSkillList", snapshot.UsedGhSkillList);
            w.WriteBoolean("ghSkillListAvailable", capabilities.HasSkillList);

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
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task<int> SearchAsync(AppOptions options, TuiServices services)
    {
        var parsed = ParseSearchArgs(options.SubcommandArgs);
        if (parsed.Query is null)
        {
            Console.Error.WriteLine("skillview: search requires a query");
            Console.Error.WriteLine("usage: skillview search <query> [--owner <o>] [--limit <n>] [--json]");
            return ExitCodes.InvalidUsage;
        }

        var report = await services.EnvironmentProbe.ProbeAsync().ConfigureAwait(false);
        if (!report.GhFound || !report.GhMeetsMinimum || !report.Capabilities.SkillSubcommandPresent)
        {
            Console.Error.WriteLine("skillview: gh or gh skill not available (run `skillview doctor`)");
            return ExitCodes.EnvironmentError;
        }

        var response = await services.SearchService.SearchAsync(
            report.GhPath!,
            parsed.Query,
            report.Capabilities,
            new GhSkillSearchService.Options(
                Owner: parsed.Owner,
                Limit: parsed.Limit ?? GhSkillSearchService.DefaultLimit,
                Page: parsed.Page ?? 1)
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
        => Console.Out.WriteLine(RenderSearchJson(rows, parsed));

    internal static string RenderSearchJson(IReadOnlyList<SearchResultSkill> rows, ParsedSearchArgs parsed)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
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
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task<int> PreviewAsync(AppOptions options, TuiServices services)
    {
        var parsed = ParsePreviewArgs(options.SubcommandArgs);
        if (parsed.Repo is null)
        {
            Console.Error.WriteLine("skillview: preview requires a repo (OWNER/REPO)");
            Console.Error.WriteLine("usage: skillview preview <owner/repo> [<skill-name>] [--version <ref>] [--json]");
            return ExitCodes.InvalidUsage;
        }

        var report = await services.EnvironmentProbe.ProbeAsync().ConfigureAwait(false);
        if (!report.GhFound || !report.GhMeetsMinimum || !report.Capabilities.SkillSubcommandPresent)
        {
            Console.Error.WriteLine("skillview: gh or gh skill not available (run `skillview doctor`)");
            return ExitCodes.EnvironmentError;
        }

        var preview = await services.PreviewService.PreviewAsync(
            report.GhPath!,
            parsed.Repo,
            parsed.SkillName,
            parsed.Version
        ).ConfigureAwait(false);

        if (!preview.Succeeded)
        {
            Console.Error.WriteLine($"skillview: preview failed (exit {preview.ExitCode}): {preview.ErrorMessage}");
            return ExitCodes.EnvironmentError;
        }

        if (parsed.Json) WritePreviewJson(preview);
        else Console.Out.WriteLine(preview.Body);

        return ExitCodes.Success;
    }

    internal record ParsedPreviewArgs(string? Repo, string? SkillName, string? Version, bool Json);

    internal static ParsedPreviewArgs ParsePreviewArgs(IReadOnlyList<string> args)
    {
        string? repo = null, skill = null, version = null;
        var json = false;
        var positional = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--json") { json = true; continue; }
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
        return new ParsedPreviewArgs(repo, skill, version, json);
    }

    private static void WritePreviewJson(PreviewResult p)
        => Console.Out.WriteLine(RenderPreviewJson(p));

    internal static string RenderPreviewJson(PreviewResult p)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
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
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task<int> InstallAsync(AppOptions options, TuiServices services)
    {
        var parsed = ParseInstallArgs(options.SubcommandArgs);
        if (parsed.Repo is null)
        {
            Console.Error.WriteLine("skillview: install requires a repo (OWNER/REPO)");
            Console.Error.WriteLine(
                "usage: skillview install <owner/repo>[@<ref>] [<skill>] [--agent <id>]..." +
                " [--scope project|user|custom] [--path <dir>] [--version <ref>] [--pin]" +
                " [--force] [--upstream <url>] [--repo-path <p>] [--from-local]" +
                " [--allow-hidden-dirs] [--json]");
            return ExitCodes.InvalidUsage;
        }

        var report = await services.EnvironmentProbe.ProbeAsync().ConfigureAwait(false);
        if (!report.GhFound || !report.GhMeetsMinimum || !report.Capabilities.SkillSubcommandPresent)
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
            RepoPath: parsed.RepoPath,
            FromLocal: parsed.FromLocal);

        // Snapshot pre-install so we can surface the post-install diff.
        var preSnapshot = await services.InventoryService.CaptureAsync(
            report.GhPath,
            report.Capabilities,
            new Inventory.LocalInventoryService.Options(
                ScanRoots: options.ScanRoots,
                AllowHiddenDirs: parsed.AllowHiddenDirs)
        ).ConfigureAwait(false);

        var result = await services.InstallService.InstallAsync(
            report.GhPath!,
            parsed.Repo,
            parsed.SkillName,
            report.Capabilities,
            installOptions
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

        var postSnapshot = await services.InventoryService.CaptureAsync(
            report.GhPath,
            report.Capabilities,
            new Inventory.LocalInventoryService.Options(
                ScanRoots: options.ScanRoots,
                AllowHiddenDirs: parsed.AllowHiddenDirs)
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
        string? RepoPath,
        bool FromLocal,
        bool AllowHiddenDirs,
        bool Json);

    internal static ParsedInstallArgs ParseInstallArgs(IReadOnlyList<string> args)
    {
        string? version = null, scope = null, path = null, upstream = null, repoPath = null;
        var agents = new List<string>();
        var positional = new List<string>();
        bool pin = false, force = false, fromLocal = false, allowHidden = false, json = false;

        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--json") { json = true; continue; }
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
            if (a.StartsWith("--repo-path=", StringComparison.Ordinal)) { repoPath = a["--repo-path=".Length..]; continue; }
            if (a == "--repo-path" && i + 1 < args.Count) { repoPath = args[++i]; continue; }
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
            upstream, repoPath, fromLocal, allowHidden, json);
    }

    private static async Task<int> UpdateAsync(AppOptions options, TuiServices services)
    {
        var parsed = ParseUpdateArgs(options.SubcommandArgs);
        var report = await services.EnvironmentProbe.ProbeAsync().ConfigureAwait(false);
        if (!report.GhFound || !report.GhMeetsMinimum || !report.Capabilities.SkillSubcommandPresent)
        {
            Console.Error.WriteLine("skillview: gh or gh skill not available (run `skillview doctor`)");
            return ExitCodes.EnvironmentError;
        }

        if (!parsed.All && parsed.Skills.Count == 0 && !parsed.DryRun)
        {
            Console.Error.WriteLine("skillview: update requires at least one skill, --all, or --dry-run");
            Console.Error.WriteLine(
                "usage: skillview update [<skill>]... [--all] [--dry-run] [--force] [--unpin]" +
                " [--yes] [--json]");
            return ExitCodes.InvalidUsage;
        }

        // Refuse `--all` without `--yes` unless the probe has
        // confirmed `--yes`/`--non-interactive`, or the user has explicitly
        // asked for a dry-run. This keeps the current `gh` baseline from
        // waiting on an interactive prompt.
        if (parsed.All && !parsed.DryRun && !parsed.Yes && !report.Capabilities.SupportsUpdateYes)
        {
            Console.Error.WriteLine(
                "skillview: refusing `--all` without `--yes` because this `gh` build lacks --yes/--non-interactive");
            Console.Error.WriteLine("hint: rerun with --dry-run, or name specific skills");
            return ExitCodes.UserError;
        }

        // Pre-snapshot for diffing: additions + version changes.
        var preSnapshot = await services.InventoryService.CaptureAsync(
            report.GhPath,
            report.Capabilities,
            new Inventory.LocalInventoryService.Options(
                ScanRoots: options.ScanRoots,
                AllowHiddenDirs: false)
        ).ConfigureAwait(false);

        var updateOptions = new GhSkillUpdateService.Options(
            Skills: parsed.Skills,
            All: parsed.All,
            DryRun: parsed.DryRun,
            Force: parsed.Force,
            Unpin: parsed.Unpin,
            Yes: parsed.Yes,
            Json: parsed.Json && report.Capabilities.SupportsUpdateJson);

        var result = await services.UpdateService.UpdateAsync(
            report.GhPath!,
            report.Capabilities,
            updateOptions
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
            var postSnapshot = await services.InventoryService.CaptureAsync(
                report.GhPath,
                report.Capabilities,
                new Inventory.LocalInventoryService.Options(
                    ScanRoots: options.ScanRoots,
                    AllowHiddenDirs: false)
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
        bool Yes,
        bool Json);

    internal static ParsedUpdateArgs ParseUpdateArgs(IReadOnlyList<string> args)
    {
        var skills = new List<string>();
        bool all = false, dryRun = false, force = false, unpin = false, yes = false, json = false;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--json") { json = true; continue; }
            if (a == "--all") { all = true; continue; }
            if (a == "--dry-run") { dryRun = true; continue; }
            if (a == "--force") { force = true; continue; }
            if (a == "--unpin") { unpin = true; continue; }
            if (a == "--yes" || a == "--non-interactive") { yes = true; continue; }
            if (a.StartsWith("--", StringComparison.Ordinal)) continue;
            skills.Add(a);
        }
        return new ParsedUpdateArgs(skills, all, dryRun, force, unpin, yes, json);
    }

    /// Per-skill TreeSha delta: skills present at the same ResolvedPath in
    /// both snapshots where `TreeSha` changed. Captures the "updated" axis
    /// the install diff (additions only) doesn't — Phase 4 carry-forward.
    internal static IReadOnlyList<UpdateDiffEntry> InventoryUpdateDiff(
        Inventory.Models.InventorySnapshot before,
        Inventory.Models.InventorySnapshot after)
    {
        var beforeIndex = new Dictionary<string, InstalledSkill>(StringComparer.Ordinal);
        foreach (var s in before.Skills) beforeIndex[s.ResolvedPath] = s;

        var changed = new List<UpdateDiffEntry>();
        foreach (var a in after.Skills)
        {
            if (!beforeIndex.TryGetValue(a.ResolvedPath, out var b)) continue;
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
        => Console.Out.WriteLine(RenderUpdateJson(r, p, added, changed));

    internal static string RenderUpdateJson(
        UpdateResult r, ParsedUpdateArgs p,
        IReadOnlyList<InstalledSkill> added,
        IReadOnlyList<UpdateDiffEntry> changed)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteBoolean("dryRun", r.DryRun);
            w.WriteBoolean("succeeded", r.Succeeded);
            w.WriteNumber("exitCode", r.ExitCode);
            if (r.ErrorMessage is not null) w.WriteString("errorMessage", r.ErrorMessage);
            w.WriteBoolean("all", p.All);
            w.WriteBoolean("force", p.Force);
            w.WriteBoolean("unpin", p.Unpin);
            w.WriteBoolean("yes", p.Yes);

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
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    internal static IReadOnlyList<InstalledSkill> InventoryDiff(
        Inventory.Models.InventorySnapshot before,
        Inventory.Models.InventorySnapshot after)
    {
        var beforeKeys = new HashSet<string>(
            before.Skills.Select(s => s.ResolvedPath),
            StringComparer.Ordinal);
        var added = new List<InstalledSkill>();
        foreach (var s in after.Skills)
        {
            if (!beforeKeys.Contains(s.ResolvedPath)) added.Add(s);
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
        => Console.Out.WriteLine(RenderInstallJson(r, p, added));

    internal static string RenderInstallJson(InstallResult r, ParsedInstallArgs p, IReadOnlyList<InstalledSkill> added)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
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
            w.WriteString("repoPath", p.RepoPath);

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
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task<int> RemoveAsync(AppOptions options, TuiServices services)
    {
        var parsed = ParseRemoveArgs(options.SubcommandArgs);
        if (parsed.Name is null)
        {
            Console.Error.WriteLine("skillview: remove requires a skill name");
            Console.Error.WriteLine("usage: skillview remove <name> [--agent <id>] [--yes] [--json]");
            return ExitCodes.InvalidUsage;
        }

        var report = await services.EnvironmentProbe.ProbeAsync().ConfigureAwait(false);
        var snapshot = await services.InventoryService.CaptureAsync(
            report.GhPath,
            report.Capabilities,
            new Inventory.LocalInventoryService.Options(
                ScanRoots: options.ScanRoots,
                AllowHiddenDirs: false)
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
        var validation = RemoveValidator.Validate(target, snapshot.ScannedRoots, snapshot.Skills);

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
        else if (validation.RequiresSecondConfirm && !parsed.Yes)
        {
            result = new RemoveService.RemoveReport(
                Succeeded: false,
                ResolvedPath: validation.ResolvedPath,
                FilesDeleted: 0,
                DirectoriesDeleted: 0,
                Errors: validation.Warnings.Select(w => $"warning {w.Kind}: {w.Detail} (pass --yes to accept)").ToImmutableArray(),
                DryRun: false);
        }
        else if (!parsed.Yes)
        {
            result = services.RemoveService.Remove(validation, new RemoveService.Options(DryRun: true));
        }
        else
        {
            result = services.RemoveService.Remove(validation);
        }

        if (parsed.Json) WriteRemoveJson(result, target, parsed, validation);
        else WriteRemoveText(result, target, parsed, validation);

        if (!validation.Allowed) return ExitCodes.UserError;
        if (validation.RequiresSecondConfirm && !parsed.Yes) return ExitCodes.UserError;
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
                Console.Error.WriteLine("hint: rerun with --yes to accept the warnings");
                return;
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
            Console.Error.WriteLine($"  remove failed with {r.Errors.Length} error(s)");
            foreach (var e in r.Errors) Console.Error.WriteLine($"  · {e}");
        }
    }

    private static void WriteRemoveJson(
        RemoveService.RemoveReport r,
        InstalledSkill target,
        ParsedRemoveArgs p,
        RemoveValidator.RemoveValidation validation)
        => Console.Out.WriteLine(RenderRemoveJson(r, target, p, validation));

    internal static string RenderRemoveJson(
        RemoveService.RemoveReport r,
        InstalledSkill target,
        ParsedRemoveArgs p,
        RemoveValidator.RemoveValidation validation)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteBoolean("dryRun", r.DryRun);
            w.WriteBoolean("succeeded", r.Succeeded);
            w.WriteBoolean("allowed", validation.Allowed);
            w.WriteString("name", target.Name);
            w.WriteString("resolvedPath", validation.ResolvedPath);
            w.WriteString("scope", target.Scope.ToString());
            w.WriteNumber("filesDeleted", r.FilesDeleted);
            w.WriteNumber("directoriesDeleted", r.DirectoriesDeleted);
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
            foreach (var e in r.Errors) w.WriteStringValue(e);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task<int> CleanupAsync(AppOptions options, TuiServices services)
    {
        var parsed = ParseCleanupArgs(options.SubcommandArgs);
        var report = await services.EnvironmentProbe.ProbeAsync().ConfigureAwait(false);
        var snapshot = await services.InventoryService.CaptureAsync(
            report.GhPath,
            report.Capabilities,
            new Inventory.LocalInventoryService.Options(
                ScanRoots: options.ScanRoots,
                AllowHiddenDirs: false)
        ).ConfigureAwait(false);

        var candidates = CleanupClassifier.Classify(snapshot, snapshot.ScannedRoots);
        if (parsed.KindFilter is { Count: > 0 })
        {
            candidates = candidates.Where(c => parsed.KindFilter.Contains(c.Kind.ToString(), StringComparer.OrdinalIgnoreCase)).ToImmutableArray();
        }

        var applied = new List<(CleanupClassifier.Candidate C, RemoveService.RemoveReport R)>();
        if (parsed.Apply)
        {
            if (!parsed.Yes)
            {
                Console.Error.WriteLine("skillview: cleanup --apply requires --yes");
                return ExitCodes.UserError;
            }
            foreach (var c in candidates)
            {
                RemoveValidator.RemoveValidation validation;
                if (c.Skill is not null)
                {
                    validation = RemoveValidator.Validate(c.Skill, snapshot.ScannedRoots, snapshot.Skills);
                }
                else
                {
                    validation = SyntheticEmptyDirValidation(c.Path, snapshot.ScannedRoots);
                }
                if (!validation.Allowed || validation.RequiresSecondConfirm)
                {
                    applied.Add((c, RemoveService.RemoveReport.Refused(validation.ResolvedPath,
                        validation.Allowed ? "requires second-confirm" : "validation refused")));
                    continue;
                }
                applied.Add((c, services.RemoveService.Remove(validation)));
            }
        }

        if (parsed.Json) WriteCleanupJson(candidates, applied, parsed);
        else WriteCleanupText(candidates, applied, parsed);

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
        if (parsed.Apply && applied.Any(a => !a.R.Succeeded)) return ExitCodes.UserError;
        return ExitCodes.Success;
    }

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

    private static RemoveValidator.RemoveValidation SyntheticEmptyDirValidation(
        string path,
        IReadOnlyList<ScanRoot> scanRoots)
    {
        var errors = ImmutableArray.CreateBuilder<RemoveValidator.Error>();
        var inside = false;
        foreach (var root in scanRoots)
        {
            if (PathResolver.IsInside(path, root.Path)) { inside = true; break; }
        }
        if (!inside)
        {
            errors.Add(new RemoveValidator.Error(
                RemoveValidator.ErrorKind.OutsideKnownRoots,
                $"'{path}' not inside any scan root"));
        }
        if (Directory.Exists(Path.Combine(path, ".git")))
        {
            errors.Add(new RemoveValidator.Error(
                RemoveValidator.ErrorKind.ContainsGitDirectory,
                $"'{path}' contains .git"));
        }
        return new RemoveValidator.RemoveValidation(
            errors.ToImmutable(),
            ImmutableArray<RemoveValidator.Warning>.Empty,
            path,
            ImmutableArray<string>.Empty);
    }

    private static void WriteCleanupText(
        IReadOnlyList<CleanupClassifier.Candidate> candidates,
        IReadOnlyList<(CleanupClassifier.Candidate C, RemoveService.RemoveReport R)> applied,
        ParsedCleanupArgs p)
    {
        Console.Out.WriteLine($"cleanup: {candidates.Count} candidate(s){(p.Apply ? " (--apply)" : "")}");
        foreach (var c in candidates)
        {
            Console.Out.WriteLine($"  {c.Kind,-22}  {c.Path}");
            Console.Out.WriteLine($"    why: {c.Reason}");
        }
        if (applied.Count > 0)
        {
            var ok = applied.Count(a => a.R.Succeeded);
            Console.Out.WriteLine();
            Console.Out.WriteLine($"applied: {ok}/{applied.Count} succeeded");
            foreach (var (c, r) in applied.Where(a => !a.R.Succeeded))
            {
                Console.Out.WriteLine($"  ✗ {c.Path}: {string.Join("; ", r.Errors)}");
            }
        }
    }

    private static void WriteCleanupJson(
        IReadOnlyList<CleanupClassifier.Candidate> candidates,
        IReadOnlyList<(CleanupClassifier.Candidate C, RemoveService.RemoveReport R)> applied,
        ParsedCleanupArgs p)
        => Console.Out.WriteLine(RenderCleanupJson(candidates, applied, p));

    internal static string RenderCleanupJson(
        IReadOnlyList<CleanupClassifier.Candidate> candidates,
        IReadOnlyList<(CleanupClassifier.Candidate C, RemoveService.RemoveReport R)> applied,
        ParsedCleanupArgs p)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
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
            }
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
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

    private static int UnknownSubcommand(string name, Logger logger)
    {
        logger.Warn("cli", $"unknown subcommand '{name}'");
        Console.Error.WriteLine($"skillview: unknown subcommand '{name}'");
        Console.Error.WriteLine("try `skillview --help`");
        return ExitCodes.InvalidUsage;
    }

    private static int PrintHelp()
    {
        Console.Out.WriteLine("""
            SkillView — view and manage AI agent skills via gh skill.

            Usage:
              skillview [--debug] [--scan-root <path>]
              skillview <subcommand> [args]

            Subcommands:
              doctor [--json] [--clear-logs]
                                  Show environment, auth, and gh support details
              list   [--json] [--scope=project|user|custom]
                     [--agent=<id>] [--path=<dir>] [--allow-hidden-dirs]
                                  List installed skills. Uses `gh skill list`
                                  when available; otherwise scans the filesystem.
              rescan              Capture a fresh inventory snapshot
              search <query> [--owner <o>] [--limit <n>] [--page <n>] [--json]
                                  Search available skills
              preview <owner/repo>[@<ref>] [<skill>] [--version <ref>] [--json]
                                  Show a skill preview
              install <owner/repo>[@<ref>] [<skill>] [--agent <id>]...
                      [--scope project|user|custom] [--path <dir>]
                      [--version <ref>] [--pin] [--force] [--upstream <url>]
                      [--repo-path <p>] [--from-local] [--allow-hidden-dirs] [--json]
                                  Install a skill and refresh the inventory
              update [<skill>]... [--all] [--dry-run] [--force] [--unpin]
                     [--yes] [--json]
                                  Update installed skills. After a real update,
                                  SkillView refreshes the inventory.
                                  Refuses --all without --yes on gh builds
                                  that would otherwise stop for confirmation.
              remove <name> [--agent <id>] [--yes] [--json]
                                  Safely remove an installed skill.
                                  Dry-run by default; re-run with --yes
                                  to make changes.
                                  Requires --yes to accept warnings such as
                                  git-tracked files or incoming symlinks.
              cleanup [--candidates=kind,...] [--apply] [--yes]
                      [--json] [--output <path>]
                                  Find and optionally remove cleanup candidates:
                                  malformed, orphan, duplicate,
                                  broken-symlink, hidden-nested,
                                  broken-shared-mapping, and empty-directory.
                                  Respects `.skillview-ignore` markers.
                                  --apply requires --yes.

            Global flags:
              --debug             Enable Debug-level logging; accepted anywhere
                                  on the command line (before or after the
                                  subcommand). Streams to stderr in CLI mode.
              --scan-root <path>  Add a custom scan root (repeatable)
              --help | -h         Show this help
              --version | -V      Show version

            Environment:
              SKILLVIEW_LOG=debug  Alternative to --debug (flag takes precedence)

            Exit codes (aligned with cli/cli#13215):
               0  Success / nothing to do
               1  User-level error (input, conflict, refused destructive op)
               2  Invalid usage (bad flags, missing args)
              10  Environment error (gh missing / too old / no capability)
              20  No matches

            With no subcommand, the TUI launches.
            """);
        return ExitCodes.Success;
    }

    private static int PrintVersion()
    {
        var version = typeof(CliDispatcher).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        Console.Out.WriteLine($"skillview {version}");
        return ExitCodes.Success;
    }
}
