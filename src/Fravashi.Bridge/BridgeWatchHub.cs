using System.Threading.Channels;

namespace Fravashi.Bridge;

/// <summary>A file change inside a workspace; paths are workspace-relative with <c>/</c> separators.</summary>
/// <param name="Type"><c>created</c>, <c>changed</c>, <c>deleted</c>, or <c>renamed</c>.</param>
/// <param name="Path">The affected path (the new path for renames).</param>
/// <param name="OldPath">The previous path for renames, else null.</param>
public sealed record BridgeFileEvent(string Type, string Path, string? OldPath);

/// <summary>
/// Per-workspace filesystem watching for bridge subscribers (the studio's external-edit sync, ADR
/// 0016, consumes this over SSE in plan 2b). One shared <see cref="FileSystemWatcher"/> per
/// workspace, created lazily and disposed when its last subscriber leaves; each subscriber gets a
/// bounded channel (drop-oldest, 1024) so one slow consumer can never stall the watcher thread.
/// Events under <c>.git/</c> are filtered here: git churn is noise to every current consumer.
/// <para>
/// A LOST event is said, never silent (backlog 253): when the watcher's own buffer overflows (on Windows its default
/// 8 KB fills under a build or a checkout) or a subscriber's queue drops its oldest, the subscriber is sent an
/// <see cref="OverflowType"/> event with an empty path, so a consumer that keeps a copy knows to look again. Nothing
/// is kept for a subscriber that is not connected; that is the consumer's to recover when it connects.
/// </para>
/// </summary>
public sealed class BridgeWatchHub(WorkspaceRegistry registry) : IDisposable
{
    /// <summary>The event type that says events were lost; its path is empty.</summary>
    public const string OverflowType = "overflow";

    // The most a watcher's buffer may be on Windows (64 KB); the default 8 KB is what overflows.
    private const int WatcherBufferBytes = 64 * 1024;

    private sealed class Subscriber
    {
        private int _dropped;

        public Subscriber() =>
            Channel = System.Threading.Channels.Channel.CreateBounded<BridgeFileEvent>(
                new BoundedChannelOptions(1024)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleWriter = false,
                    SingleReader = true,
                },
                _ => Interlocked.Exchange(ref _dropped, 1));

        public Channel<BridgeFileEvent> Channel { get; }

        /// <summary>Offers an event, followed by an overflow notice when the queue has dropped anything since the last one.</summary>
        public void Offer(BridgeFileEvent fileEvent)
        {
            Channel.Writer.TryWrite(fileEvent);
            if (Interlocked.Exchange(ref _dropped, 0) == 1)
            {
                Channel.Writer.TryWrite(new BridgeFileEvent(OverflowType, string.Empty, null));
            }
        }
    }

    private sealed class WorkspaceWatch
    {
        public required FileSystemWatcher Watcher { get; init; }
        public List<Subscriber> Subscribers { get; } = [];
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, WorkspaceWatch> _watches = new(StringComparer.Ordinal);
    private bool _disposed;

    public ChannelReader<BridgeFileEvent> Subscribe(string id, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var workspace = registry.TryGet(id) ?? throw new KeyNotFoundException($"Unknown workspace '{id}'.");

        var subscriber = new Subscriber();
        lock (_sync)
        {
            if (!_watches.TryGetValue(id, out var watch))
            {
                watch = new WorkspaceWatch { Watcher = CreateWatcher(id, workspace.RootPath) };
                _watches[id] = watch;
            }

            watch.Subscribers.Add(subscriber);
        }

        cancellationToken.Register(() => Unsubscribe(id, subscriber));
        return subscriber.Channel.Reader;
    }

    private FileSystemWatcher CreateWatcher(string id, string rootPath)
    {
        var watcher = new FileSystemWatcher(rootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = WatcherBufferBytes,
        };
        watcher.Created += (_, e) => Publish(id, rootPath, "created", e.FullPath, null);
        watcher.Changed += (_, e) => Publish(id, rootPath, "changed", e.FullPath, null);
        watcher.Deleted += (_, e) => Publish(id, rootPath, "deleted", e.FullPath, null);
        watcher.Renamed += (_, e) => Publish(id, rootPath, "renamed", e.FullPath, e.OldFullPath);

        // An overflow (or any watcher failure) means events were lost: it used to be unhandled, so nobody knew.
        watcher.Error += (_, _) => Lost(id);
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    /// <summary>Tells every subscriber of the workspace that events were lost (backlog 253).</summary>
    private void Lost(string id)
    {
        lock (_sync)
        {
            if (_watches.TryGetValue(id, out var watch))
            {
                foreach (var subscriber in watch.Subscribers)
                {
                    subscriber.Offer(new BridgeFileEvent(OverflowType, string.Empty, null));
                }
            }
        }
    }

    private void Publish(string id, string rootPath, string type, string fullPath, string? oldFullPath)
    {
        var relative = Relative(rootPath, fullPath);
        if (relative is null || IsGitInternal(relative))
        {
            return;
        }

        var oldRelative = oldFullPath is null ? null : Relative(rootPath, oldFullPath);
        if (oldRelative is not null && IsGitInternal(oldRelative))
        {
            return;
        }

        var fileEvent = new BridgeFileEvent(type, relative, oldRelative);
        lock (_sync)
        {
            if (_watches.TryGetValue(id, out var watch))
            {
                foreach (var subscriber in watch.Subscribers)
                {
                    subscriber.Offer(fileEvent);
                }
            }
        }
    }

    private void Unsubscribe(string id, Subscriber subscriber)
    {
        lock (_sync)
        {
            if (!_watches.TryGetValue(id, out var watch))
            {
                return;
            }

            watch.Subscribers.Remove(subscriber);
            subscriber.Channel.Writer.TryComplete();
            if (watch.Subscribers.Count == 0)
            {
                watch.Watcher.Dispose();
                _watches.Remove(id);
            }
        }
    }

    private static string? Relative(string rootPath, string fullPath)
    {
        var relative = Path.GetRelativePath(rootPath, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }

        return relative.Replace('\\', '/');
    }

    private static bool IsGitInternal(string relativePath) =>
        relativePath.Equals(".git", StringComparison.OrdinalIgnoreCase)
        || relativePath.StartsWith(".git/", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            foreach (var watch in _watches.Values)
            {
                watch.Watcher.Dispose();
                foreach (var subscriber in watch.Subscribers)
                {
                    subscriber.Channel.Writer.TryComplete();
                }
            }

            _watches.Clear();
        }
    }
}
