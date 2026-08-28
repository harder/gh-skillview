namespace SkillView.Bootstrapping;

/// <summary>
/// Converts Ctrl+C into cooperative cancellation for both CLI and TUI hosts
/// and reliably removes the process-wide event handler during teardown.
/// </summary>
internal sealed class RootCancellation : IDisposable
{
    private readonly CancellationTokenSource _source;
    private readonly ConsoleCancelEventHandler _handler;
    private bool _subscribed;
    private bool _disposed;

    internal RootCancellation(CancellationToken cancellationToken = default)
    {
        _source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _handler = OnCancelKeyPress;
        try
        {
            Console.CancelKeyPress += _handler;
            _subscribed = true;
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            // Hosts without a console can still use the supplied token.
        }
    }

    internal CancellationToken Token => _source.Token;

    internal void RequestCancellation()
    {
        try { _source.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_subscribed)
        {
            Console.CancelKeyPress -= _handler;
            _subscribed = false;
        }
        _source.Dispose();
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs args)
    {
        // Keep the process alive long enough for gh children, log sinks, and
        // Terminal.Gui to release their resources cooperatively.
        args.Cancel = true;
        RequestCancellation();
    }
}
