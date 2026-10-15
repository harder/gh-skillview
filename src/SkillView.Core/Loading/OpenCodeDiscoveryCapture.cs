using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Inventory.Models;

namespace SkillView.Loading;

internal sealed record OpenCodeDiscovery(ImmutableArray<LoadingDiscoveryRoot> Roots,
    ImmutableArray<string> Limitations, ImmutableArray<string> Facts);

/// Pinned v1.19.1 provider roots, shared by scanner admission and placement.
internal static class OpenCodeDiscoveryCapture
{
    internal static OpenCodeDiscovery Capture(string cwd, string project, string home, IEnumerable<string> configDirectories,
        LoadingSettings settings, Func<string, string?> environment, ImmutableArray<string>.Builder blockers, CancellationToken token)
    {
        var roots = ImmutableArray.CreateBuilder<LoadingDiscoveryRoot>(); var limits = ImmutableArray.CreateBuilder<string>();
        var facts = ImmutableArray.CreateBuilder<string>();
        bool? Flag(string key)
        {
            var raw = environment(key); facts.Add(SkillDistributorIdentity.Digest("environment", key, raw));
            if (raw is null) return false;
            if (raw is "true" or "yes" or "on" or "1" or "y") return true;
            if (raw is "false" or "no" or "off" or "0" or "n") return false;
            blockers.Add(key + " is not a supported native boolean; loading policy remains unknown."); return null;
        }
        var external = Flag("OPENCODE_DISABLE_EXTERNAL_SKILLS");
        var broadClaude = Flag("OPENCODE_DISABLE_CLAUDE_CODE");
        var claude = Flag("OPENCODE_DISABLE_CLAUDE_CODE_SKILLS");
        foreach (var directory in configDirectories)
            foreach (var name in new[] { "skill", "skills" }) Add(Path.Combine(directory, name), "config-directory", false);
        foreach (var directory in new[] { home }.Concat(LoadingConfigurationCapture.Ancestors(cwd, project, token)))
        {
            var externalState = external == true ? "excluded" : external is null ? "conditional" : "applied";
            Add(Path.Combine(directory, ".agents", "skills"), "external-agents", true, externalState,
                "OPENCODE_DISABLE_EXTERNAL_SKILLS controls external compatibility discovery.");
            var disabledClaude = external == true || broadClaude == true || claude == true;
            Add(Path.Combine(directory, ".claude", "skills"), "external-claude", true,
                disabledClaude ? "excluded" : external is null || broadClaude is null || claude is null ? "conditional" : "applied",
                "OpenCode external/Claude skill-disable flags control this compatibility root.");
        }
        var merged = settings.Merge("opencode");
        var paths = merged.Get("skills", "paths");
        var pathSource = settings.Layers.LastOrDefault(layer => layer.Applicability == "applied" && layer.Document.Root?.Get("skills", "paths") is not null)?.Document.Evidence.Path;
        if (paths is not null && (paths.Kind != "array" || paths.Items.Any(path => path.Kind != "string")))
            blockers.Add("OpenCode skills.paths must be an array of strings; native configuration decoding is unproven.");
        else foreach (var value in paths?.Items ?? [])
        {
            token.ThrowIfCancellationRequested();
            var path = value.Text!;
            if (path.Contains("{env:", StringComparison.Ordinal) || path.Contains("{file:", StringComparison.Ordinal))
            { limits.Add("OpenCode skills.paths contains unresolved native substitution; an omitted competing root is possible."); continue; }
            try
            {
                // Node path.join keeps even a slash-prefixed tilde remainder
                // beneath home; Path.Combine would discard home for ~//name.
                var expanded = path.StartsWith("~/", StringComparison.Ordinal) ? home + Path.DirectorySeparatorChar + path[2..] : path;
                Add(expanded.Length == 0 ? cwd : Path.GetFullPath(expanded, cwd), "configured-path", false, source: pathSource);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
            { limits.Add("An OpenCode skills.paths location could not be resolved; discovery is incomplete."); }
        }
        if (merged.Get("skills", "urls") is { } urls)
        {
            if (urls.Kind != "array" || urls.Items.Any(url => url.Kind != "string"))
                blockers.Add("OpenCode skills.urls must be an array of strings; native configuration decoding is unproven.");
            else if (!urls.Items.IsEmpty) limits.Add("OpenCode remote skill URLs were not fetched; remote competitors and active loading remain unknown.");
        }
        return new(roots.Distinct().ToImmutableArray(), limits.Distinct().ToImmutableArray(), facts.ToImmutable());

        void Add(string path, string kind, bool hidden, string applicability = "applied", string? reason = null, string? source = null)
        {
            token.ThrowIfCancellationRequested();
            if (roots.Count >= 256)
            { if (!limits.Contains("OpenCode loading-root limit exceeded; discovery is incomplete.")) limits.Add("OpenCode loading-root limit exceeded; discovery is incomplete."); return; }
            roots.Add(new(path, kind, hidden, applicability, reason, source));
        }
    }
}
