using System.Security.Cryptography;
using System.Text.Json;
using SkillView.Inventory.Models;

namespace SkillView.Inventory;

internal sealed partial class ManagedSkillDiscovery
{
    private void Plugin(string path, string provider, string agent, string? id, string? version,
        bool discover, ScanRoot? context = null, IReadOnlyList<ScanRoot>? payloadBoundaries = null)
    {
        token.ThrowIfCancellationRequested();
        var region = new ManagedSkillRegion(path, provider, agent, "plugin", id, version, context?.Context ?? path);
        AddRegion(region);
        var skillPaths = new HashSet<string>(StringComparer.Ordinal) { "./skills" };
        var requiredPaths = new HashSet<string>(StringComparer.Ordinal);
        // Each distributor reads its own manifest. Portable-root precedence is
        // a Codex rule and must never hide a Claude-specific declaration.
        var manifests = provider.Contains("claude", StringComparison.Ordinal)
            ? new[] { Path.Combine(path, ".claude-plugin", "plugin.json") }
            : new[] { Path.Combine(path, "plugin.json"), Path.Combine(path, ".codex-plugin", "plugin.json") };
        var manifest = manifests.FirstOrDefault(Exists);
        if (manifest is not null)
        {
            try
            {
                var bytes = ReadManifest(manifest);
                witnesses[manifest] = Convert.ToHexStringLower(SHA256.HashData(bytes));
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
                ValidateJson(document.RootElement);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    throw new IOException("Plugin manifest must be a JSON object.");
                if (!document.RootElement.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(name.GetString()) || name.GetString()!.Length > 256 || name.GetString()!.Any(char.IsControl))
                    throw new IOException("Plugin manifest requires a nonblank string name.");
                if (document.RootElement.TryGetProperty("skills", out var skills))
                {
                    if (skills.ValueKind == JsonValueKind.String) requiredPaths.Add(skills.GetString()!);
                    else if (skills.ValueKind == JsonValueKind.Array && skills.GetArrayLength() <= 32)
                    {
                        foreach (var skill in skills.EnumerateArray())
                        {
                            if (skill.ValueKind != JsonValueKind.String) throw new IOException("Plugin skill paths must be strings.");
                            requiredPaths.Add(skill.GetString()!);
                        }
                    }
                    else throw new IOException("Unsupported plugin skill declarations.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { Incomplete(manifest, ex.Message); return; }
        }
        else if (provider == "codex-plugin")
        { Incomplete(path, "Codex cached plugin has no manifest; its resources remain protected."); return; }
        if (!discover) return;
        skillPaths.UnionWith(requiredPaths);
        foreach (var relative in skillPaths)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (relative is not "." && !relative.StartsWith("./", StringComparison.Ordinal)
                    || relative.Contains('\\') || relative.Any(char.IsControl)
                    || relative.Split('/').Any(part => part == ".."))
                    throw new IOException("Plugin skill path must be contained and relative to its plugin root.");
                var candidate = Path.GetFullPath(relative, path);
                if (!PathIdentity.IsInside(candidate, path) || !region.Covers(candidate))
                    throw new IOException("Plugin skill path escapes its plugin root.");
                var canonicalRoot = OwnershipPath.CanonicalCandidate(path);
                var canonicalCandidate = OwnershipPath.CanonicalCandidate(candidate);
                if (canonicalRoot is null || canonicalCandidate is null || !PathIdentity.IsInside(canonicalCandidate, canonicalRoot))
                    throw new IOException("Plugin skill path resolves outside its plugin root.");
                if (!DirectoryExists(candidate))
                {
                    if (requiredPaths.Contains(relative)) Incomplete(candidate, "Declared plugin skill folder is missing.");
                    else if (locations.Count < 128) locations.Add(new(candidate, "absent", "No default skills directory in this plugin."));
                    continue;
                }
                if (payloadBoundaries is null) Root(candidate, region, context);
                else foreach (var boundary in payloadBoundaries)
                    {
                        token.ThrowIfCancellationRequested();
                        var physicalBoundary = OwnershipPath.CanonicalCandidate(boundary.Path)
                            ?? throw new IOException("Explicit plugin payload boundary is unobserved.");
                        if (PathIdentity.IsInside(canonicalCandidate, physicalBoundary)) Root(candidate, region, context);
                        else if (PathIdentity.IsInside(physicalBoundary, canonicalCandidate)
                            && !Exists(Path.Combine(candidate, LocalSkillScanner.SkillFileName)))
                            Root(boundary.Path, region, boundary);
                    }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { Incomplete(path, ex.Message); }
        }
    }

    private byte[] ReadManifest(string path)
    {
        token.ThrowIfCancellationRequested();
        using var stream = RegularFileInspection.Open(path);
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var remaining = Math.Min(MaxManifestBytes - checked((int)bytes.Length), MaxTotalMetadataBytes - metadataBytes);
            if (remaining < 0) throw new IOException("Managed manifest metadata budget exceeded.");
            var read = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining + 1));
            metadataBytes += read;
            if (read == 0) return bytes.ToArray();
            if (read > remaining) throw new IOException("Managed manifest metadata budget exceeded.");
            bytes.Write(buffer, 0, read);
        }
    }

    private void ValidateJson(JsonElement value)
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new IOException("Duplicate plugin manifest field: " + property.Name);
                ValidateJson(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) ValidateJson(child);
    }
}
