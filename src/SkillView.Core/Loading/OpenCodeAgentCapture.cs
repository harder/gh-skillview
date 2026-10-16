using System.Collections.Immutable;

namespace SkillView.Loading;

/// Bounded header observations at OpenCode's config-directory merge boundary.
/// Linked containers are reported as omitted instead of following an unbounded
/// graph. Prompt bodies and client/plugin initialization are never needed.
internal sealed class OpenCodeAgentCapture(Action<string>? beforeChildInspectionForTests = null)
{
    private int _entries;
    internal void Capture(string directory, LoadingConfigurationReader reader,
        ImmutableArray<LoadingSettingsLayer>.Builder layers, ImmutableArray<string>.Builder blockers, CancellationToken token)
    {
        foreach (var phase in new[] { new[] { "agent", "agents" }, new[] { "mode", "modes" } })
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prefix in phase)
                Walk(Path.Combine(directory, prefix), "", 0, phase[0] == "agent");

            void Walk(string path, string relative, int depth, bool recursive)
            {
                token.ThrowIfCancellationRequested();
                if (_entries >= 256) { Block("entry budget exhausted"); return; }
                FileAttributes attributes;
                try { attributes = File.GetAttributes(path); }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
                { if (depth > 0) Block("listed agent directory disappeared"); return; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { Block("agent directory observation incomplete"); return; }
                try
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { Block("linked agent container omitted"); return; }
                    if ((attributes & FileAttributes.Directory) == 0) return;
                    using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
                    while (entries.MoveNext())
                    {
                        token.ThrowIfCancellationRequested();
                        if (++_entries > 256) { Block("entry budget exhausted"); return; }
                        var child = entries.Current;
                        beforeChildInspectionForTests?.Invoke(child);
                        var childAttributes = File.GetAttributes(child);
                        var name = Path.GetFileName(child);
                        if ((childAttributes & FileAttributes.Directory) != 0)
                        {
                            if (!recursive) continue;
                            if (depth >= 8) { Block("agent directory depth exhausted"); continue; }
                            Walk(child, relative + name + "/", depth + 1, true);
                            continue;
                        }
                        if (!name.EndsWith(".md", StringComparison.Ordinal)) continue;
                        var document = reader.Read(child, "OpenCode " + phase[0] + " header", token, frontMatter: true);
                        if (document.Root is not { Kind: "table" } header)
                        { layers.Add(new("agent-header-observation", "observed-only", document)); Block("agent header unavailable or unsupported"); continue; }
                        var declared = header.Get("name");
                        if (declared is not null && (declared.Kind != "string" || string.IsNullOrWhiteSpace(declared.Text) || declared.Text.Any(char.IsControl)))
                        { layers.Add(new("agent-header-observation", "observed-only", document)); Block("agent name unsupported"); continue; }
                        var key = declared?.Text ?? relative + name[..^3];
                        if (!names.Add(key)) Block("duplicate agent header names have unverified native enumeration order");
                        if (!recursive && OpenCodePolicyShape.SupportedAgent(header)) header = LoadingSettings.AsPrimaryMode(header);
                        var agent = new LoadingValue("table")
                        { Fields = ImmutableDictionary<string, LoadingValue>.Empty.Add(key, header), FieldOrder = [key] };
                        var root = new LoadingValue("table")
                        { Fields = ImmutableDictionary<string, LoadingValue>.Empty.Add("agent", agent), FieldOrder = ["agent"] };
                        layers.Add(new("agent-header", "applied", document with { Root = root }));
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { Block("agent directory observation incomplete"); }
            }
        }
        void Block(string reason)
        {
            var message = "OpenCode " + reason + "; selected-role policy remains unknown.";
            if (!blockers.Contains(message)) blockers.Add(message);
        }
    }
}
