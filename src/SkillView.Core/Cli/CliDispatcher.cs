using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Diagnostics;
using SkillView.Gh;
using SkillView.Logging;
using SkillView.Ui;

namespace SkillView.Cli;

/// Non-interactive subcommand router. Phase 1 implements `doctor` (with
/// `--json` and `--clear-logs`) and `rescan`. Phases 2–7 fill out the rest.
public static class CliDispatcher
{
    public static async Task<int> RunAsync(AppOptions options, TuiServices services)
    {
        return options.SubcommandName switch
        {
            "doctor" => await DoctorAsync(options, services).ConfigureAwait(false),
            "rescan" => Rescan(services),
            "list" or "remove" or "cleanup" => NotYetImplemented(options.SubcommandName!, services.Logger),
            "--help" or "-h" or "help" => PrintHelp(),
            "--version" or "-V" => PrintVersion(),
            _ => UnknownSubcommand(options.SubcommandName ?? "<null>", services.Logger),
        };
    }

    private static async Task<int> DoctorAsync(AppOptions options, TuiServices services)
    {
        if (options.SubcommandArgs.Contains("--clear-logs"))
        {
            return ClearLogs(services);
        }

        var report = await services.EnvironmentProbe.ProbeAsync().ConfigureAwait(false);
        var json = options.SubcommandArgs.Contains("--json");
        if (json)
        {
            WriteDoctorJson(report, options);
        }
        else
        {
            WriteDoctorText(report, options);
        }

        if (!report.GhFound)
        {
            return ExitCodes.EnvironmentError;
        }
        if (!report.GhMeetsMinimum)
        {
            return ExitCodes.EnvironmentError;
        }
        if (!report.Capabilities.SkillSubcommandPresent)
        {
            return ExitCodes.EnvironmentError;
        }
        return ExitCodes.Success;
    }

    private static void WriteDoctorText(EnvironmentReport r, AppOptions options)
    {
        Console.Out.WriteLine($"invocation    : {options.InvocationMode}");
        Console.Out.WriteLine($"gh path       : {r.GhPath ?? "(not found)"}");
        Console.Out.WriteLine($"gh version    : {r.GhVersionRaw ?? "(unknown)"}");
        Console.Out.WriteLine($"gh minimum    : {GhBinaryLocator.MinimumVersion}{(r.GhMeetsMinimum ? " ✓" : " ✗ too old")}");
        Console.Out.WriteLine($"gh auth       : {AuthSummary(r.Auth)}");
        Console.Out.WriteLine($"gh skill      : {(r.Capabilities.SkillSubcommandPresent ? "present" : "(not detected)")}");
        Console.Out.WriteLine($"gh skill list : {(r.Capabilities.HasSkillList ? "present (--json supported)" : "(not detected — filesystem fallback in use)")}");
        Console.Out.WriteLine($"capabilities  : {CapabilitiesSummary(r.Capabilities)}");
        Console.Out.WriteLine($"debug         : {options.Debug}");
        Console.Out.WriteLine($"log directory : {r.LogDirectory ?? "(unset)"}");
        Console.Out.WriteLine($"scan roots    : {(options.ScanRoots.Count == 0 ? "(default)" : string.Join(", ", options.ScanRoots))}");
        Console.Out.WriteLine($"baseline      : {(r.BaselineOk ? "ok" : "degraded")}");
    }

    private static string AuthSummary(GhAuthStatus auth)
    {
        if (!auth.LoggedIn)
        {
            return "not logged in";
        }
        var host = auth.ActiveHost ?? "?";
        var acct = auth.Account ?? "?";
        var others = auth.Hosts.Length > 1 ? $" (+{auth.Hosts.Length - 1} other host{(auth.Hosts.Length == 2 ? "" : "s")})" : string.Empty;
        return $"{acct}@{host}{others}";
    }

    private static string CapabilitiesSummary(CapabilityProfile c)
    {
        if (!c.SkillSubcommandPresent) return "(gh skill not detected)";
        var bits = new List<string>();
        if (c.SupportsAllowHiddenDirs) bits.Add("allow-hidden-dirs");
        if (c.SupportsUpstream) bits.Add("upstream");
        if (c.SupportsRepoPath) bits.Add("repo-path");
        if (c.SupportsFromLocal) bits.Add("from-local");
        if (c.SupportsUpdateJson) bits.Add("update-json");
        if (c.SupportsUpdateYes) bits.Add("update-yes");
        if (c.HasSkillList) bits.Add("list-json");
        return bits.Count == 0 ? "(no probed flags found)" : string.Join(", ", bits);
    }

    private static void WriteDoctorJson(EnvironmentReport r, AppOptions options)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("invocation", options.InvocationMode.ToString());
            writer.WriteString("ghPath", r.GhPath);
            writer.WriteString("ghVersion", r.GhVersionRaw);
            writer.WriteString("ghVersionParsed", r.GhVersion?.ToString());
            writer.WriteString("ghMinimum", GhBinaryLocator.MinimumVersion.ToString());
            writer.WriteBoolean("ghMeetsMinimum", r.GhMeetsMinimum);
            writer.WriteBoolean("debug", options.Debug);
            writer.WriteString("logDirectory", r.LogDirectory);
            writer.WriteBoolean("baselineOk", r.BaselineOk);

