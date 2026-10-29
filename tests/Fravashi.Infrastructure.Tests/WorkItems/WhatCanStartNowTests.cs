using AwesomeAssertions;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;
using Xunit;

namespace Fravashi.Infrastructure.Tests.WorkItems;

/// <summary>
/// Increment C2: evidence and "what can start now" through the real SQLite store. The query is the one an
/// agent asks before picking work up and the one item 38's board reads, so these run it over a seeded project
/// that covers every rule at once rather than one rule per fixture.
/// </summary>
public sealed class WhatCanStartNowTests : IDisposable
{
    private readonly LocalBoardFixture _fixture = new();

    private ITracker Board => _fixture.Board;

    public void Dispose() => _fixture.Dispose();

    private async Task<WorkItem> ReadyAsync(string title) =>
        await _fixture.CreateAsync(new NewWorkItem
        {
            Level = WorkItemLevel.Increment,
            Title = title,
            Description = "Why it exists.",
            Status = WorkItemStatus.Ready,
            AcceptanceCriteria = [new AcceptanceCriterion(string.Empty, "It works.")],
        });

    private async Task<WorkItem> BlockAsync(WorkItem blocked, WorkItem blocker)
    {
        var linked = await Board.LinkAsync(blocker.Id, WorkItemLinkType.Blocks, blocked.Id, LocalBoardFixture.User);
        linked.Outcome.Should().Be(TrackerOutcome.Ok, string.Join("; ", linked.Errors.Select(e => e.Code)));
        return (await Board.GetAsync(blocked.Id))!;
    }

    private static IEnumerable<string> Titles(IReadOnlyList<StartableItem> entries) =>
        entries.Select(e => e.Item.Title);

    [Fact]
    public async Task The_answer_is_every_ready_item_whose_blockers_are_done_in_board_order()
    {
        var free = await ReadyAsync("Free to start");
        var alsoFree = await ReadyAsync("Also free");
        var backlog = await _fixture.CreateAsync(new NewWorkItem
        {
            Level = WorkItemLevel.Increment,
            Title = "Not ready yet",
            Description = "Why.",
            Status = WorkItemStatus.Backlog,
        });
        var doneBlocker = await _fixture.CreateAsync(new NewWorkItem
        {
            Level = WorkItemLevel.Increment,
            Title = "Finished dependency",
            Description = "Why.",
            Status = WorkItemStatus.Done,
            Resolution = WorkItemResolution.Completed,
        });
        var unblocked = await ReadyAsync("Its blocker is done");
        await BlockAsync(unblocked, doneBlocker);

        var openBlocker = await ReadyAsync("An open dependency");
        var held = await ReadyAsync("Waiting on a dependency");
        await BlockAsync(held, openBlocker);

        var report = await Board.WhatCanStartNowAsync(new StartableQuery());

        Titles(report.Startable).Should().BeEquivalentTo(
            ["Free to start", "Also free", "Its blocker is done", "An open dependency"],
            "every Ready item whose blockers are all Done qualifies, the dependency itself included");
        Titles(report.Held).Should().Contain(["Not ready yet", "Finished dependency", "Waiting on a dependency"]);

        report.Startable.Select(e => e.Item.Rank).Should().BeInAscendingOrder("the answer is in board order");
        free.Should().NotBeNull();
        alsoFree.Should().NotBeNull();
        backlog.Should().NotBeNull();
    }

    [Fact]
    public async Task Every_held_item_carries_every_reason_it_is_held()
    {
        var blocker = await ReadyAsync("An open dependency");
        var held = await _fixture.CreateAsync(new NewWorkItem
        {
            Level = WorkItemLevel.Increment,
            Title = "Held for several reasons",
            Description = "Why.",
            Status = WorkItemStatus.Backlog,
            Flags = new WorkItemFlags(true, "the design is not approved", false, null),
            PersonBlockers =
            [
                new PersonBlocker(string.Empty, "owner", "the visual spec", DateTimeOffset.UnixEpoch, null),
            ],
        });
        await BlockAsync(held, blocker);

        var report = await Board.WhatCanStartNowAsync(new StartableQuery());

        var verdict = report.Held.Single(e => e.Item.Title == "Held for several reasons").Verdict;
        verdict.Reasons.Should().BeEquivalentTo(
            [StartBlocker.NotReady, StartBlocker.BlockedFlag, StartBlocker.OpenBlocker, StartBlocker.PersonBlocker]);
        verdict.OpenBlockers.Should().ContainSingle().Which.Should().Be(blocker.Id);
        verdict.WaitingOnPeople.Should().ContainSingle().Which.Should().Be("owner");
        verdict.EffectivelyBlocked.Should().BeTrue();
    }

