using System.Diagnostics;
using System.Text;
using SkillView.Logging;

namespace SkillView.Subprocess;

/// argv-array subprocess invoker — never shell composition.
public sealed class ProcessRunner
{
    public const int DefaultMaxCapturedCharsPerStream = 1024 * 1024;
    private readonly Logger _logger;
    private readonly int _maxCapturedCharsPerStream;

    public ProcessRunner(Logger logger, int maxCapturedCharsPerStream = DefaultMaxCapturedCharsPerStream)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCapturedCharsPerStream, 1);
        _logger = logger;
        _maxCapturedCharsPerStream = maxCapturedCharsPerStream;
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

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

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

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Close();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* best-effort */ }
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

        internal void AppendLine(string line)
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

                var take = Math.Min(line.Length, remaining);
                _buffer.Append(line.AsSpan(0, take));
                remaining -= take;
                if (remaining > 0)
                {
                    _buffer.AppendLine();
                }

                if (take < line.Length || remaining <= 0)
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
