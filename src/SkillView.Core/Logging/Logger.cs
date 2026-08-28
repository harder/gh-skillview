using System.Globalization;

namespace SkillView.Logging;

/// In-memory ring buffer logger with observer callbacks for UI panes and a
/// pluggable sink for disk writes. File rotation lives elsewhere (Phase 1).
public sealed class Logger
{
    public const int DefaultMaxMessageChars = 16 * 1024;
    public const int DefaultMaxRetainedChars = 2 * 1024 * 1024;
    public const int DefaultErrorSnippetChars = 512;
    private const int MaxCategoryChars = 128;
    private readonly object _gate = new();
    private readonly LinkedList<SequencedEntry> _ring = new();
    private readonly int _capacity;
    private readonly Dictionary<long, ObserverRegistration> _observers = new();
    private long _nextObserverId;
    private long _nextSequence;
    private int _retainedChars;
    private readonly int _maxMessageChars;
    private readonly int _maxRetainedChars;
    private readonly Action<long>? _beforeObserverInvokeForTests;
    private readonly Action<long>? _observerBackpressureForTests;

    [ThreadStatic]
    private static int s_observerCallbackDepth;

    public Logger(
        LogLevel minimumLevel = LogLevel.Info,
        int capacity = 2048,
        int maxMessageChars = DefaultMaxMessageChars,
        int maxRetainedChars = DefaultMaxRetainedChars)
        : this(
            minimumLevel,
            capacity,
            maxMessageChars,
            maxRetainedChars,
            beforeObserverInvokeForTests: null,
            observerBackpressureForTests: null)
    {
    }

