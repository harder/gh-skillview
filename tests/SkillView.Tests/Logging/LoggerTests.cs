using SkillView.Logging;
using Xunit;

namespace SkillView.Tests.Logging;

public class LoggerTests
{
    [Fact]
    public void FiltersBelowMinimumLevel()
    {
        var logger = new Logger(LogLevel.Info);
        logger.Debug("t", "invisible");
        logger.Info("t", "visible");
        var entries = logger.Snapshot();
        Assert.Single(entries);
        Assert.Equal("visible", entries[0].Message);
    }

    [Fact]
    public void AppliesRedactionBeforePersistence()
    {
        var logger = new Logger(LogLevel.Info);
        logger.Info("t", "token ghp_AAAAAAAAAAAAAAAAAAAA1234567890 oops");
        var entry = Assert.Single(logger.Snapshot());
        Assert.DoesNotContain("ghp_", entry.Message);
    }

    [Fact]
    public void RingBufferHonoursCapacity()
    {
        var logger = new Logger(LogLevel.Info, capacity: 3);
        for (var i = 0; i < 10; i++)
        {
            logger.Info("t", $"msg-{i}");
        }
        var entries = logger.Snapshot();
        Assert.Equal(3, entries.Count);
        Assert.Equal("msg-7", entries[0].Message);
        Assert.Equal("msg-9", entries[^1].Message);
    }

    [Fact]
    public void SubscriberReceivesEntries()
    {
        var logger = new Logger();
        var received = new List<LogEntry>();
        logger.Subscribe(received.Add);
        logger.Info("cat", "hello");
        Assert.Single(received);
        Assert.Equal("hello", received[0].Message);
    }
}
