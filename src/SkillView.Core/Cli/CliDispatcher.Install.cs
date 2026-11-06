using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Gh;
using SkillView.Gh.Models;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Ui;

namespace SkillView.Cli;

public static partial class CliDispatcher
{

    private static async Task<int> InstallAsync(
        AppOptions options,
        TuiServices services,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < options.SubcommandArgs.Count; i++)
        {
            var arg = options.SubcommandArgs[i];
            var oldValueForm = arg.StartsWith("--upstream=", StringComparison.Ordinal);
            var oldSeparatedForm = arg == "--upstream" && i + 1 < options.SubcommandArgs.Count &&
                (options.SubcommandArgs[i + 1].StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                 options.SubcommandArgs[i + 1].StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 options.SubcommandArgs[i + 1].StartsWith("git@", StringComparison.OrdinalIgnoreCase));
            if (oldValueForm || oldSeparatedForm)
            {
                Console.Error.WriteLine("skillview: --upstream is a flag and does not accept a URL");
                return ExitCodes.InvalidUsage;
            }
        }

        var parsed = ParseInstallArgs(options.SubcommandArgs);
        if (parsed.Repo is null)
        {
            Console.Error.WriteLine("skillview: install requires a repo (OWNER/REPO)");
            Console.Error.WriteLine(
                "usage: skillview install <owner/repo>[@<ref>] [<skill>|--all] [--agent <id>]..." +
                " [--scope project|user] [--path <dir>] [--version <ref>] [--pin]" +
                " [--force] [--upstream] [--from-local]" +
                " [--allow-hidden-dirs] [--json]");
            return ExitCodes.InvalidUsage;
        }

        if (parsed.Pin && string.IsNullOrWhiteSpace(parsed.Version))
        {
            Console.Error.WriteLine("skillview: --pin requires --version <ref> or OWNER/REPO@<ref>");
            return ExitCodes.InvalidUsage;
        }

