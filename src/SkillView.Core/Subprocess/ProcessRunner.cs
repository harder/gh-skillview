using System.Diagnostics;
using System.Text;
using SkillView.Logging;

namespace SkillView.Subprocess;

/// argv-array subprocess invoker — never shell composition.
public sealed class ProcessRunner
{
    private readonly Logger _logger;

    public ProcessRunner(Logger logger)
    {
        _logger = logger;
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
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

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
}
