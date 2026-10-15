using System.Security.Cryptography;
using System.Text.Json;
using SkillView.Inventory.Models;

namespace SkillView.Inventory;

internal sealed partial class ManagedSkillDiscovery
{
    // This observed v2 registry is evidence of an installation context, never
    // evidence that a plugin is enabled or loaded. Missing records are normal
    // for synced caches and cannot authorize cleanup.
    private void ClaudeRegistry(string storage, bool discover)
    {
        var path = Path.Combine(storage, "installed_plugins.json");
        if (!Exists(path)) return;
        try
        {
            var bytes = ReadManifest(path);
            witnesses[path] = Convert.ToHexStringLower(SHA256.HashData(bytes));
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            ValidateJson(document.RootElement);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var schema)
                || !schema.TryGetInt32(out var version) || version != 2
                || !root.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Object)
                throw new IOException("Unsupported Claude plugin registry schema; ownership topology is incomplete.");
            foreach (var plugin in plugins.EnumerateObject())
            {
                token.ThrowIfCancellationRequested();
                if (plugin.Name.Length is 0 or > 256 || plugin.Name.Any(char.IsControl) || plugin.Value.ValueKind != JsonValueKind.Array)
                    throw new IOException("Unsupported Claude plugin registry entry.");
                foreach (var entry in plugin.Value.EnumerateArray())
                {
                    token.ThrowIfCancellationRequested();
                    if (++entries > MaxEntries) throw new IOException("Claude plugin registry entry budget exceeded.");
                    if (entry.ValueKind != JsonValueKind.Object) throw new IOException("Claude plugin registry record must be an object.");
                    var install = String(entry, "installPath", required: true)!;
                    if (!Path.IsPathFullyQualified(install)) throw new IOException("Claude plugin installPath must be absolute.");
                    install = Path.GetFullPath(install);
                    var scope = String(entry, "scope", required: true);
                    var context = String(entry, "projectPath");
                    var knownScope = scope switch { "user" => Scope.User, "project" or "local" => Scope.Project, _ => (Scope?)null };
                    var region = new ManagedSkillRegion(install, "claude-plugin", "claude", "registered plugin", plugin.Name,
                        String(entry, "version"), context ?? storage)
                    { RegistrationScope = knownScope, RegistrationPath = path };
                    // Retain the boundary before inspecting optional metadata or
                    // deciding whether an external path is eligible for traversal.
                    AddRegion(region);
                    if (knownScope is null || context is not null && !Path.IsPathFullyQualified(context)
                        || scope is "project" or "local" && context is null)
                    { Incomplete(install, "Unsupported or incomplete Claude installation context; resources remain protected."); continue; }
                    var physicalInstall = OwnershipPath.CanonicalCandidate(install)
                        ?? throw new IOException("Registered plugin address is unobserved.");
                    var physicalStorage = OwnershipPath.CanonicalCandidate(storage)
                        ?? throw new IOException("Plugin storage address is unobserved.");
                    // Protection accepts aliases and retained lexical evidence;
                    // traversal authority requires actual physical containment.
                    var contained = PathIdentity.IsInside(physicalInstall, physicalStorage);
                    var explicitRoots = contextRoots.Where(candidate => (candidate.Scope == Scope.Custom || candidate.Profile is not null)
                        && OwnershipPath.CanonicalCandidate(candidate.Path) is { } physicalRoot
                        && (PathIdentity.IsInside(physicalInstall, physicalRoot) || PathIdentity.IsInside(physicalRoot, physicalInstall))).ToArray();
                    if (!contained && explicitRoots.Length == 0)
                    { Incomplete(install, "External registered plugin requires an explicit --scan-root; no arbitrary external traversal performed."); continue; }
                    if (!discover) continue;
                    if (!DirectoryExists(install)) { Incomplete(install, "Registered plugin payload is absent."); continue; }
                    Plugin(install, "claude-plugin", "claude", plugin.Name, region.PackageVersion, true,
                        payloadBoundaries: contained ? null : explicitRoots);
                    // Preserve the observed user/project registration in addition
                    // to cached presence. Both retain unknown enabled/session state.
                    foreach (var payload in roots.Where(payload => payload.ManagedProvider == "claude-plugin" && CoversForObservation(region, payload.Path)).ToArray())
                        Root(payload.Path, region, new(payload.Path, knownScope.Value, "claude") { Context = context ?? storage });
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            topologyIncomplete = true;
            Incomplete(path, ex.Message);
        }
    }

    private static string? String(JsonElement record, string name, bool required = false)
    {
        if (!record.TryGetProperty(name, out var value))
        {
            if (required) throw new IOException("Claude plugin registry is missing " + name + ".");
            return null;
        }
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())
            || value.GetString()!.Length > 4096 || value.GetString()!.Any(char.IsControl))
            throw new IOException("Claude plugin registry requires a bounded string for " + name + ".");
        return value.GetString();
    }
}
