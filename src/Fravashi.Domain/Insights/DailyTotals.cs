namespace Fravashi.Domain.Insights;

/// <summary>
/// One UTC day of agent activity (backlog 36): what the sessions STARTED that day cost, and how many
/// there were. A session is counted on the day it started, never spread across the days it ran
/// through, for the same reason <c>ISessionStore.ReadUsageTotals</c> counts it that way: the
/// per-session usage summary is a single cumulative figure with no time inside it, so there is
/// nothing honest to spread.
/// </summary>
/// <param name="Day">The UTC day.</param>
/// <param name="CostUsd">The summed cost of the sessions started that day.</param>
/// <param name="SessionsStarted">How many sessions started that day.</param>
public sealed record SessionDay(DateOnly Day, decimal CostUsd, int SessionsStarted);

/// <summary>How many of something happened on one UTC day (backlog 36).</summary>
/// <param name="Day">The UTC day.</param>
/// <param name="Count">How many.</param>
public sealed record DayCount(DateOnly Day, int Count);
