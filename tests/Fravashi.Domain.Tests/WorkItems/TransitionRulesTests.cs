using AwesomeAssertions;
using Fravashi.Domain.WorkItems;
using Xunit;
using static Fravashi.Domain.Tests.WorkItems.WorkItemFixture;

namespace Fravashi.Domain.Tests.WorkItems;

/// <summary>Plan section 1.4: flags and resolution, and the transitions between statuses.</summary>
public class TransitionRulesTests
{
    private static readonly WorkItemFlags BothFlags = WorkItemFlags.None.WithBlocked("Waiting on the API").WithDeferred("Later");

    [Fact]
    public void Entering_done_sets_resolution_completed()
    {
        var item = Item(status: WorkItemStatus.InReview);

        var done = WorkItemRules.ApplyTransition(item, WorkItemStatus.Done, null, []).ShouldBeValid();

        done.Status.Should().Be(WorkItemStatus.Done);
        done.Resolution.Should().Be(WorkItemResolution.Completed);
        WorkItemRules.ApplyTransition(item, WorkItemStatus.Done, WorkItemResolution.Completed, []).ShouldBeValid()
            .Resolution.Should().Be(WorkItemResolution.Completed);
    }

    [Theory]
    [InlineData(WorkItemResolution.WontDo)]
    [InlineData(WorkItemResolution.Duplicate)]
    [InlineData(WorkItemResolution.CannotReproduce)]
    public void Done_with_any_other_resolution_is_refused(WorkItemResolution resolution)
    {
        WorkItemRules.ApplyTransition(Item(), WorkItemStatus.Done, resolution, [])
            .ShouldBeRefusedWith(WorkItemRules.Codes.DoneResolutionNotCompleted, "resolution");
    }

    [Fact]
    public void Cancelled_without_a_resolution_is_refused()
    {
        WorkItemRules.ApplyTransition(Item(), WorkItemStatus.Cancelled, null, [])
            .ShouldBeRefusedWith(WorkItemRules.Codes.CancelledResolutionRequired, "resolution");
    }

    [Fact]
    public void Cancelled_as_completed_is_refused()
    {
        WorkItemRules.ApplyTransition(Item(), WorkItemStatus.Cancelled, WorkItemResolution.Completed, [])
            .ShouldBeRefusedWith(WorkItemRules.Codes.CancelledResolutionInvalid, "resolution");
    }

    [Theory]
    [InlineData(WorkItemResolution.WontDo)]
    [InlineData(WorkItemResolution.CannotReproduce)]
    public void Cancelled_with_an_explicit_resolution_is_accepted(WorkItemResolution resolution)
    {
        WorkItemRules.ApplyTransition(Item(), WorkItemStatus.Cancelled, resolution, []).ShouldBeValid()
            .Resolution.Should().Be(resolution);
    }

    [Fact]
    public void Duplicate_without_a_duplicates_link_is_refused()
    {
        var item = Item();
        var other = Item();

        WorkItemRules.ApplyTransition(item, WorkItemStatus.Cancelled, WorkItemResolution.Duplicate, [])
            .ShouldBeRefusedWith(WorkItemRules.Codes.DuplicateLinkRequired, "resolution");

        // A duplicates link pointing INTO the item, or a relates link, does not justify it either.
        WorkItemRules.ApplyTransition(item, WorkItemStatus.Cancelled, WorkItemResolution.Duplicate,
                [Link(other, WorkItemLinkType.Duplicates, item), Link(item, WorkItemLinkType.Relates, other)])
            .ShouldBeRefusedWith(WorkItemRules.Codes.DuplicateLinkRequired);
    }

    [Fact]
    public void Duplicate_with_a_duplicates_link_from_the_item_is_accepted_in_either_reading()
    {
        var item = Item();
        var original = Item();

        WorkItemRules.ApplyTransition(item, WorkItemStatus.Cancelled, WorkItemResolution.Duplicate,
                [Link(original, WorkItemLinkType.DuplicatedBy, item)])
            .ShouldBeValid().Resolution.Should().Be(WorkItemResolution.Duplicate);
    }

