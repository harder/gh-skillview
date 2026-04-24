using System.Diagnostics;
using SkillView.Cli;
using SkillView.Logging;
using SkillView.Ui;

namespace SkillView.Bootstrapping;

/// Shared Main entry for both `skillview` and `gh-skillview` binaries.
/// Entrypoint projects wrap this with a two-line Program.cs.
public static class EntryPoint
{
    public static async Task<int> RunAsync(string[] args)
    {
        var startupStopwatch = Stopwatch.StartNew();
        string processPath;
        try
        {
            processPath = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
        }
        catch
        {
            processPath = "skillview";
        }

        AppOptions options;
        try
        {
            options = ArgParser.Parse(processPath, args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"skillview: {ex.Message}");
            return ExitCodes.InvalidUsage;
        }

        var logger = new Logger(options.Debug ? LogLevel.Debug : LogLevel.Info);
        logger.Info("startup", $"SkillView invocation={options.InvocationMode} dispatch={options.DispatchMode}");

        // File sink is best-effort; creation failures fall back to memory-only logging.
        var logDir = LogPaths.Resolve();
        FileLogSink? fileSink = null;
        try
        {
            fileSink = new FileLogSink(logDir);
            fileSink.Attach(logger);
        }
        catch (Exception ex)
        {
            logger.Warn("startup", $"file log sink disabled: {ex.Message}");
        }

        // In CLI mode, `--debug` streams structured log lines to stderr for
        // immediate visibility.
        if (options.Debug && options.DispatchMode == DispatchMode.Cli)
        {
            logger.Subscribe(entry => Console.Error.WriteLine(Logger.Format(entry)));
        }

        var services = TuiServices.Build(logger, fileSink, logDir);

        try
        {
            if (options.DispatchMode == DispatchMode.Cli)
            {
                var rc = await CliDispatcher.RunAsync(options, services).ConfigureAwait(false);
                logger.Debug("startup", $"CLI dispatch completed in {startupStopwatch.ElapsedMilliseconds}ms (exit {rc})");
                return rc;
            }

            var ui = new SkillViewApp(services, options);
            logger.Debug("startup", $"TUI boot time {startupStopwatch.ElapsedMilliseconds}ms");
            return ui.Run();
        }
        finally
        {
            fileSink?.Dispose();
        }
    }
}
