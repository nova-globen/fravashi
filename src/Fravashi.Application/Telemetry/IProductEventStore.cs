using Fravashi.Domain.Telemetry;

namespace Fravashi.Application.Telemetry;

/// <summary>
/// Durable storage for recorded product events (backlog 74). Append-only: an event is a record of
/// something that happened, so nothing updates one.
/// </summary>
public interface IProductEventStore
{
    /// <summary>Appends one event.</summary>
    void Append(ProductEvent productEvent);

    /// <summary>The most recent events, newest first, capped at <paramref name="max"/>.</summary>
    IReadOnlyList<ProductEvent> Recent(int max = 2000);

    /// <summary>
    /// Every event of one <paramref name="kind"/> that occurred at or after <paramref name="fromUtc"/>,
    /// most recently RECORDED first (backlog 36), which is the order <see cref="Recent"/> uses and is the
    /// order of occurrence for anything the studio itself wrote. Distinct from <see cref="Recent"/> because a home card asking "how long
    /// did approvals wait this month" must not answer from whatever happened to fit in the newest N rows
    /// of every kind mixed together: a busy month of session events would silently push the approvals it
    /// is summarising out of the window, and the card would report a median of the survivors as if it were
    /// the median of the month.
    /// </summary>
    IReadOnlyList<ProductEvent> Since(ProductEventKind kind, DateTimeOffset fromUtc, int max = 20000);

    /// <summary>How many events are stored.</summary>
    long Count();
}
