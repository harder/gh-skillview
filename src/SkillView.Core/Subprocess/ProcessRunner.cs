using System.Diagnostics;
using System.Text;
using SkillView.Logging;
using SkillView.Threading;

namespace SkillView.Subprocess;

/// argv-array subprocess invoker — never shell composition.
public sealed class ProcessRunner
{
    public const int DefaultMaxCapturedCharsPerStream = 1024 * 1024;
    public static readonly TimeSpan DefaultTerminationWait = TimeSpan.FromSeconds(5);
    private readonly Logger _logger;
    private readonly int _maxCapturedCharsPerStream;
    private readonly TimeSpan _terminationWait;
    private readonly IReadOnlyDictionary<string, string>? _environment;
    private readonly string? _defaultWorkingDirectory;
    private bool ReplaceEnvironment { get; init; }
    // Tests can refuse termination at the actual child boundary. Production
    // always attempts Process.Kill; this does not replace process execution.
    internal Func<Process, bool>? TerminationAdmissionForTests { get; init; }
    internal string EffectiveWorkingDirectory => _defaultWorkingDirectory ?? Environment.CurrentDirectory;

    internal string? GetEffectiveEnvironmentVariable(string name, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var local = overrides?.FirstOrDefault(pair => string.Equals(pair.Key, name, comparison));
        if (local?.Key is not null) return local.Value.Value;
        var configured = _environment?.FirstOrDefault(pair => string.Equals(pair.Key, name, comparison));
        return configured?.Key is not null ? configured.Value.Value : ReplaceEnvironment ? null : Environment.GetEnvironmentVariable(name);
    }

