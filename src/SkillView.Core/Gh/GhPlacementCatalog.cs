using SkillView.Inventory;
using SkillView.Subprocess;
using System.Runtime.InteropServices;

namespace SkillView.Gh;

// Placement contract from cli/cli v2.102.0 registry. This declares where gh
// writes; native agent loading/enablement is a separate observation.
internal static class GhPlacementCatalog
{
    internal sealed record Entry(string Id, string ProjectDirectory, string UserDirectory);
    internal static readonly Entry[] Entries =
    [
        new("github-copilot", ".agents/skills", ".copilot/skills"),
        new("claude-code", ".claude/skills", ".claude/skills"),
        new("cursor", ".agents/skills", ".cursor/skills"),
        new("codex", ".agents/skills", ".agents/skills"),
        new("gemini-cli", ".agents/skills", ".gemini/skills"),
        new("antigravity", ".agents/skills", ".gemini/antigravity/skills"),
        new("antigravity-cli", ".agents/skills", ".gemini/antigravity-cli/skills"),
        new("antigravity2.0", ".agents/skills", ".gemini/config/skills"),
        new("adal", ".adal/skills", ".adal/skills"),
        new("amp", ".agents/skills", ".config/agents/skills"),
        new("augment", ".augment/skills", ".augment/skills"),
        new("bob", ".bob/skills", ".bob/skills"),
        new("cline", ".agents/skills", ".agents/skills"),
        new("codebuddy", ".codebuddy/skills", ".codebuddy/skills"),
        new("command-code", ".commandcode/skills", ".commandcode/skills"),
        new("continue", ".continue/skills", ".continue/skills"),
        new("cortex", ".cortex/skills", ".snowflake/cortex/skills"),
        new("crush", ".crush/skills", ".config/crush/skills"),
        new("deepagents", ".agents/skills", ".deepagents/agent/skills"),
        new("devin", ".devin/skills", ".devin/skills"),
        new("droid", ".factory/skills", ".factory/skills"),
        new("firebender", ".agents/skills", ".firebender/skills"),
        new("goose", ".goose/skills", ".config/goose/skills"),
        new("grok", ".grok/skills", ".grok/skills"),
        new("iflow-cli", ".iflow/skills", ".iflow/skills"),
        new("junie", ".junie/skills", ".junie/skills"),
        new("kilo", ".kilocode/skills", ".kilocode/skills"),
        new("kimi-cli", ".agents/skills", ".config/agents/skills"),
        new("kiro-cli", ".kiro/skills", ".kiro/skills"),
        new("kode", ".kode/skills", ".kode/skills"),
        new("mcpjam", ".mcpjam/skills", ".mcpjam/skills"),
        new("mistral-vibe", ".vibe/skills", ".vibe/skills"),
        new("mux", ".mux/skills", ".mux/skills"),
        new("neovate", ".neovate/skills", ".neovate/skills"),
        new("openclaw", "skills", ".openclaw/skills"),
        new("opencode", ".agents/skills", ".config/opencode/skills"),
        new("openhands", ".openhands/skills", ".openhands/skills"),
        new("pi", ".pi/skills", ".pi/agent/skills"),
        new("pochi", ".pochi/skills", ".pochi/skills"),
        new("qoder", ".qoder/skills", ".qoder/skills"),
        new("qwen-code", ".qwen/skills", ".qwen/skills"),
        new("replit", ".agents/skills", ".config/agents/skills"),
        new("roo", ".roo/skills", ".roo/skills"),
        new("trae", ".trae/skills", ".trae/skills"),
        new("trae-cn", ".trae/skills", ".trae-cn/skills"),
        new("universal", ".agents/skills", ".agents/skills"),
        new("warp", ".agents/skills", ".agents/skills"),
        new("zencoder", ".zencoder/skills", ".zencoder/skills"),
    ];

