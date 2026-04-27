using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using SkillView.Inventory.Models;
using SkillView.Logging;

namespace SkillView.Inventory;

/// Reads `.skill-lock.json` package manifests written by skill-bundling tools
/// (e.g. `npx skills`, vercel-labs/skills, antigravity-awesome-skills). The
/// lockfile lives at the *parent* of an `agent/skills/` directory and records
/// per-skill source repos, install timestamps, and tree hashes. SkillView uses
/// it to colour each `InstalledSkill` with its origin package so the UI can
/// group/sort by source bundle.
///
/// The schema is community-driven — multiple tools write subtly different
/// shapes — so we parse tolerantly via `JsonDocument` and pull only the
/// fields we display.
public sealed class SkillLockFileReader
{
    public const string FileName = ".skill-lock.json";

    private readonly Logger _logger;

    public SkillLockFileReader(Logger logger) => _logger = logger;

    /// Build a `name → SkillPackage` map by reading every lockfile we can
    /// reach from the given scan roots. The lookup key is the skill folder
    /// name (the leaf of `<root>/<name>`), which matches the skill-name
    /// convention used by both `npx skills` and our own `InstalledSkill.Name`.
    public ImmutableDictionary<string, SkillPackage> LoadFromRoots(IEnumerable<string> scanRootPaths)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, SkillPackage>(StringComparer.Ordinal);
        var seenLockfiles = new HashSet<string>(StringComparer.Ordinal);

        foreach (var root in scanRootPaths)
        {
            if (string.IsNullOrEmpty(root)) continue;
            // Lockfile lives one level above the skills/ folder. e.g. for
            // `~/.claude/skills`, look at `~/.claude/.skill-lock.json` AND
            // also `~/.agents/.skill-lock.json` (the tool that owns the
            // symlinks). For `~/.agents/skills`, look at `~/.agents/.skill-lock.json`.
            string? parent;
            try
            {
                parent = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            catch
            {
                continue;
            }
            if (string.IsNullOrEmpty(parent)) continue;

            TryReadInto(Path.Combine(parent, FileName), builder, seenLockfiles);

            // Many configurations symlink `~/.<agent>/skills/<x>` to
            // `~/.agents/skills/<x>`. Probe the home-level `.agents`
            // directory as a sibling fallback so package metadata still
            // attaches to skills discovered via agent-specific scan roots.
            try
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home))
                {
                    TryReadInto(Path.Combine(home, ".agents", FileName), builder, seenLockfiles);
                }
            }
            catch { /* best-effort */ }
        }

        return builder.ToImmutable();
    }

    private void TryReadInto(
        string path,
        ImmutableDictionary<string, SkillPackage>.Builder builder,
        HashSet<string> seen)
    {
        if (!seen.Add(path)) return;
        if (!File.Exists(path)) return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            if (!doc.RootElement.TryGetProperty("skills", out var skills) || skills.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var added = 0;
            foreach (var entry in skills.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                var pkg = ParseEntry(entry.Value);
                if (pkg is null) continue;
                // First lockfile wins on conflict — agents-skill-lock is
                // typically the canonical one; per-agent shadows are rare.
                builder.TryAdd(entry.Name, pkg);
                added++;
            }
            _logger.Info("inventory.lockfile", $"{path}: {added} skill(s) tagged");
        }
        catch (Exception ex)
        {
            _logger.Warn("inventory.lockfile", $"{path}: parse failed — {ex.Message}");
        }
    }

    private static SkillPackage? ParseEntry(JsonElement el)
    {
        var source = ReadString(el, "source");
        if (string.IsNullOrEmpty(source)) return null;
        return new SkillPackage(
            Source: source,
            SourceType: ReadString(el, "sourceType") ?? "unknown",
            SourceUrl: ReadString(el, "sourceUrl"),
            InstalledAt: ReadDate(el, "installedAt"),
            UpdatedAt: ReadDate(el, "updatedAt"));
    }

    private static string? ReadString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? ReadDate(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(v.GetString(), out var d) ? d : null;
}
