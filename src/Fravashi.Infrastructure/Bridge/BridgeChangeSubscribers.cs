namespace Fravashi.Infrastructure.Bridge;

/// <summary>
/// The process-wide registry of bridge change subscribers, one per workspace id (backlog 261). Singleton so a
/// subscriber outlives the circuits that use it: every window on the same workspace shares one
/// <see cref="BridgeChangeSubscriber"/> and so one event stream, and windows on different workspaces get different
/// ones. A subscriber's stream stops when its last subscription ends and starts again on the next (that is
/// <see cref="BridgeChangeSubscriber"/>'s own behaviour); the subscriber object stays here for the process lifetime.
/// </summary>
public sealed class BridgeChangeSubscribers : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, BridgeChangeSubscriber> _subscribers = new(StringComparer.Ordinal);

    /// <summary>How many workspaces have a subscriber.</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _subscribers.Count;
            }
        }
    }

    /// <summary>The subscriber for the workspace <paramref name="context"/> names. Only for an active context.</summary>
    public BridgeChangeSubscriber For(BridgeWorkspaceContext context)
    {
        if (!context.IsBridgeActive)
        {
            throw new InvalidOperationException("No bridge workspace is active, so there is no stream to subscribe to.");
        }

        lock (_sync)
        {
            var key = context.ActiveWorkspaceId!;
            if (!_subscribers.TryGetValue(key, out var subscriber))
            {
                // Its own client: the typed one is scoped and would die with the circuit that built it.
                subscriber = new BridgeChangeSubscriber(new BridgeClient(new HttpClient(), context));
                _subscribers[key] = subscriber;
            }

            return subscriber;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            foreach (var subscriber in _subscribers.Values)
            {
                subscriber.Dispose();
            }

            _subscribers.Clear();
        }
    }
}
