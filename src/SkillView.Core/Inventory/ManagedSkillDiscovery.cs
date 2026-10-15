using System.Collections.Immutable;
using SkillView.Inventory.Models;
using SkillView.Subprocess;

namespace SkillView.Inventory;

/// Offline observations only. A cache or manifest never proves a running agent loaded a skill.
internal sealed partial class ManagedSkillDiscovery
{
    internal const int MaxEntries = 8192, MaxDirectories = 2048, MaxRoots = 256, MaxDepth = 8;
    internal const int MaxManifestBytes = 1024 * 1024, MaxTotalMetadataBytes = 16 * 1024 * 1024;
    private readonly CancellationToken token;
    private readonly string cwd;
    private readonly List<ManagedSkillRegion> regions = [];
    private readonly List<ScanRoot> roots = [];
    private readonly List<DiscoveryLocation> locations = [];
    private readonly List<string> incomplete = [];
    private readonly Dictionary<string, string> witnesses = new(StringComparer.Ordinal);
    private readonly HashSet<string> visited = new(StringComparer.Ordinal);
    private int entries, directories, metadataBytes;
    private bool exhausted;
    private bool topologyIncomplete;
    private ScanRoot[] contextRoots = [];

    private ManagedSkillDiscovery(string cwd, CancellationToken token) { this.cwd = cwd; this.token = token; }

    internal static ProcessRunner CaptureContext(ProcessRunner? source)
    {
        source ??= new ProcessRunner(new SkillView.Logging.Logger());
        var values = new[] { "HOME", "USERPROFILE", "CODEX_HOME", "CLAUDE_CONFIG_DIR", "CLAUDE_CODE_PLUGIN_CACHE_DIR",
            "CLAUDE_CODE_PLUGIN_DIRS", "CLAUDE_CODE_PLUGIN_SEED_DIR" }
            .ToDictionary(key => key, key => source.GetEffectiveEnvironmentVariable(key) ?? "", StringComparer.Ordinal);
        return new ProcessRunner(new SkillView.Logging.Logger(), environment: values, defaultWorkingDirectory: source.EffectiveWorkingDirectory);
    }

    internal sealed record Observation(ImmutableArray<ManagedSkillRegion> Regions, ImmutableArray<ScanRoot> Roots,
        ImmutableArray<DiscoveryLocation> Locations, ImmutableArray<string> Incomplete,
        ImmutableDictionary<string, string> Witnesses, bool TopologyIncomplete)
    {
        internal bool Complete => Incomplete.IsEmpty;
        internal IEnumerable<SkillPackage> Claims(InstalledSkill skill) => Regions.Where(region =>
            region.Covers(skill.ResolvedPath) || skill.Agents.Any(agent => region.Covers(agent.Path)))
            // Container and version observations are both useful, but the most
            // specific boundary supplies each provider's distributor identity.
            .GroupBy(region => region.Provider, StringComparer.Ordinal)
            .SelectMany(group =>
            {
                var detailed = group.Where(region => region.PluginId is not null).ToArray();
                var selected = detailed.Length == 0 ? group.ToArray() : detailed;
                var registered = selected.Where(region => region.RegistrationPath is not null).ToArray();
                return (registered.Length == 0 ? selected.Take(1) : registered)
                    .Select(region => region.Claim(skill));
            });
    }

