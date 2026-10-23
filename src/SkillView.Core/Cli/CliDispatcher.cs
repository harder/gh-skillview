using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using SkillView.Bootstrapping;
using SkillView.Logging;
using SkillView.Threading;
using SkillView.Ui;

namespace SkillView.Cli;

/// Routes non-interactive commands under a shared cancellation/deadline policy.
/// Command handlers, argument parsing and rendering live in feature partials.
public static partial class CliDispatcher
{
    public static async Task<int> RunAsync(
        AppOptions options,
        TuiServices services,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var timeout = TimeoutFor(options.SubcommandName);
        using var deadline = new CancellationSource(
            cancellationToken,
            timeout,
            CancellationCallbackReporter.For(services.Logger, "CLI deadline"));

        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            return options.SubcommandName switch
            {
                "doctor" => await DoctorAsync(options, services, deadline.Token).ConfigureAwait(false),
                "list" => await ListAsync(options, services, deadline.Token).ConfigureAwait(false),
                "effective" => await EffectiveAsync(options, services, deadline.Token).ConfigureAwait(false),
                "check" => await CheckAsync(options, services, deadline.Token).ConfigureAwait(false),
                "authoring-validate" => await AuthoringAsync(options, services, deadline.Token).ConfigureAwait(false),
                "evaluate-review" or "evaluate-run" => await EvaluateAsync(options, services, deadline.Token).ConfigureAwait(false),
                "queue" => await QueueAsync(options, services, deadline.Token).ConfigureAwait(false),
                "inspect" or "plan" or "apply" or "repair" or "verify" => await MaintenanceAsync(options, services, deadline.Token).ConfigureAwait(false),
                "undo" when options.SubcommandArgs.Contains("--plan-file") => await MaintenanceAsync(options, services, deadline.Token).ConfigureAwait(false),
                "rescan" => await RescanAsync(options, services, deadline.Token).ConfigureAwait(false),
                "search" => await SearchAsync(options, services, deadline.Token).ConfigureAwait(false),
                "preview" => await PreviewAsync(options, services, deadline.Token).ConfigureAwait(false),
                "install" => await InstallAsync(options, services, deadline.Token).ConfigureAwait(false),
                "update" => await UpdateAsync(options, services, deadline.Token).ConfigureAwait(false),
                "compare" => await CompareAsync(options, services, deadline.Token).ConfigureAwait(false),
                "backup" => await BackupAsync(options, services, deadline.Token).ConfigureAwait(false),
                "history" => await HistoryAsync(options, services, deadline.Token).ConfigureAwait(false),
                "update-merge-review" or "update-merge-choose" or "update-merge-plan" or "update-preserve" => await LocalReviewAsync(options, services, deadline.Token).ConfigureAwait(false),
                "restore-ack-source" or "update-ack-source" or "reimport-ack-source" or "restore-plan" or "restore-inspect" or "restore-apply" or "restore-recover" or "restore-undo"
                    or "update-plan" or "update-inspect" or "update-apply" or "update-recover" or "update-undo" or "update-repair-owner"
                    or "reimport-plan" or "reimport-inspect" or "reimport-apply" or "reimport-recover" or "reimport-undo"
            or "source-switch-ack-source" or "source-switch-plan" or "source-switch-inspect" or "source-switch-apply" or "source-switch-recover" or "source-switch-undo"
                    or "unpin-plan" or "unpin-inspect" or "unpin-apply" or "unpin-recover" or "unpin-undo" or "unpin-repair-owner" => await RestoreAsync(options, services, deadline.Token).ConfigureAwait(false),
                "quarantine" or "undo" or "quarantine-history" => await QuarantineAsync(options, services, deadline.Token).ConfigureAwait(false),
                "remove" => await RemoveAsync(options, services, deadline.Token).ConfigureAwait(false),
                "cleanup" => await CleanupAsync(options, services, deadline.Token).ConfigureAwait(false),
                "ignore" => await IgnoreAsync(options, services, deadline.Token).ConfigureAwait(false),
                "--help" or "-h" or "help" => PrintHelp(options),
                "--version" or "-V" => PrintVersion(options),
                _ => UnknownSubcommand(options.SubcommandName ?? "<null>", services.Logger),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                $"skillview: {options.SubcommandName ?? "operation"} timed out after {timeout.TotalSeconds:F0}s");
            return ExitCodes.EnvironmentError;
        }
    }

    internal static TimeSpan TimeoutFor(string? subcommand) => subcommand switch
    {
        "evaluate-review" => TimeSpan.FromMinutes(2),
        "evaluate-run" => TimeSpan.FromMinutes(10),
        "doctor" or "preview" => TimeSpan.FromSeconds(30),
        "list" or "effective" or "check" or "authoring-validate" or "queue" or "inspect" or "verify" or "rescan" or "search" or "compare" or "history" or "ignore" => TimeSpan.FromMinutes(2),
        "plan" or "apply" or "repair" or "install" or "update" or "remove" or "cleanup" or "backup" or "quarantine" or "undo" or "quarantine-history" or "restore-ack-source" or "update-ack-source" or "reimport-ack-source" or "restore-plan" or "restore-inspect" or "restore-apply" or "restore-recover" or "restore-undo"
            or "update-plan" or "update-inspect" or "update-apply" or "update-recover" or "update-undo" or "update-repair-owner"
            or "reimport-plan" or "reimport-inspect" or "reimport-apply" or "reimport-recover" or "reimport-undo"
            or "source-switch-ack-source" or "source-switch-plan" or "source-switch-inspect" or "source-switch-apply" or "source-switch-recover" or "source-switch-undo"
            or "unpin-plan" or "unpin-inspect" or "unpin-apply" or "unpin-recover" or "unpin-undo" or "unpin-repair-owner"
            or "update-merge-review" or "update-merge-choose" or "update-merge-plan" or "update-preserve" => TimeSpan.FromMinutes(10),
        _ => TimeSpan.FromSeconds(30),
    };

    private static void WriteJson(Action<Utf8JsonWriter> write)
    {
        using var stream = new Utf8TextWriterStream(Console.Out);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        write(writer);
        writer.Flush();
        Console.Out.WriteLine();
    }

    private static string RenderJson(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private static int UnknownSubcommand(string name, Logger logger)
    {
        logger.Warn("cli", $"unknown subcommand '{name}'");
        Console.Error.WriteLine($"skillview: unknown subcommand '{name}'");
        Console.Error.WriteLine("try `skillview --help`");
        return ExitCodes.InvalidUsage;
    }
}