    [Fact]
    public void Unlinking_the_last_duplicates_edge_of_a_duplicate_is_refused()
    {
        var original = Item();
        var other = Item();
        var duplicate = Item(status: WorkItemStatus.Cancelled, resolution: WorkItemResolution.Duplicate);
        var edge = Link(duplicate, WorkItemLinkType.Duplicates, original);

        WorkItemRules.ValidateUnlink(duplicate, edge, [edge])
            .ShouldBeRefusedWith(WorkItemRules.Codes.DuplicateLinkRequired, "link");

        var second = Link(duplicate, WorkItemLinkType.Duplicates, other);
        WorkItemRules.ValidateUnlink(duplicate, edge, [edge, second]).IsValid.Should().BeTrue();

        var notDuplicate = Item(status: WorkItemStatus.Cancelled, resolution: WorkItemResolution.WontDo);
        var plain = Link(notDuplicate, WorkItemLinkType.Duplicates, original);
        WorkItemRules.ValidateUnlink(notDuplicate, plain, [plain]).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(WorkItemStatus.Cancelled, WorkItemResolution.WontDo)]
    [InlineData(WorkItemStatus.Cancelled, WorkItemResolution.CannotReproduce)]
    public void A_terminal_to_terminal_move_clears_a_flag_the_item_still_carries(WorkItemStatus to, WorkItemResolution resolution)
    {
        // A mirrored Done item may still carry Blocked (rule 6); an AUTHORED transition clears it (rule 4).
        var done = Item(status: WorkItemStatus.Done, resolution: WorkItemResolution.Completed, flags: BothFlags);

        var cancelled = WorkItemRules.ApplyTransition(done, to, resolution, []).ShouldBeValid();

        cancelled.Flags.Should().Be(WorkItemFlags.None);
        cancelled.Flags.Blocked.Should().BeFalse();
    }

    [Fact]
    public void A_resolution_change_on_a_terminal_item_clears_a_flag_it_still_carries()
    {
        var cancelled = Item(status: WorkItemStatus.Cancelled, resolution: WorkItemResolution.WontDo, flags: BothFlags);

        WorkItemRules.ApplyTransition(cancelled, WorkItemStatus.Cancelled, WorkItemResolution.CannotReproduce, []).ShouldBeValid()
            .Flags.Should().Be(WorkItemFlags.None);
    }

    [Fact]
    public void A_mirrored_transition_keeps_the_flags_as_the_provider_said()
    {
        var done = Item(status: WorkItemStatus.Done, resolution: WorkItemResolution.Completed, flags: BothFlags);

        WorkItemRules.ApplyMirroredTransition(done, WorkItemStatus.Cancelled, null).ShouldBeValid()
            .Flags.Should().Be(BothFlags);
    }

    [Theory]
    [InlineData(WorkItemStatus.Done, null)]
    [InlineData(WorkItemStatus.Cancelled, WorkItemResolution.WontDo)]
    public void Entering_a_terminal_status_clears_both_flags(WorkItemStatus to, WorkItemResolution? resolution)
    {
        var flagged = Item(status: WorkItemStatus.InProgress, flags: BothFlags);

        var closed = WorkItemRules.ApplyTransition(flagged, to, resolution, []).ShouldBeValid();

        closed.Flags.Should().Be(WorkItemFlags.None);
    }

    [Theory]
    [InlineData(WorkItemStatus.Triage)]
    [InlineData(WorkItemStatus.Backlog)]
    [InlineData(WorkItemStatus.Ready)]
    [InlineData(WorkItemStatus.InProgress)]
    [InlineData(WorkItemStatus.InReview)]
    public void Reopening_clears_the_resolution(WorkItemStatus to)
    {
        var done = Item(status: WorkItemStatus.Done, resolution: WorkItemResolution.Completed);

        WorkItemRules.ApplyTransition(done, to, null, []).ShouldBeValid().Resolution.Should().BeNull();
    }

    [Fact]
    public void Reopening_clears_the_resolution_whatever_wrote_the_change()
    {
        var cancelled = Item(status: WorkItemStatus.Cancelled, resolution: WorkItemResolution.WontDo);

        var mirrored = WorkItemRules.ApplyMirroredTransition(cancelled, WorkItemStatus.Backlog, WorkItemResolution.WontDo).ShouldBeValid();

        mirrored.Resolution.Should().BeNull();
    }

    [Fact]
    public void Reopening_with_a_resolution_passed_is_refused_when_authored()
    {
        var done = Item(status: WorkItemStatus.Done, resolution: WorkItemResolution.Completed);

        WorkItemRules.ApplyTransition(done, WorkItemStatus.Ready, WorkItemResolution.Completed, [])
            .ShouldBeRefusedWith(WorkItemRules.Codes.ResolutionOnOpenItem, "resolution");
    }

    [Theory]
    [InlineData(WorkItemStatus.Done, WorkItemResolution.Completed)]
    [InlineData(WorkItemStatus.Cancelled, WorkItemResolution.WontDo)]
    public void Setting_a_flag_on_a_terminal_item_is_refused(WorkItemStatus status, WorkItemResolution resolution)
    {
        var closed = Item(status: status, resolution: resolution);

        WorkItemRules.ValidateFlags(closed, WorkItemFlags.None.WithBlocked("Because"))
            .ShouldBeRefusedWith(WorkItemRules.Codes.FlagOnTerminalItem, "blocked");
        WorkItemRules.ValidateFlags(closed, WorkItemFlags.None.WithDeferred(null))
            .ShouldBeRefusedWith(WorkItemRules.Codes.FlagOnTerminalItem, "deferred");
    }

