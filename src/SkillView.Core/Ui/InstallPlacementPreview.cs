using SkillView.Gh;
using SkillView.Logging;
using SkillView.Subprocess;
using SkillView.Threading;

namespace SkillView.Ui;

// One computing worker plus one replaceable pending request. UI delivery checks
// its revision again; a queued completion never owns controls after disposal.
internal sealed class InstallPlacementPreview : IDisposable
{
    private readonly object _gate = new();
    private readonly BackgroundTaskTracker _tasks;
    private readonly LatestRequestGate _requests;
    private readonly CancellationSource _lifetime;
    private readonly ProcessRunner _context;
    private readonly Logger _logger;
    private readonly Action<Action> _invoke;
    private readonly Action<InstallPlacementObservation> _publish;
    private readonly Func<bool> _contextCurrent;
    private Func<CancellationToken, Task<InstallPlacementObservation>>? _pending;
    private long _revision;
    private bool _running;
    private bool _active = true;
    internal InstallPlacementObservation? Current { get; private set; }

    internal InstallPlacementPreview(ProcessRunner context, Logger logger, Action<Action> invoke, Action<InstallPlacementObservation> publish,
        Func<bool> contextCurrent)
    {
        _context = context; _logger = logger; _invoke = invoke; _publish = publish; _contextCurrent = contextCurrent;
        var reporter = CancellationCallbackReporter.For(logger, "install placement preview");
        _lifetime = new(reporter);
        _requests = new(reporter);
        _tasks = new(ex => logger.Error("install.placement", ex.ToString()));
    }

    internal void Update(string ghPath, string repo, string? skill, GhSkillInstallService.Options options)
        => Update(token => InstallPlacementReview.CaptureAsync(_context, _logger, ghPath, repo, skill, options, token));

    internal void Update(Func<CancellationToken, Task<InstallPlacementObservation>> capture)
    {
        bool start;
        lock (_gate)
        {
            if (!_active) return;
            _revision++;
            Current = null;
            _pending = capture;
            start = !_running;
            _running = true;
        }
        _requests.Cancel();
        _publish(new("Placement observation pending; destinations/conflicts are unknown.", [], ""));
        if (start) _tasks.TryRun(RunAsync, runOnThreadPool: true);
    }

    private async Task RunAsync()
    {
        while (true)
        {
            Func<CancellationToken, Task<InstallPlacementObservation>> capture;
            long revision;
            lock (_gate)
            {
                if (!_active || _pending is null) { _running = false; return; }
                capture = _pending; _pending = null; revision = _revision;
            }
            using var request = _requests.Begin(_lifetime.Token, TimeSpan.FromSeconds(15));
            lock (_gate) { if (!_active || revision != _revision) continue; }
            InstallPlacementObservation observation;
            try
            {
                request.Token.ThrowIfCancellationRequested();
                if (!_contextCurrent()) throw new IOException("Installation context changed; reopen the install dialog.");
                observation = await capture(request.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { observation = new("Placement observation canceled or timed out; exact destinations/conflicts are unknown. Installation still rechecks admission.", [], ""); }
            catch (Exception ex) { observation = new("Placement unknown: " + InstallFailureDetails.Literal(ex.Message), [], ""); }
            _invoke(() =>
            {
                lock (_gate) { if (!_active || revision != _revision) return; }
                Current = _contextCurrent() ? observation : null;
                _publish(Current ?? new("Installation context changed; reopen the install dialog. Previous placement is stale.", [], ""));
            });
        }
    }

    internal void Cancel()
    {
        lock (_gate) { _revision++; _pending = null; }
        _requests.Cancel();
    }

    public void Dispose()
    {
        lock (_gate) { _active = false; _pending = null; _revision++; }
        _tasks.StopAccepting();
        _lifetime.TryCancel();
        _requests.Cancel();
        _tasks.DrainAsync().GetAwaiter().GetResult();
        _requests.Dispose(); _lifetime.Dispose();
    }
}