    internal static Observation Capture(ProcessRunner runner, IEnumerable<ScanRoot> ordinaryRoots,
        CancellationToken token, IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null,
        bool allowHidden = false, bool discoverPayloads = true)
    {
        var cwd = Path.GetFullPath(workingDirectory ?? runner.EffectiveWorkingDirectory);
        var probe = new ManagedSkillDiscovery(cwd, token);
        var capturedRoots = ordinaryRoots.Take(513).ToArray();
        probe.contextRoots = capturedRoots.Take(512).ToArray();
        string? Value(string key) => runner.GetEffectiveEnvironmentVariable(key, environment) is { Length: > 0 } value ? value : null;
        string Full(string value) => Path.GetFullPath(value, cwd);
        var home = Full(Value(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var codexHomes = new[] { Path.Combine(home, ".codex"), Full(Value("CODEX_HOME") ?? Path.Combine(home, ".codex")) }.Distinct(StringComparer.Ordinal);
        foreach (var codex in codexHomes)
        {
            probe.Builtin(Path.Combine(codex, "skills", ".system"), "codex-builtin", "codex", discoverPayloads);
            probe.Plugins(Path.Combine(codex, "plugins"), "codex-plugin", "codex", discoverPayloads);
        }
        var claudeHomes = new[] { Path.Combine(home, ".claude"), Full(Value("CLAUDE_CONFIG_DIR") ?? Path.Combine(home, ".claude")) }.Distinct(StringComparer.Ordinal);
        foreach (var claude in claudeHomes) probe.Plugins(Path.Combine(claude, "plugins"), "claude-plugin", "claude", discoverPayloads);
        if (Value("CLAUDE_CODE_PLUGIN_CACHE_DIR") is { } pluginRoot) probe.Plugins(Full(pluginRoot), "claude-plugin", "claude", discoverPayloads);
        if (!OperatingSystem.IsWindows()) probe.Builtin("/etc/codex/skills", "codex-admin", "codex", discoverPayloads);
        foreach (var variable in new[] { "CLAUDE_CODE_PLUGIN_DIRS", "CLAUDE_CODE_PLUGIN_SEED_DIR" })
        {
            var values = (Value(variable) ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            if (values.Length > 32) { probe.topologyIncomplete = true; probe.Incomplete(variable, "Explicit plugin location limit exceeded."); }
            foreach (var value in values.Take(32))
            {
                token.ThrowIfCancellationRequested();
                var expanded = value.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home, value[2..]) : value;
                if (!Path.IsPathFullyQualified(expanded)) { probe.Incomplete(value, "Relative Claude plugin directory is unsupported."); continue; }
                if (variable.EndsWith("SEED_DIR", StringComparison.Ordinal)) probe.Plugins(Full(expanded), "claude-plugin", "claude", discoverPayloads);
                else probe.Plugin(Full(expanded), "claude-plugin", "claude", null, null, discoverPayloads);
            }
        }
        if (capturedRoots.Length > 512) { probe.topologyIncomplete = true; probe.Incomplete(cwd, "Managed context limit exceeded."); }
        foreach (var root in capturedRoots.Take(512))
        {
            token.ThrowIfCancellationRequested();
            // Ancestors are policy observations, never recursive home scans.
            probe.Ancestors(root.Path, discoverPayloads, root);
            if (discoverPayloads) probe.Embedded(root.Path, root, 0, allowHidden);
        }
        if (discoverPayloads) probe.AddContextualRoots(capturedRoots.Take(512));
        return new(probe.regions.Distinct().ToImmutableArray(), probe.roots.Distinct().ToImmutableArray(),
            probe.locations.ToImmutableArray(), probe.incomplete.Distinct(StringComparer.Ordinal).ToImmutableArray(),
            probe.witnesses.ToImmutableDictionary(StringComparer.Ordinal), probe.topologyIncomplete);
    }

    private void AddContextualRoots(IEnumerable<ScanRoot> ordinaryRoots)
    {
        // Inventory identity is physical, but every custom/profile exposure is
        // retained through its reviewed spelling. Do not rescan plugin resources
        // merely because the enclosing ordinary root has a different alias.
        var payloads = roots.ToArray();
        foreach (var ordinary in ordinaryRoots)
        {
            token.ThrowIfCancellationRequested();
            var parent = OwnershipPath.CanonicalCandidate(ordinary.Path);
            if (parent is null) continue;
            foreach (var payload in payloads)
            {
                token.ThrowIfCancellationRequested();
                var candidate = OwnershipPath.CanonicalCandidate(payload.Path);
                if (candidate is null || !PathIdentity.IsInside(candidate, parent)) continue;
                var alias = Path.GetFullPath(Path.GetRelativePath(parent, candidate), ordinary.Path);
                var region = regions.Where(region => region.Provider == payload.ManagedProvider && region.Covers(candidate))
                    .OrderByDescending(region => region.PluginId is not null).FirstOrDefault();
                if (region is not null) Root(alias, region, ordinary);
            }
        }
    }

    internal static void Refuse(Observation observation, IEnumerable<string> paths, bool includeContainedRegions = false)
    {
        foreach (var path in paths)
            if (observation.Regions.Any(region => region.Covers(path) || includeContainedRegions && region.IsInside(path)))
                throw new IOException("Managed skill or plugin resources are read-only; use the owning agent or package manager.");
        if (observation.TopologyIncomplete)
            throw new IOException("Managed ownership discovery is incomplete: " + string.Join("; ", observation.Incomplete.Take(4)));
    }

    internal static void RefuseTarget(InstalledSkill target, CancellationToken token, ProcessRunner? runner = null)
    {
        var paths = target.Agents.Select(agent => agent.Path).Append(target.ResolvedPath).Distinct(StringComparer.Ordinal).ToArray();
        RefusePaths(paths, token, runner ?? target.OwnershipRunner);
    }

    internal static void RefusePaths(IEnumerable<string> selected, CancellationToken token, ProcessRunner? runner = null)
    {
        var paths = selected.Distinct(StringComparer.Ordinal).ToArray();
        var roots = paths.SelectMany(path => new[] { path, OwnershipPath.CanonicalCandidate(path) ?? OwnershipPath.EntryAddress(path)
            ?? throw new IOException("Managed ownership address could not be inspected.") })
            .Distinct(StringComparer.Ordinal).Select(path => new ScanRoot(path, Scope.Custom, null));
        var observed = Capture(runner ?? new ProcessRunner(new SkillView.Logging.Logger()), roots, token, discoverPayloads: false);
        Refuse(observed, paths, includeContainedRegions: true);
    }

    private void AddRegion(ManagedSkillRegion region)
    {
        token.ThrowIfCancellationRequested();
        if (regions.Any(existing => existing with { ObservedCanonicalPath = null } == region with { ObservedCanonicalPath = null })) return;
        if (regions.Count >= MaxRoots) { topologyIncomplete = true; Incomplete(region.Path, "Managed region limit exceeded."); return; }
        var canonical = OwnershipPath.CanonicalCandidate(region.Path);
        if (canonical is null) { topologyIncomplete = true; Incomplete(region.Path, "Managed boundary address could not be inspected."); }
        regions.Add(region with { ObservedCanonicalPath = canonical });
    }
    private void Builtin(string path, string provider, string agent, bool discover)
    {
        var region = new ManagedSkillRegion(path, provider, agent, provider == "codex-admin" ? "administrator skills" : "bundled skills", Context: Path.GetDirectoryName(path));
        AddRegion(region);
        if (discover && DirectoryExists(path)) Root(path, region);
    }
    private void Plugins(string path, string provider, string agent, bool discover)
    {
        var region = new ManagedSkillRegion(path, provider, agent, "plugin storage", Context: path);
        if (regions.Any(existing => existing.Path == region.Path && existing.Provider == region.Provider)) return;
        AddRegion(region);
        if (!DirectoryExists(path)) return;
        if (provider == "claude-plugin") ClaudeRegistry(path, discover);
        Cache(Path.Combine(path, "cache"), region, 0, [], discover);
        var synced = Path.Combine(path, "synced");
        if (DirectoryExists(synced) && discover) Synced(synced, region, 0);
    }
    private void Cache(string path, ManagedSkillRegion region, int depth, string[] names, bool discover)
    {
        if (!DirectoryExists(path)) return;
        foreach (var entry in Children(path))
        {
            if (IsLink(entry))
            {
                AddRegion(region with { Path = entry, Kind = "linked plugin storage", ObservedCanonicalPath = null });
                Incomplete(entry, "Linked managed container is observed but not traversed; add its physical target explicitly.");
                continue;
            }
            if (!DirectoryEntry(entry)) continue;
            var next = names.Append(Path.GetFileName(entry)).ToArray();
            if (depth == 2)
            {
                if (discover) Plugin(entry, region.Provider, region.AgentId, next[1] + "@" + next[0], next[2], true);
                else AddRegion(region with { Path = entry, Kind = "plugin", PluginId = next[1] + "@" + next[0], PackageVersion = next[2], ObservedCanonicalPath = null });
            }
            else Cache(entry, region, depth + 1, next, discover);
        }
    }
    private void Synced(string path, ManagedSkillRegion region, int depth)
    {
        if (depth >= 4) { Incomplete(path, "Unrecognized synced plugin structure exceeds discovery depth."); return; }
        if (HasManifest(path)) { Plugin(path, region.Provider, region.AgentId, Path.GetFileName(path) + "@synced", null, true); return; }
        foreach (var child in Children(path)) if (DirectoryEntry(child)) Synced(child, region, depth + 1);
    }
    private void Embedded(string path, ScanRoot ordinary, int depth, bool allowHidden)
    {
        token.ThrowIfCancellationRequested();
        if (!DirectoryExists(path) || regions.Any(region => region.Covers(path))) return;
        if (!visited.Add(PathIdentity.NormalizeKey(path) + "\0" + ordinary.Scope + "\0" + ordinary.Context + "\0" + ordinary.Profile)) return;
        if (ObserveEmbeddedPlugin(path, true, ordinary)) return;
        if (Exists(Path.Combine(path, LocalSkillScanner.SkillFileName))) return;
        if (depth >= MaxDepth) { topologyIncomplete = true; Incomplete(path, "Embedded plugin discovery depth exceeded."); return; }
        foreach (var child in Children(path))
        {
            if (!allowHidden && Path.GetFileName(child).StartsWith('.')) continue;
            if (IsLink(child))
            {
                if (PathResolver.Resolve(child) is { } physical)
                {
                    Ancestors(physical, true, ordinary);
                    foreach (var region in regions.Where(region => region.Covers(physical)).ToArray())
                    {
                        var alias = region with { Path = child, ObservedCanonicalPath = null };
                        AddRegion(alias);
                        if (Exists(Path.Combine(child, LocalSkillScanner.SkillFileName))) Root(child, alias, ordinary);
                    }
                }
                continue;
            }
            if (DirectoryEntry(child)) Embedded(child, ordinary, depth + 1, allowHidden);
        }
    }
    private void Ancestors(string path, bool discover, ScanRoot? context)
    {
        if (regions.Any(region => region.Covers(path))) return;
        string? cursor = Path.GetFullPath(path, cwd);
        for (var depth = 0; cursor is not null && depth < 128; depth++, cursor = Path.GetDirectoryName(cursor))
        {
            token.ThrowIfCancellationRequested();
            if (ObserveEmbeddedPlugin(cursor, discover, context)) return;
        }
        if (cursor is not null) { topologyIncomplete = true; Incomplete(path, "Managed ancestor inspection depth exceeded."); }
    }
    private bool HasManifest(string path) => Exists(Path.Combine(path, ".codex-plugin", "plugin.json"))
        || Exists(Path.Combine(path, ".claude-plugin", "plugin.json")) || Exists(Path.Combine(path, "plugin.json"));

    private bool ObserveEmbeddedPlugin(string path, bool discover, ScanRoot? context)
    {
        var codex = Exists(Path.Combine(path, "plugin.json")) || Exists(Path.Combine(path, ".codex-plugin", "plugin.json"));
        var claude = Exists(Path.Combine(path, ".claude-plugin", "plugin.json"));
        if (codex) Plugin(path, "local-codex-plugin", "codex", Path.GetFileName(path), null, discover, context);
        if (claude) Plugin(path, "local-claude-plugin", "claude", Path.GetFileName(path), null, discover, context);
        return codex || claude;
    }

    private void Root(string path, ManagedSkillRegion region, ScanRoot? context = null)
    {
        var direct = Exists(Path.Combine(path, LocalSkillScanner.SkillFileName));
        var root = new ScanRoot(path, context?.Scope ?? region.RegistrationScope ?? Scope.User, context?.AgentHint ?? region.AgentId)
        {
            Context = context?.Context ?? region.Context,
            Profile = context?.Profile,
            Managed = true,
            DirectSkill = direct,
            ManagedProvider = region.Provider
        };
        if (roots.Contains(root)) return;
        if (roots.Count >= MaxRoots) { Incomplete(path, "Managed payload root limit exceeded."); return; }
        if (direct || context is not null ? !DirectoryExists(path) : !DirectoryEntry(path)) return;
        roots.Add(root);
    }
    private IEnumerable<string> Children(string path)
    {
        token.ThrowIfCancellationRequested();
        if (exhausted) yield break;
        if (++directories > MaxDirectories) { exhausted = topologyIncomplete = true; Incomplete(path, "Managed directory budget exceeded."); yield break; }
        IEnumerator<string>? cursor = null;
        try { cursor = Directory.EnumerateFileSystemEntries(path).GetEnumerator(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { topologyIncomplete = true; Incomplete(path, ex.Message); }
        if (cursor is null) yield break;
        using (cursor)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (exhausted) yield break;
                bool moved;
                try { moved = cursor.MoveNext(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { topologyIncomplete = true; Incomplete(path, ex.Message); yield break; }
                if (!moved) yield break;
                if (++entries > MaxEntries) { exhausted = topologyIncomplete = true; Incomplete(path, "Managed entry budget exceeded."); yield break; }
                yield return cursor.Current;
            }
        }
    }
    private bool Exists(string path)
    {
        token.ThrowIfCancellationRequested();
        try { _ = File.GetAttributes(path); return true; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Incomplete(path, ex.Message); return true; }
    }
    private bool DirectoryExists(string path)
    {
        token.ThrowIfCancellationRequested();
        try { return (File.GetAttributes(path) & FileAttributes.Directory) != 0; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { topologyIncomplete = true; Incomplete(path, ex.Message); return false; }
    }
    private bool DirectoryEntry(string path)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) { Incomplete(path, "Linked managed container is not traversed; add its physical target explicitly."); return false; }
            return (attributes & FileAttributes.Directory) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { topologyIncomplete = true; Incomplete(path, ex.Message); return false; }
    }
    private bool IsLink(string path)
    {
        token.ThrowIfCancellationRequested();
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { topologyIncomplete = true; Incomplete(path, ex.Message); return true; }
    }
    private void Incomplete(string path, string reason)
    {
        if (reason.Length > 4096) reason = reason[..4096] + " [truncated]";
        if (incomplete.Count < 128) incomplete.Add(path + ": " + reason);
        else if (incomplete.Count == 128) incomplete.Add("Additional managed discovery diagnostics omitted; coverage remains partial.");
        if (locations.Count < 128) locations.Add(new(path, "unsupported", reason));
    }
}
