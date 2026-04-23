using SkillView.Diagnostics;
using SkillView.Gh;
using SkillView.Logging;
using SkillView.Subprocess;

namespace SkillView.Ui;

/// Bundle of services the TUI shell depends on. A thin composition root so
/// entrypoint projects don't need to know the full graph.
public sealed class TuiServices
{
    public required Logger Logger { get; init; }
    public required ProcessRunner Runner { get; init; }
    public required GhBinaryLocator GhLocator { get; init; }
    public required GhAuthService AuthService { get; init; }
    public required GhSkillCapabilityProbe CapabilityProbe { get; init; }
    public required EnvironmentProbe EnvironmentProbe { get; init; }
    public required GhSkillSearchService SearchService { get; init; }
    public required GhSkillPreviewService PreviewService { get; init; }
    public required FileLogSink? FileLogSink { get; init; }
    public required string LogDirectory { get; init; }

    public static TuiServices Build(Logger logger, FileLogSink? fileLogSink = null, string? logDirectory = null)
    {
        var runner = new ProcessRunner(logger);
        var locator = new GhBinaryLocator(runner, logger);
        var auth = new GhAuthService(runner, logger);
        var caps = new GhSkillCapabilityProbe(runner, logger);
        var dir = logDirectory ?? LogPaths.Resolve();
        var env = new EnvironmentProbe(locator, auth, caps, logger, dir);
        return new TuiServices
        {
            Logger = logger,
            Runner = runner,
            GhLocator = locator,
            AuthService = auth,
            CapabilityProbe = caps,
            EnvironmentProbe = env,
            SearchService = new GhSkillSearchService(runner, logger),
            PreviewService = new GhSkillPreviewService(runner, logger),
            FileLogSink = fileLogSink,
            LogDirectory = dir,
        };
    }
}
