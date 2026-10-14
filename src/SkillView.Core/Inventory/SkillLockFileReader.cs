using System.Buffers;
using System.Collections.Immutable;
using System.Text.Json;
using SkillView.Inventory.Models;
using SkillView.Logging;

namespace SkillView.Inventory;

/// Contextual, bounded readers. The legacy name map is display compatibility
/// only; production retains every record and its exact installation roots.
public sealed class SkillLockFileReader(Logger logger)
{
    public const string FileName = ".skill-lock.json";
    public const string ProjectFileName = "skills-lock.json";
    public const int MaxFileBytes = 1024 * 1024;
    public const int MaxFiles = 128;
    public const int MaxEntries = 4096;
    public const int MaxAggregateBytes = 16 * 1024 * 1024;

    // vercel-labs/skills 13e4063: documented project locations, never arbitrary scan descendants.
    internal static readonly string[] ProjectInstallationDirectories =
    [
        ".adal/skills",
        ".agents/skills",
        ".aider-desk/skills",
        ".augment/skills",
        ".autohand/skills",
        ".bob/skills",
        ".claude/skills",
        ".codeartsdoer/skills",
        ".codebuddy/skills",
        ".codemaker/skills",
        ".codestudio/skills",
        ".commandcode/skills",
        ".continue/skills",
        ".cortex/skills",
        ".crush/skills",
        ".devin/skills",
        ".forge/skills",
        ".goose/skills",
        ".grok/skills",
        ".hermes/skills",
        ".iflow/skills",
        ".inferencesh/skills",
        ".jazz/skills",
        ".junie/skills",
        ".kimchi/skills",
        ".kiro/skills",
        ".kode/skills",
        ".lingma/skills",
        ".mcpjam/skills",
        ".minimax/skills",
        ".moxby/skills",
        ".mux/skills",
        ".neovate/skills",
        ".ona/skills",
        ".openhands/skills",
        ".pochi/skills",
        ".posit/assistant/skills",
        ".qoder/skills",
        ".qwen/skills",
        ".reasonix/skills",
        ".roo/skills",
        ".rovodev/skills",
        ".tabnine/agent/skills",
        ".terramind/skills",
        ".tinycloud/skills",
        ".trae/skills",
        ".vibe/skills",
        ".windsurf/skills",
        ".zcode/skills",
        ".zencoder/skills",
        "agent/skills",
        "data/skills",
        "skills",
    ];

    public sealed record Options(string? HomeDirectory = null, string? XdgStateHome = null);

    public ImmutableDictionary<string, SkillPackage> LoadFromRoots(IEnumerable<string> roots) =>
        LoadFromRootsWithCancellation(roots, CancellationToken.None);

    internal ImmutableDictionary<string, SkillPackage> LoadFromRootsWithCancellation(IEnumerable<string> roots, CancellationToken token) =>
        LoadObservations(roots, token).GroupBy(entry => entry.Name, StringComparer.Ordinal)
            .ToImmutableDictionary(group => group.Key, group => group.First().Package, StringComparer.Ordinal);

