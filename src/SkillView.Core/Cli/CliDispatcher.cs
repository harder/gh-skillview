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
public static partial class CliDispatcher
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
            CancellationCallbackReporter.For(services.Logger, "CLI deadline"));

        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            return options.SubcommandName switch
            {
                "doctor" => await DoctorAsync(options, services, deadline.Token).ConfigureAwait(false),
                "list" => await ListAsync(options, services, deadline.Token).ConfigureAwait(false),
                "effective" => await EffectiveAsync(options, services, deadline.Token).ConfigureAwait(false),
                "rescan" => await RescanAsync(options, services, deadline.Token).ConfigureAwait(false),
                "search" => await SearchAsync(options, services, deadline.Token).ConfigureAwait(false),
                "preview" => await PreviewAsync(options, services, deadline.Token).ConfigureAwait(false),
                "install" => await InstallAsync(options, services, deadline.Token).ConfigureAwait(false),
                "update" => await UpdateAsync(options, services, deadline.Token).ConfigureAwait(false),
                "compare" => await CompareAsync(options, services, deadline.Token).ConfigureAwait(false),
                "backup" => await BackupAsync(options, services, deadline.Token).ConfigureAwait(false),
                "history" => await HistoryAsync(options, services, deadline.Token).ConfigureAwait(false),
                "update-merge-review" or "update-merge-choose" or "update-merge-plan" or "update-preserve" => await LocalReviewAsync(options, services, deadline.Token).ConfigureAwait(false),
                "restore-plan" or "restore-inspect" or "restore-apply" or "restore-recover" or "restore-undo"
                    or "update-plan" or "update-inspect" or "update-apply" or "update-recover" or "update-undo" or "update-repair-owner"
                    or "unpin-plan" or "unpin-inspect" or "unpin-apply" or "unpin-recover" or "unpin-undo" or "unpin-repair-owner" => await RestoreAsync(options, services, deadline.Token).ConfigureAwait(false),
                "quarantine" or "undo" or "quarantine-history" => await QuarantineAsync(options, services, deadline.Token).ConfigureAwait(false),
                "remove" => await RemoveAsync(options, services, deadline.Token).ConfigureAwait(false),
                "cleanup" => await CleanupAsync(options, services, deadline.Token).ConfigureAwait(false),
                "ignore" => await IgnoreAsync(options, services, deadline.Token).ConfigureAwait(false),
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
        "list" or "effective" or "rescan" or "search" or "compare" or "history" or "ignore" => TimeSpan.FromMinutes(2),
        "install" or "update" or "remove" or "cleanup" or "backup" or "quarantine" or "undo" or "quarantine-history" or "restore-plan" or "restore-inspect" or "restore-apply" or "restore-recover" or "restore-undo"
            or "update-plan" or "update-inspect" or "update-apply" or "update-recover" or "update-undo" or "update-repair-owner"
            or "unpin-plan" or "unpin-inspect" or "unpin-apply" or "unpin-recover" or "unpin-undo" or "unpin-repair-owner"
            or "update-merge-review" or "update-merge-choose" or "update-merge-plan" or "update-preserve" => TimeSpan.FromMinutes(10),
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
        var inventory = options.SubcommandArgs.Contains("--inventory")
            ? await services.InventoryService.CaptureAsync(report.GhPath,
                new LocalInventoryService.Options(options.ScanRoots, false, WorkspaceDirectories: options.WorkspaceDirectories, Profiles: options.Profiles), cancellationToken).ConfigureAwait(false)
            : null;
        var json = options.SubcommandArgs.Contains("--json");
        if (json)
        {
            WriteDoctorJson(report, options, inventory);
        }
        else
        {
            WriteDoctorText(report, options, inventory);
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

    private static void WriteDoctorText(EnvironmentReport r, AppOptions options, InventorySnapshot? inventory = null)
        => Console.Out.Write(RenderDoctorText(r, options, inventory));

    internal static string RenderDoctorText(EnvironmentReport r, AppOptions options, InventorySnapshot? inventory = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"invocation    : {options.InvocationMode}");
        sb.AppendLine($"gh path       : {r.GhPath ?? "(not found)"}");
        sb.AppendLine($"gh version    : {r.GhVersionRaw ?? "(unknown)"}");
        sb.AppendLine($"gh minimum    : {GhBinaryLocator.MinimumVersion}{(r.GhMeetsMinimum ? " ✓" : " ✗ too old")}");
        sb.AppendLine($"gh auth       : {AuthSummary(r.Auth)}");
        sb.AppendLine($"gh skill      : {(r.GhSkillAvailable ? "present" : "(not detected)")}");
        sb.AppendLine($"debug         : {options.Debug}");
        sb.AppendLine($"log directory : {r.LogDirectory ?? "(unset)"}");
        sb.AppendLine($"scan roots    : {(options.ScanRoots.Count == 0 ? "(default)" : string.Join(", ", options.ScanRoots))}");
        sb.AppendLine($"baseline      : {(r.BaselineOk ? "ok" : "degraded")}");
        if (inventory is not null) sb.Append(DoctorScreen.RenderManagedInventory(inventory));
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

    private static void WriteDoctorJson(EnvironmentReport r, AppOptions options, InventorySnapshot? inventory = null)
        => WriteJson(w => WriteDoctorJson(w, r, options, inventory));

    internal static string RenderDoctorJson(EnvironmentReport r, AppOptions options, InventorySnapshot? inventory = null)
        => RenderJson(w => WriteDoctorJson(w, r, options, inventory));

    private static void WriteDoctorJson(Utf8JsonWriter writer, EnvironmentReport r, AppOptions options, InventorySnapshot? inventory = null)
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

        // The minimum supported gh version guarantees the required command
        // surface; the smoke check records whether `gh skill` is available.
        writer.WriteBoolean("ghSkillAvailable", r.GhSkillAvailable);

        writer.WriteStartArray("scanRoots");
        foreach (var root in options.ScanRoots) writer.WriteStringValue(root);
        writer.WriteEndArray();

        if (inventory is not null)
        {
            writer.WritePropertyName("inventory");
            WriteListJson(writer, inventory);
        }

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
        var report = await services.EnvironmentProbe.ProbeAsync(cancellationToken, includeAuthentication: false).ConfigureAwait(false);
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
                FilterAgent: agent, WorkspaceDirectories: options.WorkspaceDirectories, Profiles: options.Profiles)
        , cancellationToken).ConfigureAwait(false);
    }

    private static void WriteListText(Inventory.Models.InventorySnapshot snapshot)
    {
        foreach (var diagnostic in snapshot.PreferenceDiagnostics)
            Console.Out.WriteLine(TerminalEscapeSanitizer.Sanitize(diagnostic));
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
            if (skill.CleanupSuppression is { State: not "none" } suppression)
                Console.Out.WriteLine(TerminalEscapeSanitizer.Sanitize($"  cleanup suppression: {suppression.State}; legacy={suppression.LegacyMarker}, external={suppression.External}. {suppression.Diagnostic}"));
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
        w.WriteNumber("schemaVersion", 3);
        w.WriteBoolean("partial", snapshot.IsPartial);
        w.WriteStartArray("preferenceDiagnostics");
        foreach (var message in snapshot.PreferenceDiagnostics) w.WriteStringValue(message);
        w.WriteEndArray();
        w.WriteStartArray("inaccessiblePaths");
        foreach (var path in snapshot.Diagnostics.InaccessiblePaths) w.WriteStringValue(path);
        w.WriteEndArray();
        w.WriteStartArray("providers");
        foreach (var provider in snapshot.Providers)
        {
            w.WriteStartObject();
            w.WriteString("provider", provider.Provider);
            w.WriteBoolean("succeeded", provider.Succeeded);
            w.WriteString("error", provider.Error);
            w.WriteStartArray("coveredRoots");
            foreach (var path in provider.CoveredRoots) w.WriteStringValue(path);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("discoveryLocations");
        foreach (var location in snapshot.Diagnostics.Locations)
        {
            w.WriteStartObject();
            w.WriteString("path", location.Path); w.WriteString("state", location.State); w.WriteString("reason", location.Reason);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("ownerRecords");
        foreach (var owner in snapshot.OwnershipRecords) WriteOwnerRecord(w, owner);
        w.WriteEndArray();

        w.WriteStartArray("managedRegions");
        foreach (var region in snapshot.ManagedRegions)
        {
            w.WriteStartObject();
            w.WriteString("path", region.Path);
            w.WriteString("canonicalBoundaryPath", region.ObservedCanonicalPath);
            w.WriteString("provider", region.Provider);
            w.WriteString("agent", region.AgentId);
            w.WriteString("kind", region.Kind);
            w.WriteString("plugin", region.PluginId);
            w.WriteString("distributorVersion", region.PackageVersion);
            w.WriteString("context", region.Context);
            w.WriteString("registrationScope", region.RegistrationScope?.ToString());
            w.WriteString("registrationPath", region.RegistrationPath);
            w.WriteBoolean("readOnly", true);
            w.WriteString("activeSessionState", "unknown");
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("scannedRoots");
        foreach (var r in snapshot.ScannedRoots)
        {
            w.WriteStartObject();
            w.WriteString("path", r.Path);
            w.WriteString("scope", r.Scope.ToString());
            w.WriteString("agentHint", r.AgentHint);
            w.WriteString("profile", r.Profile);
            w.WriteString("context", r.Context);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("skills");
        foreach (var skill in snapshot.Skills)
        {
            w.WriteStartObject();
            w.WriteString("name", skill.Name);
            w.WriteString("installationId", skill.InstallationId);
            w.WriteString("resolvedPath", skill.ResolvedPath);
            w.WriteString("scanRoot", skill.ScanRoot);
            w.WriteString("scope", skill.Scope.ToString());
            w.WriteString("provenance", skill.Provenance.ToString());
            w.WriteString("validity", skill.Validity.ToString());
            w.WriteBoolean("pinned", skill.Pinned);
            w.WriteBoolean("isSymlinked", skill.IsSymlinked);
            w.WriteBoolean("ignored", skill.Ignored);
            WriteSuppression(w, skill.CleanupSuppression);
            w.WriteString("githubTreeSha", skill.TreeSha);
            w.WriteString("version", skill.FrontMatter.Version);
            w.WriteString("description", skill.FrontMatter.Description);
            w.WriteString("source", skill.FrontMatter.Upstream);
            w.WriteString("repositoryRef", skill.FrontMatter.GithubRef);
            w.WriteString("sourcePath", skill.FrontMatter.GithubPath);
            w.WriteString("pinnedRef", skill.FrontMatter.PinnedRef);
            w.WriteString("localSourcePath", skill.FrontMatter.LocalPath);
            var sourceIdentity = skill.SourceIdentity;
            w.WriteStartObject("sourceIdentity");
            w.WriteString("id", sourceIdentity.Key);
            w.WriteString("provider", sourceIdentity.Provider);
            w.WriteString("origin", sourceIdentity.Origin);
            w.WriteString("subpath", sourceIdentity.Subpath);
            w.WriteString("evidence", sourceIdentity.Evidence);
            w.WriteBoolean("conflicted", sourceIdentity.Conflicted);
            w.WriteEndObject();
            var revision = skill.Revision;
            w.WriteStartObject("revision");
            w.WriteString("authoredVersion", revision.AuthoredVersion);
            w.WriteString("repositoryRef", revision.RepositoryRef);
            w.WriteString("recordedTree", revision.RecordedTree);
            w.WriteString("pinnedRef", revision.PinnedRef);
            w.WriteString("payloadFingerprint", revision.PayloadFingerprint);
            w.WriteNull("resolvedCommit");
            w.WriteString("ownerRecordedHash", revision.OwnerRecordedHash);
            w.WriteString("ownerHashKind", revision.OwnerHashKind);
            w.WriteString("distributorVersion", revision.DistributorVersion);
            w.WriteEndObject();
            w.WriteStartObject("physicalInstallation");
            w.WriteString("id", skill.InstallationId);
            w.WriteString("path", skill.ResolvedPath);
            w.WriteString("materialization", skill.PhysicalInstallation.Materialization);
            w.WriteString("idRole", "installation-address");
            w.WriteBoolean("nativeIdentityCaptured", skill.ObservedSubject?.NativeObject.Status == "observed");
            WriteNativeObservation(w, "nativeObject", skill.ObservedSubject?.NativeObject);
            w.WriteEndObject();
            if (skill.ObservedSubject is { } subject) WriteSubject(w, subject);
            WriteDistributors(w, skill.ObservedSubject?.Distributors ?? skill.AllOwnershipClaims.Select(SkillDistributorIdentity.From).ToImmutableArray());
            w.WriteStartArray("ownerRecords");
            foreach (var owner in skill.AllOwnershipClaims)
                WriteOwnerRecord(w, owner);
            w.WriteEndArray();
            w.WriteStartArray("diagnostics");
            foreach (var diagnostic in skill.FrontMatter.Diagnostics.Concat(skill.MetadataConflicts).Concat(skill.OwnershipDiagnostics)) w.WriteStringValue(diagnostic);
            w.WriteEndArray();
            w.WriteStartArray("agents");
            foreach (var a in skill.Agents)
            {
                w.WriteStartObject();
                w.WriteString("id", a.AgentId);
                w.WriteString("path", a.Path);
                w.WriteBoolean("isSymlink", a.IsSymlink);
                w.WriteString("scope", a.Scope?.ToString());
                w.WriteString("context", a.Context);
                w.WriteString("discoveryRoot", a.DiscoveryRoot);
                w.WriteString("profile", a.Profile);
                w.WriteString("agentVersion", a.AgentVersion);
                if (a.Enabled is { } enabled) w.WriteBoolean("enabled", enabled); else w.WriteNull("enabled");
                w.WriteString("loadingRule", a.LoadingRule);
                w.WriteString("activeSessionState", "unknown");
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteEndObject();
    }

    private static void WriteOwnerRecord(Utf8JsonWriter w, SkillPackage owner)
    {
        w.WriteStartObject();
        w.WriteString("provider", owner.Provider);
        w.WriteString("recordPath", owner.LockFilePath);
        w.WriteString("recordFingerprint", owner.RecordFingerprint);
        w.WriteString("entryFingerprint", owner.EntryFingerprint);
        w.WriteString("contextRoot", owner.ContextRoot);
        w.WriteString("installationRoot", owner.InstallationRoot);
        w.WriteStartArray("coveredRoots");
        foreach (var root in owner.CoveredRoots) w.WriteStringValue(root);
        w.WriteEndArray();
        w.WriteString("selector", owner.Selector);
        w.WriteString("source", owner.Source);
        w.WriteString("sourceType", owner.SourceType);
        w.WriteString("sourceUrl", owner.SourceUrl);
        w.WriteString("sourcePath", owner.SkillPath);
        w.WriteString("repositoryRef", owner.RepositoryRef);
        w.WriteString("pinnedRef", owner.PinnedRef);
        w.WriteString("distributorVersion", owner.PackageVersion);
        w.WriteString("contentHash", owner.ContentHash);
        w.WriteString("hashKind", owner.HashKind);
        w.WriteString("installationMode", owner.InstallationMode);
        w.WriteBoolean("exclusiveOwner", owner.IsExclusiveOwner);
        w.WriteBoolean("readOnly", owner.ReadOnly);
        w.WriteString("diagnostic", owner.Diagnostic);
        if (owner.SchemaVersion is { } schema) w.WriteNumber("schemaVersion", schema); else w.WriteNull("schemaVersion");
        w.WriteEndObject();
    }

    private static async Task<int> QuarantineAsync(AppOptions options, TuiServices services, CancellationToken token)
    {
        var command = options.SubcommandName;
        string? path = null, id = null, expected = null;
        var json = false; var execute = false; var standalone = false;
        var args = options.SubcommandArgs;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--json") { json = true; continue; }
            if (args[i] == "--yes" && command != "quarantine-history") { execute = true; continue; }
            if (args[i] == "--standalone" && command == "quarantine") { standalone = true; continue; }
            if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                if (args[i] == "--path" && command == "quarantine" && path is null && id is null) { path = args[++i]; continue; }
                if (args[i] == "--install-id" && command == "quarantine" && path is null && id is null) { id = args[++i]; continue; }
                if (args[i] == "--id" && command == "undo" && id is null) { id = args[++i]; continue; }
                if (args[i] == "--expect" && command == "quarantine" && expected is null) { expected = args[++i]; continue; }
            }
            Console.Error.WriteLine("skillview: unsupported or incomplete quarantine/undo argument");
            return ExitCodes.InvalidUsage;
        }
        if (command == "quarantine" && ((path is null) == (id is null)
                || path is not null && string.IsNullOrWhiteSpace(path) || id is not null && string.IsNullOrWhiteSpace(id)
                || execute && (!standalone || !SkillView.Maintenance.QuarantineStore.ValidHash(expected!)))
            || command == "undo" && !SkillView.Maintenance.QuarantineStore.ValidId(id!)
            || expected is not null && !SkillView.Maintenance.QuarantineStore.ValidHash(expected))
        {
            Console.Error.WriteLine("skillview: quarantine needs one --path/--install-id; apply needs --yes --standalone --expect <preview fingerprint>. Undo needs --id <operation ID>.");
            return ExitCodes.InvalidUsage;
        }
        OperationImpact? frozenImpact = null;
        try
        {
            if (path is not null) path = Path.GetFullPath(path, services.Runner.EffectiveWorkingDirectory);
            var inventory = await services.InventoryService.CaptureAsync(services.GhLocator.FindOnPath(),
                new LocalInventoryService.Options(options.ScanRoots, false, WorkspaceDirectories: options.WorkspaceDirectories, Profiles: options.Profiles), token).ConfigureAwait(false);
            var backupRoot = SkillView.Maintenance.MaintenanceBackupStore.ResolveRoot(services.Runner);
            var store = new SkillView.Maintenance.QuarantineStore(Path.Combine(Path.GetDirectoryName(backupRoot)!, "quarantine"), services.Logger, runner: services.Runner,
                subjectObserved: impact => frozenImpact = impact with { Operation = command! });
            IReadOnlyList<SkillView.Maintenance.QuarantineResult> results;
            if (command == "quarantine-history") results = await Task.Run(() => store.History(inventory, token), token).ConfigureAwait(false);
            else if (command == "undo") results = [await Task.Run(() => store.Undo(id!, inventory, execute, token), token).ConfigureAwait(false)];
            else
            {
                var selected = inventory.Skills.Where(skill => id is not null ? skill.InstallationId == id : PathIdentity.Equals(skill.ResolvedPath, path!)).ToArray();
                if (selected.Length != 1) throw new IOException("Select one captured standalone payload; put its --scan-root before quarantine.");
                frozenImpact = OperationImpact.Payload(OperationSubject.From(selected[0]), command!);
                if (execute) results = [await Task.Run(() => store.Apply(selected[0], inventory, expected!, standalone, token), token).ConfigureAwait(false)];
                else
                {
                    var preview = await Task.Run(() => store.Preview(selected[0], inventory, token), token).ConfigureAwait(false);
                    results = [new(preview.Id, "preview", true, false, false, preview.OriginalPath,
                        Path.Combine(store.Root, preview.Id, "payload"), preview.ReviewFingerprint,
                        "Preview only: relocate this standalone directory on the same filesystem. Retains live permissions and links; agent sessions were not observed. Apply requires --yes --standalone --expect <reviewFingerprint>.")
                    {
                        Impact = frozenImpact with
                        {
                            PayloadRetained = true,
                            RetainedPayloadPath = preview.OriginalPath,
                            RetentionEvidence = "quarantine-preview; original payload observed; no relocation attempted"
                        }
                    }];
                }
            }
            results = results.Select(result => result with
            { Impact = result.Impact is { } impact ? impact with { Operation = command! } : frozenImpact }).ToArray();
            if (json) WriteJson(w =>
            {
                w.WriteStartObject(); w.WriteNumber("schemaVersion", 1); w.WriteBoolean("preview", !execute && command != "quarantine-history");
                w.WriteBoolean("securityIsolation", false); w.WriteString("sessionState", "unknown");
                w.WriteStartArray("operations"); foreach (var result in results) WriteQuarantineResult(w, result);
                w.WriteEndArray(); w.WriteEndObject();
            });
            else foreach (var result in results) Console.WriteLine(TerminalEscapeSanitizer.Sanitize(
                $"{result.Id}  {result.Status}  {result.OriginalPath}\nReview: {result.ReviewFingerprint}\n{result.Message}"));
            if (!json) foreach (var result in results) WriteImpactText(result.Impact);
            return results.Any(result => result.Canceled) ? ExitCodes.Cancelled
                : command == "quarantine-history" ? results.Any(result => result.Status is "prepared" or "undo-prepared" or "needs-inspection" or "unrecognized") ? ExitCodes.UserError : ExitCodes.Success
                : results.All(result => result.Succeeded) ? ExitCodes.Success : ExitCodes.UserError;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or EntryPointNotFoundException
            || ex is OperationCanceledException && token.IsCancellationRequested)
        {
            if (json) WriteJson(w => { w.WriteStartObject(); w.WriteNumber("schemaVersion", 1); w.WriteBoolean("succeeded", false); w.WriteString("error", ex.Message); w.WriteBoolean("canceled", ex is OperationCanceledException); WriteImpact(w, frozenImpact); w.WriteEndObject(); });
            else Console.Error.WriteLine("skillview: " + TerminalEscapeSanitizer.Sanitize(ex.Message));
            if (!json) WriteImpactText(frozenImpact);
            return ex is OperationCanceledException ? ExitCodes.Cancelled : ex is ArgumentException ? ExitCodes.InvalidUsage : ExitCodes.UserError;
        }
    }

    private static void WriteQuarantineResult(Utf8JsonWriter w, SkillView.Maintenance.QuarantineResult result)
    {
        w.WriteStartObject(); w.WriteString("id", result.Id); w.WriteString("status", result.Status);
        w.WriteBoolean("succeeded", result.Succeeded); w.WriteBoolean("mutationObserved", result.MutationObserved);
        w.WriteBoolean("canceled", result.Canceled); w.WriteString("originalPath", result.OriginalPath);
        w.WriteString("retainedPath", result.RetainedPath); w.WriteString("reviewFingerprint", result.ReviewFingerprint);
        w.WriteString("message", result.Message); WriteImpact(w, result.Impact); w.WriteEndObject();
    }

    private static async Task<int> HistoryAsync(AppOptions options, TuiServices services, CancellationToken token)
    {
        var json = false;
        string? id = null;
        var args = options.SubcommandArgs;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--json") { json = true; continue; }
            if (args[i] == "--id" && id is null && i + 1 < args.Count) { id = args[++i]; continue; }
            Console.Error.WriteLine("skillview: unsupported or incomplete history argument");
            return ExitCodes.InvalidUsage;
        }
        if (id is not null && (id.Length != 32 || id.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c))))
        { Console.Error.WriteLine("skillview: history --id requires a lowercase 32-character backup ID"); return ExitCodes.InvalidUsage; }
        try
        {
            var store = new SkillView.Maintenance.MaintenanceBackupStore(SkillView.Maintenance.MaintenanceBackupStore.ResolveRoot(services.Runner), services.Runner);
            var entries = await Task.Run(() => store.ReadHistory(token), token).ConfigureAwait(false);
            if (id is not null) entries = entries.Where(entry => entry.Id == id).ToArray();
            if (json) WriteJson(w =>
            {
                w.WriteStartObject(); w.WriteNumber("schemaVersion", 1); w.WriteString("metadataContract", "verified raw payload evidence; no in-place restore authority");
                w.WriteStartArray("entries");
                foreach (var entry in entries)
                {
                    w.WriteStartObject(); w.WriteString("id", entry.Id); w.WriteString("status", entry.Status);
                    w.WriteString("installationId", entry.Record?.InstallationId); w.WriteString("path", entry.Record?.Path);
                    w.WriteString("fingerprint", entry.Record?.BeforeFingerprint); w.WriteString("archiveHash", entry.Record?.ArchiveHash);
                    w.WriteString("archivePath", entry.Record?.ArchivePath); w.WriteNumber("payloadEntries", entry.PayloadEntries);
                    w.WriteNumber("payloadBytes", entry.PayloadBytes); w.WriteString("error", entry.Error); w.WriteEndObject();
                }
                w.WriteEndArray(); w.WriteEndObject();
            });
            else foreach (var entry in entries) Console.WriteLine(TerminalEscapeSanitizer.Sanitize($"{entry.Id}  {entry.Status}  {entry.Record?.Path ?? entry.Error}"));
            return id is not null && entries.Count == 0 ? ExitCodes.NoMatches
                : entries.Any(entry => entry.Status != "verified") ? ExitCodes.UserError : ExitCodes.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (json) WriteJson(w => { w.WriteStartObject(); w.WriteNumber("schemaVersion", 1); w.WriteBoolean("succeeded", false); w.WriteString("error", ex.Message); w.WriteEndObject(); });
            else Console.Error.WriteLine("skillview: " + TerminalEscapeSanitizer.Sanitize(ex.Message));
            return ExitCodes.EnvironmentError;
        }
    }

    private static async Task<int> BackupAsync(AppOptions options, TuiServices services, CancellationToken token)
    {
        string? path = null, id = null;
        var selectors = 0;
        var json = false;
        var args = options.SubcommandArgs;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--json") { json = true; continue; }
            if (args[i] is "--path" or "--install-id" && i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            { if (++selectors > 1) break; if (args[i] == "--path") path = args[++i]; else id = args[++i]; continue; }
            Console.Error.WriteLine("skillview: unsupported or incomplete backup argument");
            return ExitCodes.InvalidUsage;
        }
        if (selectors != 1 || (path is null) == (id is null) || path is not null && string.IsNullOrWhiteSpace(path)
            || id is not null && string.IsNullOrWhiteSpace(id))
        {
            Console.Error.WriteLine("skillview: backup requires exactly one --path or --install-id");
            return ExitCodes.InvalidUsage;
        }
        if (path is not null)
        {
            try { path = Path.GetFullPath(path, services.Runner.EffectiveWorkingDirectory); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            { Console.Error.WriteLine("skillview: invalid backup path: " + TerminalEscapeSanitizer.Sanitize(ex.Message)); return ExitCodes.InvalidUsage; }
        }
        var snapshot = await services.InventoryService.CaptureAsync(services.GhLocator.FindOnPath(),
            new LocalInventoryService.Options(options.ScanRoots, false, WorkspaceDirectories: options.WorkspaceDirectories, Profiles: options.Profiles), token).ConfigureAwait(false);
        var selected = snapshot.Skills.Where(skill => id is not null ? skill.InstallationId == id
            : PathIdentity.Equals(skill.ResolvedPath, path!) || skill.Agents.Any(agent => PathIdentity.Equals(agent.Path, path!))).ToArray();
        if (selected.Length != 1)
        { Console.Error.WriteLine("skillview: backup selector must identify one captured payload; include its --scan-root before backup"); return ExitCodes.NoMatches; }
        var impact = OperationImpact.ObservedRetention(OperationSubject.From(selected[0]), "backup");
        try
        {
            if (!snapshot.ConfiguredRootsComplete) throw new IOException("Configured agent/workspace root coverage is incomplete; resolve omitted or unavailable contexts before backup.");
            var store = new SkillView.Maintenance.MaintenanceBackupStore(SkillView.Maintenance.MaintenanceBackupStore.ResolveRoot(services.Runner), services.Runner);
            var record = await Task.Run(() => store.Capture(selected[0], token, snapshot.Skills,
                snapshot.ProtectedRoots.Where(scan => scan.AgentHint is not null).ToArray()), token).ConfigureAwait(false);
            impact = impact with
            {
                PayloadRetained = true,
                RetainedPayloadPath = record.Path,
                RetentionEvidence = "verified-backup-capture; original payload was not mutated"
            };
            if (json) WriteJson(w =>
            {
                w.WriteStartObject(); w.WriteNumber("schemaVersion", 1); w.WriteBoolean("succeeded", true);
                w.WriteString("id", record.Id); w.WriteString("installationId", record.InstallationId); w.WriteString("path", record.Path);
                w.WriteString("fingerprint", record.BeforeFingerprint); w.WriteString("archiveHash", record.ArchiveHash);
                w.WriteString("archivePath", record.ArchivePath); w.WriteString("status", record.Status);
                w.WriteString("metadataContract", "file bytes, entry Unix modes and native link bytes; in-place restore requires a reviewed plan");
                WriteImpact(w, impact);
                w.WriteEndObject();
            });
            else Console.WriteLine("Payload evidence captured: " + TerminalEscapeSanitizer.Sanitize(record.ArchivePath));
            if (!json) WriteImpactText(impact);
            return ExitCodes.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException || ex is OperationCanceledException && token.IsCancellationRequested)
        {
            if (json) WriteJson(w => { w.WriteStartObject(); w.WriteNumber("schemaVersion", 1); w.WriteBoolean("succeeded", false); w.WriteString("error", ex.Message); w.WriteBoolean("canceled", ex is OperationCanceledException); WriteImpact(w, impact); w.WriteEndObject(); });
            else Console.Error.WriteLine("skillview: " + TerminalEscapeSanitizer.Sanitize(ex.Message));
            if (!json) WriteImpactText(impact);
            return ex is OperationCanceledException ? ExitCodes.Cancelled : ExitCodes.EnvironmentError;
        }
    }

    private static async Task<int> CompareAsync(AppOptions options, TuiServices services, CancellationToken token)
    {
        var parsed = ParseComparisonArgs(options.SubcommandArgs);
        if (parsed.Error is not null)
        {
            Console.Error.WriteLine($"skillview: {parsed.Error}");
            return ExitCodes.InvalidUsage;
        }
        // Comparison uses offline inventory and never performs auth or remote
        // update checks. gh list supplements filesystem facts when available.
        var snapshot = await services.InventoryService.CaptureAsync(services.GhLocator.FindOnPath(),
            new LocalInventoryService.Options(options.ScanRoots, AllowHiddenDirs: false, WorkspaceDirectories: options.WorkspaceDirectories, Profiles: options.Profiles), token).ConfigureAwait(false);
        InstalledSkill[] Match(string path) => snapshot.Skills.Where(skill => PathIdentity.Equals(skill.ResolvedPath, path)
            || skill.Agents.Any(agent => PathIdentity.Equals(agent.Path, path))).ToArray();
        var left = Match(parsed.LeftPath!);
        var right = Match(parsed.RightPath!);
        if (left.Length != 1 || right.Length != 1)
        {
            Console.Error.WriteLine("skillview: each comparison path must identify one captured installation; include its --scan-root before compare");
            return ExitCodes.NoMatches;
        }
        var result = await new SkillView.Maintenance.SkillComparisonService().CompareAsync(left[0], right[0], token,
            inventoryComplete: !snapshot.IsPartial).ConfigureAwait(false);
        if (parsed.Json) Console.WriteLine(RenderComparisonJson(result));
        else
        {
            Console.WriteLine($"source: {result.SourceRelationship} · revision: {result.RevisionRelationship}");
            Console.WriteLine($"payload: {result.PayloadRelationship} · contexts: {result.ContextRelationship}");
            Console.WriteLine(result.Recommendation);
            foreach (var path in result.DifferingFiles) Console.WriteLine("  " + SkillView.Ui.TerminalEscapeSanitizer.Sanitize(path));
            Console.WriteLine($"{result.DifferingFileCount} differing file(s); complete: {result.Complete}");
            foreach (var diagnostic in result.Diagnostics) Console.Error.WriteLine(SkillView.Ui.TerminalEscapeSanitizer.Sanitize(diagnostic));
        }
        return result.Complete ? ExitCodes.Success : ExitCodes.UserError;
    }

    internal sealed record ParsedComparisonArgs(string? LeftPath, string? RightPath, bool Json, string? Error);

    internal static ParsedComparisonArgs ParseComparisonArgs(IReadOnlyList<string> args)
    {
        string? left = null, right = null, error = null;
        var json = false;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--json") { json = true; continue; }
            if (args[i] is "--path" or "--other-path" && i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                if (args[i] == "--path") left = args[++i]; else right = args[++i];
                continue;
            }
            error = $"unsupported or incomplete compare argument: {args[i]}";
        }
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            error = "compare requires --path <installation> and --other-path <installation>";
        return new(left, right, json, error);
    }

    internal static string RenderComparisonJson(SkillView.Maintenance.SkillComparisonResult result) => RenderJson(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("schemaVersion", 1);
        w.WriteString("leftInstallationId", result.LeftInstallationId);
        w.WriteString("rightInstallationId", result.RightInstallationId);
        w.WriteString("sourceRelationship", result.SourceRelationship);
        w.WriteString("revisionRelationship", result.RevisionRelationship);
        w.WriteString("contextRelationship", result.ContextRelationship);
        w.WriteString("payloadRelationship", result.PayloadRelationship);
        w.WriteString("leftFingerprint", result.LeftFingerprint);
        w.WriteString("rightFingerprint", result.RightFingerprint);
        w.WriteString("leftObjectStateFingerprint", result.LeftObjectStateFingerprint);
        w.WriteString("rightObjectStateFingerprint", result.RightObjectStateFingerprint);
        if (result.LeftRootUnixMode is { } leftMode) w.WriteNumber("leftRootUnixMode", leftMode); else w.WriteNull("leftRootUnixMode");
        if (result.RightRootUnixMode is { } rightMode) w.WriteNumber("rightRootUnixMode", rightMode); else w.WriteNull("rightRootUnixMode");
        w.WriteString("objectEvidenceContract", "native IDs, generations and observed Unix modes; full ACL/xattr/alternate-stream metadata is not attested");
        w.WriteBoolean("complete", result.Complete);
        w.WriteBoolean("requiresExposureMigration", result.RequiresExposureMigration);
        w.WriteNumber("differingFileCount", result.DifferingFileCount);
        w.WriteString("recommendation", result.Recommendation);
        w.WriteStartArray("differingFiles");
        foreach (var path in result.DifferingFiles) w.WriteStringValue(path);
        w.WriteEndArray();
        w.WriteStartArray("diagnostics");
        foreach (var diagnostic in result.Diagnostics) w.WriteStringValue(diagnostic);
        w.WriteEndArray();
        w.WriteEndObject();
    });

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
                " [--scope project|user|custom] [--path <dir>] [--version <ref>] [--pin]" +
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
                ScanRoots: options.ScanRoots, WorkspaceDirectories: options.WorkspaceDirectories, Profiles: options.Profiles,
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
                ScanRoots: options.ScanRoots, WorkspaceDirectories: options.WorkspaceDirectories, Profiles: options.Profiles,
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

    private static async Task<int> UpdateAsync(
        AppOptions options,
        TuiServices services,
        CancellationToken cancellationToken)
    {
        var parsed = ParseUpdateArgs(options.SubcommandArgs);
        if (parsed.Error is not null)
        {
            Console.Error.WriteLine($"skillview: {parsed.Error}");
            return ExitCodes.InvalidUsage;
        }
        var report = await services.EnvironmentProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (!report.GhFound || !report.GhMeetsMinimum || !report.GhSkillAvailable)
        {
            Console.Error.WriteLine("skillview: gh or gh skill not available (run `skillview doctor`)");
            return ExitCodes.EnvironmentError;
        }

        if (!parsed.All && parsed.Skills.Count == 0 && !parsed.DryRun
            && parsed.Path is null && parsed.InstallationId is null)
        {
            Console.Error.WriteLine("skillview: update requires at least one skill, --all, or --dry-run");
            Console.Error.WriteLine(
                "usage: skillview update [<skill>]... [--all] [--dry-run] [--force] [--unpin]" +
                " [--path <path> | --install-id <id>] [--json]");
            return ExitCodes.InvalidUsage;
        }

        // Pre-snapshot for diffing: additions + version changes.
        var preSnapshot = await services.InventoryService.CaptureAsync(
            report.GhPath,
            new Inventory.LocalInventoryService.Options(
                ScanRoots: options.ScanRoots, WorkspaceDirectories: options.WorkspaceDirectories, Profiles: options.Profiles,
                AllowHiddenDirs: parsed.AllowHiddenDirectories),
            cancellationToken
        ).ConfigureAwait(false);

        if (parsed.Path is not null)
            parsed = parsed with { Path = Path.GetFullPath(parsed.Path, services.Runner.EffectiveWorkingDirectory) };

        bool MatchesSelector(InstalledSkill skill, string name) => name == skill.Name
            || skill.Agents.Any(agent => agent.DiscoveryRoot is not null
                && name == Path.GetRelativePath(agent.DiscoveryRoot, agent.Path).Replace(Path.DirectorySeparatorChar, '/'));
        var selected = preSnapshot.Skills.AsEnumerable();
        if (parsed.Skills.Count > 0)
            selected = selected.Where(skill => parsed.Skills.Any(name => MatchesSelector(skill, name)));
        if (parsed.Path is not null)
            selected = selected.Where(skill => PathIdentity.Equals(skill.ResolvedPath, parsed.Path)
                || skill.Agents.Any(agent => PathIdentity.Equals(agent.Path, parsed.Path)));
        if (parsed.InstallationId is not null)
            selected = selected.Where(skill => string.Equals(skill.InstallationId, parsed.InstallationId, StringComparison.OrdinalIgnoreCase));
        var targets = selected.ToArray();
        if (targets.Length == 0)
        {
            Console.Error.WriteLine("skillview: no physical installations match the selection");
            return ExitCodes.NoMatches;
        }
        if (parsed.Path is null && parsed.InstallationId is null && parsed.Skills.Count > 0
            && parsed.Skills.Any(name => targets.Where(target => MatchesSelector(target, name))
                .Select(target => target.InstallationId).Distinct(StringComparer.Ordinal).Count() > 1))
        {
            Console.Error.WriteLine("skillview: a name matches multiple installations; select --path or --install-id");
            return ExitCodes.UserError;
        }

        var updateOptions = new GhSkillUpdateService.Options(
            Skills: parsed.Skills,
            All: parsed.All,
            DryRun: parsed.DryRun,
            Force: parsed.Force,
            Unpin: parsed.Unpin,
            Targets: targets,
            AllowHiddenDirectories: parsed.AllowHiddenDirectories);

        var result = await Task.Run(() => services.SavedUpdates.PrepareAsync(
            report.GhPath!, updateOptions, preSnapshot, cancellationToken), cancellationToken).ConfigureAwait(false);
        IReadOnlyList<InstalledSkill> added = Array.Empty<InstalledSkill>();
        IReadOnlyList<UpdateDiffEntry> changed = Array.Empty<UpdateDiffEntry>();
        if (cancellationToken.IsCancellationRequested)
            result = result with { Cancelled = true, Succeeded = false, ExitCode = 130 };

        if (parsed.Json) WriteUpdateJson(result, parsed, added, changed);
        else WriteUpdateText(result, parsed, added, changed);

        return result.ExitCode;
    }

    internal record ParsedUpdateArgs(
        List<string> Skills,
        bool All,
        bool DryRun,
        bool Force,
        bool Unpin,
        bool Json,
        string? Path = null,
        string? InstallationId = null,
        string? Error = null,
        bool AllowHiddenDirectories = false);

    internal static ParsedUpdateArgs ParseUpdateArgs(IReadOnlyList<string> args)
    {
        var skills = new List<string>();
        bool all = false, dryRun = false, force = false, unpin = false, json = false, allowHidden = false;
        string? path = null, installationId = null, error = null;
        bool positionals = false;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (positionals) { skills.Add(a); continue; }
            if (a == "--") { positionals = true; continue; }
            if (a.StartsWith("--path=", StringComparison.Ordinal)) { path = a[7..]; continue; }
            if (a.StartsWith("--install-id=", StringComparison.Ordinal)) { installationId = a[13..]; continue; }
            if (a is "--path" or "--install-id")
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                { error = $"{a} requires a value"; continue; }
                if (a == "--path") path = args[++i]; else installationId = args[++i];
                continue;
            }
            // `--json` selects SkillView's own machine-readable output; it is
            // not passed to `gh skill update` (which has no --json flag).
            if (a == "--json") { json = true; continue; }
            if (a == "--allow-hidden-dirs") { allowHidden = true; continue; }
            if (a == "--all") { all = true; continue; }
            if (a == "--dry-run") { dryRun = true; continue; }
            if (a == "--force") { force = true; continue; }
            if (a == "--unpin") { unpin = true; continue; }
            if (a.StartsWith("-", StringComparison.Ordinal)) { error = $"unsupported update flag: {a}"; continue; }
            skills.Add(a);
        }
        if (path is not null && string.IsNullOrWhiteSpace(path)) error = "--path must not be empty";
        if (installationId is not null && string.IsNullOrWhiteSpace(installationId)) error = "--install-id must not be empty";
        return new ParsedUpdateArgs(skills, all, dryRun, force, unpin, json, path, installationId, error, allowHidden);
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
        if (!string.IsNullOrWhiteSpace(r.StdErr)) Console.Error.WriteLine(r.StdErr.TrimEnd());
        foreach (var outcome in r.Outcomes)
        {
            Console.Out.WriteLine($"  {outcome.Status}: {outcome.Path} · {outcome.Message ?? outcome.ChangedFileCount + " changed file(s)"}");
            WriteImpactText(outcome.Impact);
        }

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

        if (r.SavedPlan is { } plan)
        {
            Console.Out.WriteLine(TerminalEscapeSanitizer.Sanitize(UpdateScreen.RenderPlan(plan)));
            Console.Out.WriteLine($"Apply: skillview update-apply --id {plan.Id} --expect {plan.PlanFingerprint} --yes --disposition retain-original{(p.AllowHiddenDirectories ? " --allow-hidden-dirs" : "")}");
            Console.Out.WriteLine("Keep the same HOME, state, working directory and global scan/workspace/profile options for apply.");
        }
        if (p.DryRun || !r.MutationAttempted) return;

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
        w.WriteNumber("schemaVersion", 2);
        w.WritePropertyName("savedPlan");
        if (r.SavedPlan is null) w.WriteNullValue();
        else JsonSerializer.Serialize(w, r.SavedPlan, SkillView.Maintenance.ReplacementJsonContext.Default.ReplacementResult);
        if (r.SavedPlan is { } plan)
        {
            w.WriteStartArray("applyArguments");
            foreach (var arg in new[] { "update-apply", "--id", plan.Id, "--expect", plan.PlanFingerprint, "--yes", "--disposition", "retain-original" }) w.WriteStringValue(arg);
            if (p.AllowHiddenDirectories) w.WriteStringValue("--allow-hidden-dirs");
            w.WriteEndArray();
            w.WriteString("applyContext", "Reuse the same HOME, state, working directory and global scan/workspace/profile options.");
        }
        w.WriteString("stdout", r.StdOut);
        w.WriteString("stderr", r.StdErr);
        w.WriteBoolean("canceled", r.Cancelled);
        w.WriteBoolean("mutationAttempted", r.MutationAttempted);
        w.WriteBoolean("partial", r.ReconciliationPartial || !r.Succeeded || r.Outcomes.Any(o => o.Status is "failed" or "refused" or "canceled" or "not-attempted" or "skipped" or "untracked" or "verification-incomplete" or "source-unavailable" or "availability-unknown" or "not-started" or "recovery-required"));
        w.WriteStartArray("outcomes");
        foreach (var outcome in r.Outcomes)
        {
            w.WriteStartObject();
            w.WriteString("installationId", outcome.InstallationId);
            w.WriteString("path", outcome.Path);
            w.WriteString("status", outcome.Status);
            w.WriteString("beforeFingerprint", outcome.BeforeFingerprint);
            w.WriteString("afterFingerprint", outcome.AfterFingerprint);
            w.WriteString("message", outcome.Message);
            w.WriteNumber("changedFileCount", outcome.ChangedFileCount);
            w.WriteStartArray("changedFiles");
            foreach (var file in outcome.ChangedFiles) w.WriteStringValue(file);
            w.WriteEndArray();
            w.WriteStartArray("exposures");
            foreach (var exposure in outcome.Exposures) w.WriteStringValue(exposure);
            w.WriteEndArray();
            WriteImpact(w, outcome.Impact);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteStartArray("notices");
        foreach (var notice in r.Notices)
        {
            w.WriteStartObject();
            w.WriteString("stream", notice.Stream);
            w.WriteString("text", notice.Text);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteBoolean("dryRun", r.DryRun);
        w.WriteBoolean("succeeded", r.Succeeded);
        w.WriteNumber("exitCode", r.ExitCode);
        if (r.ErrorMessage is not null) w.WriteString("errorMessage", r.ErrorMessage);
        w.WriteBoolean("all", p.All);
        w.WriteBoolean("force", p.Force);
        w.WriteBoolean("unpin", p.Unpin);
        w.WriteBoolean("allowHiddenDirectories", p.AllowHiddenDirectories);

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
        if (parsed.Error is not null || parsed.Name is null && parsed.Path is null && parsed.InstallationId is null)
        {
            Console.Error.WriteLine("skillview: " + (parsed.Error ?? "remove requires a skill name or exact selector"));
            Console.Error.WriteLine("usage: skillview remove [name] [--path <path> | --install-id <id>] [--agent <id>] [--yes] [--json]");
            return ExitCodes.InvalidUsage;
        }
        if (parsed.Path is { } path)
        {
            try { parsed = parsed with { Path = Path.GetFullPath(path, services.Runner.EffectiveWorkingDirectory) }; }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            { Console.Error.WriteLine("skillview: invalid remove path: " + TerminalEscapeSanitizer.Sanitize(ex.Message)); return ExitCodes.InvalidUsage; }
        }

        var report = await services.EnvironmentProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await services.InventoryService.CaptureAsync(
            report.GhPath,
            new Inventory.LocalInventoryService.Options(
                ScanRoots: options.ScanRoots, WorkspaceDirectories: options.WorkspaceDirectories, Profiles: options.Profiles,
                AllowHiddenDirs: false),
            cancellationToken
        ).ConfigureAwait(false);

        var matches = SelectRemoveTargets(snapshot, parsed);

        if (matches.Count == 0)
        {
            Console.Error.WriteLine($"skillview: no installed skill matches '{parsed.Name ?? parsed.Path ?? parsed.InstallationId}'" +
                (parsed.Agent is null ? "" : $" for agent '{parsed.Agent}'"));
            return ExitCodes.NoMatches;
        }
        if (matches.Count > 1)
        {
            Console.Error.WriteLine($"skillview: {matches.Count} skills match '{parsed.Name}' — select --path or --install-id");
            foreach (var m in matches) Console.Error.WriteLine($"  · {m.ResolvedPath} ({string.Join(",", m.Agents.Select(a => a.AgentId))})");
            return ExitCodes.UserError;
        }

        var target = matches[0];
        var (validation, result) = await RemoveTargetAsync(target, snapshot, parsed.Yes,
            services.RemoveService, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded && parsed.Yes) services.ListAdapter.Invalidate();

        if (parsed.Json) WriteRemoveJson(result, target, parsed, validation);
        else WriteRemoveText(result, target, parsed, validation);

        if (result.IsCanceled) return ExitCodes.Cancelled;
        if (!validation.Allowed)
        {
            return IsEnvironmentRefusal(validation)
                ? ExitCodes.EnvironmentError
                : ExitCodes.UserError;
        }
        if (!result.Succeeded) return ExitCodes.EnvironmentError;
        return ExitCodes.Success;
    }

    internal static async Task<(RemoveValidator.RemoveValidation Validation, RemoveService.RemoveReport Report)> RemoveTargetAsync(
        InstalledSkill target, InventorySnapshot snapshot, bool execute, RemoveService service, CancellationToken token,
        IProgress<RemoveService.RemoveProgress>? progress = null)
    {
        var impact = OperationImpact.Payload(OperationSubject.From(target), "remove");
        RemoveValidator.RemoveValidation validation;
        try
        {
            validation = execute
                ? RemoveValidator.Validate(target, snapshot.ProtectedRoots.ToArray(), snapshot.Skills, token)
                : RemoveValidator.ValidateForPreview(target, snapshot.ProtectedRoots.ToArray(), snapshot.Skills, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            validation = new([], [], target.ResolvedPath, []) { Impact = impact };
            return (validation, new(false, target.ResolvedPath, 0, 0, [], !execute)
            { IsCanceled = true, Impact = impact });
        }
        var result = await service.RemoveWithOutcomeAsync(validation, new(DryRun: !execute), token, progress).ConfigureAwait(false);
        return (validation, result);
    }

    internal record ParsedRemoveArgs(string? Name, string? Agent, bool Yes, bool Json,
        string? Path = null, string? InstallationId = null, string? Error = null);

    internal static List<InstalledSkill> SelectRemoveTargets(InventorySnapshot snapshot, ParsedRemoveArgs parsed) => snapshot.Skills
        .Where(skill => parsed.Name is null || string.Equals(skill.Name, parsed.Name, StringComparison.OrdinalIgnoreCase))
        .Where(skill => parsed.Agent is null || skill.Agents.Any(agent => AgentIdentity.Matches(agent.AgentId, parsed.Agent)))
        .Where(skill => parsed.Path is null || PathIdentity.Equals(skill.ResolvedPath, parsed.Path)
            || skill.Agents.Any(agent => PathIdentity.Equals(agent.Path, parsed.Path)))
        .Where(skill => parsed.InstallationId is null || string.Equals(skill.InstallationId, parsed.InstallationId, StringComparison.OrdinalIgnoreCase))
        .ToList();

    internal static ParsedRemoveArgs ParseRemoveArgs(IReadOnlyList<string> args)
    {
        string? name = null, agent = null, path = null, id = null, error = null;
        bool yes = false, json = false;
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--json") { json = true; continue; }
            if (a == "--yes" || a == "-y") { yes = true; continue; }
            var key = a.Split('=', 2)[0];
            if (key is "--agent" or "--path" or "--install-id")
            {
                var value = a.Contains('=') ? a[(a.IndexOf('=') + 1)..]
                    : i + 1 < args.Count && !args[i + 1].StartsWith('-') ? args[++i] : null;
                if (string.IsNullOrWhiteSpace(value)) { error = key + " requires a nonempty value"; continue; }
                if (key == "--agent") { if (agent is not null) error = "repeat --agent is unsupported"; agent = value; }
                else if (key == "--path") { if (path is not null) error = "repeat --path is unsupported"; path = value; }
                else { if (id is not null) error = "repeat --install-id is unsupported"; id = value; }
                continue;
            }
            if (a.StartsWith('-')) { error = "unsupported remove flag: " + a; continue; }
            if (name is null) name = a; else error = "remove accepts one name";
        }
        if (path is not null && id is not null) error = "select only one of --path and --install-id";
        return new ParsedRemoveArgs(name, agent, yes, json, path, id, error);
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
        WriteImpactText(r.Impact ?? validation.Impact);
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
        w.WriteBoolean("canceled", r.IsCanceled);
        w.WriteBoolean("allowed", validation.Allowed);
        w.WriteString("name", target.Name);
        w.WriteString("resolvedPath", validation.ResolvedPath);
        w.WriteString("scope", target.Scope.ToString());
        WriteImpact(w, r.Impact ?? validation.Impact);
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
        var report = await services.EnvironmentProbe.ProbeAsync(cancellationToken, includeAuthentication: false).ConfigureAwait(false);
        var snapshot = await services.InventoryService.CaptureAsync(
            report.GhPath,
            new Inventory.LocalInventoryService.Options(
                ScanRoots: options.ScanRoots, WorkspaceDirectories: options.WorkspaceDirectories, Profiles: options.Profiles,
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
            var execution = await ApplyCleanupAsync(candidates, snapshot, services.RemoveService,
                services.Runner, cancellationToken).ConfigureAwait(false);
            applied.AddRange(execution.Applied);
            duplicateSkips = execution.Duplicates;
            environmentRefusal = execution.EnvironmentRefusal;
            if (execution.Applied.Any(item => item.R.Succeeded || item.R.FilesDeleted > 0 || item.R.DirectoriesDeleted > 0))
                services.ListAdapter.Invalidate();
        }

        if (parsed.Json) WriteCleanupJson(candidates, applied, duplicateSkips, parsed, snapshot);
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

        if (applied.Any(item => item.R.IsCanceled)) return ExitCodes.Cancelled;
        if (candidates.Length == 0) return ExitCodes.Success;
        if (parsed.Apply && environmentRefusal) return ExitCodes.EnvironmentError;
        if (parsed.Apply && applied.Any(a => !a.R.Succeeded)) return ExitCodes.UserError;
        return ExitCodes.Success;
    }

    internal sealed record CleanupExecution(
        IReadOnlyList<(CleanupClassifier.Candidate C, RemoveService.RemoveReport R)> Applied,
        ImmutableArray<CleanupClassifier.Candidate> Duplicates, bool EnvironmentRefusal);

    internal static async Task<CleanupExecution> ApplyCleanupAsync(ImmutableArray<CleanupClassifier.Candidate> candidates,
        InventorySnapshot snapshot, RemoveService service, SkillView.Subprocess.ProcessRunner? runner, CancellationToken token)
    {
        var applied = new List<(CleanupClassifier.Candidate C, RemoveService.RemoveReport R)>();
        CleanupClassifier.CandidateSelection selection;
        try { selection = CleanupClassifier.DeduplicateByPath(candidates, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new(candidates.Select(candidate => (candidate, CanceledBeforeStart(candidate))).ToArray(), [], false);
        }
        var environmentRefusal = false;
        foreach (var candidate in selection.Unique)
        {
            if (token.IsCancellationRequested) { applied.Add((candidate, CanceledBeforeStart(candidate))); continue; }
            RemoveValidator.RemoveValidation validation;
            try { validation = ValidateCleanupCandidate(candidate, snapshot.ProtectedRoots.ToArray(), snapshot.Skills, token, runner); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            { applied.Add((candidate, CanceledBeforeStart(candidate))); continue; }
            validation = validation with { Impact = validation.Impact ?? candidate.Impact };
            if (!validation.Allowed || validation.RequiresSecondConfirm)
            {
                environmentRefusal |= IsEnvironmentRefusal(validation);
                var reason = validation.Allowed ? "requires second-confirm"
                    : string.Join("; ", validation.Errors.Select(error => $"{error.Kind}: {error.Detail}"));
                applied.Add((candidate, RemoveService.RemoveReport.Refused(validation.ResolvedPath, reason) with { Impact = validation.Impact }));
                continue;
            }
            applied.Add((candidate, await service.RemoveWithOutcomeAsync(validation, cancellationToken: token).ConfigureAwait(false)));
        }
        return new(applied, selection.Duplicates, environmentRefusal);
    }

    private static RemoveService.RemoveReport CanceledBeforeStart(CleanupClassifier.Candidate candidate) =>
        new(false, candidate.Path, 0, 0, [], false) { IsCanceled = true, NotAttempted = true, Impact = candidate.Impact };

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
            if (a == "--candidates"
                && i + 1 < args.Count
                && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
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
        IReadOnlyList<InstalledSkill> allSkills,
        CancellationToken cancellationToken, SkillView.Subprocess.ProcessRunner? ownershipRunner = null)
    {
        return candidate.ReviewOnly ? RefuseUnsupportedCleanupCandidate(candidate) : candidate.Kind switch
        {
            CleanupClassifier.CandidateKind.BrokenSymlink =>
                RemoveValidator.ValidateBrokenSymlink(candidate.Path, scanRoots, cancellationToken, ownershipRunner),
            CleanupClassifier.CandidateKind.EmptyDirectory =>
                RemoveValidator.ValidateEmptyDirectory(candidate.Path, scanRoots, cancellationToken, ownershipRunner),
            _ when candidate.Skill is not null => RemoveValidator.Validate(candidate.Skill, scanRoots, allSkills, cancellationToken),
            _ => RefuseUnsupportedCleanupCandidate(candidate),
        };
    }

    private static RemoveValidator.RemoveValidation RefuseUnsupportedCleanupCandidate(
        CleanupClassifier.Candidate candidate) =>
        new(
            ImmutableArray.Create(new RemoveValidator.Error(
                RemoveValidator.ErrorKind.NotASkillDirectory,
                $"cleanup candidate kind '{candidate.Kind}' requires comparison or ownership review, not bulk removal")),
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
            WriteImpactText(c.Impact);
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
        ParsedCleanupArgs p, InventorySnapshot? snapshot = null)
        => WriteJson(w => WriteCleanupJson(w, candidates, applied, duplicateSkips, p, snapshot));

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
        ParsedCleanupArgs p, InventorySnapshot? snapshot = null)
    {
        w.WriteStartObject();
        w.WriteBoolean("apply", p.Apply);
        w.WriteBoolean("partial", (snapshot?.IsPartial ?? false) || applied.Any(item => item.R.IsCanceled));
        w.WriteBoolean("canceled", applied.Any(item => item.R.IsCanceled));
        w.WriteStartArray("inaccessiblePaths");
        foreach (var path in snapshot?.Diagnostics.InaccessiblePaths ?? []) w.WriteStringValue(path);
        w.WriteEndArray();
        w.WriteNumber("candidates", candidates.Count);
        w.WriteStartArray("entries");
        foreach (var c in candidates)
        {
            w.WriteStartObject();
            w.WriteString("kind", c.Kind.ToString());
            w.WriteBoolean("reviewOnly", c.ReviewOnly);
            w.WriteString("path", c.Path);
            w.WriteString("reason", c.Reason);
            w.WriteString("name", c.Skill?.Name);
            WriteImpact(w, c.Impact);
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
                w.WriteBoolean("canceled", r.IsCanceled);
                w.WriteString("status", r.NotAttempted ? "not-attempted" : r.IsCanceled ? "canceled" : r.Succeeded ? "completed" : "refused-or-failed");
                WriteImpact(w, r.Impact ?? c.Impact);
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
                w.WriteBoolean("reviewOnly", candidate.ReviewOnly);
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
            | `--workspace <path>` | Observe known project skill directories in another workspace. Repeat for a collection. |
            | `--profile <agent[:name]=skills-directory>` | Observe an explicit agent profile without guessing enabled state. |

            ## Subcommands

            | Subcommand | Purpose | Key options |
            | --- | --- | --- |
            | `doctor` | Inspect `gh`, auth state, `gh skill` availability, log path, and optional inventory. | `--json`, `--inventory`, `--clear-logs` |
            | `list` | Show installed skills from the filesystem and, when supported, `gh skill list`. | `--json`, `--scope`, `--agent`, `--dir`, `--allow-hidden-dirs` |
            | `effective` | Explain conditional loading policy for an exact agent/profile/project. Native inventory and active-session state stay separate. | `--agent`, `--profile`, `--project`, `--cwd`, `--config-dir`, `--client-version`, `--agent-name`, `--trust`, `--json` |
            | `rescan` | Rebuild the local inventory snapshot and print a summary. | _none_ |
            | `search <query>` | Search public skill repositories. | `--owner`, `--limit`, `--page`, `--json` |
            | `preview OWNER/REPO [SKILL]` | Render a skill preview without installing it. | `--version`, `--allow-hidden-dirs`, `--rendered`, `--json` |
            | `install OWNER/REPO [SKILL]` | Install one skill, or every skill in the repo with `--all`, then rescan inventory. | `--all`, `--agent`, `--scope`, `--path`, `--version`, `--pin`, `--force`, `--upstream`, `--from-local`, `--allow-hidden-dirs`, `--json` |
            | `update [SKILL...]` | Check availability or stage one exact saved update for review. Other selected targets remain not started until this review is applied/skipped. | `--all`, `--path`, `--install-id`, `--dry-run`, `--unpin`, `--allow-hidden-dirs`, `--json`; `--force` never approves replacement |
            | `compare` | Compare complete payloads and recorded source/revision facts for two exact installations. Performs no remote check or mutation. | `--path`, `--other-path`, `--json` |
            | `backup` | Capture bounded payload evidence outside installed skills. Does not replace a payload or grant restore authority. | `--path` or `--install-id`, `--json` |
            | `restore-plan` | Stage the recorded immutable Git tree and save an exact replacement plan. | `--path` or `--install-id`, `--allow-hidden-dirs`, `--json` |
            | `update-plan` | Resolve and stage an immutable newer revision, retaining a recorded base and local-change review. | `--path` or `--install-id`, `--unpin`, `--allow-hidden-dirs`, `--json` |
            | `unpin-plan` | Save a pin-only change to one physical installation, preserving installed content and its recorded source revision. | `--path` or `--install-id`, `--allow-hidden-dirs`, `--json` |
            | `unpin-inspect`, `unpin-apply`, `unpin-recover`, `unpin-undo`, `unpin-repair-owner` | Inspect or execute a saved pin-only transaction. Apply retains the original directory. | `--id`, `--expect`, `--yes`, `--disposition unpin-only`, `--json` |
            | `update-merge-review` | Capture immutable recorded/installed/source evidence for a never-started update. Closes the earlier replacement approval. | `--id`, `--expect <source plan fingerprint>`, `--json` |
            | `update-merge-choose` | Save complete explicit path choices; use `/` for root mode. Freeze a custom regular file or reuse previously captured bytes. | `--id`, `--expect <local review fingerprint>`, repeated `--choose-installed <path>` / `--choose-source <path>` / `--choose-resolved <path>` / `--resolve <path> <file>`, `--json` |
            | `update-merge-plan` | Build and save a separately approved merged candidate with preserved supported native metadata. No live installation change. | `--id`, `--expect <local review fingerprint>`, `--json` |
            | `update-preserve` | Verify and keep the current installation without moving payloads, writing its owner or changing preferences. | `--id`, `--expect <plan fingerprint>`, `--json` |
            | `update-inspect`, `update-apply`, `update-recover`, `update-undo` | Inspect, apply, recover or undo a saved revision update. Apply must match its saved replacement or merged disposition. | `--id`, `--expect`, `--yes`, `--disposition retain-original` / `merge-reviewed`, `--json` |
            | `update-repair-owner` | Repair only a recognized torn registry from its explicitly reviewed retained image. Inspect again before resuming payload recovery. | `--id`, `--yes --expect`, `--json` |
            | `restore-inspect` | Verify saved plan locations and obtain a recovery review fingerprint. | `--id`, `--json` |
            | `restore-apply` | Apply a saved plan, retaining the original directory. | `--id`, `--yes --disposition retain-original --expect`, `--json` |
            | `restore-recover`, `restore-undo` | Resume or undo after inspecting current evidence; never overwrite another object. | `--id`, `--yes --expect`, `--json` |
            | `quarantine` | Preview relocation of one standalone custom-directory copy. Apply requires a reviewed fingerprint and explicit confirmation. | `--path` or `--install-id`, `--yes --standalone --expect`, `--json` |
            | `undo` | Preview or restore an unchanged quarantined directory to its vacant original location. | `--id`, `--yes`, `--json` |
            | `quarantine-history` | Inspect recorded relocation states; incomplete operations require inspection. | `--json` |
            | `history` | Verify retained backup journals and every payload blob; reports interrupted, orphaned and corrupt evidence without extracting it. | `--id`, `--json` |
            | `remove [SKILL]` | Safely remove one exact installation. Defaults to dry-run until you pass confirmation. | `--path` or `--install-id`, `--agent`, `--yes`, `--json` |
            | `cleanup` | Find duplicates, residue, malformed installs, and other cleanup candidates. | `--candidates`, `--apply`, `--yes`, `--json`, `--output` |
            | `ignore` | Set external cleanup suppression for one physical install; never changes skill files or agent loading. | `--path` or `--install-id`, `--clear`, `--json` |

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

            - SkillView requires `gh` 2.97.0 or newer and checks that `gh skill` is available.
            - `--debug` is the only global flag accepted after a subcommand. Put other global flags before the subcommand.
            - `SKILLVIEW_LOG=debug` is the environment-variable alternative to `--debug`.
            - On Apple Silicon macOS and x86_64 Linux, install the standalone app with `brew install harder/tap/skillview`.
            - WinGet manifests are generated for release review, but are not submitted automatically.

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