    [Fact]
    public void A_blocked_flag_needs_a_reason_and_deferred_does_not()
    {
        var item = Item();

        WorkItemRules.ValidateFlags(item, WorkItemFlags.None.WithBlocked("  "))
            .ShouldBeRefusedWith(WorkItemRules.Codes.BlockedReasonRequired, "blockedReason");
        WorkItemRules.ValidateFlags(item, WorkItemFlags.None.WithBlocked(null))
            .ShouldBeRefusedWith(WorkItemRules.Codes.BlockedReasonRequired, "blockedReason");
        WorkItemRules.ValidateFlags(item, WorkItemFlags.None.WithBlocked("Waiting on the API")).ShouldBeValid()
            .Flags.BlockedReason.Should().Be("Waiting on the API");
        WorkItemRules.ValidateFlags(item, WorkItemFlags.None.WithDeferred(null)).ShouldBeValid().Flags.Deferred.Should().BeTrue();
    }

    [Fact]
    public void Clearing_a_flag_is_always_allowed_and_drops_its_reason()
    {
        var flagged = Item(flags: BothFlags);

        var cleared = WorkItemRules.ValidateFlags(flagged, flagged.Flags.WithoutBlocked().WithoutDeferred()).ShouldBeValid();

        cleared.Flags.Should().Be(WorkItemFlags.None);
        new WorkItemFlags(false, "stale", false, "stale").Should().Be(WorkItemFlags.None);
    }

    [Fact]
    public void A_flag_the_change_leaves_alone_is_not_re_judged()
    {
        // A mirrored ADO item: Blocked is a bare Yes/No, so no reason.
        var mirrored = Item(flags: new WorkItemFlags(true, null, false, null));

        WorkItemRules.ValidateFlags(mirrored, mirrored.Flags.WithDeferred("Parked")).ShouldBeValid();
    }

    public static TheoryData<WorkItemStatus, WorkItemStatus> OpenPairs()
    {
        var data = new TheoryData<WorkItemStatus, WorkItemStatus>();
        foreach (var from in WorkItemStatuses.All.Where(s => s.IsOpen()))
        {
            foreach (var to in WorkItemStatuses.All.Where(s => s.IsOpen()))
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(OpenPairs))]
    public void Every_open_to_open_move_is_allowed(WorkItemStatus from, WorkItemStatus to)
    {
        var item = Item(status: from, flags: BothFlags);

        var moved = WorkItemRules.ApplyTransition(item, to, null, []).ShouldBeValid();

        moved.Status.Should().Be(to);
        moved.Flags.Should().Be(BothFlags, "an open-to-open move leaves the flags alone");
    }

    [Fact]
    public void A_transition_to_the_current_status_changes_nothing()
    {
        var item = Item(status: WorkItemStatus.Ready);

        WorkItemRules.ApplyTransition(item, WorkItemStatus.Ready, null, []).ShouldBeValid().Should().BeSameAs(item);
    }

    [Fact]
    public void Closed_to_closed_changes_the_resolution_under_the_same_rules()
    {
        var done = Item(status: WorkItemStatus.Done, resolution: WorkItemResolution.Completed);

        WorkItemRules.ApplyTransition(done, WorkItemStatus.Cancelled, WorkItemResolution.WontDo, []).ShouldBeValid()
            .Resolution.Should().Be(WorkItemResolution.WontDo);
        WorkItemRules.ApplyTransition(done, WorkItemStatus.Cancelled, null, [])
            .ShouldBeRefusedWith(WorkItemRules.Codes.CancelledResolutionRequired);
    }

    [Fact]
    public void A_create_straight_into_a_terminal_status_follows_the_same_rules()
    {
        WorkItemRules.ValidateNew(State(status: WorkItemStatus.Done)).ShouldBeValid().Resolution.Should().Be(WorkItemResolution.Completed);
        WorkItemRules.ValidateNew(State(status: WorkItemStatus.Cancelled)).ShouldBeRefusedWith(WorkItemRules.Codes.CancelledResolutionRequired);
        WorkItemRules.ValidateNew(State(status: WorkItemStatus.Cancelled, resolution: WorkItemResolution.Duplicate))
            .ShouldBeRefusedWith(WorkItemRules.Codes.DuplicateLinkRequired);
        WorkItemRules.ValidateNew(State(status: WorkItemStatus.Done, flags: WorkItemFlags.None.WithBlocked("x")))
            .ShouldBeRefusedWith(WorkItemRules.Codes.FlagOnTerminalItem);
        WorkItemRules.ValidateNew(State(flags: WorkItemFlags.None.WithBlocked(null)))
            .ShouldBeRefusedWith(WorkItemRules.Codes.BlockedReasonRequired);
    }

    [Fact]
    public void A_resolution_on_an_open_item_cannot_be_restored()
    {
        WorkItem.Restore(State(status: WorkItemStatus.Ready, resolution: WorkItemResolution.WontDo))
            .ShouldBeRefusedWith(WorkItemRules.Codes.ResolutionOnOpenItem, "resolution");
    }
}
