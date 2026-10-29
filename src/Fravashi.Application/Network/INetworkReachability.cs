using Fravashi.Application.Time;

namespace Fravashi.Application.Network;

/// <summary>A host and port a reachability probe connects to.</summary>
public sealed record NetworkEndpoint(string Host, int Port)
{
    /// <inheritdoc />
    public override string ToString() => $"{Host}:{Port}";
}

/// <summary>
/// What a reachability probe last found (backlog 228). <paramref name="Since"/> is when the current answer began, so
/// "unreachable since 14:02" survives any number of failed probes after the first.
/// </summary>
/// <param name="Target">What is probed.</param>
/// <param name="Reachable">Null before the first probe has answered.</param>
/// <param name="Since">When the current answer began; null before the first probe.</param>
/// <param name="LastFailure">Why the last probe failed; null while reachable.</param>
/// <param name="NextProbeAt">When the next probe is due; null when nothing is scheduled.</param>
public sealed record NetworkReachabilityState(
    NetworkEndpoint Target,
    bool? Reachable,
    DateTimeOffset? Since,
    NetworkFailure? LastFailure,
    DateTimeOffset? NextProbeAt)
{
    /// <summary>Plain words: "api.anthropic.com unreachable since 14:02 (name not resolved)".</summary>
    public string Words => Sentence.Plain();

    /// <summary>The same sentence as parts, so a page says the time in the person's zone (backlog 246).</summary>
    public StampedWords Sentence
    {
        get
        {
            var since = Since is { } at ? StampedWords.At(at, plainAsWritten: true) : null;
            return Reachable switch
            {
                null => StampedWords.Of($"{Target.Host} not checked yet"),
                true => StampedWords.Of($"{Target.Host} reachable since ", since),
                false => StampedWords.Of($"{Target.Host} unreachable since ", since, $" ({LastFailure?.Words ?? "unknown"})"),
            };
        }
    }
}

/// <summary>
/// Is a named host:port reachable from here right now (backlog 228, plan @49): a name lookup plus a TCP connect,
/// repeated with backoff while it is not, publishing "reachable / unreachable since". Slices 229 onwards decide what
/// to probe and what to do with the answer.
/// </summary>
public interface INetworkReachability
{
    /// <summary>The last answer.</summary>
    NetworkReachabilityState Current { get; }

    /// <summary>Raised when the answer changes between reachable and unreachable, never on a repeat of the same one.</summary>
    event Action<NetworkReachabilityState>? Changed;

    /// <summary>Probes now, outside the schedule, and answers the new state.</summary>
    Task<NetworkReachabilityState> ProbeNowAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The probe's one network act: look the name up and open a TCP connection to it. Answers null when it connected,
/// otherwise the classified failure. Never throws for a network failure.
/// </summary>
public interface INetworkConnector
{
    /// <summary>Connects to <paramref name="target"/> within <paramref name="timeout"/>, then closes the connection.</summary>
    Task<NetworkFailure?> ConnectAsync(NetworkEndpoint target, TimeSpan timeout, CancellationToken cancellationToken);
}
