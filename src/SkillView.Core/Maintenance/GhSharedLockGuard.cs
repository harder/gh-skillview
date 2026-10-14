using System.Text.Json;
using SkillView.Inventory;
using System.Collections.Immutable;
using SkillView.Inventory.Models;

namespace SkillView.Maintenance;

/// gh rewrites the whole shared lock through a narrower schema than Vercel.
/// Refuse a write that would erase unknown fields; preservation belongs in a
/// reviewed maintenance journal, not an implicit metadata rewrite.
internal static class GhSharedLockGuard
{
    private static readonly HashSet<string> RootFields = ["version", "skills", "dismissed"];
    private static readonly HashSet<string> EntryFields =
        ["source", "sourceType", "sourceUrl", "skillPath", "skillFolderHash", "installedAt", "updatedAt", "pinnedRef"];

    internal sealed record Record(string? SourceUrl, string? Source, string? SourceType, string? SkillPath, string? Tree, string? Pin);

    internal static ImmutableDictionary<string, Record> ValidateCurrent(CancellationToken token, string? home = null)
    {
        token.ThrowIfCancellationRequested();
        home ??= Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home)) throw new IOException("Cannot establish gh's global lockfile home.");
        var path = Path.Combine(home, ".agents", SkillLockFileReader.FileName);
        if (!File.Exists(path) && !PathResolver.IsSymlink(path)) return ImmutableDictionary<string, Record>.Empty;
        if (PathResolver.IsSymlink(path)) throw new IOException("Shared lockfile is a link; an owner-aware plan is required before gh writes it.");
        var bytes = SkillLockFileReader.ReadBytes(path, token);
        ValidatePayload(bytes, token);
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.GetProperty("skills").EnumerateObject().ToImmutableDictionary(entry => entry.Name,
            entry => new Record(Text(entry.Value, "sourceUrl"), Text(entry.Value, "source"), Text(entry.Value, "sourceType"),
                Text(entry.Value, "skillPath"), Text(entry.Value, "skillFolderHash"), Text(entry.Value, "pinnedRef")), StringComparer.Ordinal);
        static string? Text(JsonElement value, string key) => value.TryGetProperty(key, out var property)
            && property.GetString() is { Length: > 0 } text ? text : null;
    }

    internal static void ValidateUpdateRecords(ImmutableDictionary<string, Record> records, IReadOnlyList<InstalledSkill>? targets)
    {
        if (records.IsEmpty && (targets is null || !targets.Any(target => target.AllOwnershipClaims.Any(claim => claim.Provider == "gh-vercel-global")))) return;
        if (targets is not { Count: > 0 })
            throw new IOException("Existing shared records require exact update ownership; use a coordinated maintenance plan.");
        foreach (var target in targets)
        {
            // gh InstallName is an installation selector, never an authored display name.
            var selectors = target.AllOwnershipClaims.Where(claim => claim.Provider == "gh-vercel-global"
                && claim.Selector is not null).Select(claim => claim.Selector!).Append(Path.GetFileName(target.ResolvedPath))
                .Distinct(StringComparer.Ordinal);
            foreach (var selector in selectors)
            {
                if (!records.TryGetValue(selector, out var record))
                {
                    if (target.AllOwnershipClaims.Any(claim => claim.Provider == "gh-vercel-global" && claim.Selector == selector))
                        throw new IOException("A captured shared ownership record disappeared; refresh before updating.");
                    continue;
                }
                var expectedRepository = target.FrontMatter.Upstream;
                var observedRepository = record.SourceUrl ?? (record.SourceType == "github" ? "https://github.com/" + record.Source : null);
                if (expectedRepository is null || observedRepository is null
                    || SkillSourceIdentity.NormalizeRepository(expectedRepository) != SkillSourceIdentity.NormalizeRepository(observedRepository)
                    || SkillSourceIdentity.NormalizeSubpath(record.SkillPath) != SkillSourceIdentity.NormalizeSubpath(target.FrontMatter.GithubPath)
                    || record.Tree != target.TreeSha || record.Pin != target.FrontMatter.PinnedRef)
                    throw new IOException("The shared selector belongs to different source or revision evidence; use a coordinated maintenance plan.");
            }
        }
    }

    internal static void ValidateCapturedOwners(IReadOnlyList<InstalledSkill>? targets, CancellationToken token)
    {
        if (targets is null) return;
        foreach (var group in targets.SelectMany(target => target.AllOwnershipClaims).GroupBy(claim => claim.LockFilePath, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (group.Key is null || group.Any(claim => claim.EntryFingerprint is null || claim.Selector is null))
                throw new IOException("Captured owner evidence is incomplete; refresh before updating.");
            if (group.Any(claim => claim.ReadOnly || claim.IsExclusiveOwner))
                throw new IOException("Captured contextual ownership requires a coordinated maintenance plan before updating.");
            try
            {
                var bytes = SkillLockFileReader.ReadBytes(group.Key, token);
                if (group.Any(claim => claim.Provider == "gh-vercel-global")) ValidatePayload(bytes, token);
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("skills", out var skills) || skills.ValueKind != JsonValueKind.Object)
                    throw new IOException("Captured owner structure changed; refresh before updating.");
                foreach (var claim in group)
                    if (!skills.TryGetProperty(claim.Selector!, out var entry)
                        || claim.EntryFingerprint != OwnershipRecordFingerprint.Compute(entry, claim.Provider == "gh-vercel-global"))
                        throw new IOException("A captured owner record disappeared or changed; refresh before updating.");
            }
            catch (JsonException ex) { throw new IOException("Captured owner structure is malformed; refresh before updating.", ex); }
        }
    }

    internal static void ValidatePayload(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 3
                || !root.TryGetProperty("skills", out var skills) || skills.ValueKind != JsonValueKind.Object)
                throw new IOException("Shared lockfile schema is unsupported; gh could replace its records. Review a preservation plan first.");
            CheckProperties(root, RootFields);
            if (root.TryGetProperty("dismissed", out var dismissed))
            {
                if (dismissed.ValueKind != JsonValueKind.Object)
                    throw new IOException("Shared lockfile dismissed-state schema is incompatible with gh.");
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in dismissed.EnumerateObject())
                    if (!keys.Add(field.Name) || field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw new IOException("Shared lockfile dismissed-state values are incompatible with gh.");
            }
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in skills.EnumerateObject())
            {
                token.ThrowIfCancellationRequested();
                if (names.Count >= SkillLockFileReader.MaxEntries) throw new IOException("Shared lockfile entry limit exceeded.");
                if (!names.Add(entry.Name) || entry.Value.ValueKind != JsonValueKind.Object)
                    throw new IOException("Shared lockfile has duplicate or malformed entries; no gh write was admitted.");
                CheckProperties(entry.Value, EntryFields);
                if (entry.Value.EnumerateObject().Any(field => field.Value.ValueKind != JsonValueKind.String))
                    throw new IOException("Shared lockfile entry field types are incompatible with gh.");
            }
        }
        catch (JsonException ex) { throw new IOException("Shared lockfile is malformed; no gh write was admitted.", ex); }
    }

    private static void CheckProperties(JsonElement element, HashSet<string> allowed)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in element.EnumerateObject())
            if (!seen.Add(field.Name) || !allowed.Contains(field.Name))
                throw new IOException("Shared lockfile contains extensions or duplicate fields that gh would discard. Review a preservation plan first.");
    }
}
