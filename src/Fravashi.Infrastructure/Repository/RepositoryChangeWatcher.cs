using System.Collections.Concurrent;
using Fravashi.Application.Repository;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fravashi.Infrastructure.Repository;

/// <summary>
/// <see cref="IRepositoryChangeNotifier"/> over a <see cref="FileSystemWatcher"/> on
/// <c>Repository:Root</c> (ADR 0016). Singleton; the watcher starts lazily on the first subscription
/// (and re-checks the root on each one, so it comes alive after onboarding's clone finishes without a
/// restart). Noise control: <c>.git</c> and the reader's denied directories are filtered, events are
/// debounced per path (~300 ms of quiet), renames surface as delete + create, and a watcher buffer
/// overflow degrades honestly to one "anything changed" event (empty path).
/// </summary>
public sealed class RepositoryChangeWatcher : IRepositoryChangeNotifier, IDisposable
{
    private static readonly string[] NoisyDirectories = [".git", "bin", "obj", "node_modules", ".vs", ".idea"];
    private static readonly TimeSpan DebounceQuiet = TimeSpan.FromMilliseconds(300);

    private readonly IOptionsMonitor<RepositoryReaderOptions> _options;
    private readonly ILogger<RepositoryChangeWatcher> _logger;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<Guid, Action<RepositoryChange>> _subscribers = new();
    private readonly ConcurrentDictionary<string, PendingChange> _pending = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private string _watchedRoot = string.Empty;
    private bool _disposed;

    /// <param name="clock">What the quiet window is measured on. Production gets the system clock; a test
    /// moves one by hand so the debounce is proved without timing the machine (backlog 204).</param>
    public RepositoryChangeWatcher(
        IOptionsMonitor<RepositoryReaderOptions> options, ILogger<RepositoryChangeWatcher> logger, TimeProvider? clock = null)
    {
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public IDisposable Subscribe(Action<RepositoryChange> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var key = Guid.NewGuid();
        _subscribers[key] = handler;
        EnsureWatching();
        return new Subscription(this, key);
    }

    /// <summary>(Re)create the watcher when the configured root changed or nothing is running yet.</summary>
    private void EnsureWatching()
    {
        var configured = _options.CurrentValue.Root;
        var root = string.IsNullOrWhiteSpace(configured)
            ? string.Empty
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));

        lock (_gate)
        {
            if (_disposed || (string.Equals(root, _watchedRoot, StringComparison.OrdinalIgnoreCase) && _watcher is not null))
            {
                return;
            }

            _watcher?.Dispose();
            _watcher = null;
            _watchedRoot = root;
            if (root.Length == 0 || !Directory.Exists(root))
            {
                return;
            }

            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Changed += (_, e) => Enqueue(RepositoryChangeKind.ChangedOrCreated, e.FullPath);
                watcher.Created += (_, e) => Enqueue(RepositoryChangeKind.ChangedOrCreated, e.FullPath);
                watcher.Deleted += (_, e) => Enqueue(RepositoryChangeKind.Deleted, e.FullPath);
                watcher.Renamed += (_, e) =>
                {
                    Enqueue(RepositoryChangeKind.Deleted, e.OldFullPath);
                    Enqueue(RepositoryChangeKind.ChangedOrCreated, e.FullPath);
                };
                watcher.Error += (_, e) =>
                {
                    _logger.LogWarning(e.GetException(), "Repository watcher overflowed; broadcasting a full-refresh event.");
                    Dispatch(new RepositoryChange(RepositoryChangeKind.ChangedOrCreated, string.Empty));
                };
                watcher.EnableRaisingEvents = true;
                _watcher = watcher;
            }
            catch (Exception ex)
            {
                // A watcher that cannot start (exotic mount, permissions) degrades to the focus-time
                // mtime checks the Code page does anyway; never fail a subscription over it.
                _logger.LogWarning(ex, "Repository watcher could not start for {Root}.", root);
                _watcher = null;
            }
        }
    }

    private void Enqueue(RepositoryChangeKind kind, string fullPath)
    {
        var relative = ToRelative(fullPath);
        if (relative is null || IsNoise(relative))
        {
            return;
        }

        // Latest kind wins per path; the timer restarts on every event so a rapid write burst
        // (editors write temp + rename, git touches many files) collapses to one dispatch.
        var pending = _pending.AddOrUpdate(
            relative,
            path => new PendingChange(new RepositoryChange(kind, path), CreateTimer(path)),
            (path, existing) =>
            {
                existing.Change = new RepositoryChange(kind, path);
                return existing;
            });
        pending.Timer.Change(DebounceQuiet, Timeout.InfiniteTimeSpan);
    }

    // The path rides as the timer's state, so a test clock can tell which path a timer is for.
    private ITimer CreateTimer(string path) =>
        _clock.CreateTimer(state =>
        {
            if (_pending.TryRemove((string)state!, out var done))
            {
                done.Timer.Dispose();
                Dispatch(done.Change);
            }
        }, path, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    private void Dispatch(RepositoryChange change)
    {
        foreach (var handler in _subscribers.Values)
        {
            try
            {
                handler(change);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A repository-change subscriber threw for {Path}.", change.RelativePath);
            }
        }
    }

    private string? ToRelative(string fullPath)
    {
        var root = _watchedRoot;
        if (root.Length == 0)
        {
            return null;
        }

        var full = Path.GetFullPath(fullPath);
        var rootWithSeparator = root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return full[rootWithSeparator.Length..].Replace('\\', '/');
    }

    private static bool IsNoise(string relativePath)
    {
        foreach (var segment in relativePath.Split('/'))
        {
            if (NoisyDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _watcher?.Dispose();
            _watcher = null;
        }

        foreach (var pending in _pending.Values)
        {
            pending.Timer.Dispose();
        }

        _pending.Clear();
        _subscribers.Clear();
    }

    private sealed class PendingChange(RepositoryChange change, ITimer timer)
    {
        public RepositoryChange Change { get; set; } = change;

        public ITimer Timer { get; } = timer;
    }

    private sealed class Subscription(RepositoryChangeWatcher owner, Guid key) : IDisposable
    {
        public void Dispose() => owner._subscribers.TryRemove(key, out _);
    }
}
