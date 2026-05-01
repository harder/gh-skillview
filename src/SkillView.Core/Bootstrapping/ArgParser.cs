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
            subcommandArgs);
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
