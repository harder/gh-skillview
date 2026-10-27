using System.Diagnostics;
using SkillView.Logging;
using SkillView.Subprocess;
using Xunit;

namespace SkillView.Tests.Subprocess;

// Test-only ownership of the exact child observed at ProcessRunner's termination
// boundary. Keep a separate opened Process handle across runner disposal. Never
// find a process by executable/path, and never terminate any descendants here.
internal sealed class RefusingTerminationChild : IDisposable
{
    internal static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(100);
    private Process? _child;
    private readonly TaskCompletionSource _observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Observed => _observed.Task;
    internal bool IsAlive => _child is { HasExited: false };

    internal ProcessRunner Runner(Logger logger) => new(logger, terminationWait: Grace)
    { TerminationAdmissionForTests = Refuse };

    private bool Refuse(Process actualChild)
    {
        if (_child is null)
        {
            _child = Process.GetProcessById(actualChild.Id);
            _ = _child.SafeHandle; // Open while the actual child is still owned/alive.
            Assert.Equal(actualChild.StartTime, _child.StartTime);
        }
        Assert.Equal(actualChild.Id, _child.Id);
        _observed.TrySetResult();
        return false;
    }

    public void Dispose()
    {
        if (_child is not { } child) return;
        _child = null;
        try
        {
            if (!child.HasExited) child.Kill(entireProcessTree: false);
            Assert.True(child.WaitForExit(5000), "The exact owned test child did not drain after cleanup kill.");
        }
        finally { child.Dispose(); }
    }
}
