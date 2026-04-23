using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace SkillView.Logging;

/// Appends already-redacted log entries to a daily-rotated file under the
/// SkillView cache log directory. Implements §18.3: daily rotation, 14-day
/// retention, 50 MB bound, POSIX mode 0600.
///
/// Redaction is applied upstream by `Logger`; this sink trusts `LogEntry.Message`
/// to already be safe (§13, §24.13).
public sealed class FileLogSink : IDisposable
{
    public const int RetentionDays = 14;
    public const long TotalSizeBudgetBytes = 50L * 1024 * 1024;

    private readonly string _directory;
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _clock;
    private StreamWriter? _writer;
    private DateOnly _currentDay;
    private bool _disposed;
    private bool _trimPending;

    public FileLogSink(string directory, Func<DateTimeOffset>? clock = null)
    {
        _directory = directory;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public string Directory => _directory;

    public void Attach(Logger logger)
    {
        // Emit the ring-buffer snapshot first so the file reflects pre-attach entries too.
        foreach (var entry in logger.Snapshot())
        {
            Append(entry);
        }
        logger.Subscribe(Append);
    }

    public void Append(LogEntry entry)
    {
        if (_disposed) return;
        lock (_gate)
        {
            try
            {
                var today = DateOnly.FromDateTime(entry.Timestamp.ToLocalTime().DateTime);
                EnsureWriter(today);
                _writer!.WriteLine(Logger.Format(entry));
                _writer.Flush();

                if (_trimPending)
                {
                    _trimPending = false;
                    TrimLocked();
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

    /// Delete every rotated log file. Used by `doctor --clear-logs` (§18.3).
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
        var path = Path.Combine(_directory, LogPaths.FileNameForDate(day));
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false,
        };
        _currentDay = day;
        TrySetPosixMode(path);
        _trimPending = true;
    }

    private void TrimLocked()
    {
        if (!System.IO.Directory.Exists(_directory)) return;

        var files = System.IO.Directory
            .EnumerateFiles(_directory, "skillview-*.log")
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.Name, StringComparer.Ordinal) // newest date first
            .ToList();

        var now = _clock().LocalDateTime;
        var retentionCutoff = DateOnly.FromDateTime(now.AddDays(-RetentionDays));
        var runningTotal = 0L;
        var toDelete = new List<FileInfo>();

        foreach (var file in files)
        {
            if (TryParseLogFileDate(file.Name, out var date) && date < retentionCutoff)
            {
                toDelete.Add(file);
                continue;
            }
            runningTotal += file.Length;
            if (runningTotal > TotalSizeBudgetBytes)
            {
                toDelete.Add(file);
            }
        }

        foreach (var f in toDelete)
        {
            try { f.Delete(); } catch { /* best effort */ }
        }
    }

    private static bool TryParseLogFileDate(string fileName, out DateOnly date)
    {
        date = default;
        const string prefix = "skillview-";
        const string suffix = ".log";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal) ||
            !fileName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }
        var mid = fileName.Substring(prefix.Length, fileName.Length - prefix.Length - suffix.Length);
        return DateOnly.TryParseExact(mid, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out date);
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
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CloseWriterLocked();
        }
    }
}
