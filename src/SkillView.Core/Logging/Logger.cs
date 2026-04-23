using System.Collections.Concurrent;
using System.Globalization;

namespace SkillView.Logging;

/// In-memory ring buffer logger with observer callbacks for UI panes and a
/// pluggable sink for disk writes. File rotation lives elsewhere (Phase 1).
public sealed class Logger
{
    private readonly object _gate = new();
    private readonly LinkedList<LogEntry> _ring = new();
    private readonly int _capacity;
    private readonly ConcurrentBag<Action<LogEntry>> _observers = new();

    public Logger(LogLevel minimumLevel = LogLevel.Info, int capacity = 2048)
    {
        MinimumLevel = minimumLevel;
        _capacity = capacity;
    }

    public LogLevel MinimumLevel { get; set; }

    public void Subscribe(Action<LogEntry> observer) => _observers.Add(observer);

    public void Log(LogLevel level, string category, string message)
    {
        if (level < MinimumLevel)
        {
            return;
        }

        var entry = new LogEntry(
            DateTimeOffset.UtcNow,
            level,
            category,
            Redactor.Redact(message));

        lock (_gate)
        {
            _ring.AddLast(entry);
            while (_ring.Count > _capacity)
            {
                _ring.RemoveFirst();
            }
        }

        foreach (var observer in _observers)
        {
            try { observer(entry); }
            catch { /* observer faults must not kill the logger */ }
        }
    }

    public void Debug(string category, string message) => Log(LogLevel.Debug, category, message);
    public void Info(string category, string message) => Log(LogLevel.Info, category, message);
    public void Warn(string category, string message) => Log(LogLevel.Warning, category, message);
    public void Error(string category, string message) => Log(LogLevel.Error, category, message);

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_gate)
        {
            return _ring.ToArray();
        }
    }

    public static string Format(LogEntry entry)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:HH:mm:ss.fff} {1,-5} [{2}] {3}",
            entry.Timestamp.ToLocalTime(),
            entry.Level,
            entry.Category,
            entry.Message);
    }
}
