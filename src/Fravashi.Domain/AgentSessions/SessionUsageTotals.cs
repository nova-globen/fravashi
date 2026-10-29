namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// A usage figure rolled up across a set of sessions (backlog 19): what they cost and how many billed
/// tokens they moved, plus how many sessions the figure is over. Cost stays <see cref="decimal"/> like
/// every other money value in the studio, so a total is the exact sum of the per-session costs it is
/// built from and reconciles with them to the cent.
/// </summary>
public sealed record UsageTotal(decimal CostUsd, long TotalTokens, int Sessions)
{
    public static UsageTotal Zero { get; } = new(0m, 0, 0);
}

/// <summary>
/// Cross-session usage for the totals surface (backlog 19): the current UTC day, the current UTC
/// calendar month and all time, across every session and every workspace. Each window is a subset of
/// the next, built from the same per-session figures, so they never disagree about a session they both
/// count.
/// <para>
/// The day was added for backlog 62, where it is not decoration: a daily ceiling that cannot be seen
/// until it stops a run is exactly the surprise that item exists to remove.
/// </para>
/// </summary>
public sealed record SessionUsageTotals(UsageTotal Today, UsageTotal Month, UsageTotal AllTime)
{
    public static SessionUsageTotals Empty { get; } = new(UsageTotal.Zero, UsageTotal.Zero, UsageTotal.Zero);
}

/// <summary>
/// Decides whether a run may start, given what has been spent and what the owner allowed (backlog 62).
/// Pure on purpose: the same decision is made in three places (starting a session, sending a turn, and
/// the turn boundary of a run already going), and they must not be able to disagree.
/// </summary>
public static class AgentSpendGuard
{
    /// <summary>
    /// The ceiling this spend has reached, or null when there is room. The DAY is checked first: when
    /// both are reached it is the one that will clear soonest, which is what the user most needs to know.
    /// <para>
    /// "Reached" is <c>&gt;=</c>, not <c>&gt;</c>: a limit of $10 with $10 spent is spent, and letting one
    /// more turn through would be the studio deciding the number meant something else.
    /// </para>
    /// </summary>
    public static AgentSpendRefusal? Refuse(SessionUsageTotals totals, AgentSpendLimits limits)
    {
        ArgumentNullException.ThrowIfNull(totals);
        ArgumentNullException.ThrowIfNull(limits);

        if (limits.PerDayUsd is { } day && totals.Today.CostUsd >= day)
        {
            return new AgentSpendRefusal(AgentSpendPeriod.Day, totals.Today.CostUsd, day);
        }

        if (limits.PerMonthUsd is { } month && totals.Month.CostUsd >= month)
        {
            return new AgentSpendRefusal(AgentSpendPeriod.Month, totals.Month.CostUsd, month);
        }

        return null;
    }
}
