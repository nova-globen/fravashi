using System.Globalization;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Web.Components.Sessions;

/// <summary>
/// How the one-line usage strip writes its numbers (backlog 18 and 19). The strip replaced a four-line
/// stack, so every value has to survive a 280px panel: that is the whole reason token counts are
/// abbreviated and the cost keeps two decimals rather than three.
/// </summary>
public static class SessionUsageFormat
{
    /// <summary>
    /// A token count short enough for the strip: under a thousand as itself, then thousands with one
    /// decimal, then millions. Always invariant, because these are figures rather than prose.
    /// </summary>
    public static string Tokens(long tokens) => tokens switch
    {
        < 0 => "0",
        < 1_000 => tokens.ToString(CultureInfo.InvariantCulture),
        < 1_000_000 => (tokens / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        _ => (tokens / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "M",
    };

    /// <summary>Free-tier units (backlog 377), abbreviated like tokens: "13.7k units".</summary>
    public static string Units(long units) => Tokens(units) + (units == 1 ? " unit" : " units");

    /// <summary>What a free-tier figure means, for its tooltip.</summary>
    public static string FreeTierTitle(long units) =>
        $"{units.ToString("N0", CultureInfo.InvariantCulture)} free-tier units, as this studio counted them. The free tier's " +
        $"daily limit is {FreeTierUnits.DailyLimit.ToString("N0", CultureInfo.InvariantCulture)} units, counted by the credential " +
        "broker. These turns cost you nothing and never count against your spending limits.";

    /// <summary>
    /// The cost so far. Two decimals is the strip's width budget, and a run that has cost something but
    /// less than a cent reads as "&lt;$0.01" rather than as the "$0.00" that would say it was free.
    /// </summary>
    public static string Cost(decimal costUsd) => costUsd switch
    {
        <= 0m => "$0.00",
        < 0.01m => "<$0.01",
        _ => "$" + costUsd.ToString("0.00", CultureInfo.InvariantCulture),
    };

    /// <summary>A share of the context window as whole percent, rounded up so a live session never reads 0%.</summary>
    public static string Percent(double fraction) =>
        WholePercent(fraction).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>The meter's width, as a CSS length. Same rounding as the number beside it, so they agree.</summary>
    public static string MeterWidth(double fraction) =>
        WholePercent(fraction).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// The width of the meter's conversation segment when it is drawn behind a fixed-prefix segment: the
    /// difference of the two ROUNDED widths, so the pair always sums to exactly the single-segment width
    /// and the split never changes how full the meter reads.
    /// </summary>
    public static string ConversationWidth(double fraction, double baselineFraction) =>
        Math.Max(0, WholePercent(fraction) - WholePercent(baselineFraction))
            .ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// The context readout's tooltip: the raw figures, and WHY they are that large this early. The
    /// number is dominated by a fixed prefix the CLI sends with every request (its system prompt, tool
    /// schemas and the project's instructions), which the owner cannot see and did not type, so a bare
    /// total after two short turns reads as a wrong number. When the session's first reading is known the
    /// split is stated; when it is not (a transcript stored before it was recorded), the prefix is at
    /// least NAMED. Cached tokens are included on purpose and the tooltip says so: caching lowers cost,
    /// not what occupies the window.
    /// </summary>
    public static string ContextTitle(AgentStatsSnapshot stats)
    {
        var total = stats.ContextTokens.ToString("N0", CultureInfo.InvariantCulture);
        if (stats.ContextWindow <= 0)
        {
            return $"{total} tokens in context; the model's context window has not been reported yet";
        }

        var window = stats.ContextWindow.ToString("N0", CultureInfo.InvariantCulture);
        var split = stats.BaselineTokens > 0 && stats.BaselineTokens <= stats.ContextTokens
            ? $" ~{stats.BaselineTokens.ToString("N0", CultureInfo.InvariantCulture)} is a fixed prefix "
                + "(agent setup, tool schemas, project instructions, plus your first message) "
                + $"and ~{stats.ConversationTokens.ToString("N0", CultureInfo.InvariantCulture)} is the conversation since."
            : " Every turn carries a fixed prefix (agent setup, tool schemas, project instructions) on top of the conversation.";

        return $"{total} of {window} tokens in the model's context window.{split}"
            + " Cached tokens count: caching lowers cost, not context.";
    }

    private static int WholePercent(double fraction) => Math.Clamp((int)Math.Ceiling(fraction * 100), 0, 100);

    /// <summary>"1 tool" and "2 tools": a strip this short cannot afford to be wrong about a plural.</summary>
    public static string Tools(int calls) =>
        calls == 1 ? "1 tool" : calls.ToString(CultureInfo.InvariantCulture) + " tools";
}
