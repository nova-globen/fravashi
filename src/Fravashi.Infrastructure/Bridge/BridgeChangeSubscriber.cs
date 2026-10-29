using Fravashi.Application.Repository;

namespace Fravashi.Infrastructure.Bridge;

/// <summary>
/// Maps the bridge's SSE file events onto the studio's <see cref="RepositoryChange"/> stream: one
/// shared background loop feeding every subscriber (started on the first subscribe, stopped when
/// the last unsubscribes). Renames arrive as delete-old + change-new, matching what
/// RepositoryChangeWatcher emits locally, so the Code and Git pages need no mode awareness.
/// <para>
/// The stream is NOT lossless (backlog 257, the Code page's half of 253): the bridge keeps nothing for a client that is
/// not connected, and says when it lost events. So each (re)connection and each overflow notice becomes one
/// "anything changed" change (an empty path, what the local watcher's overflow always sent), on which the Code page
/// refreshes its tree and checks every open buffer and the Git page its status. At most one per
/// <see cref="MinimumLookAgainSpacing"/>, whatever asks, so a flapping bridge cannot make the pages rescan constantly; a
/// request inside the window is kept and answered at its end, never dropped, because it may be the reconnect after the
/// very gap that needs it.
/// </para>
/// </summary>
public sealed class BridgeChangeSubscriber(BridgeClient client, TimeProvider? clock = null) : IDisposable
{
    /// <summary>The "anything changed" change (backlog 257).</summary>
    private static readonly RepositoryChange LookAgain = new(RepositoryChangeKind.ChangedOrCreated, string.Empty);

    private readonly object _sync = new();
    private readonly List<Action<RepositoryChange>> _handlers = [];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private CancellationTokenSource? _loop;
    private DateTimeOffset? _lastLookAgain;
    private ITimer? _deferredLookAgain;
    private bool _disposed;

    /// <summary>The least time between two "anything changed" changes (backlog 257).</summary>
    public TimeSpan MinimumLookAgainSpacing { get; init; } = TimeSpan.FromSeconds(30);

    public IDisposable Subscribe(Action<RepositoryChange> handler)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            _handlers.Add(handler);
            if (_loop is null)
            {
                _loop = new CancellationTokenSource();
                var token = _loop.Token;
                _ = Task.Run(() => client.SubscribeEventsAsync(e =>
                {
                    Publish(e);
                    return Task.CompletedTask;
                }, token), token);
            }
        }

        return new Subscription(this, handler);
    }

    private void Publish(BridgeFileEventDto fileEvent)
    {
        var changes = fileEvent.Type switch
        {
            "created" or "changed" => new[] { new RepositoryChange(RepositoryChangeKind.ChangedOrCreated, fileEvent.Path) },
            "deleted" => [new RepositoryChange(RepositoryChangeKind.Deleted, fileEvent.Path)],
            "renamed" =>
            [
                new RepositoryChange(RepositoryChangeKind.Deleted, fileEvent.OldPath ?? fileEvent.Path),
                new RepositoryChange(RepositoryChangeKind.ChangedOrCreated, fileEvent.Path),
            ],
            BridgeClient.ConnectedEventType or BridgeClient.OverflowEventType => RequestLookAgain(),
            _ => [],
        };

        Dispatch(changes);
    }

    /// <summary>
    /// The look-again to send now, or nothing when one was sent within the spacing; then one is scheduled for the
    /// window's end unless it already is (backlog 257).
    /// </summary>
    private RepositoryChange[] RequestLookAgain()
    {
        lock (_sync)
        {
            if (_disposed || _deferredLookAgain is not null)
            {
                return [];
            }

            var now = _clock.GetUtcNow();
            if (_lastLookAgain is { } last && now - last < MinimumLookAgainSpacing)
            {
                _deferredLookAgain = _clock.CreateTimer(
                    _ => SendDeferredLookAgain(), null, last + MinimumLookAgainSpacing - now, Timeout.InfiniteTimeSpan);
                return [];
            }

            _lastLookAgain = now;
            return [LookAgain];
        }
    }

    private void SendDeferredLookAgain()
    {
        lock (_sync)
        {
            if (_deferredLookAgain is null)
            {
                return; // disposed meanwhile
            }

            _deferredLookAgain.Dispose();
            _deferredLookAgain = null;
            _lastLookAgain = _clock.GetUtcNow();
        }

        Dispatch([LookAgain]);
    }

    private void Dispatch(RepositoryChange[] changes)
    {
        if (changes.Length == 0)
        {
            return;
        }

        Action<RepositoryChange>[] handlers;
        lock (_sync)
        {
            handlers = [.. _handlers];
        }

        foreach (var change in changes)
        {
            foreach (var handler in handlers)
            {
                try
                {
                    handler(change);
                }
                catch (Exception)
                {
                    // A misbehaving subscriber must never take the event loop down.
                }
            }
        }
    }

    private void Unsubscribe(Action<RepositoryChange> handler)
    {
        lock (_sync)
        {
            _handlers.Remove(handler);
            if (_handlers.Count == 0 && _loop is { } loop)
            {
                _loop = null;
                loop.Cancel();
                loop.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _handlers.Clear();
            _loop?.Cancel();
            _loop?.Dispose();
            _loop = null;
            _deferredLookAgain?.Dispose();
            _deferredLookAgain = null;
        }
    }

    private sealed class Subscription(BridgeChangeSubscriber owner, Action<RepositoryChange> handler) : IDisposable
    {
        public void Dispose() => owner.Unsubscribe(handler);
    }
}
