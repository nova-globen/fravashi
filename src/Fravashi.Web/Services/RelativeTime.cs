namespace Fravashi.Web.Services;

/// <summary>
/// How long ago something happened, in words. Pure, and it takes NOW as an argument.
/// <para>
/// It was the same function written twice, in the Code page and the Git page, each reading
/// <c>DateTimeOffset.UtcNow</c> where it stood. Two costs came with that. Relative time computed from an
/// ambient clock cannot be tested, so neither copy ever was, and the two had already drifted into different
/// wording without anybody deciding they should differ. And a surface that reads the clock directly is a
/// surface no fixed clock can make deterministic, which is the whole of backlog 71's second promise
/// (see <c>docs/plans/demo-mode.md</c>: these two were the only such readers on a surface a demo shows).
/// </para>
/// <para>
/// The drift is kept rather than flattened, because it was a real difference in voice: the Code page has
/// room for "3 hours ago" beside a file name, and the Git page puts a time in a dense row where "3h ago"
/// belongs. It is a parameter now instead of a fork.
/// </para>
/// </summary>
public static class RelativeTime
{
    /// <summary>
    /// How long before <paramref name="now"/> the moment <paramref name="when"/> was. Beyond a month it
    /// stops counting and gives the date, because "412 days ago" is a number nobody converts.
    /// </summary>
    /// <param name="when">The moment in question.</param>
    /// <param name="now">What time it is. Passed in, never read, so this can be tested and fixed.</param>
    /// <param name="brief">True for the dense spelling ("3h ago") used where a row is tight.</param>
    /// <param name="zone">
    /// The person's zone for the date given past a month (backlog 246); null when it is not known, and the date then
    /// says UTC.
    /// </param>
    public static string Since(DateTimeOffset when, DateTimeOffset now, bool brief = false, TimeZoneInfo? zone = null)
    {
        var span = now.ToUniversalTime() - when.ToUniversalTime();

        // A moment in the FUTURE reads as just now rather than as a negative age. Clocks disagree: a file
        // stamped by another machine, or a commit made a second before this one's clock caught up, would
        // otherwise render "-1 hours ago", which looks like a defect in the product rather than in a clock.
        if (span.TotalMinutes < 1)
        {
            return "just now";
        }

        return span switch
        {
            { TotalHours: < 1 } => brief ? $"{(int)span.TotalMinutes}m ago" : $"{(int)span.TotalMinutes} minutes ago",
            { TotalDays: < 1 } => brief ? $"{(int)span.TotalHours}h ago" : $"{(int)span.TotalHours} hours ago",
            { TotalDays: < 30 } => brief ? $"{(int)span.TotalDays}d ago" : $"{(int)span.TotalDays} days ago",
            _ => PersonTime.Format(when, zone, now, LocalTimeStyle.Date),
        };
    }
}