    internal ImmutableArray<PackageObservation> LoadObservations(IEnumerable<string> roots, CancellationToken token,
        Options? options = null, ICollection<LockReadDiagnostic>? diagnostics = null)
    {
        token.ThrowIfCancellationRequested();
        options ??= new();
        var locations = new Dictionary<string, Location>(StringComparer.Ordinal);
        var home = options.HomeDirectory ?? Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var xdg = options.XdgStateHome ?? (options.HomeDirectory is null ? Environment.GetEnvironmentVariable("XDG_STATE_HOME") : null);
        if (!string.IsNullOrWhiteSpace(home))
        {
            Add(Path.Combine(home, ".agents", FileName), Path.Combine(home, ".agents", "skills"), home, "gh-vercel-global");
            if (!string.IsNullOrWhiteSpace(xdg))
                Add(Path.Combine(xdg, "skills", FileName), Path.Combine(home, ".agents", "skills"), home, "vercel-global-xdg");
        }
        var rootCount = 0;
        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            if (++rootCount > 256) { Report(root, "lock discovery root limit exceeded", allRoots: true); break; }
            if (string.IsNullOrWhiteSpace(root)) continue;
            var lexical = Path.GetFullPath(root);
            var canonical = OwnershipPath.CanonicalCandidate(lexical);
            foreach (var full in new[] { lexical, canonical }.OfType<string>().Distinct(StringComparer.Ordinal))
            {
                var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(full));
                if (parent is null) continue;
                Add(Path.Combine(parent, FileName), full, parent, "legacy-lock");
                var cursor = parent;
                for (var depth = 0; depth < 8 && cursor is not null; depth++)
                {
                    token.ThrowIfCancellationRequested();
                    if (ProjectInstallationDirectories.Any(relative => PathIdentity.Equals(full, Path.Combine(cursor, relative))))
                        Add(Path.Combine(cursor, ProjectFileName), full, cursor, "vercel-project");
                    if (!string.IsNullOrWhiteSpace(home) && PathIdentity.Equals(cursor, home)) break;
                    cursor = Path.GetDirectoryName(cursor);
                }
            }
        }
        var result = ImmutableArray.CreateBuilder<PackageObservation>();
        var files = 0;
        var totalBytes = 0;
        var entries = 0;
        foreach (var location in locations.Values)
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(location.Path) && !PathResolver.IsSymlink(location.Path)) continue;
            if (++files > MaxFiles) { Report(location.Path, "lockfile count limit exceeded", allRoots: true); break; }
            try
            {
                var bytes = ReadBytes(location.Path, token);
                totalBytes += bytes.Length;
                if (totalBytes > MaxAggregateBytes) { Report(location.Path, "aggregate lockfile byte limit exceeded", allRoots: true); break; }
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("skills", out var skills) || skills.ValueKind != JsonValueKind.Object)
                { Report(location.Path, "unsupported lockfile structure"); continue; }
                int? version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var number) ? number : null;
                var malformed = DuplicateProperties(root) || DuplicateProperties(skills)
                    || (root.TryGetProperty("version", out _) && version is null);
                if (malformed) Report(location.Path, "Duplicate properties or invalid schema version; owner evidence is incomplete.");
                var supported = !malformed && (location.Provider == "vercel-project" ? version == 1
                    : location.Provider is "gh-vercel-global" or "vercel-global-xdg" ? version == 3
                    : version is null or 1 or 2 or 3);
                var diagnostic = supported ? null : $"Unsupported {location.Provider} schema version {version?.ToString() ?? "missing"}; read-only evidence.";
                if (diagnostic is not null) Report(location.Path, diagnostic);
                var recordFingerprint = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
                foreach (var entry in skills.EnumerateObject())
                {
                    token.ThrowIfCancellationRequested();
                    if (++entries > MaxEntries) { Report(location.Path, "lock entry limit exceeded", allRoots: true); return result.ToImmutable(); }
                    if (entry.Value.ValueKind != JsonValueKind.Object) { Report(location.Path, "invalid lock entry object"); continue; }
                    var source = String(entry.Value, "source");
                    if (string.IsNullOrWhiteSpace(source)) { Report(location.Path, "missing or invalid source in lock entry"); continue; }
                    var entryMalformed = DuplicateProperties(entry.Value);
                    if (entryMalformed) Report(location.Path, "Duplicate entry fields; owner evidence is incomplete.");
                    var covered = location.Roots.Order(StringComparer.Ordinal).ToImmutableArray();
                    var computedHash = String(entry.Value, "computedHash");
                    var treeHash = String(entry.Value, "skillFolderHash");
                    var package = new SkillPackage(source, String(entry.Value, "sourceType") ?? "unknown", String(entry.Value, "sourceUrl"),
                        Date(entry.Value, "installedAt"), Date(entry.Value, "updatedAt"))
                    {
                        LockFilePath = location.Path,
                        InstallationRoot = covered[0],
                        CoveredRoots = covered,
                        ContextRoot = location.Context,
                        Selector = entry.Name,
                        Provider = location.Provider,
                        SchemaVersion = version,
                        SkillPath = String(entry.Value, "skillPath"),
                        RepositoryRef = String(entry.Value, "ref"),
                        PinnedRef = String(entry.Value, "pinnedRef"),
                        PluginName = String(entry.Value, "pluginName"),
                        ContentHash = computedHash ?? treeHash,
                        HashKind = computedHash is not null ? "vercel-project-sha256" : treeHash is not null ? "recorded-git-tree" : null,
                        RecordFingerprint = recordFingerprint,
                        EntryFingerprint = OwnershipRecordFingerprint.Compute(entry.Value, location.Provider == "gh-vercel-global"),
                        PackageVersion = String(entry.Value, "version"),
                        InstallationMode = String(entry.Value, "installationMode"),
                        ReadOnly = !supported || entryMalformed,
                        Diagnostic = diagnostic,
                        IsExclusiveOwner = location.Provider != "gh-vercel-global"
                    };
                    result.Add(new(entry.Name, package));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { Report(location.Path, ex.Message); }
        }
        return result.ToImmutable();

        void Add(string path, string installationRoot, string context, string provider)
        {
            var key = LocationKey(path);
            if (locations.TryGetValue(key, out var existing))
            {
                existing.Roots.Add(installationRoot);
                return;
            }
            locations.Add(key, new(path, context, provider, new HashSet<string>(StringComparer.Ordinal) { installationRoot }));
        }
        void Report(string path, string message, bool allRoots = false)
        {
            logger.Warn("inventory.lockfile", $"{path}: {message}");
            if (diagnostics is null) return;
            if (diagnostics.Count >= 128)
            {
                // The diagnostic bound cannot turn unobserved ownership into
                // permission to mutate. Reserve a global incomplete sentinel.
                var last = diagnostics.Last();
                if (!last.CoveredRoots.IsEmpty)
                {
                    diagnostics.Remove(last);
                    diagnostics.Add(new(path, "Owner diagnostics omitted; inventory ownership is incomplete.", []));
                }
                return;
            }
            diagnostics.Add(new(path, message, !allRoots && locations.TryGetValue(LocationKey(path), out var location)
                ? location.Roots.ToImmutableArray() : []));
        }
        static string LocationKey(string path)
        {
            // Native directory canonicalization pins the parent; preserve the
            // final name so a linked lock still reaches ReadBytes' refusal.
            var parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
            return PathIdentity.NormalizeKey(Path.Combine(OwnershipPath.CanonicalCandidate(parent) ?? parent, Path.GetFileName(path)));
        }
    }

    internal static byte[] ReadBytes(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!SecureRemovalBackend.IsSupported) throw new IOException("native regular-file inspection unavailable");
        using var stream = OperatingSystem.IsWindows()
            ? WindowsSecureRemovalBackend.OpenRegularFileForSnapshot(path)
            : UnixSecureRemovalBackend.OpenRegularFileForSnapshot(path, out _);
        var buffer = ArrayPool<byte>.Shared.Rent(MaxFileBytes + 1);
        try
        {
            var count = 0;
            while (count <= MaxFileBytes)
            {
                token.ThrowIfCancellationRequested();
                var read = stream.Read(buffer, count, MaxFileBytes + 1 - count);
                if (read == 0) break;
                count += read;
            }
            if (count > MaxFileBytes) throw new IOException($"lockfile exceeds {MaxFileBytes} bytes");
            return buffer.AsSpan(0, count).ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static string? String(JsonElement element, string name) => element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 8192 } text ? text : null;
    private static DateTimeOffset? Date(JsonElement element, string name) => DateTimeOffset.TryParse(String(element, name), out var date) ? date : null;
    private sealed record Location(string Path, string Context, string Provider, HashSet<string> Roots);
    private static bool DuplicateProperties(JsonElement element)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return element.EnumerateObject().Any(property => !names.Add(property.Name));
    }
}

internal sealed record PackageObservation(string Name, SkillPackage Package);
internal sealed record LockReadDiagnostic(string Path, string Message, ImmutableArray<string> CoveredRoots);
