namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// Live counters for one session. The CLI reports usage and cost PER TURN (its <c>result</c> line), so
/// these accumulate across the turns of a conversation rather than being overwritten. Negative deltas
/// are ignored instead of thrown: a malformed line must never take a running session down.
/// </summary>
public sealed class AgentRunStats
{
    private long _inputTokens;
    private long _outputTokens;
    private long _cacheReadTokens;
    private long _cacheWriteTokens;
    private long _freeTierUnits;
    private decimal _costUsd;
    private int _toolCalls;
    private int _rateLimitHits;
    private int _turns;
    private long _contextTokens;
    private long _contextWindow;
    private long _baselineTokens;

    /// <summary>
    /// Fold one turn's reported usage into the totals. A turn the credential broker carried costs nothing and reports
    /// the broker's <paramref name="freeTierUnits"/> instead (backlog 377).
    /// </summary>
    public void AddTurn(
        long inputTokens, long outputTokens, long cacheReadTokens, decimal costUsd, long cacheWriteTokens = 0, long freeTierUnits = 0)
    {
        _inputTokens += Math.Max(0, inputTokens);
        _outputTokens += Math.Max(0, outputTokens);
        _cacheReadTokens += Math.Max(0, cacheReadTokens);
        _cacheWriteTokens += Math.Max(0, cacheWriteTokens);
        _freeTierUnits += Math.Max(0, freeTierUnits);
        _costUsd += costUsd > 0 ? costUsd : 0m;
        _turns++;
    }

    /// <summary>
    /// How much of the model's context the conversation occupies right now: the input side of the most
    /// recent request, which is the whole conversation as it was actually sent.
    /// <para>
    /// This SETS rather than accumulates, and that is the difference between a level and a flow. The
    /// token totals above are flows and add up; context is a level that rises as the conversation grows
    /// and falls when the CLI compacts it, so the newest reading replaces the previous one.
    /// </para>
    /// </summary>
    public void ObserveContext(long contextTokens)
    {
        if (contextTokens > 0)
        {
            // The FIRST reading is remembered separately as the session's baseline: it is dominated by
            // what rides in every request before anyone types (system prompt, tool schemas, project
            // instructions) plus the first user message. It is what lets a surface explain why a young
            // conversation already occupies a large share of the window.
            if (_baselineTokens == 0)
            {
                _baselineTokens = contextTokens;
            }

            _contextTokens = contextTokens;
        }
    }

    public void IncrementToolCalls() => _toolCalls++;

    public void IncrementRateLimitHits() => _rateLimitHits++;

    /// <summary>The window that context is measured against, as the provider reports it for the model.</summary>
    public void ObserveContextWindow(long contextWindow)
    {
        if (contextWindow > 0)
        {
            _contextWindow = contextWindow;
        }
    }

    public AgentStatsSnapshot Snapshot() =>
        new(
            _inputTokens,
            _outputTokens,
            _cacheReadTokens,
            _costUsd,
            _toolCalls,
            _rateLimitHits,
            _turns,
            _contextTokens,
            _contextWindow,
            _baselineTokens,
            _cacheWriteTokens,
            _freeTierUnits);
}

/// <summary>An immutable point-in-time copy of <see cref="AgentRunStats"/> for streaming to the UI.</summary>
/// <param name="ContextTokens">
/// The input side of the most recent request: what the conversation currently occupies of the model's
/// context. Zero until a request has been reported.
/// </param>
/// <param name="ContextWindow">
/// The model's context window as the provider reports it, or zero while it is unknown. It arrives only
/// with a finished turn, so a first turn can honestly know the occupancy and not yet the window; a
/// surface must show the tokens and no percentage in that case rather than inventing a denominator.
/// </param>
/// <param name="BaselineTokens">
/// The session's FIRST context reading, or zero while none has arrived (and in rows persisted before it
/// was recorded). It is what every request carries before the conversation contributes anything: the
/// agent's system prompt, its tool schemas and the project's instructions, plus the one thing it cannot
/// exclude, the first user message. A surface uses it to split "fixed prefix" from "conversation" so a
/// large percentage after two short turns can explain itself.
/// </param>
/// <param name="CacheWriteTokens">
/// Tokens written to the prompt cache (backlog 377). Billed at more than an input token, and most of a first turn's
/// cost; zero in rows persisted before it was recorded.
/// </param>
/// <param name="FreeTierUnits">
/// What the credential broker's free tier counted for the turns it carried, in its own units (infra D54). Those turns
/// cost the person nothing, so <see cref="CostUsd"/> leaves them out.
/// </param>
public sealed record AgentStatsSnapshot(
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    decimal CostUsd,
    int ToolCalls,
    int RateLimitHits,
    int Turns,
    long ContextTokens = 0,
    long ContextWindow = 0,
    long BaselineTokens = 0,
    long CacheWriteTokens = 0,
    long FreeTierUnits = 0)
{
    public static AgentStatsSnapshot Empty { get; } = new(0, 0, 0, 0m, 0, 0, 0);

    /// <summary>Billed tokens moved in and out; cache reads are counted separately because they are not.</summary>
    public long TotalTokens => InputTokens + OutputTokens;

    /// <summary>
    /// How full the context is, 0 to 1, or null when either half of the fraction is unknown. Capped at 1:
    /// a reading above the window is a provider accounting detail, never a meter drawn past its end.
    /// </summary>
    public double? ContextFraction =>
        ContextTokens > 0 && ContextWindow > 0
            ? Math.Min(1d, (double)ContextTokens / ContextWindow)
            : null;

    /// <summary>
    /// What the conversation itself has added on top of the fixed prefix, or zero when the baseline is
    /// unknown. Clamped at zero because a compaction can bring the level BELOW the first reading, and a
    /// negative token count is not a thing a surface should ever be handed.
    /// </summary>
    public long ConversationTokens =>
        BaselineTokens > 0 ? Math.Max(0, ContextTokens - BaselineTokens) : 0;

    /// <summary>
    /// The fixed prefix as a share of the window, 0 to 1, or null when it is unknown or the split would
    /// not be honest: after a compaction the level can sit below the first reading, and drawing the
    /// baseline PAST the fill would claim more fixed prefix than context. Capped at the whole fraction
    /// for the same reason.
    /// </summary>
    public double? BaselineFraction =>
        BaselineTokens > 0 && ContextFraction is { } whole
            ? Math.Min(whole, (double)BaselineTokens / ContextWindow)
            : null;
}
