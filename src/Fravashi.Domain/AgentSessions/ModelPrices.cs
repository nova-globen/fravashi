namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// One model's first-party prices, in US dollars per million tokens (backlog 377). Cache writes are priced from the
/// input rate by their lifetime (1.25 times for five minutes, 2 times for one hour) and cache reads at their own rate.
/// A model with a long-context tier (Haiku 5.5) is priced at <see cref="LongContext"/> for a request whose prompt is
/// larger than <see cref="LongContextAbove"/> tokens.
/// </summary>
public sealed record ModelPrice(decimal Input, decimal Output, decimal CacheRead, long LongContextAbove = 0, ModelPrice? LongContext = null)
{
    private const decimal PerToken = 1_000_000m;

    /// <summary>What <paramref name="usage"/> costs at these prices. <paramref name="promptTokens"/> picks the tier.</summary>
    public decimal Cost(TokenUsage usage, long promptTokens)
    {
        if (LongContext is { } longer && LongContextAbove > 0 && promptTokens > LongContextAbove)
        {
            return longer.Cost(usage, 0);
        }

        return ((usage.Input * Input)
                + (usage.Output * Output)
                + (usage.CacheRead * CacheRead)
                + (usage.CacheWrite5m * Input * 1.25m)
                + (usage.CacheWrite1h * Input * 2m)) / PerToken;
    }
}

/// <summary>Tokens of one kind of each, as a turn or a request reports them.</summary>
public readonly record struct TokenUsage(long Input, long Output, long CacheRead, long CacheWrite5m, long CacheWrite1h)
{
    public long CacheWrite => CacheWrite5m + CacheWrite1h;

    public static TokenUsage operator +(TokenUsage a, TokenUsage b) =>
        new(a.Input + b.Input, a.Output + b.Output, a.CacheRead + b.CacheRead,
            a.CacheWrite5m + b.CacheWrite5m, a.CacheWrite1h + b.CacheWrite1h);
}

/// <summary>
/// The studio's own price table (backlog 377). The pinned agent CLI prices each turn itself, and the v1.0.0 drill
/// measured it charging a Haiku 5.5 turn about fifty times its price, because the CLI predates the model. So the studio
/// prices a turn from the tokens wherever it knows the model, and keeps the CLI's figure only for a model this table
/// does not list.
/// <para>
/// Source: Anthropic's pricing page, https://platform.claude.com/docs/en/about-claude/pricing, read 2026-10-31 (every
/// rate below, including each model's cache-read rate: 0.1 times input, except Fable 5.1 and Mythos 5.1 at 0.025 times,
/// Opus 5.5 and Sonnet 5.5 at 0.05 times). Haiku 5.5 prices a request by its own prompt length, cache reads and writes
/// included. Not priced here: the 1.1 times data-residency multiplier, fast mode and batch, none of which the studio's
/// CLI runs use. A model served by another provider (Bedrock, Vertex) may be priced differently there; the studio runs
/// the CLI against the first-party API or the credential broker, which carries it to the same.
/// </para>
/// </summary>
public static class ModelPrices
{
    private static readonly (string Model, ModelPrice Price)[] Table =
    [
        ("claude-fable-5-1", new(10m, 50m, 0.25m)),
        ("claude-mythos-5-1", new(10m, 50m, 0.25m)),
        ("claude-fable-5", new(10m, 50m, 1m)),
        ("claude-mythos-5", new(10m, 50m, 1m)),
        ("claude-opus-5-5", new(4m, 20m, 0.20m)),
        ("claude-opus-5", new(5m, 25m, 0.50m)),
        ("claude-opus-4-8", new(5m, 25m, 0.50m)),
        ("claude-opus-4-7", new(5m, 25m, 0.50m)),
        ("claude-opus-4-6", new(5m, 25m, 0.50m)),
        ("claude-opus-4-5", new(5m, 25m, 0.50m)),
        ("claude-opus-4-1", new(15m, 75m, 1.50m)),
        ("claude-opus-4", new(15m, 75m, 1.50m)),
        ("claude-sonnet-5-5", new(2m, 10m, 0.10m)),
        ("claude-sonnet-5", new(2m, 10m, 0.20m)),
        ("claude-sonnet-4-6", new(3m, 15m, 0.30m)),
        ("claude-sonnet-4-5", new(3m, 15m, 0.30m)),
        ("claude-sonnet-4", new(3m, 15m, 0.30m)),
        ("claude-haiku-5-5", new(0.10m, 0.50m, 0.01m, 100_000, new(0.50m, 2.50m, 0.05m))),
        ("claude-haiku-4-5", new(1m, 5m, 0.10m)),
    ];

    /// <summary>
    /// The prices for <paramref name="model"/>, or null when the table does not list it. A model id as the CLI reports
    /// it may carry a date (<c>claude-haiku-4-5-20251001</c>) or a context suffix (<c>claude-opus-5[1m]</c>).
    /// </summary>
    public static ModelPrice? For(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var id = model.Trim().ToLowerInvariant();
        var bracket = id.IndexOf('[', StringComparison.Ordinal);
        if (bracket >= 0)
        {
            id = id[..bracket];
        }

        foreach (var (listed, price) in Table)
        {
            // Only a date may follow a listed id: "claude-opus-5-5" must not be read as "claude-opus-5" plus "-5".
            if (id == listed
                || (id.Length == listed.Length + 9 && id.StartsWith(listed + "-", StringComparison.Ordinal)
                    && id[(listed.Length + 1)..].All(char.IsAsciiDigit)))
            {
                return price;
            }
        }

        return null;
    }
}

/// <summary>
/// The credential broker's free-tier units (infra D54): what a brokered turn uses of a person's daily allowance. The
/// weights are the broker's own defaults, which production uses (infra read them from its configuration on 2026-10-31):
/// input 1, cache write 1.25, cache read 0.1, output 5, all five times over for a prompt past 100,000 tokens. So the
/// studio's count is the figure the broker charges, not an estimate of a price nobody pays.
/// <para>
/// The studio shows what each conversation used and the daily limit, never a running "N of the limit": the broker's
/// counter is its own (in memory, reset when the service restarts), and no endpoint lets a person read it yet.
/// </para>
/// </summary>
public static class FreeTierUnits
{
    public const decimal Input = 1m;
    public const decimal CacheWrite = 1.25m;
    public const decimal CacheRead = 0.1m;
    public const decimal Output = 5m;
    public const long LongContextAbove = 100_000;
    public const decimal LongContextMultiplier = 5m;

    /// <summary>The free tier's daily limit per person, in these units (infra D54a; the broker's QuotaTokens).</summary>
    public const long DailyLimit = 1_000_000;

    /// <summary>The units for <paramref name="usage"/>; <paramref name="promptTokens"/> past 100,000 multiplies them by five.</summary>
    public static long Of(TokenUsage usage, long promptTokens = 0)
    {
        var units = (usage.Input * Input) + (usage.CacheWrite * CacheWrite)
                    + (usage.CacheRead * CacheRead) + (usage.Output * Output);
        return (long)Math.Ceiling(promptTokens > LongContextAbove ? units * LongContextMultiplier : units);
    }
}
