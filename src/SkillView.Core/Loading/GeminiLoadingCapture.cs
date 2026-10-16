using System.Collections.Immutable;
using System.Text.RegularExpressions;
using SkillView.Inventory;

namespace SkillView.Loading;

internal sealed record GeminiExtensionExpectation(string InstalledDirectory, string PayloadDirectory,
    string? Name, string? Version, bool? Enabled, ImmutableArray<string> Reasons);

/// Local-file expectations for the tagged 0.46.0 contract. No extension is
/// initialized or hydrated, and no saved trust decision is written.
internal static class GeminiLoadingCapture
{
    internal static string SavedTrust(LoadingConfigurationDocument document, string cwd, CancellationToken token)
    {
        if (document.Evidence.State == "missing") return "no-matching-rule";
        if (document.Root is not { Kind: "table" } root) return "unknown";
        var rules = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();
        try
        {
            foreach (var key in root.FieldOrder)
            {
                token.ThrowIfCancellationRequested();
                if (root.Fields[key] is not { Kind: "string", Text: "TRUST_FOLDER" or "TRUST_PARENT" or "DO_NOT_TRUST" } value) return "unknown";
                var normalized = Normalize(key, cwd);
                if (!rules.ContainsKey(normalized)) order.Add(normalized);
                rules[normalized] = value.Text!;
            }
            var location = Normalize(RealPath(cwd), cwd);
            string? winner = null; var length = -1;
            foreach (var key in order)
            {
                token.ThrowIfCancellationRequested();
                var effective = rules[key] == "TRUST_PARENT" ? Path.GetDirectoryName(key) ?? key : key;
                var parent = Normalize(RealPath(effective), cwd).TrimEnd('/');
                if ((location == parent || location.StartsWith(parent + "/", StringComparison.Ordinal)) && key.Length > length)
                { winner = rules[key]; length = key.Length; }
            }
            return winner is null ? "no-matching-rule" : winner == "DO_NOT_TRUST" ? "untrusted" : "trusted";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return "unknown"; }

        static string Normalize(string path, string directory)
        {
            var full = Path.GetFullPath(path, directory).Replace('\\', '/');
            // This intentionally models Gemini's provider contract, not
            // SkillView's path identity or any mutation authority.
            return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? full.ToLowerInvariant() : full;
        }
        static string RealPath(string path) => Directory.Exists(path) || File.Exists(path)
            ? OwnershipPath.CanonicalCandidate(path) ?? throw new IOException("Trust path unavailable.") : path;
    }

    internal static bool? ExtensionEnabled(LoadingConfigurationDocument document, string name, string cwd, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (document.Evidence.State == "missing") return true;
        if (document.Root is not { Kind: "table" } root || root.Fields.Values.Any(value => value.Kind != "table"
            || value.Get("overrides") is not { Kind: "array" } array || array.Items.Any(rule => rule.Kind != "string"))) return null;
        var workspace = cwd.Replace('\\', '/');
        if (!workspace.StartsWith('/')) workspace = "/" + workspace;
        if (!workspace.EndsWith('/')) workspace += "/";
        var enabled = true;
        foreach (var value in root.Get(name, "overrides")?.Items ?? [])
        {
            token.ThrowIfCancellationRequested();
            var rule = value.Text!;
            if (rule.Length > 2048) return null;
            var disable = rule.StartsWith('!');
            if (disable) rule = rule[1..];
            // Stored rules are not normalized. '?' is literal; each '*'
            // (including its optional preceding '/') is an optional group.
            var escaped = Regex.Escape(rule).Replace("/\\*", "(/.*)?", StringComparison.Ordinal)
                .Replace("\\*", "(.*)?", StringComparison.Ordinal);
            try
            {
                if (Regex.IsMatch(workspace, "\\A" + escaped + "\\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)) enabled = !disable;
            }
            catch (NotSupportedException) { return null; }
        }
        return enabled;
    }

    internal static ImmutableArray<GeminiExtensionExpectation> Extensions(string config, string cwd,
        LoadingConfigurationReader files, ImmutableArray<LoadingSettingsLayer>.Builder layers,
        ImmutableArray<string>.Builder diagnostics, ImmutableArray<string>.Builder blockers, CancellationToken token)
    {
        var result = ImmutableArray.CreateBuilder<GeminiExtensionExpectation>();
        var directory = Path.Combine(config, "extensions");
        var enablement = Read(Path.Combine(directory, "extension-enablement.json"), "Gemini extension enablement");
        try { _ = File.GetAttributes(directory); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { blockers.Add("Gemini extension directory could not be inspected; discovery is incomplete."); return []; }
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
            var count = 0;
            while (entries.MoveNext())
            {
                token.ThrowIfCancellationRequested();
                if (++count > 128) { blockers.Add("Gemini extension discovery was bounded; omitted extension competitors remain possible."); break; }
                var installed = entries.Current;
                var attributes = File.GetAttributes(installed);
                if ((attributes & FileAttributes.Directory) == 0) continue;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                { blockers.Add("Gemini extension directory link was not traversed: " + installed); continue; }
                var metadata = Read(Path.Combine(installed, ".gemini-extension-install.json"), "Gemini extension install metadata");
                var payload = installed;
                var reasons = ImmutableArray.CreateBuilder<string>();
                bool? enabled = true;
                if (metadata.Evidence.State is not ("observed" or "missing")) enabled = null;
                if (metadata.Root?.Get("type")?.Text == "link")
                {
                    if (metadata.Root.Get("source") is { Kind: "string", Text: { } source }
                        && Path.IsPathFullyQualified(source) && !source.Contains('$') && !source.Contains('\0')) payload = source;
                    else { enabled = null; reasons.Add("Linked extension source was not a resolved absolute local path."); }
                }
                var manifest = Read(Path.Combine(payload, "gemini-extension.json"), "Gemini extension manifest");
                var name = manifest.Root?.Get("name") is { Kind: "string", Text: { Length: > 0 } text } && Regex.IsMatch(text, "\\A[a-zA-Z0-9-]+\\z", RegexOptions.NonBacktracking) ? text : null;
                var version = manifest.Root?.Get("version") is { Kind: "string", Text: { Length: > 0 } revision } ? revision : null;
                if (name is null || version is null) enabled = null;
                if (enabled == true) enabled = name is null ? null : ExtensionEnabled(enablement, name, cwd, token);
                reasons.Add("Extension manifest/install metadata and saved workspace enablement are local observations; hydration, security policy, remote admin and session selection remain unobserved.");
                result.Add(new(installed, payload, name, version, enabled, reasons.ToImmutable()));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { blockers.Add("Gemini extension discovery is incomplete; an installed directory could not be observed."); }
        // An upstream duplicate manifest name aborts loading; never pick an
        // arbitrary local delivery as the active one.
        var duplicates = result.Where(item => item.Name is not null).GroupBy(item => item.Name, StringComparer.Ordinal).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        if (duplicates.Count > 0) blockers.Add("Duplicate Gemini extension manifest names prevent the native extension list from loading; every extension is conditional.");
        return result.Select(item => duplicates.Contains(item.Name) ? item with
        {
            Enabled = null,
            Reasons = item.Reasons.Add("Duplicate extension manifest names prevent a definite load expectation.")
        } : item).ToImmutableArray();

        LoadingConfigurationDocument Read(string path, string purpose)
        {
            var document = files.Read(path, purpose, token, format: LoadingContentFormat.Json);
            layers.Add(new("gemini-extension-observation", "observed-only", document));
            return document;
        }
    }
}
