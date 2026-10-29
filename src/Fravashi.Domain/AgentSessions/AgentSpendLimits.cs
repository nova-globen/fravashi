using System.Globalization;

namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// Which spend limit a refusal is about (backlog 62). The per-session ceiling is not in here because it
/// is not ours to enforce: it rides on the CLI's own <c>--max-budget-usd</c>, which stops the invocation
/// from inside. These two are the studio's, because no single invocation can see across sessions.
/// </summary>
public enum AgentSpendPeriod
{
    /// <summary>The current UTC day.</summary>
    Day,

    /// <summary>The current UTC calendar month.</summary>
    Month,
}

/// <summary>
/// The owner's spend ceilings for agent runs (backlog 62), in USD. Every one of them is optional and
/// null means "no limit", which is what an empty settings field stores: a limit nobody typed must never
/// become a limit that stops a run.
/// <para>
/// <see cref="PerSessionUsd"/> is handed to the CLI as <c>--max-budget-usd</c>, so it is enforced inside
/// the invocation it applies to. The day and month ceilings cannot be: they are sums across sessions,
/// which only the studio can see, so <see cref="AgentSpendGuard"/> enforces those.
/// </para>
/// </summary>
public sealed record AgentSpendLimits(decimal? PerSessionUsd, decimal? PerDayUsd, decimal? PerMonthUsd)
{
    /// <summary>No ceiling at all, which is what the studio ships with.</summary>
    public static AgentSpendLimits None { get; } = new(null, null, null);

    /// <summary>True when at least one ceiling is set, i.e. when there is anything to show or enforce.</summary>
    public bool Any => PerSessionUsd is not null || PerDayUsd is not null || PerMonthUsd is not null;
}

/// <summary>
/// A run the studio refused because spending reached a ceiling (backlog 62). It carries the numbers
/// rather than a pre-baked sentence so that the transcript, the composer's error line and a test can
/// all say the same thing without three copies of the wording.
/// </summary>
public sealed record AgentSpendRefusal(AgentSpendPeriod Period, decimal SpentUsd, decimal LimitUsd)
{
    /// <summary>
    /// What the user is told, naming BOTH the limit and the spend, because "you hit a limit" without
    /// either number is a message that sends someone hunting through settings to find out what happened.
    /// The UTC wording is deliberate: the day and month boundaries are UTC, so a late-evening run in a
    /// positive offset can be refused against "tomorrow's" spend and the user has to be able to work that
    /// out from the message alone.
    /// </summary>
    public string Message =>
        $"Agent spending has reached the {Word} limit: {Money(SpentUsd)} spent {When} against a "
        + $"{Money(LimitUsd)} limit. Change it in Settings > Agents > Spending and limits.";

    private string Word => Period == AgentSpendPeriod.Day ? "daily" : "monthly";

    private string When => Period == AgentSpendPeriod.Day ? "today (UTC)" : "this month (UTC)";

    /// <summary>
    /// A dollar figure in the message, always invariant: this host runs a Swedish culture, where the
    /// default rendering would put a comma where the user reads a decimal point.
    /// </summary>
    public static string Money(decimal amount) => "$" + amount.ToString("0.00", CultureInfo.InvariantCulture);
}
