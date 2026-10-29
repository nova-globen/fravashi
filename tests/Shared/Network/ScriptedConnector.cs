using System.Net.Sockets;
using Fravashi.Application.Network;

namespace Fravashi.Testing.Network;

/// <summary>
/// The reachability probe's connect, played from a <see cref="NetworkScript"/> (backlog 228). A connect writes
/// nothing, so a reset after write is a connect that succeeded; a hang runs out the probe's own timeout on the fake
/// clock. Each failure goes through the real classifier, as the socket connector's does.
/// </summary>
public sealed class ScriptedConnector(NetworkScript script) : INetworkConnector
{
    /// <summary>The targets asked about, in order, with the time on the fake clock.</summary>
    public List<(DateTimeOffset At, NetworkEndpoint Target)> Calls { get; } = [];

    public Task<NetworkFailure?> ConnectAsync(NetworkEndpoint target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add((script.Clock.GetUtcNow(), target));
        NetworkFailure? failure = script.Next() switch
        {
            NetworkBehaviour.Healthy or NetworkBehaviour.ResetAfterWrite => null,
            NetworkBehaviour.NameFails => NetworkFailure.Classify(new SocketException((int)SocketError.HostNotFound)),
            NetworkBehaviour.Refuses => NetworkFailure.ClassifyConnect(SocketError.ConnectionRefused),
            NetworkBehaviour.NoRoute => NetworkFailure.ClassifyConnect(SocketError.NetworkUnreachable),
            NetworkBehaviour.ResetBeforeSend => NetworkFailure.ClassifyConnect(SocketError.ConnectionReset),
            NetworkBehaviour.Hangs => HangFor(timeout),
            var other => throw new InvalidOperationException($"No connect shape for {other}."),
        };

        return Task.FromResult(failure);
    }

    private NetworkFailure HangFor(TimeSpan timeout)
    {
        script.Clock.Advance(timeout);
        return NetworkFailure.ClassifyConnect(SocketError.TimedOut);
    }
}
