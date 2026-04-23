using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Diagnostics;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory.Models;
using SkillView.Logging;
using SkillView.Ui;

namespace SkillView.Cli;

/// Non-interactive subcommand router. Phase 1 implements `doctor`; Phase 2
/// adds `list` and `rescan`. Phases 3–7 fill in `remove`, `cleanup`, etc.
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
            "remove" or "cleanup" => NotYetImplemented(options.SubcommandName!, services.Logger),
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
        Console.Out.WriteLine(System.Text.Encoding.UTF8.GetString(ms.ToArray()));
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
        Console.Out.WriteLine($"rescan: {snapshot.Skills.Length} skill(s) across {snapshot.ScannedRoots.Length} root(s)" +
                              (snapshot.UsedGhSkillList ? " (gh skill list used)" : " (filesystem only)"));
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

    private static (string? scope, string? agent, string? path, List<string> scanRoots) ParseListArgs(
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
        Console.Out.WriteLine(System.Text.Encoding.UTF8.GetString(ms.ToArray()));
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

    private record ParsedSearchArgs(string? Query, string? Owner, int? Limit, int? Page, bool Json);

    private static ParsedSearchArgs ParseSearchArgs(IReadOnlyList<string> args)
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
        Console.Out.WriteLine(System.Text.Encoding.UTF8.GetString(ms.ToArray()));
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

    private record ParsedPreviewArgs(string? Repo, string? SkillName, string? Version, bool Json);

    private static ParsedPreviewArgs ParsePreviewArgs(IReadOnlyList<string> args)
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
        Console.Out.WriteLine(System.Text.Encoding.UTF8.GetString(ms.ToArray()));
    }

    private static int NotYetImplemented(string name, Logger logger)
    {
        logger.Warn("cli", $"subcommand '{name}' is not yet implemented (scheduled for Phase 2-7)");
        Console.Error.WriteLine($"skillview: '{name}' is not yet implemented");
        return ExitCodes.EnvironmentError;
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
                                  Environment, auth, and gh capability report
              list   [--json] [--scope=project|user|custom]
                     [--agent=<id>] [--path=<dir>] [--allow-hidden-dirs]
                                  Installed-skill inventory (gh skill list
                                  when the capability probe detects it,
                                  filesystem scan otherwise)
              rescan              Capture a fresh inventory snapshot
              search <query> [--owner <o>] [--limit <n>] [--page <n>] [--json]
                                  gh skill search adapter
              preview <owner/repo>[@<ref>] [<skill>] [--version <ref>] [--json]
                                  gh skill preview adapter
              remove <name>       (Phase 6) safe skill removal
              cleanup             (Phase 6) cleanup candidate review

            Global flags:
              --debug             Enable Debug-level logging (streams to stderr in CLI mode)
              --scan-root <path>  Add a custom scan root (repeatable)
              --help | -h         Show this help
              --version | -V      Show version

            Environment:
              SKILLVIEW_LOG=debug  Alternative to --debug (flag takes precedence)

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
