using AwesomeAssertions;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;
using Xunit;

namespace Fravashi.Infrastructure.Tests.WorkItems;

/// <summary>
/// Requested 2026-10-22 (evidence on Done): a move into Done asks for evidence on each acceptance criterion,
/// or a written reason, through the real SQLite board.
/// </summary>
public sealed class DoneEvidenceTests : IDisposable
{
    private readonly LocalBoardFixture _fixture = new();

    private ITracker Board => _fixture.Board;

    public void Dispose() => _fixture.Dispose();

    private async Task<WorkItem> ReadyAsync(params string[] criteria) =>
        await _fixture.CreateAsync(new NewWorkItem
        {
            Level = WorkItemLevel.Task,
            Title = "Something to finish",
            Description = "Why it exists.",
            Status = WorkItemStatus.Ready,
            AcceptanceCriteria = [.. criteria.Select(c => new AcceptanceCriterion(string.Empty, c))],
        });

    private Task<TrackerResult<WorkItem>> ToAsync(WorkItem item, WorkItemStatus to, MutationContext? context = null) =>
        Board.TransitionAsync(
            item.Id,
            to,
            to switch
            {
                WorkItemStatus.Done => WorkItemResolution.Completed,
                WorkItemStatus.Cancelled => WorkItemResolution.WontDo,
                _ => null,
            },
            null,
            null,
            item.Version,
            context ?? LocalBoardFixture.User);

    private async Task<WorkItem> ProveAsync(WorkItem item, int criterion, EvidenceResult result = EvidenceResult.Pass)
    {
        var recorded = await Board.RecordEvidenceAsync(
            item.Id,
            new NewEvidence
            {
                Kind = EvidenceKind.TestRun,
                Result = result,
                Summary = "The suite is green.",
                Criterion = item.AcceptanceCriteria[criterion].Id,
            },
            LocalBoardFixture.User);
        recorded.Outcome.Should().Be(TrackerOutcome.Ok);
        return (await Board.GetAsync(item.Id))!;
    }

    [Fact]
    public async Task Done_is_refused_naming_each_criterion_without_evidence_and_writes_nothing()
    {
        var item = await ReadyAsync("The page loads.", "The export works.");
        item = await ProveAsync(item, 0);
        var before = _fixture.StorageSnapshot();

        var moved = await ToAsync(item, WorkItemStatus.Done);

        moved.Outcome.Should().Be(TrackerOutcome.Invalid);
        var refusal = moved.Errors.Should().ContainSingle(e => e.Code == TrackerCodes.DoneNeedsEvidence).Subject;
        refusal.Message.Should().Contain("'The export works.'").And.NotContain("'The page loads.'");
        refusal.Message.Should().Contain("reason");
        _fixture.StorageSnapshot().Should().Be(before, "a refusal writes nothing");
        (await Board.GetAsync(item.Id))!.Status.Should().Be(WorkItemStatus.Ready);
    }

    [Fact]
    public async Task Done_is_allowed_with_evidence_on_every_criterion()
    {
        var item = await ReadyAsync("The page loads.", "The export works.");
        item = await ProveAsync(item, 0);
        item = await ProveAsync(item, 1);

        var moved = await ToAsync(item, WorkItemStatus.Done);

        moved.Outcome.Should().Be(TrackerOutcome.Ok, string.Join("; ", moved.Errors.Select(e => e.Message)));
        moved.Value.Status.Should().Be(WorkItemStatus.Done);
        (await Board.EvidenceAsync(item.Id)).Should().NotContain(e => e.Kind == EvidenceKind.NoEvidenceReason);
    }

    [Fact]
    public async Task Done_is_allowed_with_a_reason_which_is_stored_on_the_item()
    {
        var item = await ReadyAsync("The page loads.", "The export works.");
        item = await ProveAsync(item, 0);

        var moved = await ToAsync(
            item,
            WorkItemStatus.Done,
            MutationContext.User with { Reason = "  The export was removed from scope on #12.  " });

        moved.Outcome.Should().Be(TrackerOutcome.Ok, string.Join("; ", moved.Errors.Select(e => e.Message)));
        moved.Value.Status.Should().Be(WorkItemStatus.Done);

        var reason = (await Board.EvidenceAsync(item.Id)).Should()
            .ContainSingle(e => e.Kind == EvidenceKind.NoEvidenceReason).Subject;
        reason.Summary.Should().Be("The export was removed from scope on #12.");
        reason.Criterion.Should().BeNull("a reason is tied to the item, never to a criterion");
        reason.Gate.Should().Be(DoneEvidence.Gate);
        reason.Actor!.Kind.Should().Be(WorkItemActorKind.User);
        reason.Details.Should().Contain(item.AcceptanceCriteria[1].Id).And.NotContain(item.AcceptanceCriteria[0].Id);
    }

