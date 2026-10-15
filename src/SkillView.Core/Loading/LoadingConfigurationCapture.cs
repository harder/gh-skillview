using System.Collections.Immutable;
using SkillView.Inventory;
using SkillView.Subprocess;

namespace SkillView.Loading;

internal sealed record LoadingCaptureRequest(string AgentId, string? Profile = null,
    string? ProjectDirectory = null, string? WorkingDirectory = null, string? ConfigDirectory = null,
    string? ClientVersion = null, string? AgentName = null, string ProjectTrust = "unknown");

internal sealed record CapturedLoadingConfiguration(SkillLoadingContext Context, LoadingSettings Settings,
    string SystemDirectory, ImmutableArray<string> Diagnostics)
{
    internal ImmutableArray<GeminiExtensionExpectation> GeminiExtensions { get; init; } = [];
    internal ImmutableArray<LoadingDiscoveryRoot> DiscoveryRoots { get; init; } = [];
    internal ImmutableArray<string> DiscoveryLimitations { get; init; } = [];
}

/// Captures local files only. No client startup, network, plugin initialization,
/// variable/file interpolation or persisted trust mutation is part of analysis.
/// Must run on an owned background worker, never while rendering a view.
internal sealed class LoadingConfigurationCapture(ProcessRunner runner, string? systemDirectoryForTests = null)
{
    internal CapturedLoadingConfiguration Capture(LoadingCaptureRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var agent = AgentIdentity.Normalize(request.AgentId);
        if (request.ProjectTrust is not ("unknown" or "trusted" or "untrusted"))
            throw new ArgumentException("Project trust must be unknown, trusted or untrusted.");
        if (request.Profile is { } profile && (profile.Length is < 1 or > 128
            || profile.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))))
            throw new ArgumentException("Loading profile names must use 1-128 ASCII letters, digits, hyphens or underscores.");
        var explicitProject = request.ProjectDirectory is { } selectedProject ? Path.GetFullPath(selectedProject, runner.EffectiveWorkingDirectory) : null;
        var cwd = Path.GetFullPath(request.WorkingDirectory ?? explicitProject ?? runner.EffectiveWorkingDirectory, runner.EffectiveWorkingDirectory);
        var project = explicitProject ?? ScanRootResolver.FindGitRootWithCancellation(cwd, token) ?? cwd;
        if (!PathIdentity.IsInside(cwd, project)) throw new ArgumentException("The loading working directory must be inside the selected project.");
        string? Env(string name) => runner.GetEffectiveEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
        string? EnvPath(string name) => Env(name) is { } value ? Path.GetFullPath(value, cwd) : null;
        var home = EnvPath(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (agent == "gemini-cli") home = EnvPath("GEMINI_CLI_HOME") ?? home;
        var config = Path.GetFullPath(request.ConfigDirectory ?? (agent switch
        {
            "claude-code" => EnvPath("CLAUDE_CONFIG_DIR") ?? Path.Combine(home, ".claude"),
            "codex" => EnvPath("CODEX_HOME") ?? Path.Combine(home, ".codex"),
            "gemini-cli" => Path.Combine(home, ".gemini"),
            "opencode" => Path.Combine(EnvPath("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config"), "opencode"),
            _ => home
        }), cwd);
        var system = systemDirectoryForTests ?? SystemDirectory(agent, EnvPath("ProgramData"));
        var files = new LoadingConfigurationReader(512);
        var layers = ImmutableArray.CreateBuilder<LoadingSettingsLayer>();
        var unknown = ImmutableArray.CreateBuilder<string>();
        var blockers = ImmutableArray.CreateBuilder<string>();
        var diagnostics = ImmutableArray.CreateBuilder<string>();
        var extensions = ImmutableArray<GeminiExtensionExpectation>.Empty;
        var openCodeDirectories = ImmutableArray<string>.Empty;
        var discoveryRoots = ImmutableArray<LoadingDiscoveryRoot>.Empty;
        var discoveryLimitations = ImmutableArray<string>.Empty;
        var capturedFacts = ImmutableArray.CreateBuilder<string>();
        var projectApplicability = request.ProjectTrust switch { "trusted" => "applied", "untrusted" => "excluded", _ => "conditional" };
        if (request.ProjectTrust != "unknown") diagnostics.Add("Project trust is an explicit analysis assumption, not an observation of an active client session.");
        if (request.Profile is not null && request.ConfigDirectory is null && agent != "codex")
            blockers.Add("Profile-to-config-directory association was not supplied; default configuration cannot establish selected-profile policy.");

        switch (agent)
        {
            case "claude-code":
                Add(Path.Combine(config, "settings.json"), "user");
                Add(Path.Combine(cwd, ".claude", "settings.json"), "project-shared");
                layers[^1] = layers[^1] with { ProjectTrust = request.ProjectTrust };
                // Current docs relocate local settings to the repository root
                // (main checkout for worktrees), retaining older CWD files.
                // File observations are retained; runtime/worktree applicability
                // cannot be inferred from a guessed version or from existence.
                if (!PathIdentity.Equals(cwd, project)) Add(Path.Combine(cwd, ".claude", "settings.local.json"), "legacy-project-local", "conditional");
                Add(Path.Combine(project, ".claude", "settings.local.json"), "project-local");
                layers[^1] = layers[^1] with { ProjectTrust = request.ProjectTrust == "trusted" ? "trusted" : "unknown" };
                Add(Path.Combine(system, "managed-settings.json"), "managed-file-preview");
                CaptureClaudeDropIns();
                unknown.Add("Claude remote account/MDM/registry/embedding-host policy and managed-source selection are unobserved; file policy is conditional on selection.");
                unknown.Add("Claude session settings sources, CLI flags, bare/safe mode, plugin enablement and synced account skills are unobserved.");
                unknown.Add("Claude project-local placement/track status and trust may differ by client version or worktree; observed files do not establish active applicability.");
                break;
            case "gemini-cli":
                var systemFile = EnvPath("GEMINI_CLI_SYSTEM_SETTINGS_PATH") ?? Path.Combine(system, "settings.json");
                var defaultsFile = EnvPath("GEMINI_CLI_SYSTEM_DEFAULTS_PATH") ?? Path.Combine(Path.GetDirectoryName(systemFile)!, "system-defaults.json");
                Add(defaultsFile, "system-defaults", syntax: LoadingJsonSyntax.Comments);
                Add(Path.Combine(config, "settings.json"), "user", syntax: LoadingJsonSyntax.Comments);
                // System overrides also decide whether folder trust is enabled.
                var systemDocument = files.Read(systemFile, "system-overrides", token, LoadingJsonSyntax.Comments, format: LoadingContentFormat.Json);
                var baseSettings = new LoadingSettings(layers.ToImmutable().Add(new("system-overrides", "applied", systemDocument)), []);
                var folderTrust = baseSettings.Merge(agent).Get("security", "folderTrust", "enabled");
                if (Env("GEMINI_CLI_TRUST_WORKSPACE") == "true" || folderTrust?.Boolean == false) projectApplicability = "applied";
                if (!PathIdentity.Equals(cwd, home)) Add(Path.Combine(cwd, ".gemini", "settings.json"), "workspace", projectApplicability, LoadingJsonSyntax.Comments);
                layers.Add(new("system-overrides", "applied", systemDocument));
                unknown.Add("Gemini remote admin skills policy replaces file-based admin settings; it has not been observed.");
                var savedTrustFile = files.Read(EnvPath("GEMINI_CLI_TRUSTED_FOLDERS_PATH") ?? Path.Combine(config, "trustedFolders.json"),
                    "Gemini saved trust rules", token, LoadingJsonSyntax.Comments, format: LoadingContentFormat.Json);
                layers.Add(new("saved-trust-observation", "observed-only", savedTrustFile));
                diagnostics.Add("Gemini saved trust expectation: " + GeminiLoadingCapture.SavedTrust(savedTrustFile, cwd, token)
                    + "; IDE/session trust may take precedence; explicit analysis/environment trust is reported separately.");
                extensions = GeminiLoadingCapture.Extensions(config, cwd, files, layers, diagnostics, blockers, token);
                unknown.Add("Gemini IDE/session trust and runtime extension selection remain unobserved; saved trust is a conditional file expectation.");
                break;
            case "codex":
                Add(Path.Combine(system, "config.toml"), "system-defaults", format: LoadingContentFormat.Toml);
                Add(Path.Combine(config, "config.toml"), "user", format: LoadingContentFormat.Toml);
                if (request.Profile is not null) Add(Path.Combine(config, request.Profile + ".config.toml"), "selected-profile", format: LoadingContentFormat.Toml);
                foreach (var directory in Ancestors(cwd, project, token).Reverse())
                    Add(Path.Combine(directory, ".codex", "config.toml"), "project", projectApplicability, format: LoadingContentFormat.Toml);
                Add(Path.Combine(system, "requirements.toml"), "system-requirements-observation", "observed-only", format: LoadingContentFormat.Toml);
                unknown.Add("Codex cloud defaults, MDM/managed requirements, CLI overrides and active-session configuration are unobserved.");
                break;
            case "opencode":
                Add(Path.Combine(config, "config.json"), "legacy-global", syntax: LoadingJsonSyntax.Jsonc);
                AddPair(config, "global");
                if (EnvPath("OPENCODE_CONFIG") is { } custom) Add(custom, "custom-config", syntax: LoadingJsonSyntax.Jsonc);
                var ancestors = Ancestors(cwd, project, token);
                var disableProjectValue = runner.GetEffectiveEnvironmentVariable("OPENCODE_DISABLE_PROJECT_CONFIG");
                var disableProject = disableProjectValue?.ToLowerInvariant() is "true" or "1";
                capturedFacts.Add(SkillView.Inventory.Models.SkillDistributorIdentity.Digest("OPENCODE_DISABLE_PROJECT_CONFIG", disableProjectValue));
                foreach (var directory in disableProject ? [] : ancestors.Reverse())
                {
                    AddPair(directory, "project");
                }
                var agentCapture = new OpenCodeAgentCapture();
                openCodeDirectories = new[] { config }.Concat(disableProject ? [] : ancestors.Select(directory => Path.Combine(directory, ".opencode")))
                    .Concat(new[] { Path.Combine(home, ".opencode"), EnvPath("OPENCODE_CONFIG_DIR") }.OfType<string>()).Distinct(StringComparer.Ordinal).ToImmutableArray();
                foreach (var directory in openCodeDirectories)
                {
                    if (directory.EndsWith(".opencode", StringComparison.Ordinal) || directory == EnvPath("OPENCODE_CONFIG_DIR")) AddPair(directory, "config-directory");
                    agentCapture.Capture(directory, files, layers, blockers, token);
                }
                // This legacy file can trigger a native migration. Observe it
                // without migrating or assuming it has no loading policy.
                Add(Path.Combine(config, "config"), "legacy-toml-observation", "conditional", format: LoadingContentFormat.Toml);
                if (Env("OPENCODE_CONFIG_CONTENT") is { } inline)
                    blockers.Add("OpenCode inline runtime configuration is present but has not been interpreted; fingerprint=" +
                        SkillView.Inventory.Models.SkillDistributorIdentity.Digest(inline));
                if (Env("OPENCODE_PERMISSION") is { } permission)
                    blockers.Add("OpenCode runtime permission configuration is present but has not been interpreted; fingerprint=" +
                        SkillView.Inventory.Models.SkillDistributorIdentity.Digest(permission));
                AddPair(system, "managed-file-preview");
                unknown.Add("OpenCode remote organizational defaults, MDM preferences, session overrides remain unobserved.");
                unknown.Add("OpenCode environment/file substitutions are not expanded; unresolved policy values remain unknown.");
                break;
            default:
                unknown.Add("This agent has no cited local configuration provider.");
                break;
        }

        token.ThrowIfCancellationRequested();
        if (agent == "opencode")
        {
            var intermediate = new LoadingSettings(layers.ToImmutable(), unknown.ToImmutable()) { LocalPolicyBlockers = blockers.ToImmutable() };
            var discovery = OpenCodeDiscoveryCapture.Capture(cwd, project, home, openCodeDirectories, intermediate,
                key => runner.GetEffectiveEnvironmentVariable(key), blockers, token);
            discoveryRoots = discovery.Roots; discoveryLimitations = discovery.Limitations;
            capturedFacts.AddRange(discovery.Facts);
            capturedFacts.AddRange(discoveryRoots.Select(root => SkillView.Inventory.Models.SkillDistributorIdentity.Digest(root.Path, root.Placement,
                root.IncludeHidden.ToString(), root.Applicability, root.Reason, root.SourceEvidence)));
            capturedFacts.AddRange(discoveryLimitations);
            if (intermediate.HasUncertainLocalPolicy)
                discoveryLimitations = discoveryLimitations.Add("OpenCode configuration is unavailable or conditional; additional loading roots cannot be ruled out.");
        }
        var settings = new LoadingSettings(layers.ToImmutable(), unknown.ToImmutable())
        {
            LocalPolicyBlockers = blockers.ToImmutable(),
            CapturedFacts = diagnostics.ToImmutable().AddRange(capturedFacts),
            ExpectedProjectTrust = projectApplicability switch { "applied" => "trusted", "excluded" => "untrusted", _ => "unknown" }
        };
        var rules = AgentLoadingRules.Find(agent);
        var context = SkillLoadingContext.Capture(agent, request.Profile, project, cwd, home, config,
            request.ClientVersion, request.AgentName, request.ProjectTrust, settings.Fingerprint, rules?.Revision ?? "unsupported");
        return new(context, settings, system, diagnostics.ToImmutable())
        { GeminiExtensions = extensions, DiscoveryRoots = discoveryRoots, DiscoveryLimitations = discoveryLimitations };

        void Add(string path, string scope, string applicability = "applied", LoadingJsonSyntax syntax = LoadingJsonSyntax.Strict,
            LoadingContentFormat format = LoadingContentFormat.Json)
        {
            token.ThrowIfCancellationRequested();
            layers.Add(new(scope, applicability, files.Read(path, scope + " local-file expectation", token, syntax, format: format)));
        }
        void AddPair(string directory, string scope)
        {
            Add(Path.Combine(directory, "opencode.json"), scope, syntax: LoadingJsonSyntax.Jsonc);
            Add(Path.Combine(directory, "opencode.jsonc"), scope, syntax: LoadingJsonSyntax.Jsonc);
        }
        void CaptureClaudeDropIns()
        {
            var directory = Path.Combine(system, "managed-settings.d");
            try
            {
                var names = new List<string>();
                using var entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
                var count = 0;
                while (entries.MoveNext())
                {
                    token.ThrowIfCancellationRequested();
                    if (++count > LoadingConfigurationReader.MaxFiles) { blockers.Add("Claude managed drop-in enumeration was bounded; omitted policy is unknown."); break; }
                    var name = Path.GetFileName(entries.Current);
                    if (!name.StartsWith('.') && name.EndsWith(".json", StringComparison.Ordinal)) names.Add(entries.Current);
                }
                foreach (var path in names.Order(StringComparer.Ordinal)) Add(path, "managed-drop-in-preview");
            }
            catch (DirectoryNotFoundException) { }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { blockers.Add("Claude managed drop-in directory is unavailable; omitted policy remains unknown."); }
        }
    }

    internal static ImmutableArray<string> Ancestors(string cwd, string project, CancellationToken token)
    {
        var result = ImmutableArray.CreateBuilder<string>();
        string? cursor = cwd;
        for (var depth = 0; cursor is not null && depth < 32; depth++)
        {
            token.ThrowIfCancellationRequested();
            result.Add(cursor);
            if (PathIdentity.Equals(cursor, project)) return result.ToImmutable();
            cursor = Path.GetDirectoryName(cursor);
        }
        throw new IOException("Loading context ancestor depth exceeded; no complete policy was inferred.");
    }

    private static string SystemDirectory(string agent, string? programData) => agent switch
    {
        "claude-code" => OperatingSystem.IsMacOS() ? "/Library/Application Support/ClaudeCode" : OperatingSystem.IsWindows() ? @"C:\Program Files\ClaudeCode" : "/etc/claude-code",
        "gemini-cli" => OperatingSystem.IsMacOS() ? "/Library/Application Support/GeminiCli" : OperatingSystem.IsWindows() ? @"C:\ProgramData\gemini-cli" : "/etc/gemini-cli",
        "opencode" => OperatingSystem.IsMacOS() ? "/Library/Application Support/opencode" : OperatingSystem.IsWindows()
            ? Path.Combine(programData ?? @"C:\ProgramData", "opencode") : "/etc/opencode",
        "codex" => OperatingSystem.IsWindows() ? Path.Combine(programData ?? @"C:\ProgramData", "OpenAI", "Codex") : "/etc/codex",
        _ => "/nonexistent-skillview-agent-policy"
    };
}
