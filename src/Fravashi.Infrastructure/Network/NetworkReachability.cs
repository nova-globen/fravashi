using System.Net;
using System.Net.Sockets;
using Fravashi.Application.Network;

namespace Fravashi.Infrastructure.Network;

/// <summary>
/// The reachability probe (backlog 228, plan @49): one name lookup plus TCP connect per step, with backoff while
/// the target is unreachable. <see cref="StepAsync"/> is the whole decision and answers how long to wait before the
/// next step, so it is driven directly on a fake clock in tests; <see cref="RunAsync"/> only loops it.
/// <para>
/// Registered by backlog 229 (<c>SessionsServiceCollectionExtensions.AgentModelEndpoint</c>) against the host the agent
/// CLI calls, the broker's when the broker carries the runtime, and asked only while a conversation waits for the
/// network: <see cref="RunAsync"/> is not started, because a probe nobody reads would only add traffic.
/// </para>
/// </summary>
public sealed class NetworkReachability : INetworkReachability
{
    /// <summary>The first wait after a failed probe (plan @48, S2).</summary>
    public static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(5);

    /// <summary>The longest wait between failed probes; the wait doubles from <see cref="FirstRetry"/> up to this.</summary>
    public static readonly TimeSpan MaxRetry = TimeSpan.FromSeconds(60);

    /// <summary>The wait between probes while the target is reachable.</summary>
    public static readonly TimeSpan HealthyInterval = TimeSpan.FromSeconds(60);

    /// <summary>How long one connect may take before it counts as timed out.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly INetworkConnector _connector;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TimeSpan _nextRetry = FirstRetry;
    private NetworkReachabilityState _current;

    public NetworkReachability(NetworkEndpoint target, INetworkConnector connector, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(target);
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _current = new(target, null, null, null, null);
    }

    /// <inheritdoc />
    public NetworkReachabilityState Current => Volatile.Read(ref _current);

    /// <inheritdoc />
    public event Action<NetworkReachabilityState>? Changed;

    /// <inheritdoc />
    public async Task<NetworkReachabilityState> ProbeNowAsync(CancellationToken cancellationToken = default)
    {
        await StepAsync(cancellationToken).ConfigureAwait(false);
        return Current;
    }

    /// <summary>
    /// Probes once, publishes the answer, and answers the wait before the next probe: <see cref="HealthyInterval"/>
    /// while reachable, otherwise <see cref="FirstRetry"/> doubling to <see cref="MaxRetry"/>, starting again from
    /// <see cref="FirstRetry"/> after any success.
    /// </summary>
    public async Task<TimeSpan> StepAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        NetworkReachabilityState? changed = null;
        TimeSpan wait;
        try
        {
            var failure = await _connector.ConnectAsync(Current.Target, ConnectTimeout, cancellationToken).ConfigureAwait(false);
            var now = _clock.GetUtcNow();
            var before = Current;
            var reachable = failure is null;

            if (reachable)
            {
                wait = HealthyInterval;
                _nextRetry = FirstRetry;
            }
            else
            {
                wait = _nextRetry;
                _nextRetry = TimeSpan.FromTicks(Math.Min(_nextRetry.Ticks * 2, MaxRetry.Ticks));
            }

            var since = before.Reachable == reachable ? before.Since : now;
            var next = new NetworkReachabilityState(before.Target, reachable, since, failure, now + wait);
            Volatile.Write(ref _current, next);
            if (before.Reachable != reachable)
            {
                changed = next;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (changed is not null)
        {
            Changed?.Invoke(changed);
        }

        return wait;
    }

    /// <summary>Probes on the schedule <see cref="StepAsync"/> answers until <paramref name="cancellationToken"/> fires.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var wait = await StepAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(wait, _clock, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// The real probe act: <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/>, then a TCP connect to each
/// address in turn until one answers. A failure is classified by <see cref="NetworkFailure"/>; a connect that runs
/// out of time is <see cref="NetworkFailureKind.TimedOutConnecting"/>. Nothing is written on the connection.
/// </summary>
public sealed class SocketNetworkConnector : INetworkConnector
{
    /// <inheritdoc />
    public async Task<NetworkFailure?> ConnectAsync(NetworkEndpoint target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);

        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(target.Host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(target.Host, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new NetworkFailure(NetworkFailureKind.NameNotResolved, "the name lookup timed out");
        }
        catch (SocketException ex)
        {
            return NetworkFailure.Classify(ex) is { IsNetwork: true } named ? named : new(NetworkFailureKind.NameNotResolved, ex.SocketErrorCode.ToString());
        }

        if (addresses.Length == 0)
        {
            return new NetworkFailure(NetworkFailureKind.NameNotResolved, "no addresses");
        }

        NetworkFailure? last = null;
        foreach (var address in addresses)
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, target.Port), limit.Token).ConfigureAwait(false);
                return null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new NetworkFailure(NetworkFailureKind.TimedOutConnecting, $"no answer from {address} within {timeout.TotalSeconds:0} s");
            }
            catch (SocketException ex)
            {
                last = NetworkFailure.ClassifyConnect(ex.SocketErrorCode);
            }
        }

        return last;
    }
}