    public ProcessRunner(
        Logger logger,
        int maxCapturedCharsPerStream = DefaultMaxCapturedCharsPerStream,
        TimeSpan? terminationWait = null,
        IReadOnlyDictionary<string, string>? environment = null, string? defaultWorkingDirectory = null)
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
        _environment = environment;
        _defaultWorkingDirectory = defaultWorkingDirectory is null ? null : Path.GetFullPath(defaultWorkingDirectory);
    }

    internal ProcessRunner WithContext(string workingDirectory, IReadOnlyDictionary<string, string> overrides)
    {
        var values = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (!ReplaceEnvironment)
            foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
                if (entry.Key is string key && entry.Value is string value) values[key] = value;
        if (_environment is not null) foreach (var pair in _environment) values[pair.Key] = pair.Value;
        foreach (var pair in overrides) values[pair.Key] = pair.Value;
        return new(_logger, _maxCapturedCharsPerStream, _terminationWait, values, workingDirectory) { ReplaceEnvironment = true, TerminationAdmissionForTests = TerminationAdmissionForTests };
    }

    /// An explicit allowlist context uses the same frozen-environment mechanism
    /// as WithContext, but deliberately inherits no ambient/configured values.
    internal ProcessRunner WithIsolatedContext(string workingDirectory, IReadOnlyDictionary<string, string> environment)
    {
        var values = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var pair in environment) values[pair.Key] = pair.Value;
        return new(_logger, _maxCapturedCharsPerStream, _terminationWait, values, workingDirectory) { ReplaceEnvironment = true, TerminationAdmissionForTests = TerminationAdmissionForTests };
    }

    internal ProcessRunner WithCaptureLimit(int remainingCharacters) =>
        new(_logger, Math.Min(_maxCapturedCharsPerStream, Math.Max(1, remainingCharacters)), _terminationWait, _environment, _defaultWorkingDirectory)
        { ReplaceEnvironment = ReplaceEnvironment, TerminationAdmissionForTests = TerminationAdmissionForTests };

    public async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default,
        bool retainCancellationOutput = false,
        IReadOnlyDictionary<string, string>? environment = null) =>
        await RunCoreAsync(executable, arguments, workingDirectory, cancellationToken,
            retainCancellationOutput, environment, retainExecutionFailure: false).ConfigureAwait(false);

    private async Task<ProcessResult> RunCoreAsync(string executable, IReadOnlyList<string> arguments,
        string? workingDirectory, CancellationToken cancellationToken, bool retainCancellationOutput,
        IReadOnlyDictionary<string, string>? environment, bool retainExecutionFailure, Action? beforeStart = null)
    {
        // Cancellation closes process admission. In particular, do not start
        // and then immediately kill a child for an already-canceled caller.
        cancellationToken.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? EffectiveWorkingDirectory,
        };
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }
        if (ReplaceEnvironment) psi.Environment.Clear();
        if (_environment is not null)
            foreach (var (name, value) in _environment) psi.Environment[name] = value;
        if (environment is not null)
            foreach (var (name, value) in environment) psi.Environment[name] = value;

        cancellationToken.ThrowIfCancellationRequested();
        _logger.Debug("subprocess", $"exec: {executable} {string.Join(' ', arguments)}");

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new BoundedTextBuffer(_maxCapturedCharsPerStream);
        var stderr = new BoundedTextBuffer(_maxCapturedCharsPerStream);

        var sw = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            beforeStart?.Invoke();
            if (!process.Start())
                return new(executable, arguments, -1, "", "Process.Start returned no child.", sw.Elapsed)
                { StartState = ProcessStartState.NotStarted, ChildDrained = true };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warn("subprocess", $"failed to start {executable}: {ex.Message}");
            return new ProcessResult(executable, arguments, -1, string.Empty, ex.Message, sw.Elapsed)
            { StartState = ProcessStartState.Unknown, ChildDrained = false };
        }

        using var readers = new CancellationSource(cancellationToken,
            CancellationCallbackReporter.For(_logger, "subprocess output readers", "subprocess"));
        Task stdoutDrain = Task.CompletedTask, stderrDrain = Task.CompletedTask;
        try
        {
            process.StandardInput.Close();
            stdoutDrain = DrainAsync(process.StandardOutput, stdout, readers.Token);
            stderrDrain = DrainAsync(process.StandardError, stderr, readers.Token);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutDrain, stderrDrain).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is OperationCanceledException || retainExecutionFailure)
        {
            // A transport failure can reach here without caller cancellation.
            // Cancel both original readers before observing them; otherwise a
            // sibling pipe held open by a surviving child could wait forever.
            readers.TryCancel();
            try
            {
                if (TerminationAdmissionForTests?.Invoke(process) != false)
                    process.Kill(entireProcessTree: true);
                else
                    _logger.Warn("subprocess", $"termination refused by test admission: {executable}");
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                _logger.Warn("subprocess", $"failed to terminate {executable}: {ex.Message}");
            }

            // Process.Kill is asynchronous on every supported platform. Wait
            // for a bounded grace period so the parent and redirected pipes can
            // settle, but never let a wedged or unkillable child stall shutdown.
            using (var termination = new CancellationSource(
                _terminationWait,
                CancellationCallbackReporter.For(
                    _logger,
                    "termination deadline",
                    "subprocess")))
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
                catch (System.ComponentModel.Win32Exception ex) when (retainExecutionFailure)
                {
                    // Retain the positively observed start even if the OS
                    // cannot confirm exit. Ordinary RunAsync still propagates.
                    _logger.Warn("subprocess", $"child exit wait failed for {executable}: {ex.Message}");
                }
            }

            // Observe both canceled readers before the independent bounded
            // retention drain. No parent-exit or pipe-EOF wait is unbounded,
            // including when termination fails or is unconfirmed.
            try { await Task.WhenAll(stdoutDrain, stderrDrain).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (IOException) { }
            if (retainCancellationOutput)
            {
                // Once the canceled readers quiesce, retain bytes already in
                // the pipes/reader buffers under an independent bounded drain.
                using var retention = new CancellationSource(_terminationWait,
                    CancellationCallbackReporter.For(_logger, "canceled output retention", "subprocess"));
                try
                {
                    await Task.WhenAll(DrainAsync(process.StandardOutput, stdout, retention.Token),
                        DrainAsync(process.StandardError, stderr, retention.Token)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
                var childDrained = false;
                try { childDrained = process.HasExited; }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                { _logger.Warn("subprocess", $"child exit could not be verified for {executable}: {ex.Message}"); }
                return new ProcessResult(executable, arguments, failure is OperationCanceledException ? 130 : -1,
                    stdout.ToString(), stderr + (failure is OperationCanceledException ? string.Empty : "\n" + failure.Message), sw.Elapsed)
                { Started = true, StartState = ProcessStartState.Started, ChildDrained = childDrained, Cancelled = failure is OperationCanceledException, StdOutTruncated = stdout.Truncated, StdErrTruncated = stderr.Truncated };
            }
            throw;
        }

        sw.Stop();

        var result = new ProcessResult(
            executable,
            arguments,
            process.ExitCode,
            stdout.ToString(),
            stderr.ToString(),
            sw.Elapsed)
        { Started = true, StartState = ProcessStartState.Started, ChildDrained = true, StdOutTruncated = stdout.Truncated, StdErrTruncated = stderr.Truncated };

        _logger.Debug("subprocess",
            $"exit={result.ExitCode} dur={result.Duration.TotalMilliseconds:F0}ms {executable}");
        return result;
    }

    // Install-only transport boundary. Legacy RunAsync cancellation semantics
    // stay unchanged; this path retains output and reports whether the writer
    // exited within the same bounded grace. Unknown exit forbids reconciliation.
    internal async Task<ProcessResult> RunInstallAsync(string executable, IReadOnlyList<string> arguments,
        string? workingDirectory, CancellationToken token, IReadOnlyDictionary<string, string>? environment)
    {
        var startAdmissionReached = false;
        try
        {
            token.ThrowIfCancellationRequested();
            // This install-only absence check closes admission itself. A
            // Process.Start exception, even Win32Exception, cannot establish
            // whether a platform failed before or after creating a child.
            if (Path.IsPathFullyQualified(executable))
            {
                try { _ = File.GetAttributes(executable); }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    return new(executable, arguments, -1, "", ex.Message, TimeSpan.Zero)
                    { StartState = ProcessStartState.NotStarted, ChildDrained = true };
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return await RunCoreAsync(executable, arguments, workingDirectory, token,
                retainCancellationOutput: true, environment: environment, retainExecutionFailure: true,
                beforeStart: () => startAdmissionReached = true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // The flag can only weaken an uncertain outcome to Unknown. Only
            // Process.Start returning true ever confirms a started child.
            return new(executable, arguments, 130, "", "Install canceled.", TimeSpan.Zero)
            {
                StartState = startAdmissionReached ? ProcessStartState.Unknown : ProcessStartState.NotStarted,
                ChildDrained = !startAdmissionReached,
                Cancelled = true,
            };
        }
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
        internal bool Truncated { get { lock (_gate) return _truncated; } }

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
