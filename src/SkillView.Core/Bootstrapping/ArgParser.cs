using System.IO;

namespace SkillView.Bootstrapping;

/// Minimal global-flag splitter. Parses the global flags we recognize and
/// leaves everything else (first non-flag token and beyond) as the subcommand
/// for CLI mode dispatch. No third-party argument parser by design: v1 wants
/// a tiny, inspectable surface.
public static class ArgParser
{
    public static AppOptions Parse(string processPath, string[] args)
    {
        var invocation = DetermineInvocationMode(processPath);
        var debug = Environment.GetEnvironmentVariable("SKILLVIEW_LOG")?.Equals("debug", StringComparison.OrdinalIgnoreCase) ?? false;
        var theme = ParseTheme(Environment.GetEnvironmentVariable("SKILLVIEW_THEME"));
        var scanRoots = new List<string>();
        var workspaces = new List<string>();
        var profiles = new List<SkillView.Inventory.ScanRootResolver.AgentProfileRoot>();
        string? subcommand = null;
        var subcommandArgs = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            // `--debug` is global: recognized anywhere before or after the
            // subcommand. Strip it from the subcommand payload so downstream
            // parsers don't have to know about it.
            if (arg == "--debug")
            {
                debug = true;
                continue;
            }

            if (arg.StartsWith("--theme=", StringComparison.Ordinal))
            {
                theme = ParseTheme(arg["--theme=".Length..]);
                continue;
            }

            if (arg == "--theme")
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("--theme requires a value");
                }
                theme = ParseTheme(args[++i]);
                continue;
            }

            if (subcommand is not null)
            {
                subcommandArgs.Add(arg);
                continue;
            }

            if (arg.StartsWith("--scan-root=", StringComparison.Ordinal))
            {
                scanRoots.Add(arg["--scan-root=".Length..]);
                continue;
            }

            if (arg is "--workspace" or "--profile" || arg.StartsWith("--workspace=", StringComparison.Ordinal) || arg.StartsWith("--profile=", StringComparison.Ordinal))
            {
                var equals = arg.IndexOf('=');
                var flag = equals < 0 ? arg : arg[..equals];
                var value = equals >= 0 ? arg[(equals + 1)..] : ++i < args.Length ? args[i] : throw new ArgumentException(flag + " requires a value");
                if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(flag + " requires a nonblank value");
                if (flag == "--workspace") workspaces.Add(value);
                else
                {
                    var split = value.IndexOf('=');
                    if (split <= 0 || split == value.Length - 1) throw new ArgumentException("--profile requires agent[:name]=skills-directory");
                    var identity = value[..split].Split(':', 2);
                    if (!SkillView.Gh.GhPlacementCatalog.Entries.Any(entry => entry.Id == SkillView.Inventory.AgentIdentity.Normalize(identity[0])))
                        throw new ArgumentException("Unknown profile agent: " + identity[0]);
                    if (identity.Length == 2 && (string.IsNullOrWhiteSpace(identity[1]) || identity[1].Length > 128 || identity[1].Any(char.IsControl)))
                        throw new ArgumentException("Profile name must be nonblank, at most 128 characters and contain no control characters");
                    profiles.Add(new(identity[0], value[(split + 1)..], identity.Length == 2 ? identity[1] : null));
                }
                if (workspaces.Count > 32 || profiles.Count > 32) throw new ArgumentException("Workspace/profile limit exceeded (32 each)");
                continue;
            }

            if (arg == "--scan-root")
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("--scan-root requires a path argument");
                }
                scanRoots.Add(args[++i]);
                continue;
            }

            // First non-global-flag token starts the subcommand payload.
            subcommand = arg;
        }

        var dispatch = subcommand is null ? DispatchMode.Tui : DispatchMode.Cli;
        return new AppOptions(
            invocation,
            dispatch,
            debug,
            theme,
            scanRoots,
            subcommand,
            subcommandArgs)
        { WorkspaceDirectories = workspaces, Profiles = profiles };
    }

    internal static AppTheme ParseTheme(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "high-contrast" or "highcontrast" or "contrast" => AppTheme.HighContrast,
            _ => AppTheme.Default,
        };

    private static InvocationMode DetermineInvocationMode(string processPath)
    {
        var name = Path.GetFileNameWithoutExtension(processPath);
        return name.StartsWith("gh-skillview", StringComparison.OrdinalIgnoreCase)
            ? InvocationMode.GhExtension
            : InvocationMode.Standalone;
    }
}