    [Fact]
    public async Task A_blank_reason_is_no_reason()
    {
        var item = await ReadyAsync("The page loads.");

        var moved = await ToAsync(item, WorkItemStatus.Done, MutationContext.User with { Reason = "   " });

        moved.Errors.Should().ContainSingle(e => e.Code == TrackerCodes.DoneNeedsEvidence);
    }

    [Fact]
    public async Task A_reason_never_counts_as_evidence_for_a_later_move_to_done()
    {
        var item = await ReadyAsync("The page loads.");
        var done = await ToAsync(item, WorkItemStatus.Done, MutationContext.User with { Reason = "Proven by hand." });
        done.Outcome.Should().Be(TrackerOutcome.Ok);
        var reopened = await ToAsync(done.Value, WorkItemStatus.InProgress);
        reopened.Outcome.Should().Be(TrackerOutcome.Ok);

        var again = await ToAsync(reopened.Value, WorkItemStatus.Done);

        again.Errors.Should().ContainSingle(e => e.Code == TrackerCodes.DoneNeedsEvidence);
    }

    [Fact]
    public async Task An_item_with_no_criteria_moves_to_done_as_before()
    {
        var item = await ReadyAsync();

        var moved = await ToAsync(item, WorkItemStatus.Done);

        moved.Outcome.Should().Be(TrackerOutcome.Ok);
        (await Board.EvidenceAsync(item.Id)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(WorkItemStatus.Cancelled)]
    [InlineData(WorkItemStatus.InProgress)]
    [InlineData(WorkItemStatus.InReview)]
    [InlineData(WorkItemStatus.Backlog)]
    public async Task Every_other_status_is_unaffected(WorkItemStatus to)
    {
        var item = await ReadyAsync("The page loads.");

        var moved = await ToAsync(item, to);

        moved.Outcome.Should().Be(TrackerOutcome.Ok, string.Join("; ", moved.Errors.Select(e => e.Message)));
        moved.Value.Status.Should().Be(to);
    }

    [Fact]
    public async Task A_move_to_done_that_arrives_from_a_provider_is_not_refused()
    {
        var item = await ReadyAsync("The page loads.");

        var moved = await ToAsync(
            item,
            WorkItemStatus.Done,
            new MutationContext(WorkItemActor.System, "reconcile-1") { FromProvider = true });

        moved.Outcome.Should().Be(TrackerOutcome.Ok, string.Join("; ", moved.Errors.Select(e => e.Message)));
        (await Board.EvidenceAsync(item.Id)).Should().BeEmpty("the provider gave no reason and none is invented");
    }

    [Fact]
    public async Task A_system_move_that_is_not_a_providers_is_judged_like_any_other()
    {
        var item = await ReadyAsync("The page loads.");

        var moved = await ToAsync(item, WorkItemStatus.Done, new MutationContext(WorkItemActor.System, "autopilot-1"));

        moved.Errors.Should().ContainSingle(e => e.Code == TrackerCodes.DoneNeedsEvidence);
    }

    [Fact]
    public async Task An_item_already_done_is_untouched_by_a_done_to_done_change()
    {
        var item = await _fixture.CreateAsync(new NewWorkItem
        {
            Level = WorkItemLevel.Task,
            Title = "Finished before the rule",
            Description = "Why.",
            Status = WorkItemStatus.Done,
            Resolution = WorkItemResolution.Completed,
            AcceptanceCriteria = [new AcceptanceCriterion(string.Empty, "It works.")],
        });

        var moved = await ToAsync(item, WorkItemStatus.Done);

        moved.Outcome.Should().Be(TrackerOutcome.Ok);
    }
}
