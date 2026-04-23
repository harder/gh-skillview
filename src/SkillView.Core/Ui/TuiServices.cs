using SkillView.Diagnostics;
using SkillView.Gh;
using SkillView.Inventory;
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
    public required GhSkillInstallService InstallService { get; init; }
    public required GhSkillListAdapter ListAdapter { get; init; }
    public required ScanRootResolver ScanRootResolver { get; init; }
    public required LocalSkillScanner Scanner { get; init; }
    public required LocalInventoryService InventoryService { get; init; }
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
        var list = new GhSkillListAdapter(runner, logger);
        var resolver = new ScanRootResolver();
        var scanner = new LocalSkillScanner(logger);
        var inventory = new LocalInventoryService(resolver, scanner, list, logger);
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
            InstallService = new GhSkillInstallService(runner, logger),
            ListAdapter = list,
            ScanRootResolver = resolver,
            Scanner = scanner,
            InventoryService = inventory,
            FileLogSink = fileLogSink,
            LogDirectory = dir,
        };
    }
}
