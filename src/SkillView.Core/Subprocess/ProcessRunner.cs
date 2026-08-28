using System.Diagnostics;
using System.Text;
using SkillView.Logging;

namespace SkillView.Subprocess;

/// argv-array subprocess invoker — never shell composition.
public sealed class ProcessRunner
{
    public const int DefaultMaxCapturedCharsPerStream = 1024 * 1024;
    public static readonly TimeSpan DefaultTerminationWait = TimeSpan.FromSeconds(5);
    private readonly Logger _logger;
    private readonly int _maxCapturedCharsPerStream;
    private readonly TimeSpan _terminationWait;

    public ProcessRunner(
        Logger logger,
        int maxCapturedCharsPerStream = DefaultMaxCapturedCharsPerStream,
        TimeSpan? terminationWait = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCapturedCharsPerStream, 1);
        var resolvedTerminationWait = terminationWait ?? DefaultTerminationWait;
        if (resolvedTerminationWait <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(terminationWait));
        }
        _logger = logger;
        _maxCapturedCharsPerStream = maxCapturedCharsPerStream;
        _terminationWait = resolvedTerminationWait;
    }

    public async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        _logger.Debug("subprocess", $"exec: {executable} {string.Join(' ', arguments)}");

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new BoundedTextBuffer(_maxCapturedCharsPerStream);
        var stderr = new BoundedTextBuffer(_maxCapturedCharsPerStream);

        var sw = Stopwatch.StartNew();
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.Warn("subprocess", $"failed to start {executable}: {ex.Message}");
            return new ProcessResult(executable, arguments, -1, string.Empty, ex.Message, sw.Elapsed);
        }

        process.StandardInput.Close();
        var stdoutDrain = DrainAsync(process.StandardOutput, stdout, cancellationToken);
        var stderrDrain = DrainAsync(process.StandardError, stderr, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutDrain, stderrDrain).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                _logger.Warn("subprocess", $"failed to terminate {executable}: {ex.Message}");
            }

            // Process.Kill is asynchronous on every supported platform. Wait
            // for a bounded grace period so the parent and redirected pipes can
            // settle, but never let a wedged or unkillable child stall shutdown.
            using (var termination = new CancellationTokenSource(_terminationWait))
            {
                try
                {
                    await process.WaitForExitAsync(termination.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (termination.IsCancellationRequested)
                {
                    _logger.Warn(
                        "subprocess",
                        $"process did not exit within {_terminationWait.TotalSeconds:F1}s after termination: {executable}");
                }
                catch (InvalidOperationException)
                {
                    // Process exited between Kill and the wait registration.
                }
            }

            // Observe both readers. They use the same cancellation token, so
            // cancellation cannot leave background reads attached to a process
            // that this method is about to dispose.
            try { await Task.WhenAll(stdoutDrain, stderrDrain).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (IOException) { }
            throw;
        }

        sw.Stop();

        var result = new ProcessResult(
            executable,
            arguments,
            process.ExitCode,
            stdout.ToString(),
            stderr.ToString(),
            sw.Elapsed);

        _logger.Debug("subprocess",
            $"exit={result.ExitCode} dur={result.Duration.TotalMilliseconds:F0}ms {executable}");
        return result;
    }

    private static async Task DrainAsync(
        StreamReader reader,
        BoundedTextBuffer destination,
        CancellationToken cancellationToken)
    {
        // StreamReader's line-oriented APIs retain a whole unterminated line.
        // Fixed-size reads keep memory bounded even for a child that never
        // emits a newline, while continuing to drain bytes after capture fills.
        var chunk = new char[4096];
        while (true)
        {
            var read = await reader
                .ReadAsync(chunk.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            destination.Append(chunk.AsSpan(0, read));
        }
    }

    /// Retains only the leading portion of a stream. Process output is
    /// untrusted and can be arbitrarily large, so callers get a clear marker
    /// instead of allowing a noisy child process to grow the app indefinitely.
    private sealed class BoundedTextBuffer
    {
        private readonly object _gate = new();
        private readonly int _limit;
        private readonly StringBuilder _buffer;
        private bool _truncated;

        internal BoundedTextBuffer(int limit)
        {
            _limit = limit;
            _buffer = new StringBuilder(Math.Min(limit, 16 * 1024));
        }

        internal void Append(ReadOnlySpan<char> text)
        {
            lock (_gate)
            {
                if (_truncated) return;
                var remaining = _limit - _buffer.Length;
                if (remaining <= 0)
                {
                    _truncated = true;
                    return;
                }

                var take = Math.Min(text.Length, remaining);
                _buffer.Append(text[..take]);
                if (take < text.Length)
                {
                    _truncated = true;
                }
            }
        }

        public override string ToString()
        {
            lock (_gate)
            {
                if (!_truncated) return _buffer.ToString();
                return $"{_buffer}\n… output truncated after {_limit} characters …\n";
            }
        }
    }
}
