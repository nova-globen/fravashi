namespace Fravashi.Domain.Insights;

/// <summary>
/// The shape of a set of measured durations (backlog 36): how many there were, the middle one, and
/// the worst one. Deliberately not a mean: one runaway session drags an average somewhere no session
/// actually was, and the home surface may only show figures that are true of real runs.
/// </summary>
/// <param name="Count">How many durations went into this, which is also whether it says anything at all.</param>
/// <param name="Median">The middle duration (the mean of the two middles when the count is even).</param>
/// <param name="Longest">The largest duration in the set.</param>
public sealed record DurationStats(int Count, TimeSpan Median, TimeSpan Longest)
{
    /// <summary>Nothing was measured. A card holding this says "nothing yet", never a zero.</summary>
    public static DurationStats None { get; } = new(0, TimeSpan.Zero, TimeSpan.Zero);

    /// <summary>True when there is at least one measured duration to show.</summary>
    public bool HasAny => Count > 0;

    /// <summary>
    /// Reduces measured durations to their median and longest. Negative durations are dropped rather
    /// than reported: a stamp pair that runs backwards is a clock artefact, not a run that took less
    /// than no time, and averaging one in would quietly move the median.
    /// </summary>
    public static DurationStats From(IEnumerable<TimeSpan> durations)
    {
        ArgumentNullException.ThrowIfNull(durations);

        var ordered = durations.Where(d => d >= TimeSpan.Zero).OrderBy(d => d).ToList();
        if (ordered.Count == 0)
        {
            return None;
        }

        var middle = ordered.Count / 2;
        var median = ordered.Count % 2 == 1
            ? ordered[middle]
            : new TimeSpan((ordered[middle - 1].Ticks + ordered[middle].Ticks) / 2);

        return new DurationStats(ordered.Count, median, ordered[^1]);
    }
}
