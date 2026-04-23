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
            using var sink = new FileLogSink(dir);
            sink.Append(new LogEntry(
                new DateTimeOffset(2026, 4, 23, 12, 0, 0, TimeSpan.Zero),
                LogLevel.Info, "test", "hello"));
            sink.Dispose();

            var file = Path.Combine(dir, "skillview-2026-04-23.log");
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
}
