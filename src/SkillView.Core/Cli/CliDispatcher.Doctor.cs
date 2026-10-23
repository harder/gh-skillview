using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Diagnostics;
using SkillView.Gh;
using SkillView.Inventory;
using SkillView.Inventory.Models;
using SkillView.Ui;

namespace SkillView.Cli;

public static partial class CliDispatcher
{

    private static async Task<int> DoctorAsync(
        AppOptions options,
        TuiServices services,
        CancellationToken cancellationToken)
    {
        if (options.SubcommandArgs.Contains("--clear-logs"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ClearLogs(services, cancellationToken);
        }

        var report = await services.EnvironmentProbe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        var inventory = options.SubcommandArgs.Contains("--inventory")
            ? await services.InventoryService.CaptureAsync(report.GhPath,
                new LocalInventoryService.Options(options.ScanRoots, false, WorkspaceDirectories: options.WorkspaceDirectories, Profiles: options.Profiles), cancellationToken).ConfigureAwait(false)
            : null;
        var json = options.SubcommandArgs.Contains("--json");
        if (json)
        {
            WriteDoctorJson(report, options, inventory);
        }
        else
        {
            WriteDoctorText(report, options, inventory);
        }

        return report.BaselineOk ? ExitCodes.Success : ExitCodes.EnvironmentError;
    }

    private static void WriteDoctorText(EnvironmentReport r, AppOptions options, InventorySnapshot? inventory = null)
        => Console.Out.Write(RenderDoctorText(r, options, inventory));

    internal static string RenderDoctorText(EnvironmentReport r, AppOptions options, InventorySnapshot? inventory = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"invocation    : {options.InvocationMode}");
        sb.AppendLine($"gh path       : {r.GhPath ?? "(not found)"}");
        sb.AppendLine($"gh version    : {r.GhVersionRaw ?? "(unknown)"}");
        sb.AppendLine($"gh minimum    : {GhBinaryLocator.MinimumVersion}{(r.GhMeetsMinimum ? " ✓" : " ✗ too old")}");
        sb.AppendLine($"gh auth       : {AuthSummary(r.Auth)}");
        sb.AppendLine($"gh skill      : {(r.GhSkillAvailable ? "present" : "(not detected)")}");
        sb.AppendLine($"debug         : {options.Debug}");
        sb.AppendLine($"log directory : {r.LogDirectory ?? "(unset)"}");
        sb.AppendLine($"scan roots    : {(options.ScanRoots.Count == 0 ? "(default)" : string.Join(", ", options.ScanRoots))}");
        sb.AppendLine($"baseline      : {(r.BaselineOk ? "ok" : "degraded")}");
        if (inventory is not null) sb.Append(DoctorScreen.RenderManagedInventory(inventory));
        return sb.ToString();
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

    private static void WriteDoctorJson(EnvironmentReport r, AppOptions options, InventorySnapshot? inventory = null)
        => WriteJson(w => WriteDoctorJson(w, r, options, inventory));

    internal static string RenderDoctorJson(EnvironmentReport r, AppOptions options, InventorySnapshot? inventory = null)
        => RenderJson(w => WriteDoctorJson(w, r, options, inventory));

    private static void WriteDoctorJson(Utf8JsonWriter writer, EnvironmentReport r, AppOptions options, InventorySnapshot? inventory = null)
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

        // The minimum supported gh version guarantees the required command
        // surface; the smoke check records whether `gh skill` is available.
        writer.WriteBoolean("ghSkillAvailable", r.GhSkillAvailable);

        writer.WriteStartArray("scanRoots");
        foreach (var root in options.ScanRoots) writer.WriteStringValue(root);
        writer.WriteEndArray();

        if (inventory is not null)
        {
            writer.WritePropertyName("inventory");
            WriteListJson(writer, inventory);
        }

        writer.WriteEndObject();
    }


    private static int ClearLogs(TuiServices services, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (services.FileLogSink is null)
        {
            Console.Error.WriteLine("skillview: log sink not initialized; nothing to clear");
            return ExitCodes.EnvironmentError;
        }
        var count = services.FileLogSink.ClearAll(cancellationToken);
        Console.Out.WriteLine($"cleared {count} log file(s) from {services.LogDirectory}");
        return ExitCodes.Success;
    }
}