    [Fact]
    public async Task Closing_the_last_open_blocker_makes_its_dependant_startable()
    {
        var blocker = await ReadyAsync("The dependency");
        var dependant = await ReadyAsync("The dependant");
        await BlockAsync(dependant, blocker);

        Titles((await Board.WhatCanStartNowAsync(new StartableQuery())).Startable)
            .Should().NotContain("The dependant");

        var closed = await Board.TransitionAsync(
            blocker.Id, WorkItemStatus.Done, WorkItemResolution.Completed, null, null,
            (await Board.GetAsync(blocker.Id))!.Version,

            // The item has a criterion and no evidence, so Done asks for a reason (requested 2026-10-22,
            // evidence on Done); what is under test here is only that Done releases the dependant.
            LocalBoardFixture.User with { Reason = "Not what this test is about." });
        closed.Outcome.Should().Be(TrackerOutcome.Ok, string.Join("; ", closed.Errors.Select(e => e.Code)));

        Titles((await Board.WhatCanStartNowAsync(new StartableQuery())).Startable)
            .Should().Contain("The dependant", "its only blocker is Done now");
    }

    [Fact]
    public async Task A_parents_blocker_is_not_inherited_by_its_child()
    {
        // Decision D11: a child is its own unit of work, and inheriting would make every child of a blocked
        // feature unstartable even when nothing is in the child's own way.
        var blocker = await ReadyAsync("Blocks the feature");
        var feature = await _fixture.CreateAsync(new NewWorkItem
        {
            Level = WorkItemLevel.Feature,
            Title = "The feature",
            Description = "Why.",
            Status = WorkItemStatus.Ready,
        });
        await BlockAsync(feature, blocker);
        var child = await _fixture.CreateAsync(new NewWorkItem
        {
            Level = WorkItemLevel.Increment,
            Title = "A child of the feature",
            Description = "Why.",
            Status = WorkItemStatus.Ready,
            Parent = feature.Id,
        });

        var report = await Board.WhatCanStartNowAsync(new StartableQuery());

        Titles(report.Startable).Should().Contain("A child of the feature");
        Titles(report.Held).Should().Contain("The feature");
        child.Should().NotBeNull();
    }

    [Fact]
    public async Task The_query_narrows_by_parent_and_by_assignee_without_changing_the_rules()
    {
        var feature = await _fixture.CreateAsync(new NewWorkItem
        {
            Level = WorkItemLevel.Feature,
            Title = "A feature",
            Description = "Why.",
            Status = WorkItemStatus.Ready,
        });
        await _fixture.CreateAsync(new NewWorkItem
        {
            Level = WorkItemLevel.Increment,
            Title = "Mine, under the feature",
            Description = "Why.",
            Status = WorkItemStatus.Ready,
            Parent = feature.Id,
            Assignee = "owner",
        });
        await _fixture.CreateAsync(new NewWorkItem
        {
            Level = WorkItemLevel.Increment,
            Title = "Someone else's, under the feature",
            Description = "Why.",
            Status = WorkItemStatus.Ready,
            Parent = feature.Id,
            Assignee = "someone else",
        });
        await ReadyAsync("Elsewhere entirely");

        var underFeature = await Board.WhatCanStartNowAsync(new StartableQuery(Parent: feature.Id));
        var mine = await Board.WhatCanStartNowAsync(new StartableQuery(Assignee: "owner"));

        Titles(underFeature.Startable).Should().BeEquivalentTo(
            ["Mine, under the feature", "Someone else's, under the feature"]);
        Titles(mine.Startable).Should().BeEquivalentTo(["Mine, under the feature"]);
    }

    [Fact]
    public async Task Evidence_is_appended_attributed_and_read_back_in_order()
    {
        var item = await ReadyAsync("Something to prove");

        var first = await Board.RecordEvidenceAsync(
            item.Id,
            new NewEvidence
            {
                Kind = EvidenceKind.TestRun,
                Result = EvidenceResult.Pass,
                Summary = "The suite is green.",
                Gate = "verify",
                Criterion = item.AcceptanceCriteria[0].Id,
            },
            LocalBoardFixture.User);
        _fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        var second = await Board.RecordEvidenceAsync(
            item.Id,
            new NewEvidence
            {
                Kind = EvidenceKind.Screenshot,
                Result = EvidenceResult.Partial,
                Summary = "The window shows it, the dark theme is off.",
            },
            new MutationContext(WorkItemActor.Session("session-7")));

        first.Outcome.Should().Be(TrackerOutcome.Ok, string.Join("; ", first.Errors.Select(e => e.Code)));
        second.Outcome.Should().Be(TrackerOutcome.Ok);

        var recorded = await Board.EvidenceAsync(item.Id);
        recorded.Select(e => e.Summary).Should().Equal(
            "The suite is green.", "The window shows it, the dark theme is off.");
        recorded[0].Criterion.Should().Be(item.AcceptanceCriteria[0].Id);
        recorded[0].Actor!.Kind.Should().Be(WorkItemActorKind.User);
        recorded[1].Actor!.Kind.Should().Be(WorkItemActorKind.Session);
        recorded[1].Actor!.SessionId.Should().Be("session-7");

        var after = (await Board.GetAsync(item.Id))!;
        after.Version.Should().BeGreaterThan(item.Version, "a reader holding the old version has not seen this");
        (await Board.HistoryAsync(item.Id)).Should()
            .Contain(e => e.Operation == WorkItemEventOperation.EvidenceRecorded);
    }

