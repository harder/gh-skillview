using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SkillView.Inventory;

namespace SkillView.Logging;

/// Appends already-redacted log entries to a daily-rotated file under the
/// SkillView cache log directory. Implements daily rotation, 14-day
/// retention, 50 MB bound, POSIX mode 0600.
///
/// Redaction is applied upstream by `Logger`; this sink trusts `LogEntry.Message`
/// to already be safe.
public sealed class FileLogSink : IDisposable
{
    public const int RetentionDays = 14;
    public const long TotalSizeBudgetBytes = 50L * 1024 * 1024;
    public const long MaxFileSizeBytes = 5L * 1024 * 1024;

    private readonly string _directory;
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly long _maxFileSizeBytes;
    private readonly long _totalSizeBudgetBytes;
    private StreamWriter? _writer;
    private DateOnly _currentDay;
    private string? _currentPath;
    private long _currentBytes;
    private int _currentPart;
    private bool _disposed;
    private bool _trimPending;
    private bool _retainedBytesKnown;
    private long _retainedBytes;
    private long _nextTrimAtRetainedBytes;
    private IDisposable? _subscription;
    private bool _attaching;
    private readonly Action? _beforeAppendLockForTests;

    public FileLogSink(string directory, Func<DateTimeOffset>? clock = null)
        : this(
            directory,
            clock,
            beforeAppendLockForTests: null,
            maxFileSizeBytes: MaxFileSizeBytes,
            totalSizeBudgetBytes: TotalSizeBudgetBytes)
    {
    }

    internal FileLogSink(
        string directory,
        Func<DateTimeOffset>? clock,
        Action? beforeAppendLockForTests,
        long maxFileSizeBytes = MaxFileSizeBytes,
        long totalSizeBudgetBytes = TotalSizeBudgetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFileSizeBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(totalSizeBudgetBytes, 1);
        _directory = directory;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _beforeAppendLockForTests = beforeAppendLockForTests;
        _maxFileSizeBytes = maxFileSizeBytes;
        _totalSizeBudgetBytes = totalSizeBudgetBytes;
    }

    public string Directory => _directory;

