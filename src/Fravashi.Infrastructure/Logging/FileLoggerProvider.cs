using Fravashi.Infrastructure.Sessions;
using Microsoft.Extensions.Logging;

namespace Fravashi.Infrastructure.Logging;

/// <summary>
/// Minimal rolling file logger (directive 2026-07-26): every app writes its log stream into
/// <c>&lt;data dir&gt;/logs</c> so one folder tells the whole story. One file per app per day
/// (<c>&lt;app&gt;-yyyy-MM-dd.log</c>), append-only, and strictly best effort: logging must never
/// take the app down. Deliberately no external logging package, so the dependency license gate
/// stays untouched.
/// <para>
/// Backlog 75: a log is evidence like a transcript, so every line passes through
/// <see cref="TranscriptRedactor"/> WHOLE (message and exception together) before it is written, and this
/// app's own files older than <see cref="RetentionDays"/> days are deleted the first time a line is written on
/// a new day. Only this app's files (<c>&lt;app&gt;-*.log</c>) are pruned: another app's logs in the same
/// folder are that app's to keep.
/// </para>
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly string _appName;
    private readonly LogLevel _minimum;
    /// <summary>How many days of this app's log files are kept (backlog 75).</summary>
    public const int RetentionDays = 14;

    private readonly object _sync = new();
    private bool _directoryReady;
    private DateTime _prunedFor = DateTime.MinValue;

    public FileLoggerProvider(string directory, string appName, LogLevel minimum = LogLevel.Information)
    {
        _directory = directory;
        _appName = appName;
        _minimum = minimum;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    internal bool IsEnabled(LogLevel level) => level >= _minimum && level != LogLevel.None;

    internal void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{Shorten(level)}] {category}: {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        line = TranscriptRedactor.Redact(line);

        lock (_sync)
        {
            try
            {
                if (!_directoryReady)
                {
                    Directory.CreateDirectory(_directory);
                    _directoryReady = true;
                }

                var today = DateTime.Now.Date;
                if (_prunedFor != today)
                {
                    _prunedFor = today;
                    PruneOlderThan(today.AddDays(-RetentionDays));
                }

                File.AppendAllText(
                    Path.Combine(_directory, $"{_appName}-{today:yyyy-MM-dd}.log"),
                    line + Environment.NewLine);
            }
            catch (Exception)
            {
                // Best effort by contract: a full disk or a locked file must never crash the app.
            }
        }
    }

    /// <summary>
    /// Deletes this app's dated files whose date in the NAME is before <paramref name="cutoff"/>. The name, not
    /// the timestamp, because a copied or touched file keeps its day in its name.
    /// </summary>
    private void PruneOlderThan(DateTime cutoff)
    {
        foreach (var file in Directory.EnumerateFiles(_directory, _appName + "-*.log").ToList())
        {
            var stem = Path.GetFileNameWithoutExtension(file)[(_appName.Length + 1)..];
            if (DateTime.TryParseExact(stem, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var day)
                && day < cutoff)
            {
                File.Delete(file);
            }
        }
    }

    private static string Shorten(LogLevel level) => level switch
    {
        LogLevel.Trace => "trc",
        LogLevel.Debug => "dbg",
        LogLevel.Information => "inf",
        LogLevel.Warning => "wrn",
        LogLevel.Error => "err",
        LogLevel.Critical => "crt",
        _ => level.ToString(),
    };

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => owner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                owner.Write(category, logLevel, formatter(state, exception), exception);
            }
        }
    }
}
