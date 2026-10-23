using System.Collections.Immutable;
using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Inventory.Models;
using SkillView.Ui;

namespace SkillView.Cli;

public static partial class CliDispatcher
{

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
}