    public void Attach(Logger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_attaching || _subscription is not null)
            {
                throw new InvalidOperationException("FileLogSink can only be attached once.");
            }
            _attaching = true;
        }

        IDisposable? subscription = null;
        try
        {
            subscription = logger.SubscribeWithReplay(Append);
            lock (_gate)
            {
                if (_disposed)
                {
                    throw new ObjectDisposedException(nameof(FileLogSink));
                }
                _subscription = subscription;
                subscription = null;
                _attaching = false;
            }
        }
        finally
        {
            subscription?.Dispose();
            lock (_gate)
            {
                _attaching = false;
            }
        }
    }

    public void Append(LogEntry entry)
    {
        _beforeAppendLockForTests?.Invoke();
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                var today = DateOnly.FromDateTime(entry.Timestamp.ToLocalTime().DateTime);
                EnsureWriter(today);
                var line = Logger.Format(entry);
                var lineBytes = Encoding.UTF8.GetByteCount(line)
                    + Encoding.UTF8.GetByteCount(_writer!.NewLine);
                if (_currentBytes > 0 && _currentBytes + lineBytes > _maxFileSizeBytes)
                {
                    RotateForSizeLocked(today);
                }
                _writer!.WriteLine(line);
                _writer.Flush();
                _currentBytes += lineBytes;

                if (_trimPending)
                {
                    RefreshRetentionAccountingLocked();
                    _trimPending = false;
                }
                else if (_retainedBytesKnown)
                {
                    _retainedBytes = _retainedBytes > long.MaxValue - lineBytes
                        ? long.MaxValue
                        : _retainedBytes + lineBytes;
                    if (_retainedBytes >= _nextTrimAtRetainedBytes)
                    {
                        RefreshRetentionAccountingLocked();
                    }
                }
            }
            catch
            {
                // Disk-write failures must never crash the app. They already
                // surfaced to in-memory ring via Logger itself.
            }
        }
    }

    public IReadOnlyList<string> ListLogFiles()
    {
        if (!System.IO.Directory.Exists(_directory)) return Array.Empty<string>();
        return System.IO.Directory
            .EnumerateFiles(_directory, "skillview-*.log")
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
    }

    /// Delete every rotated log file. Used by `doctor --clear-logs`.
    public int ClearAll()
    {
        lock (_gate)
        {
            CloseWriterLocked();
            if (!System.IO.Directory.Exists(_directory)) return 0;
            var count = 0;
            foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "skillview-*.log"))
            {
                try
                {
                    File.Delete(file);
                    count++;
                }
                catch { /* best effort */ }
            }
            _currentDay = default;
            _currentPath = null;
            _currentBytes = 0;
            _currentPart = 0;
            _retainedBytesKnown = true;
            _retainedBytes = 0;
            _nextTrimAtRetainedBytes = ByteAfter(_totalSizeBudgetBytes);
            return count;
        }
    }

    private void EnsureWriter(DateOnly day)
    {
        if (_writer is not null && day == _currentDay)
        {
            return;
        }

        CloseWriterLocked();

        System.IO.Directory.CreateDirectory(_directory);
        var candidate = FindWritableFile(day);
        OpenWriterLocked(day, candidate.Part, candidate.Path);
    }

    private (string Path, int Part) FindWritableFile(DateOnly day)
    {
        var latest = System.IO.Directory
            .EnumerateFiles(_directory, $"skillview-{day:yyyy-MM-dd}*.log")
            .Select(path => new FileInfo(path))
            .Select(file => TryParseLogFileIdentity(file.Name, out var fileDay, out var part)
                && fileDay == day
                    ? (File: file, Part: part)
                    : (File: (FileInfo?)null, Part: -1))
            .Where(item => item.File is not null)
            .OrderByDescending(item => item.Part)
            .FirstOrDefault();

        if (latest.File is null)
        {
            return (Path.Combine(_directory, LogPaths.FileNameForDate(day)), 0);
        }
        if (latest.File.Length < _maxFileSizeBytes)
        {
            return (latest.File.FullName, latest.Part);
        }

        var nextPart = latest.Part + 1;
        return (Path.Combine(_directory, LogPaths.FileNameForDate(day, nextPart)), nextPart);
    }

    private void RotateForSizeLocked(DateOnly day)
    {
        var nextPart = _currentPart + 1;
        CloseWriterLocked();
        OpenWriterLocked(
            day,
            nextPart,
            Path.Combine(_directory, LogPaths.FileNameForDate(day, nextPart)));
    }

    private void OpenWriterLocked(DateOnly day, int part, string path)
    {
        var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false,
        };
        _currentDay = day;
        _currentPath = path;
        _currentBytes = stream.Length;
        _currentPart = part;
        TrySetPosixMode(path);
        _trimPending = true;
    }

    private void RefreshRetentionAccountingLocked()
    {
        _retainedBytes = TrimLocked();
        _retainedBytesKnown = true;

        if (_retainedBytes <= _totalSizeBudgetBytes)
        {
            // Incremental accounting makes the next pass happen on the first
            // append that crosses the configured aggregate budget. Avoid a
            // directory enumeration for every ordinary log entry.
            _nextTrimAtRetainedBytes = ByteAfter(_totalSizeBudgetBytes);
            return;
        }

        // The active part is never deleted, and another process or filesystem
        // policy can prevent an old part from being removed. In that case the
        // budget cannot be restored immediately. Retry after bounded growth
        // instead of rescanning the directory on every subsequent line.
        var retryGrowthBytes = Math.Clamp(_maxFileSizeBytes / 16, 4 * 1024, 64 * 1024);
        _nextTrimAtRetainedBytes = _retainedBytes > long.MaxValue - retryGrowthBytes
            ? long.MaxValue
            : _retainedBytes + retryGrowthBytes;
    }

    private static long ByteAfter(long value) =>
        value == long.MaxValue ? long.MaxValue : value + 1;

    private long TrimLocked()
    {
        if (!System.IO.Directory.Exists(_directory)) return 0;

        var files = System.IO.Directory
            .EnumerateFiles(_directory, "skillview-*.log")
            .Select(p => new FileInfo(p))
            .Select(file => TryParseLogFileIdentity(file.Name, out var date, out var part)
                ? new LogFile(file, date, part, Parsed: true)
                : new LogFile(file, DateOnly.MinValue, -1, Parsed: false))
            .OrderByDescending(item => item.Date)
            .ThenByDescending(item => item.Part)
            .ToList();

        var now = _clock().LocalDateTime;
        var retentionCutoff = DateOnly.FromDateTime(now.AddDays(-RetentionDays));
        var runningTotal = 0L;
        var toDelete = new List<FileInfo>();
        var retainedTotal = files
            .Where(item => item.Parsed)
            .Sum(item => item.File.Length);

        foreach (var item in files)
        {
            var file = item.File;
            if (!item.Parsed)
            {
                continue;
            }
            var isActive = _currentPath is not null
                && PathIdentity.Equals(file.FullName, _currentPath);
            if (!isActive && item.Date < retentionCutoff)
            {
                toDelete.Add(file);
                continue;
            }
            runningTotal += file.Length;
            if (!isActive && runningTotal > _totalSizeBudgetBytes)
            {
                toDelete.Add(file);
            }
        }

        foreach (var f in toDelete)
        {
            try
            {
                var length = f.Length;
                f.Delete();
                retainedTotal -= length;
            }
            catch { /* best effort */ }
        }

        return retainedTotal;
    }

    private static bool TryParseLogFileIdentity(string fileName, out DateOnly date, out int part)
    {
        date = default;
        part = 0;
        const string prefix = "skillview-";
        const string suffix = ".log";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal) ||
            !fileName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }
        var middle = fileName.AsSpan(prefix.Length, fileName.Length - prefix.Length - suffix.Length);
        if (middle.Length < 10
            || !DateOnly.TryParseExact(middle[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out date))
        {
            return false;
        }
        if (middle.Length == 10)
        {
            return true;
        }
        return middle.Length > 11
            && middle[10] == '-'
            && int.TryParse(middle[11..], NumberStyles.None, CultureInfo.InvariantCulture, out part)
            && part > 0;
    }

    private void CloseWriterLocked()
    {
        if (_writer is null) return;
        try { _writer.Flush(); } catch { }
        try { _writer.Dispose(); } catch { }
        _writer = null;
    }

    private static void TrySetPosixMode(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { /* best effort — filesystem may not support it */ }
    }

    public void Dispose()
    {
        IDisposable? subscription;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            subscription = _subscription;
            _subscription = null;
        }

        // Logger subscription disposal waits for an in-flight callback to
        // finish. Never wait while holding _gate: Append takes the logger's
        // registration lock first and then this lock, so the reverse order
        // here would deadlock shutdown.
        subscription?.Dispose();

        lock (_gate)
        {
            CloseWriterLocked();
        }
    }

    internal bool IsDisposedForTests => Volatile.Read(ref _disposed);

    private sealed record LogFile(FileInfo File, DateOnly Date, int Part, bool Parsed);
}