        var report = await services.EnvironmentProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!report.BaselineOk)
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

        var result = await services.InstallService.InstallAsync(
            report.GhPath!,
            parsed.Repo,
            parsed.SkillName,
            installOptions,
            cancellationToken
        ).ConfigureAwait(false);

        InventorySnapshot? observed = null;
        string? refreshError = null;
        if (result.MayHaveChangedFiles)
        {
            services.ListAdapter.Invalidate();
            if (!result.ChildDrained) refreshError = "Child completion is unverified; refresh after it stops.";
            else
            {
                using var reconciliation = new SkillView.Threading.CancellationSource(TimeSpan.FromSeconds(10),
                    SkillView.Threading.CancellationCallbackReporter.For(services.Logger, "CLI install reconciliation"));
                try
                {
                    observed = await services.InventoryService.CaptureAsync(report.GhPath,
                        new LocalInventoryService.Options(ScanRoots: options.ScanRoots,
                            WorkspaceDirectories: options.WorkspaceDirectories, Profiles: options.Profiles,
                            AllowHiddenDirs: parsed.AllowHiddenDirs)
                        { BypassGhCache = true },
                        reconciliation.Token).ConfigureAwait(false);
                    if (observed.IsPartial || !observed.ConfiguredRootsComplete)
                        refreshError = "Inventory refresh is incomplete; unknown files remain protected.";
                }
                catch (Exception ex) { refreshError = ex.Message; }
            }
        }
        // A preflight refusal must not trigger inventory I/O. Without a
        // pre-install capture, report observations rather than invented additions.
        if (parsed.Json) WriteInstallJson(result, parsed, added: null, observed, refreshError);
        else WriteInstallText(result, observed, refreshError, Console.Out, Console.Error);
        return result.Canceled ? ExitCodes.Cancelled : result.Succeeded ? ExitCodes.Success : ExitCodes.EnvironmentError;
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
        bool Upstream,
        bool FromLocal,
        bool AllowHiddenDirs,
        bool Json,
        bool All);

    internal static ParsedInstallArgs ParseInstallArgs(IReadOnlyList<string> args)
    {
        string? version = null, scope = null, path = null;
        var agents = new List<string>();
        var positional = new List<string>();
        bool pin = false, force = false, upstream = false, fromLocal = false, allowHidden = false, json = false, all = false;

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
            if (a == "--upstream") { upstream = true; continue; }
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

    internal static void WriteInstallText(InstallResult result, InventorySnapshot? observed, string? refreshError,
        TextWriter output, TextWriter error)
    {
        var completeObservation = result.ChildDrained && refreshError is null
            && observed is { IsPartial: false, ConfiguredRootsComplete: true };
        var cleanSuccess = result.Succeeded && !result.Canceled && completeObservation;
        if (cleanSuccess)
            output.WriteLine($"installed: {result.Repo}{(result.SkillName is null ? "" : "/" + result.SkillName)}"
                + (result.Version is null ? "" : $"@{result.Version}"));
        if (!string.IsNullOrWhiteSpace(result.StdOut)) output.WriteLine(result.StdOut.TrimEnd());
        if (result.Succeeded && !string.IsNullOrWhiteSpace(result.StdErr)) error.WriteLine(result.StdErr.TrimEnd());
        if (!result.Succeeded)
            error.WriteLine($"skillview: install {(result.Canceled ? "canceled" : "failed")} (exit {result.ExitCode}): {result.ErrorMessage}");
        if (result.MayHaveChangedFiles && !cleanSuccess)
            error.WriteLine("The install may have changed files. " + (completeObservation
                ? "Inventory refreshed; review the result before retrying."
                : "Inventory refresh could not establish complete coverage; refresh explicitly before retrying. "
                    + (refreshError ?? "A complete fresh observation is unavailable.")));
        else if (!result.MayHaveChangedFiles) error.WriteLine("No install started.");
        if (completeObservation) output.WriteLine($"rescan: {observed!.Skills.Length} skill(s) observed");
    }

    private static void WriteInstallJson(InstallResult r, ParsedInstallArgs p, IReadOnlyList<InstalledSkill>? added,
        InventorySnapshot? observed = null, string? refreshError = null)
        => WriteJson(w => WriteInstallJson(w, r, p, added, observed, refreshError));

    internal static string RenderInstallJson(InstallResult r, ParsedInstallArgs p, IReadOnlyList<InstalledSkill>? added,
        InventorySnapshot? observed = null, string? refreshError = null)
        => RenderJson(w => WriteInstallJson(w, r, p, added, observed, refreshError));

    private static void WriteInstallJson(
        Utf8JsonWriter w,
        InstallResult r,
        ParsedInstallArgs p,
        IReadOnlyList<InstalledSkill>? added, InventorySnapshot? observed = null, string? refreshError = null)
    {
        w.WriteStartObject();
        w.WriteString("repo", r.Repo);
        w.WriteString("skillName", r.SkillName);
        w.WriteString("version", r.Version);
        w.WriteBoolean("succeeded", r.Succeeded);
        w.WriteString("attempt", r.Attempt.ToString());
        w.WriteBoolean("mayHaveChangedFiles", r.MayHaveChangedFiles);
        w.WriteBoolean("childDrained", r.ChildDrained);
        w.WriteBoolean("canceled", r.Canceled);
        w.WriteString("stdout", r.StdOut);
        w.WriteString("stderr", r.StdErr);
        w.WriteBoolean("stdoutTruncated", r.StdOutTruncated);
        w.WriteBoolean("stderrTruncated", r.StdErrTruncated);
        w.WriteString("refresh", !r.MayHaveChangedFiles ? "not-needed" : !r.ChildDrained ? "writer-unverified"
            : observed is not null ? observed.IsPartial || !observed.ConfiguredRootsComplete ? "incomplete" : "completed"
            : refreshError is not null ? "failed" : "not-captured");
        if (refreshError is not null) w.WriteString("refreshError", refreshError);
        w.WriteStartArray("observed");
        if (observed is not null)
            foreach (var skill in observed.Skills)
            {
                w.WriteStartObject();
                w.WriteString("name", skill.Name);
                w.WriteString("resolvedPath", skill.ResolvedPath);
                w.WriteEndObject();
            }
        w.WriteEndArray();
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

        w.WriteBoolean("addedKnown", added is not null);
        if (added is null) w.WriteNull("added");
        else
        {
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
        }

        w.WriteEndObject();
    }
}