    [Fact]
    public async Task Evidence_naming_something_the_item_does_not_have_is_refused()
    {
        var item = await ReadyAsync("Something to prove");

        var criterion = await Board.RecordEvidenceAsync(
            item.Id,
            new NewEvidence
            {
                Kind = EvidenceKind.TestRun,
                Result = EvidenceResult.Pass,
                Summary = "Green.",
                Criterion = "not-a-criterion-of-this-item",
            },
            LocalBoardFixture.User);
        var scenario = await Board.RecordEvidenceAsync(
            item.Id,
            new NewEvidence
            {
                Kind = EvidenceKind.TestRun,
                Result = EvidenceResult.Pass,
                Summary = "Green.",
                Scenario = "not-a-scenario-of-this-item",
            },
            LocalBoardFixture.User);
        var blank = await Board.RecordEvidenceAsync(
            item.Id,
            new NewEvidence { Kind = EvidenceKind.Log, Result = EvidenceResult.Fail, Summary = "   " },
            LocalBoardFixture.User);

        criterion.Errors.Should().ContainSingle(e => e.Code == TrackerCodes.EvidenceCriterionNotFound);
        scenario.Errors.Should().ContainSingle(e => e.Code == TrackerCodes.EvidenceScenarioNotFound);
        blank.Errors.Should().ContainSingle(e => e.Code == TrackerCodes.EvidenceSummaryRequired);
        (await Board.EvidenceAsync(item.Id)).Should().BeEmpty("a refusal writes nothing");
    }

    [Fact]
    public async Task A_criterion_or_scenario_with_evidence_cannot_be_dropped()
    {
        var item = await _fixture.CreateAsync(new NewWorkItem
        {
            Level = WorkItemLevel.Increment,
            Title = "Proven",
            Description = "Why.",
            Status = WorkItemStatus.Ready,
            AcceptanceCriteria = [new AcceptanceCriterion(string.Empty, "It works.")],
            Scenarios =
            [
                new TestScenario(
                    string.Empty, "Drive it", [new ScenarioStep(string.Empty, "Click", "It opens")]),
            ],
        });
        await Board.RecordEvidenceAsync(
            item.Id,
            new NewEvidence
            {
                Kind = EvidenceKind.TestRun,
                Result = EvidenceResult.Pass,
                Summary = "Green.",
                Criterion = item.AcceptanceCriteria[0].Id,
                Scenario = item.Scenarios[0].Id,
            },
            LocalBoardFixture.User);

        var current = (await Board.GetAsync(item.Id))!;
        var droppedCriterion = await Board.UpdateAsync(
            item.Id,
            new WorkItemPatch { AcceptanceCriteria = Optional<IReadOnlyList<AcceptanceCriterion>>.Of([]) },
            current.Version,
            LocalBoardFixture.User);
        var droppedScenario = await Board.UpdateAsync(
            item.Id,
            new WorkItemPatch { Scenarios = Optional<IReadOnlyList<TestScenario>>.Of([]) },
            current.Version,
            LocalBoardFixture.User);

        droppedCriterion.Errors.Should().ContainSingle(e => e.Code == TrackerCodes.CriterionHasEvidence);
        droppedScenario.Errors.Should().ContainSingle(e => e.Code == TrackerCodes.ScenarioHasEvidence);

        var unchanged = (await Board.GetAsync(item.Id))!;
        unchanged.AcceptanceCriteria.Should().ContainSingle();
        unchanged.Scenarios.Should().ContainSingle();
    }

    [Fact]
    public async Task Editing_the_text_of_a_proven_criterion_is_allowed()
    {
        // The rule protects the POINTER, not the wording: evidence says "this criterion was checked", and a
        // clearer sentence for the same criterion does not make that false.
        var item = await ReadyAsync("Proven");
        await Board.RecordEvidenceAsync(
            item.Id,
            new NewEvidence
            {
                Kind = EvidenceKind.TestRun,
                Result = EvidenceResult.Pass,
                Summary = "Green.",
                Criterion = item.AcceptanceCriteria[0].Id,
            },
            LocalBoardFixture.User);

        var current = (await Board.GetAsync(item.Id))!;
        var edited = await Board.UpdateAsync(
            item.Id,
            new WorkItemPatch
            {
                AcceptanceCriteria = Optional<IReadOnlyList<AcceptanceCriterion>>.Of(
                    [current.AcceptanceCriteria[0] with { Text = "It works, including on a reload." }]),
            },
            current.Version,
            LocalBoardFixture.User);

        edited.Outcome.Should().Be(TrackerOutcome.Ok, string.Join("; ", edited.Errors.Select(e => e.Code)));
        edited.Value.AcceptanceCriteria[0].Text.Should().Be("It works, including on a reload.");
    }
}
