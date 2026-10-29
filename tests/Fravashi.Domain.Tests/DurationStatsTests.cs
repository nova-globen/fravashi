using AwesomeAssertions;
using Fravashi.Domain.Insights;
using Xunit;

namespace Fravashi.Domain.Tests;

/// <summary>
/// Backlog 36: the middle and the worst of a set of measured durations. The home surface's "where time
/// goes" card is built on this, and a median that quietly became a mean would put a figure in front of
/// the owner that no run of theirs ever took.
/// </summary>
public class DurationStatsTests
{
    [Fact]
    public void An_empty_set_says_nothing_rather_than_zero()
    {
        var stats = DurationStats.From([]);

        stats.Should().Be(DurationStats.None);
        stats.HasAny.Should().BeFalse();
        stats.Count.Should().Be(0);
    }

    [Fact]
    public void An_odd_set_takes_the_middle_value()
    {
        var stats = DurationStats.From(
            [TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(2), TimeSpan.FromHours(4)]);

        stats.Count.Should().Be(3);
        stats.Median.Should().Be(TimeSpan.FromMinutes(30), "input order must not matter");
        stats.Longest.Should().Be(TimeSpan.FromHours(4));
    }

    [Fact]
    public void An_even_set_takes_the_mean_of_the_two_middles()
    {
        var stats = DurationStats.From(
            [TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(60)]);

        stats.Median.Should().Be(TimeSpan.FromMinutes(15));
        stats.Longest.Should().Be(TimeSpan.FromMinutes(60));
    }

    [Fact]
    public void The_median_is_not_a_mean()
    {
        // One runaway run drags an average somewhere nothing actually was; the median must not move.
        var stats = DurationStats.From(
            [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromHours(40)]);

        stats.Median.Should().Be(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void A_negative_duration_is_dropped_rather_than_counted()
    {
        // A stamp pair that runs backwards is a clock artefact, not a run that took less than no time.
        var stats = DurationStats.From(
            [TimeSpan.FromMinutes(-5), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(6)]);

        stats.Count.Should().Be(2);
        stats.Median.Should().Be(TimeSpan.FromMinutes(5));
    }
}
