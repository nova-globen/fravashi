using Fravashi.Application.Time;

namespace Fravashi.Application.Network;

/// <summary>
/// A provider this studio could not reach, and since when (backlog 232, plan @58).
/// </summary>
/// <param name="Connection">The provider as a person names it ("Jira", "Confluence", "Azure DevOps").</param>
/// <param name="Since">When the first call that provably never left failed.</param>
/// <param name="Failure">The most recent such failure, for its words.</param>
public sealed record ProviderOutage(string Connection, DateTimeOffset Since, NetworkFailure Failure)
{
    /// <summary>"Jira unreachable since 14:02 (name not resolved)", in the shape #228's reachability uses.</summary>
    public string Words => Sentence.Plain();

    /// <summary>The same sentence as parts, so a page says the time in the person's zone (backlog 246).</summary>
    public StampedWords Sentence =>
        StampedWords.Of($"{Connection} unreachable since ", StampedWords.At(Since, plainAsWritten: true), $" ({Failure.Words})");

    /// <summary>
    /// The line the provider sync page shows: what is unreachable, how much is waiting for it, and that nothing is lost,
    /// because a queue that waits is the read-and-queue degrade ADR 0018 designed and not a failure.
    /// </summary>
    public string WithWaiting(int waiting) => SentenceWithWaiting(waiting).Plain();

    /// <summary><see cref="WithWaiting"/> as parts (backlog 246).</summary>
    public StampedWords SentenceWithWaiting(int waiting) =>
        StampedWords.Of(
            Sentence,
            $"; {waiting} change(s) waiting; nothing lost. What this page shows is this board's own copy, and Sync now sends what waits once {Connection} answers.");
}

/// <summary>
/// Which providers have been unreachable, and since when (backlog 232, slice E of 219, plan @58). One per process,
/// shared by the board's sync, the wiki's publishing and the wiki mirror's pull, so every surface names the same
/// outage in the same words.
/// <para>
/// An outage STARTS at a failure that provably never left (<see cref="NetworkFailure.NeverLeft"/>). A failure where the
/// request may have arrived proves neither that the provider is down nor that it is up, so it leaves the state as it
/// is: a lone reset after write (F2's first call) is a question about one write, not an outage. An outage ENDS at any
/// answer from the provider, a refusal, a 429 or a 5xx included, because each of those means it was reached.
/// </para>
/// <para>
/// Kept in memory on purpose. It is a display hint: the durable fact, the queue of what waits, is already in the
/// database, and after a restart the line comes back the first time an act meets the outage again.
/// </para>
/// </summary>
public sealed class ProviderOutages(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, ProviderOutage> _down = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Raised when a provider goes down or comes back, never on a repeat of the same state.</summary>
    public event Action? Changed;

    /// <summary>The outage for <paramref name="connection"/>, or null while it is answering.</summary>
    public ProviderOutage? For(string connection)
    {
        lock (_gate)
        {
            return _down.GetValueOrDefault(connection);
        }
    }

    /// <summary>
    /// Records what one call to <paramref name="connection"/> found: <paramref name="network"/> is the classified
    /// failure behind it, or null when the provider answered (or nothing was called).
    /// </summary>
    public void Observe(string connection, NetworkFailure? network)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (network is { NeverLeft: true })
        {
            Failed(connection, network);
        }
        else if (network is not { IsNetwork: true })
        {
            Answered(connection);
        }
    }

    /// <summary>A call to <paramref name="connection"/> provably never left.</summary>
    public void Failed(string connection, NetworkFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        bool began;
        lock (_gate)
        {
            began = !_down.TryGetValue(connection, out var already);
            _down[connection] = new ProviderOutage(connection, already?.Since ?? _clock.GetUtcNow(), failure);
        }

        if (began)
        {
            Changed?.Invoke();
        }
    }

    /// <summary><paramref name="connection"/> answered.</summary>
    public void Answered(string connection)
    {
        bool ended;
        lock (_gate)
        {
            ended = _down.Remove(connection);
        }

        if (ended)
        {
            Changed?.Invoke();
        }
    }
}