    internal static async Task<IReadOnlyList<string>> DestinationsAsync(GhSkillInstallService.Options options, ProcessRunner runner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var workingDirectory = Path.GetFullPath(options.WorkingDirectory ?? Environment.CurrentDirectory);
        if (options.Path is { Length: > 0 } path) return [Path.GetFullPath(path, workingDirectory)];
        var scope = options.Scope ?? "project";
        var home = runner.GetEffectiveEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME", options.ProcessEnvironment)
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var project = scope == "project" ? await ProjectRootAsync(runner, options, workingDirectory, token).ConfigureAwait(false) : null;
        if (options.Agents?.Count > 128) throw new IOException("Installation destination limit exceeded.");
        var ids = options.Agents?.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray() ?? [];
        if (ids.Length == 0) ids = ["github-copilot"];
        var paths = new List<string>();
        foreach (var id in ids)
        {
            token.ThrowIfCancellationRequested();
            var entry = Entries.SingleOrDefault(entry => entry.Id == AgentIdentity.Normalize(id))
                ?? throw new IOException("Unknown gh installation destination; select an explicit directory.");
            if (scope == "project") paths.Add(Path.Combine(project!, entry.ProjectDirectory));
            else if (scope == "user")
            {
                var overrideKey = entry.Id == "claude-code" ? "CLAUDE_CONFIG_DIR" : entry.Id == "pi" ? "PI_CODING_AGENT_DIR" : null;
                var config = overrideKey is null ? null : runner.GetEffectiveEnvironmentVariable(overrideKey, options.ProcessEnvironment);
                paths.Add(Path.Combine(home, entry.UserDirectory));
                if (entry.Id == "codex") paths.Add(Path.Combine(home, ".codex", "skills"));
                // PI's override became authoritative in gh2.99. Probe both
                // possible roots until placement has an observed host version.
                if (!string.IsNullOrWhiteSpace(config)) paths.Add(Path.Combine(config, "skills"));
            }
            else throw new IOException("Unknown install scope; select an explicit directory.");
        }
        return paths.Select(path => Path.GetFullPath(path, workingDirectory))
            .GroupBy(PathIdentity.NormalizeKey, StringComparer.Ordinal).Select(group => group.First()).ToArray();
    }

    private static async Task<string> ProjectRootAsync(ProcessRunner runner, GhSkillInstallService.Options options,
        string workingDirectory, CancellationToken token)
    {
        // gh resolves Git's top level under its effective child environment,
        // including GIT_DIR/GIT_WORK_TREE, and falls back to CWD on Git failure.
        var search = runner.GetEffectiveEnvironmentVariable("PATH", options.ProcessEnvironment) ?? "";
        var configuredExtensions = runner.GetEffectiveEnvironmentVariable("PATHEXT", options.ProcessEnvironment);
        var extensions = OperatingSystem.IsWindows()
            ? (string.IsNullOrEmpty(configuredExtensions) ? ".com;.exe;.bat;.cmd" : configuredExtensions)
                .Split(';', StringSplitOptions.RemoveEmptyEntries).Select(extension => extension.StartsWith('.') ? extension : "." + extension).ToArray()
            : [""];
        if (extensions.Length > 32) throw new IOException("Git executable extension limit exceeded.");
        var names = extensions.Select(extension => "git" + extension).ToArray();
        var searchDirectories = search.Length == 0 ? [] : search.Split(Path.PathSeparator);
        if (searchDirectories.Length > 256 || (OperatingSystem.IsWindows() && search.Contains('"'))
            || searchDirectories.Any(directory => !string.IsNullOrEmpty(directory) && !Path.IsPathFullyQualified(directory))
            || (!OperatingSystem.IsWindows() && searchDirectories.Any(string.IsNullOrEmpty)))
            throw new IOException("Git executable discovery is ambiguous; select an explicit installation directory.");
        if (OperatingSystem.IsWindows() && runner.GetEffectiveEnvironmentVariable("NoDefaultCurrentDirectoryInExePath", options.ProcessEnvironment) is null
            && names.Any(name => File.Exists(Path.Combine(workingDirectory, name))))
            throw new IOException("A CWD Git executable makes placement ambiguous; select an explicit installation directory.");
        string? executable = null;
        foreach (var directory in searchDirectories)
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(directory)) continue;
            foreach (var name in names)
            {
                var candidate = Path.GetFullPath(Path.Combine(directory, name), workingDirectory);
                if (File.Exists(candidate) && Executable(candidate)) { executable = candidate; break; }
            }
            if (executable is not null) break;
        }
        if (executable is null) return workingDirectory;
        var result = await runner.RunAsync(executable, ["rev-parse", "--show-toplevel"],
            workingDirectory: workingDirectory, environment: options.ProcessEnvironment, cancellationToken: token).ConfigureAwait(false);
        if (!result.Succeeded) return workingDirectory;
        var root = result.StdOut.TrimEnd('\r', '\n');
        if (result.StdOutTruncated || result.StdErrTruncated || !Path.IsPathFullyQualified(root) || root.Contains('\n') || root.Contains('\r'))
            throw new IOException("Git project-root discovery is incomplete; no destination was inferred.");
        return Path.GetFullPath(root);
    }

    private static bool Executable(string path)
    {
        if (OperatingSystem.IsWindows()) return true;
        // Match Go's effective-user execute check; only unsupported/blocked
        // access probes may fall back to mode bits, as Go does.
        if (Native.faccessat(OperatingSystem.IsMacOS() ? -2 : -100, path, 1, OperatingSystem.IsMacOS() ? 0x10 : 0x200) == 0) return true;
        var error = Marshal.GetLastPInvokeError();
        if (error is not 1 && error != (OperatingSystem.IsMacOS() ? 78 : 38)) return false;
        return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }

    private static class Native
    {
        [DllImport("libc", SetLastError = true)]
        internal static extern int faccessat(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode, int flags);
    }
}
