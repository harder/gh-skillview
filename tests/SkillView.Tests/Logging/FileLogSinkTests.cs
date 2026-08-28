using SkillView.Logging;
using Xunit;

namespace SkillView.Tests.Logging;

public class FileLogSinkTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "skillview-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Append_writes_to_dated_log_file()
    {
        var dir = NewTempDir();
        try
        {
            var recent = new DateTimeOffset(2026, 4, 23, 12, 0, 0, TimeSpan.Zero);
            using var sink = new FileLogSink(dir, () => recent);
            sink.Append(new LogEntry(
                recent,
                LogLevel.Info, "test", "hello"));
            sink.Dispose();

            var file = Path.Combine(dir, LogPaths.FileNameForDate(DateOnly.FromDateTime(recent.LocalDateTime)));
            Assert.True(File.Exists(file));
            Assert.Contains("hello", File.ReadAllText(file));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Append_survives_redacted_payloads_without_re_writing_secrets()
    {
        var dir = NewTempDir();
        try
        {
            // The sink trusts the Logger to redact upstream; we verify the
            // `Logger → FileLogSink` pipeline end-to-end here.
            var logger = new Logger(LogLevel.Info);
            using var sink = new FileLogSink(dir);
            sink.Attach(logger);
            logger.Info("auth", "token: ghp_0123456789abcdef0123456789abcdef");
            sink.Dispose();

            var file = Directory.EnumerateFiles(dir, "skillview-*.log").Single();
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("ghp_0123456789abcdef", text);
            Assert.Contains("[REDACTED]", text);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Trim_removes_files_older_than_retention()
    {
        var dir = NewTempDir();
        try
        {
            // Seed a very old file and a recent file directly on disk.
            var oldFile = Path.Combine(dir, "skillview-2020-01-01.log");
            File.WriteAllText(oldFile, "ancient\n");
            // Using a fixed clock that matches the "recent" log name so trim keeps it.
            var recent = new DateTimeOffset(2026, 4, 23, 12, 0, 0, TimeSpan.Zero);
            var recentFile = Path.Combine(dir, LogPaths.FileNameForDate(
                DateOnly.FromDateTime(recent.LocalDateTime)));
            File.WriteAllText(recentFile, "fresh\n");

            using var sink = new FileLogSink(dir, () => recent);
            // Triggering an append forces the rotate + trim pass.
            sink.Append(new LogEntry(recent, LogLevel.Info, "t", "kick"));
            sink.Dispose();

            Assert.False(File.Exists(oldFile));
            Assert.True(File.Exists(recentFile));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ClearAll_removes_every_log_file()
    {
        var dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "skillview-2026-04-20.log"), "a");
            File.WriteAllText(Path.Combine(dir, "skillview-2026-04-21.log"), "b");
            File.WriteAllText(Path.Combine(dir, "unrelated.txt"), "keep");

            using var sink = new FileLogSink(dir);
            var count = sink.ClearAll();

            Assert.Equal(2, count);
            Assert.False(File.Exists(Path.Combine(dir, "skillview-2026-04-20.log")));
            Assert.True(File.Exists(Path.Combine(dir, "unrelated.txt")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Dispose_does_not_deadlock_with_callback_waiting_for_sink_lock()
    {
        var dir = NewTempDir();
        var appendReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAppend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var logger = new Logger(LogLevel.Info);
            var sink = new FileLogSink(
                dir,
                clock: null,
                beforeAppendLockForTests: () =>
                {
                    appendReached.TrySetResult();
                    releaseAppend.Task.GetAwaiter().GetResult();
                });
            sink.Attach(logger);

            var logTask = Task.Run(() => logger.Info("test", "concurrent append"), TestContext.Current.CancellationToken);
            await appendReached.Task.WaitAsync(TestContext.Current.CancellationToken);

            var disposeTask = Task.Run(sink.Dispose, TestContext.Current.CancellationToken);
            Assert.True(SpinWait.SpinUntil(
                () => sink.IsDisposedForTests,
                TimeSpan.FromSeconds(2)));

            releaseAppend.TrySetResult();
            await Task.WhenAll(logTask, disposeTask)
                .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

            Assert.True(sink.IsDisposedForTests);
        }
        finally
        {
            releaseAppend.TrySetResult();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Attach_ReplayAndConcurrentEntry_ArePersistedExactlyOnce()
    {
        var dir = NewTempDir();
        var firstAppendReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstAppend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var appendCount = 0;
        try
        {
            var logger = new Logger(LogLevel.Info);
            logger.Info("test", "before-attach");
            using var sink = new FileLogSink(
                dir,
                clock: null,
                beforeAppendLockForTests: () =>
                {
                    if (Interlocked.Increment(ref appendCount) == 1)
                    {
                        firstAppendReached.SetResult();
                        releaseFirstAppend.Task.GetAwaiter().GetResult();
                    }
                });

            var attach = Task.Run(() => sink.Attach(logger), TestContext.Current.CancellationToken);
            await firstAppendReached.Task.WaitAsync(TestContext.Current.CancellationToken);
            var concurrentLog = Task.Run(
                () => logger.Info("test", "during-attach"),
                TestContext.Current.CancellationToken);

            releaseFirstAppend.SetResult();
            await Task.WhenAll(attach, concurrentLog);
            logger.Info("test", "after-attach");
            sink.Dispose();

            var file = Directory.EnumerateFiles(dir, "skillview-*.log").Single();
            var lines = File.ReadAllLines(file);
            Assert.Equal(1, lines.Count(line => line.Contains("before-attach", StringComparison.Ordinal)));
            Assert.Equal(1, lines.Count(line => line.Contains("during-attach", StringComparison.Ordinal)));
            Assert.Equal(1, lines.Count(line => line.Contains("after-attach", StringComparison.Ordinal)));
        }
        finally
        {
            releaseFirstAppend.TrySetResult();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Attach_IsOneShot()
    {
        var dir = NewTempDir();
        try
        {
            var logger = new Logger();
            using var sink = new FileLogSink(dir);
            sink.Attach(logger);

            Assert.Throws<InvalidOperationException>(() => sink.Attach(logger));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SameDayGrowth_RotatesBySize()
    {
        var dir = NewTempDir();
        try
        {
            var now = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
            using var sink = new FileLogSink(
                dir,
                () => now,
                beforeAppendLockForTests: null,
                maxFileSizeBytes: 180,
                totalSizeBudgetBytes: 10_000);

            for (var index = 0; index < 12; index++)
            {
                sink.Append(new LogEntry(now, LogLevel.Info, "rotation", $"message-{index}-" + new string('x', 35)));
            }
            sink.Dispose();

            var files = Directory.EnumerateFiles(dir, "skillview-*.log").ToArray();
            Assert.True(files.Length > 1);
            Assert.All(files, file => Assert.True(new FileInfo(file).Length <= 180));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void RestartWithOversizedCurrentFile_StartsNewPartBeforeTrimming()
    {
        var dir = NewTempDir();
        try
        {
            var now = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
            var baseFile = Path.Combine(dir, LogPaths.FileNameForDate(DateOnly.FromDateTime(now.LocalDateTime)));
            File.WriteAllText(baseFile, new string('x', 500));
            using var sink = new FileLogSink(
                dir,
                () => now,
                beforeAppendLockForTests: null,
                maxFileSizeBytes: 100,
                totalSizeBudgetBytes: 1_000);

            sink.Append(new LogEntry(now, LogLevel.Info, "restart", "new-entry"));
            sink.Dispose();

            Assert.True(File.Exists(baseFile));
            Assert.True(File.Exists(Path.Combine(
                dir,
                LogPaths.FileNameForDate(DateOnly.FromDateTime(now.LocalDateTime), 1))));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Trim_NeverDeletesActiveFile_WhenItAloneExceedsBudget()
    {
        var dir = NewTempDir();
        try
        {
            var now = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
            using var sink = new FileLogSink(
                dir,
                () => now,
                beforeAppendLockForTests: null,
                maxFileSizeBytes: 1_000,
                totalSizeBudgetBytes: 50);

            sink.Append(new LogEntry(now, LogLevel.Info, "budget", new string('x', 100)));

            var active = Assert.Single(Directory.EnumerateFiles(dir, "skillview-*.log"));
            Assert.True(File.Exists(active));
            Assert.True(new FileInfo(active).Length > 50);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