            writer.WriteStartObject("auth");
            writer.WriteBoolean("loggedIn", r.Auth.LoggedIn);
            writer.WriteString("activeHost", r.Auth.ActiveHost);
            writer.WriteString("account", r.Auth.Account);
            writer.WriteStartArray("hosts");
            foreach (var h in r.Auth.Hosts) writer.WriteStringValue(h);
            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WriteStartObject("capabilities");
            writer.WriteBoolean("skillSubcommandPresent", r.Capabilities.SkillSubcommandPresent);
            writer.WriteBoolean("listSubcommandPresent", r.Capabilities.ListSubcommandPresent);
            writer.WriteBoolean("hasSkillList", r.Capabilities.HasSkillList);
            writer.WriteBoolean("supportsAllowHiddenDirs", r.Capabilities.SupportsAllowHiddenDirs);
            writer.WriteBoolean("supportsUpstream", r.Capabilities.SupportsUpstream);
            writer.WriteBoolean("supportsRepoPath", r.Capabilities.SupportsRepoPath);
            writer.WriteBoolean("supportsFromLocal", r.Capabilities.SupportsFromLocal);
            writer.WriteBoolean("supportsUpdateJson", r.Capabilities.SupportsUpdateJson);
            writer.WriteBoolean("supportsUpdateYes", r.Capabilities.SupportsUpdateYes);
            WriteFlagArray(writer, "installFlags", r.Capabilities.InstallFlags);
            WriteFlagArray(writer, "updateFlags", r.Capabilities.UpdateFlags);
            WriteFlagArray(writer, "listFlags", r.Capabilities.ListFlags);
            WriteFlagArray(writer, "searchFlags", r.Capabilities.SearchFlags);
            writer.WriteEndObject();

            writer.WriteStartArray("scanRoots");
            foreach (var root in options.ScanRoots) writer.WriteStringValue(root);
            writer.WriteEndArray();

            writer.WriteEndObject();
        }
        Console.Out.WriteLine(System.Text.Encoding.UTF8.GetString(ms.ToArray()));
    }

    private static void WriteFlagArray(Utf8JsonWriter writer, string name, IEnumerable<string> flags)
    {
        writer.WriteStartArray(name);
        foreach (var f in flags.OrderBy(x => x, StringComparer.Ordinal))
        {
            writer.WriteStringValue(f);
        }
        writer.WriteEndArray();
    }

    private static int ClearLogs(TuiServices services)
    {
        if (services.FileLogSink is null)
        {
            Console.Error.WriteLine("skillview: log sink not initialized; nothing to clear");
            return ExitCodes.EnvironmentError;
        }
        var count = services.FileLogSink.ClearAll();
        Console.Out.WriteLine($"cleared {count} log file(s) from {services.LogDirectory}");
        return ExitCodes.Success;
    }

    private static int Rescan(TuiServices services)
    {
        services.Logger.Info("cli.rescan", "rescan requested — inventory service lands in Phase 2");
        return ExitCodes.Success;
    }

    private static int NotYetImplemented(string name, Logger logger)
    {
        logger.Warn("cli", $"subcommand '{name}' is not yet implemented (scheduled for Phase 2-7)");
        Console.Error.WriteLine($"skillview: '{name}' is not yet implemented");
        return ExitCodes.EnvironmentError;
    }

    private static int UnknownSubcommand(string name, Logger logger)
    {
        logger.Warn("cli", $"unknown subcommand '{name}'");
        Console.Error.WriteLine($"skillview: unknown subcommand '{name}'");
        Console.Error.WriteLine("try `skillview --help`");
        return ExitCodes.InvalidUsage;
    }

    private static int PrintHelp()
    {
        Console.Out.WriteLine("""
            SkillView — view and manage AI agent skills via gh skill.

            Usage:
              skillview [--debug] [--scan-root <path>]
              skillview <subcommand> [args]

            Subcommands (Phase 1 implements doctor and rescan):
              doctor [--json] [--clear-logs]
                                  Environment, auth, and gh capability report
              rescan              Trigger an inventory rescan
              list                (Phase 2) inventory listing
              remove <name>       (Phase 6) safe skill removal
              cleanup             (Phase 6) cleanup candidate review

            Global flags:
              --debug             Enable Debug-level logging (streams to stderr in CLI mode)
              --scan-root <path>  Add a custom scan root (repeatable)
              --help | -h         Show this help
              --version | -V      Show version

            Environment:
              SKILLVIEW_LOG=debug  Alternative to --debug (flag takes precedence)

            With no subcommand, the TUI launches.
            """);
        return ExitCodes.Success;
    }

    private static int PrintVersion()
    {
        var version = typeof(CliDispatcher).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        Console.Out.WriteLine($"skillview {version}");
        return ExitCodes.Success;
    }
}
