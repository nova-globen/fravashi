using AwesomeAssertions;
using Fravashi.Domain.Mirroring;
using Fravashi.Domain.WorkItems;
using Xunit;

namespace Fravashi.Domain.Tests.Mirroring;

/// <summary>
/// ADR 0018 decision 8: reads are served from a local materialised model WITH A VISIBLE FRESHNESS
/// INDICATOR. These hold the two things that make such an indicator worth having: it tells the truth about
/// what the reader is looking at, and when several things are true at once it says the one that matters.
/// </summary>
public class MirrorFreshnessTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);

    private static MirrorState State(
        DateTimeOffset? confirmed = null,
        int pending = 0,
        int stuck = 0,
        int conflicts = 0,
        DateTimeOffset? tombstoned = null,
        string? reason = null) =>
        new(
            new WorkItemId("item-1"),
            "jira",
            "PROJ-12",
            confirmed,
            "etag-1",
            LocalVersionAtSync: 3,
            PendingWrites: pending,
            StuckWrites: stuck,
            ConflictCount: conflicts,
            TombstonedUtc: tombstoned,
            TombstoneReason: reason);

    [Fact]
    public void A_board_with_no_provider_is_not_a_stale_copy_of_anything()
    {
        // The local board is the source of truth when nothing is configured (ADR 0018 decision 1). Showing
        // it as "never synced" would be a caveat over a product that is working exactly as designed.
        var freshness = MirrorFreshness.Describe(null, Now);

        freshness.Level.Should().Be(MirrorLevel.LocalOnly);
        freshness.NeedsCaveat.Should().BeFalse();
    }

    [Fact]
    public void A_recent_confirmation_is_fresh_and_carries_no_caveat()
    {
        var freshness = MirrorFreshness.Describe(State(confirmed: Now.AddMinutes(-2)), Now);

        freshness.Level.Should().Be(MirrorLevel.Fresh);
        freshness.NeedsCaveat.Should().BeFalse();
        freshness.Age.Should().Be(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void An_old_confirmation_says_so_rather_than_claiming_currency()
    {
        var freshness = MirrorFreshness.Describe(State(confirmed: Now.AddHours(-3)), Now);

        freshness.Level.Should().Be(MirrorLevel.Stale);
        freshness.Sentence.Should().Contain("3 hours ago");
    }

    [Fact]
    public void An_item_the_provider_has_never_confirmed_is_not_merely_old()
    {
        // Collapsing this into Stale would let an item that was never mirrored read as one that was mirrored
        // a while ago, which is a different and much more reassuring claim.
        MirrorFreshness.Describe(State(confirmed: null), Now).Level.Should().Be(MirrorLevel.NeverConfirmed);
    }

    [Fact]
    public void A_queued_write_is_what_the_reader_is_told_about()
    {
        var freshness = MirrorFreshness.Describe(State(confirmed: Now.AddHours(-5), pending: 2), Now);

        freshness.Level.Should().Be(MirrorLevel.Queued);
        freshness.Sentence.Should().Contain("2 changes are waiting");
    }

    [Fact]
    public void A_conflict_outranks_a_queued_write_because_it_is_the_one_that_waits_for_a_person()
    {
        var freshness = MirrorFreshness.Describe(State(confirmed: Now, pending: 4, conflicts: 1), Now);

        freshness.Level.Should().Be(MirrorLevel.Conflicted);
        freshness.Sentence.Should().Contain("Choose which one holds");
    }

    [Fact]
    public void A_write_that_will_not_go_outranks_a_conflict_because_the_provider_may_already_have_moved()
    {
        // A conflict has both values safely in hand. An unresolved write may already have changed the
        // provider in a way nothing here recorded, which is the worse thing to leave unsaid.
        var freshness = MirrorFreshness.Describe(State(confirmed: Now, pending: 1, stuck: 1, conflicts: 2), Now);

        freshness.Level.Should().Be(MirrorLevel.WriteStuck);
        freshness.Sentence.Should().Contain("will not retry on its own");
    }

    [Fact]
    public void A_tombstone_outranks_everything_else_that_is_true_about_the_item()
    {
        // Saying "3 changes are waiting to reach jira" about an item jira no longer has would be a smaller
        // truth told over a much larger one.
        var freshness = MirrorFreshness.Describe(
            State(confirmed: Now, pending: 3, stuck: 1, conflicts: 2, tombstoned: Now.AddMinutes(-1), reason: "It was deleted there on the 16th."),
            Now);

        freshness.Level.Should().Be(MirrorLevel.Tombstoned);
        freshness.Sentence.Should().Contain("no longer has PROJ-12").And.Contain("deleted there on the 16th");
    }

    [Fact]
    public void A_tombstone_with_no_recorded_reason_still_reads_as_a_sentence()
    {
        MirrorFreshness.Describe(State(tombstoned: Now), Now)
            .Sentence.Should().Contain("No reason was recorded");
    }

    [Fact]
    public void A_clock_that_moved_backwards_does_not_produce_an_age_in_the_future()
    {
        // Host clocks do move. An indicator that reads "confirmed in 4 minutes" is one nobody trusts again.
        var freshness = MirrorFreshness.Describe(State(confirmed: Now.AddMinutes(4)), Now);

        freshness.Age.Should().Be(TimeSpan.Zero);
        freshness.Level.Should().Be(MirrorLevel.Fresh);
    }

    [Theory]
    [InlineData(30, "just now")]
    [InlineData(60 * 25, "25 minutes ago")]
    [InlineData(60 * 60, "an hour ago")]
    [InlineData(60 * 60 * 24, "a day ago")]
    [InlineData(60 * 60 * 24 * 9, "9 days ago")]
    public void The_age_is_said_the_way_a_person_would_say_it(int secondsAgo, string expected)
    {
        MirrorFreshness.Describe(State(confirmed: Now.AddSeconds(-secondsAgo)), Now, TimeSpan.Zero)
            .Sentence.Should().Contain(expected);
    }
}
