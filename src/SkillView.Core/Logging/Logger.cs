using System.Globalization;

namespace SkillView.Logging;

/// In-memory ring buffer logger with observer callbacks for UI panes and a
/// pluggable sink for disk writes. File rotation lives elsewhere (Phase 1).
public sealed class Logger
{
    private readonly object _gate = new();
    private readonly object _observerGate = new();
    private readonly LinkedList<LogEntry> _ring = new();
    private readonly int _capacity;
    private readonly Dictionary<long, ObserverRegistration> _observers = new();
    private long _nextObserverId;

    public Logger(LogLevel minimumLevel = LogLevel.Info, int capacity = 2048)
    {
        MinimumLevel = minimumLevel;
        _capacity = capacity;
    }

    public LogLevel MinimumLevel { get; set; }

    public IDisposable Subscribe(Action<LogEntry> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_observerGate)
        {
            var id = ++_nextObserverId;
            var registration = new ObserverRegistration(observer);
            _observers.Add(id, registration);
            return new Subscription(this, id, registration);
        }
    }

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

        ObserverRegistration[] observers;
        lock (_observerGate)
        {
            observers = _observers.Values.ToArray();
        }
        foreach (var observer in observers)
        {
            try { observer.Invoke(entry); }
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

    private void Unsubscribe(long id, ObserverRegistration registration)
    {
        lock (_observerGate)
        {
            _observers.Remove(id);
        }

        // Do not hold the collection lock while waiting for an in-flight
        // callback. This avoids lock inversion if an observer subscribes or
        // disposes a subscription from inside its callback.
        registration.Deactivate();
    }

    private sealed class ObserverRegistration
    {
        private readonly object _gate = new();
        private readonly Action<LogEntry> _observer;
        private bool _active = true;

        internal ObserverRegistration(Action<LogEntry> observer)
        {
            _observer = observer;
        }

        internal void Invoke(LogEntry entry)
        {
            lock (_gate)
            {
                if (_active)
                {
                    _observer(entry);
                }
            }
        }

        internal void Deactivate()
        {
            lock (_gate)
            {
                _active = false;
            }
        }
    }

    private sealed class Subscription : IDisposable
    {
        private Logger? _owner;
        private readonly long _id;
        private readonly ObserverRegistration _registration;

        internal Subscription(Logger owner, long id, ObserverRegistration registration)
        {
            _owner = owner;
            _id = id;
            _registration = registration;
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Unsubscribe(_id, _registration);
    }
}
