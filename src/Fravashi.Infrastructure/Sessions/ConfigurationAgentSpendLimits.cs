using System.Globalization;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Microsoft.Extensions.Configuration;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// The spend ceilings as the settings table holds them (backlog 62). Three optional rows, read fresh on
/// every call so that saving a limit in Settings &gt; Agents &gt; Spending and limits takes effect on the next
/// turn and not on the next restart.
/// </summary>
public sealed class ConfigurationAgentSpendLimits(IConfiguration configuration) : IAgentSpendLimitSource
{
    /// <summary>The per-invocation ceiling, handed to the CLI as <c>--max-budget-usd</c>.</summary>
    public const string PerSessionKey = "Sessions:SpendLimitPerSessionUsd";

    /// <summary>The studio's own ceiling over one UTC day, summed across sessions.</summary>
    public const string PerDayKey = "Sessions:SpendLimitPerDayUsd";

    /// <summary>The studio's own ceiling over one UTC calendar month, summed across sessions.</summary>
    public const string PerMonthKey = "Sessions:SpendLimitPerMonthUsd";

    private readonly IConfiguration _configuration =
        configuration ?? throw new ArgumentNullException(nameof(configuration));

    public AgentSpendLimits Current() => new(
        Read(_configuration[PerSessionKey]),
        Read(_configuration[PerDayKey]),
        Read(_configuration[PerMonthKey]));

    /// <summary>
    /// One stored field as a limit, or null for "no limit". Deliberately forgiving in one direction only:
    /// blank, unparseable and non-positive all mean NO ceiling, because the failure that matters here is a
    /// typo silently becoming a ceiling that stops every run. Parsed invariantly, so a stored "2.50" means
    /// two dollars fifty on a Swedish host as much as on an American one.
    /// <para>
    /// A GROUP separator is refused rather than ignored, and that is the case this host makes real: the
    /// owner's culture writes two dollars fifty as "2,50", and invariant parsing with thousands allowed
    /// reads that as <b>250</b>, a hundredfold ceiling nobody typed. Refusing it is what lets the settings
    /// form say so instead. The cost is that "1,000" is refused too, which is the safe direction.
    /// </para>
    /// </summary>
    public static decimal? Read(string? stored) =>
        decimal.TryParse(stored, Amount, CultureInfo.InvariantCulture, out var value) && value > 0m
            ? value
            : null;

    /// <summary>An amount and nothing else: a decimal point, and whatever whitespace a field carries.</summary>
    private const NumberStyles Amount =
        NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite;

    /// <summary>
    /// A limit as the settings row stores it, or an empty string for "no limit". The inverse of
    /// <see cref="Read"/>, invariant for the same reason, and the one place the settings form and any
    /// other writer agree on the shape.
    /// </summary>
    public static string Write(decimal? limit) =>
        limit is { } value && value > 0m ? value.ToString("0.####", CultureInfo.InvariantCulture) : string.Empty;
}
