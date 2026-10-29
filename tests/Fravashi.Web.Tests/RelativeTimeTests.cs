using System.Text.RegularExpressions;
using Fravashi.Web.Services;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// The Code page and the Git page each wrote this function, each read the clock where it stood, and so
/// neither was ever tested. The two had already drifted into different wording with nobody deciding they
/// should. These are the tests that were not possible before now took an argument.
/// </summary>
public sealed class RelativeTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(30, "just now")]
    [InlineData(59, "just now")]
    [InlineData(60, "1 minutes ago")]
    [InlineData(60 * 59, "59 minutes ago")]
    [InlineData(60 * 60, "1 hours ago")]
    [InlineData(60 * 60 * 23, "23 hours ago")]
    [InlineData(60 * 60 * 24, "1 days ago")]
    [InlineData(60 * 60 * 24 * 29, "29 days ago")]
    public void The_long_spelling_counts_up_through_each_unit(int secondsAgo, string expected) =>
        Assert.Equal(expected, RelativeTime.Since(Now.AddSeconds(-secondsAgo), Now));

    [Theory]
    [InlineData(60, "1m ago")]
    [InlineData(60 * 60, "1h ago")]
    [InlineData(60 * 60 * 24, "1d ago")]
    public void The_brief_spelling_is_the_same_reading_in_fewer_characters(int secondsAgo, string expected) =>
        Assert.Equal(expected, RelativeTime.Since(Now.AddSeconds(-secondsAgo), Now, brief: true));

    [Fact]
    public void Past_a_month_it_stops_counting_and_gives_the_date()
    {
        // "412 days ago" is a number nobody converts, so it hands over to a date. The date is the PERSON's local
        // date (backlog 246), which is what somebody reading a file list wants, so this asserts the shape as well
        // as the value; with no zone known it is the UTC date and says so.
        var old = Now.AddDays(-400);
        var kolkata = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");

        var said = RelativeTime.Since(old, Now, zone: kolkata);

        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2}$"), said);
        Assert.Equal(TimeZoneInfo.ConvertTime(old, kolkata).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), said);
        Assert.Equal(said, RelativeTime.Since(old, Now, brief: true, zone: kolkata));
        Assert.Equal(old.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + " UTC", RelativeTime.Since(old, Now));
    }

    [Fact]
    public void A_moment_in_the_future_reads_as_just_now_rather_than_a_negative_age()
    {
        // Clocks disagree: a file stamped by another machine, or a commit made a second before this one's
        // clock caught up. "-1 hours ago" looks like a defect in the product rather than in a clock.
        Assert.Equal("just now", RelativeTime.Since(Now.AddHours(3), Now));
        Assert.Equal("just now", RelativeTime.Since(Now.AddDays(90), Now, brief: true));
    }

    [Fact]
    public void The_two_sides_are_compared_in_utc_whatever_offset_either_carries()
    {
        // The same instant written in two offsets is the same instant, and an hour of difference here would
        // be a whole unit of the answer.
        var sameInstantElsewhere = new DateTimeOffset(2026, 9, 22, 14, 0, 0, TimeSpan.FromHours(2));

        Assert.Equal("just now", RelativeTime.Since(sameInstantElsewhere, Now));
    }
}
