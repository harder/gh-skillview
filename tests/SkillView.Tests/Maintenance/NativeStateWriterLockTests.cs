using Microsoft.Win32.SafeHandles;
using SkillView.Inventory;
using Xunit;

namespace SkillView.Tests.Maintenance;

public sealed class NativeStateWriterLockTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnixFinalizerCleanupNeverThrowsForOpenOrAlreadyClosedHandle(bool closed)
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("skillview-state-finalizer-");
        try
        {
            using var state = NativeStateDirectory.OpenOrCreate(Path.Combine(root.FullName, "state"), [], TestContext.Current.CancellationToken);
            using var writer = state.OpenFile("writer.lock", true, TestContext.Current.CancellationToken);
            var fd = UnixSecureRemovalBackend.DupWithRetry(writer.SafeFileHandle.DangerousGetHandle().ToInt32());
            Assert.True(fd >= 0);
            using var duplicate = new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
            if (closed) writer.SafeFileHandle.Dispose();
            var method = writer.GetType().GetMethod("Dispose", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null, [typeof(bool)], null)!;
            Assert.Null(Record.Exception(() => method.Invoke(writer, [false])));
            if (closed) duplicate.Dispose(); // an externally closed owner cannot unlock its description
            using var next = state.OpenExistingFile("writer.lock", true, TestContext.Current.CancellationToken);
            Assert.Equal(0, next.Length);
        }
        finally { root.Delete(true); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UnixWriterDisposalUnlocksRetainedOpenFileDescription(bool existingOnly, bool asynchronous)
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Directory.CreateTempSubdirectory("skillview-state-writer-");
        try
        {
            using var state = NativeStateDirectory.OpenOrCreate(Path.Combine(root.FullName, "state"), [], TestContext.Current.CancellationToken);
            using (var create = state.OpenFile("writer.lock", true, TestContext.Current.CancellationToken)) Assert.Equal(0, create.Length);
            using var writer = existingOnly ? state.OpenExistingFile("writer.lock", true, TestContext.Current.CancellationToken)
                : state.OpenFile("writer.lock", true, TestContext.Current.CancellationToken);
            var fd = UnixSecureRemovalBackend.DupWithRetry(writer.SafeFileHandle.DangerousGetHandle().ToInt32());
            Assert.True(fd >= 0);
            using var duplicate = new SafeFileHandle(new IntPtr(fd), ownsHandle: true);
            Assert.ThrowsAny<IOException>(() => state.OpenExistingFile("writer.lock", true, TestContext.Current.CancellationToken));
            if (asynchronous) await writer.DisposeAsync(); else writer.Dispose();
            // Keep the duplicate alive. Closing only the owner's descriptor used
            // to leave its lock held, including during a child's fork/exec gap.
            using var next = state.OpenExistingFile("writer.lock", true, TestContext.Current.CancellationToken);
            // Repeated old-owner disposal must not unlock the new owner's lock.
            writer.Dispose(); await writer.DisposeAsync();
            Assert.ThrowsAny<IOException>(() => state.OpenFile("writer.lock", true, TestContext.Current.CancellationToken));
            Assert.Equal(0, next.Length);
        }
        finally { root.Delete(true); }
    }
}