    internal Logger(
        LogLevel minimumLevel,
        int capacity,
        int maxMessageChars,
        int maxRetainedChars,
        Action<long>? beforeObserverInvokeForTests,
        Action<long>? observerBackpressureForTests)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        ArgumentOutOfRangeException.ThrowIfNegative(maxMessageChars);
        ArgumentOutOfRangeException.ThrowIfNegative(maxRetainedChars);
        MinimumLevel = minimumLevel;
        _capacity = capacity;
        _maxMessageChars = maxMessageChars;
        _maxRetainedChars = maxRetainedChars;
        _beforeObserverInvokeForTests = beforeObserverInvokeForTests;
        _observerBackpressureForTests = observerBackpressureForTests;
    }

    public LogLevel MinimumLevel { get; set; }

    /// <summary>
    /// Subscribes to future entries. Callbacks are synchronously ordered and
    /// must not write to any logger while the callback is active.
    /// </summary>
    public IDisposable Subscribe(Action<LogEntry> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
        {
            var id = ++_nextObserverId;
            var registration = new ObserverRegistration(
                observer,
                nextSequence: _nextSequence + 1,
                replaying: false,
                _beforeObserverInvokeForTests,
                _observerBackpressureForTests);
            _observers.Add(id, registration);
            return new Subscription(this, id, registration);
        }
    }

    /// <summary>
    /// Atomically subscribes at the ring-buffer boundary and replays retained
    /// entries before live delivery. Concurrent producers are held behind
    /// synchronous sequence backpressure until replay completes, preventing
    /// gaps, duplicates, out-of-order callbacks, and an unbounded handoff
    /// buffer. Callbacks must not write to any logger while active.
    /// </summary>
    public IDisposable SubscribeWithReplay(Action<LogEntry> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        long id;
        ObserverRegistration registration;
        SequencedEntry[] replay;
        lock (_gate)
        {
            id = ++_nextObserverId;
            replay = _ring.ToArray();
            var nextSequence = replay.Length > 0
                ? replay[0].Sequence
                : _nextSequence + 1;
            registration = new ObserverRegistration(
                observer,
                nextSequence,
                replaying: true,
                _beforeObserverInvokeForTests,
                _observerBackpressureForTests);
            _observers.Add(id, registration);
        }

        registration.Replay(replay);
        return new Subscription(this, id, registration);
    }

    public void Log(LogLevel level, string category, string message)
    {
        if (level < MinimumLevel)
        {
            return;
        }

        // Ordered observer delivery applies synchronous backpressure while
        // holding a registration lock. Any logger write from a callback can
        // create a cross-logger, cross-thread lock cycle, so reject all such
        // writes before assigning a sequence or mutating a retained ring.
        if (s_observerCallbackDepth > 0)
        {
            throw new InvalidOperationException(
                "A logger observer cannot write to a Logger while its callback is active.");
        }

        var entry = new LogEntry(
            DateTimeOffset.UtcNow,
            level,
            Truncate(category ?? string.Empty, MaxCategoryChars),
            Truncate(Redactor.Redact(message ?? string.Empty), _maxMessageChars));

        SequencedEntry sequenced;
        ObserverRegistration[] observers;
        lock (_gate)
        {
            sequenced = new SequencedEntry(++_nextSequence, entry);
            _ring.AddLast(sequenced);
            _retainedChars += entry.Message.Length;
            while (_ring.Count > _capacity || _retainedChars > _maxRetainedChars)
            {
                _retainedChars -= _ring.First!.Value.Entry.Message.Length;
                _ring.RemoveFirst();
            }
            observers = _observers.Values.ToArray();
        }
        foreach (var observer in observers)
        {
            try { observer.Invoke(sequenced); }
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
            return _ring.Select(item => item.Entry).ToArray();
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

    /// <summary>Returns a compact single-line excerpt suitable for log metadata.</summary>
    public static string ErrorSnippet(string? text, int maxChars = DefaultErrorSnippetChars)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxChars);
        if (string.IsNullOrWhiteSpace(text) || maxChars == 0) return string.Empty;

        var builder = new System.Text.StringBuilder(Math.Min(maxChars, text.Length));
        var previousWasWhitespace = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                if (builder.Length == 0 || previousWasWhitespace) continue;
                previousWasWhitespace = true;
                if (builder.Length < maxChars) builder.Append(' ');
            }
            else
            {
                previousWasWhitespace = false;
                if (builder.Length < maxChars) builder.Append(character);
            }

            if (builder.Length == maxChars) break;
        }
        return builder.ToString().TrimEnd();
    }

    private static string Truncate(string value, int maxChars)
    {
        if (value.Length <= maxChars) return value;
        if (maxChars == 0) return string.Empty;

        const string marker = "… truncated";
        if (maxChars <= marker.Length)
        {
            return marker[..maxChars];
        }
        return string.Concat(value.AsSpan(0, maxChars - marker.Length), marker);
    }

    private void Unsubscribe(long id, ObserverRegistration registration)
    {
        lock (_gate)
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
        private readonly Action<long>? _beforeInvokeForTests;
        private readonly Action<long>? _backpressureForTests;
        private long _nextSequence;
        private bool _replaying;
        private bool _active = true;

        internal ObserverRegistration(
            Action<LogEntry> observer,
            long nextSequence,
            bool replaying,
            Action<long>? beforeInvokeForTests,
            Action<long>? backpressureForTests)
        {
            _observer = observer;
            _nextSequence = nextSequence;
            _replaying = replaying;
            _beforeInvokeForTests = beforeInvokeForTests;
            _backpressureForTests = backpressureForTests;
        }

        internal void Replay(IReadOnlyList<SequencedEntry> entries)
        {
            lock (_gate)
            {
                if (!_active) return;
                foreach (var entry in entries)
                {
                    if (entry.Sequence < _nextSequence) continue;
                    if (entry.Sequence > _nextSequence) break;

                    Deliver(entry.Entry);
                    _nextSequence++;
                }
                _replaying = false;
                Monitor.PulseAll(_gate);
            }
        }

        internal void Invoke(SequencedEntry entry)
        {
            _beforeInvokeForTests?.Invoke(entry.Sequence);
            lock (_gate)
            {
                // Do not retain a later entry while a lower sequence is still
                // between ring insertion and this registration. The producer
                // waits here, bounding logger-owned observer state to O(1).
                while (_active
                    && (_replaying || entry.Sequence > _nextSequence))
                {
                    _backpressureForTests?.Invoke(entry.Sequence);
                    Monitor.Wait(_gate);
                }

                if (!_active || entry.Sequence < _nextSequence) return;
                Deliver(entry.Entry);
                _nextSequence++;
                Monitor.PulseAll(_gate);
            }
        }

        internal void Deactivate()
        {
            lock (_gate)
            {
                _active = false;
                Monitor.PulseAll(_gate);
            }
        }

        private void Deliver(LogEntry entry)
        {
            s_observerCallbackDepth++;
            try { _observer(entry); }
            catch { /* observer faults must not kill logging or later delivery */ }
            finally { s_observerCallbackDepth--; }
        }
    }

    private readonly record struct SequencedEntry(long Sequence, LogEntry Entry);

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
