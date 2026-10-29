namespace Fravashi.Application.Notifications;

/// <summary>
/// Everything currently waiting on a person, across every live session and every open autopilot run (ADR 0031
/// decision 12, backlog 304): every pending approval, every open agent question, and every parked autopilot
/// item.
/// </summary>
public interface IWaitingForPersonList
{
    /// <summary>Every entry waiting right now, unordered. Order with <see cref="WaitingForPersonOrdering"/>.</summary>
    Task<IReadOnlyList<WaitingForPersonEntry>> CurrentAsync(CancellationToken ct = default);
}

/// <summary>The one ordering the home page's list and anything else reading it agree on: oldest wait first.</summary>
public static class WaitingForPersonOrdering
{
    public static IReadOnlyList<WaitingForPersonEntry> Order(IEnumerable<WaitingForPersonEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return [.. entries.OrderBy(e => e.SinceUtc)];
    }
}
