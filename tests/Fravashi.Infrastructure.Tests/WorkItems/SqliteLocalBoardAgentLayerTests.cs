using AwesomeAssertions;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;
using Xunit;

namespace Fravashi.Infrastructure.Tests.WorkItems;

/// <summary>
/// Increment C1 of the canonical artifact model plan: the agent-first field set through the real SQLite store.
/// The point of every one of these is the round trip, because the layer is what makes the model worth owning:
/// no external tracker carries an addressable acceptance criterion, a readiness checklist with an ambiguity
/// score, a curated context bundle, a change budget or an executable scenario.
/// </summary>
public sealed class SqliteLocalBoardAgentLayerTests : IDisposable
{
    private readonly LocalBoardFixture _fixture = new();

    private ITracker Board => _fixture.Board;

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task Reading_one_item_by_its_number_returns_the_same_shape_as_reading_it_by_id()
    {
        // Backlog 38 found this: the by-number read skipped the agent layer, so the studio's detail view
        // addressed an item by the number in its route and was handed an item with no acceptance criteria
        // and no guardrails. Which key a caller happens to hold must not change what an item IS.
        var created = await _fixture.CreateAsync(Authored());

        var byId = await Board.GetAsync(created.Id);
        var byNumber = await Board.GetByNumberAsync(created.Number);

        byNumber.Should().Be(byId);
        byNumber!.AcceptanceCriteria.Should().HaveCount(2);
        byNumber.Scenarios.Should().NotBeEmpty();
    }

    [Fact]
    public async Task The_suggested_model_and_effort_round_trip_and_default_is_null()
    {
        // Requested 2026-10-22 (model and effort per work item): authored with the item, read back by id and by
        // number alike, changed by a patch that names them, kept by one that does not, and cleared to Default.
        var created = await _fixture.CreateAsync(Authored() with
        {
            SuggestedModel = Fravashi.Domain.AgentSessions.AgentModelAlias.Opus,
            SuggestedEffort = Fravashi.Domain.AgentSessions.AgentEffort.High,
        });

        var read = (await Board.GetByNumberAsync(created.Number))!;
        read.SuggestedModel.Should().Be(Fravashi.Domain.AgentSessions.AgentModelAlias.Opus);
        read.SuggestedEffort.Should().Be(Fravashi.Domain.AgentSessions.AgentEffort.High);
        (await Board.GetAsync(created.Id)).Should().Be(read);

        var kept = await Board.UpdateAsync(
            created.Id, new WorkItemPatch { Title = Optional<string>.Of("Renamed") }, read.Version, MutationContext.User);
        kept.Value.SuggestedModel.Should().Be(Fravashi.Domain.AgentSessions.AgentModelAlias.Opus);
        kept.Value.SuggestedEffort.Should().Be(Fravashi.Domain.AgentSessions.AgentEffort.High);

        var cleared = await Board.UpdateAsync(
            created.Id,
            new WorkItemPatch
            {
                SuggestedModel = Optional<Fravashi.Domain.AgentSessions.AgentModelAlias?>.Of(null),
                SuggestedEffort = Optional<Fravashi.Domain.AgentSessions.AgentEffort?>.Of(null),
            },
            kept.Value.Version,
            MutationContext.User);
        cleared.Value.SuggestedModel.Should().BeNull();
        cleared.Value.SuggestedEffort.Should().BeNull();
        (await Board.GetAsync(created.Id))!.SuggestedEffort.Should().BeNull();
    }

    private static NewWorkItem Authored() => new()
    {
        Level = WorkItemLevel.Increment,
        Title = "Paste an image into the chat",
        Description = "An image pasted into the composer reaches the agent and survives a reload.",
        AcceptanceCriteria =
        [
            new AcceptanceCriterion(string.Empty, "A pasted image is attached visibly."),
            new AcceptanceCriterion(string.Empty, "The agent can see it."),
        ],
        Readiness =
        [
            new ReadinessItem(string.Empty, "The CLI's image block shape is measured.", true),
            new ReadinessItem(string.Empty, "The storage cap is decided.", false),
        ],
        Ambiguity = new AmbiguityAssessment(15, "owner", new DateTimeOffset(2026, 9, 15, 8, 0, 0, TimeSpan.Zero)),
        PersonBlockers =
        [
            new PersonBlocker(
                string.Empty, "owner", "the thumbnail's look", new DateTimeOffset(2026, 9, 15, 8, 0, 0, TimeSpan.Zero), null),
        ],
        Refs =
        [
            new ArtifactRef(string.Empty, ArtifactRefRole.Context, ArtifactRefKind.Adr, "docs/adr/0022.md"),
            new ArtifactRef(string.Empty, ArtifactRefRole.Context, ArtifactRefKind.Wiki, "wiki:image-handling"),
            new ArtifactRef(
                string.Empty,
                ArtifactRefRole.Mockup,
                ArtifactRefKind.File,
                "docs/design/composer.svg",
                WorkspaceId: "ws-1",
                Label: "The composer with an attachment",
                Note: "Approved 2026-09-15."),
        ],
        ChangeBudget = new ChangeBudget(12),
        ScopePatterns =
        [
            new ScopePattern(string.Empty, ScopeMode.Allowed, "src/Fravashi.Web/**", "ws-1"),
            new ScopePattern(string.Empty, ScopeMode.Forbidden, "setup/**"),
        ],
        NextAction = "Measure the CLI's image content block.",
        Scenarios =
        [
            new TestScenario(
                string.Empty,
                "An image reaches the agent",
                [
                    new ScenarioStep(string.Empty, "Paste an image into the composer.", "A thumbnail appears.", null),
                    new ScenarioStep(string.Empty, "Send the turn.", "The agent describes the image.", "{\"image\":\"probe.png\"}"),
                ]),
        ],
    };

    [Fact]
    public async Task Every_agent_layer_group_round_trips_with_its_order_kept()
    {
        var created = await _fixture.CreateAsync(Authored());

        var read = await Board.GetAsync(created.Id);

        read.Should().NotBeNull();
        read!.AcceptanceCriteria.Select(c => c.Text).Should()
            .Equal("A pasted image is attached visibly.", "The agent can see it.");
        read.Readiness.Select(r => (r.Text, r.Checked)).Should().Equal(
            ("The CLI's image block shape is measured.", true), ("The storage cap is decided.", false));
        read.Ambiguity.Should().NotBeNull();
        read.Ambiguity!.Score.Should().Be(15);
        read.Ambiguity.AssessedBy.Should().Be("owner");
        read.PersonBlockers.Should().ContainSingle().Which.Person.Should().Be("owner");
        read.ActivePersonBlockers.Should().ContainSingle();
        read.Refs.Should().HaveCount(3);
        read.RefFor(ArtifactRefRole.Mockup)!.WorkspaceId.Should().Be("ws-1", "a file ref carries the workspace its path is in");
        read.RefFor(ArtifactRefRole.Mockup)!.Label.Should().Be("The composer with an attachment");
        read.ChangeBudget.MaxFiles.Should().Be(12);
        read.ScopePatterns.Select(p => (p.Mode, p.Pattern, p.WorkspaceId)).Should().Equal(
            (ScopeMode.Allowed, "src/Fravashi.Web/**", "ws-1"), (ScopeMode.Forbidden, "setup/**", null));
        read.NextAction.Should().Be("Measure the CLI's image content block.");
        var scenario = read.Scenarios.Should().ContainSingle().Which;
        scenario.Title.Should().Be("An image reaches the agent");
        scenario.Steps.Select(s => s.Action).Should().Equal("Paste an image into the composer.", "Send the turn.");
        scenario.Steps[1].Data.Should().Be("{\"image\":\"probe.png\"}");
    }

    [Fact]
    public async Task Every_entry_is_given_an_id_so_something_else_can_point_at_it()
    {
        var created = await _fixture.CreateAsync(Authored());

        created.AcceptanceCriteria.Should().OnlyContain(c => c.Id.Length > 0);
        created.Readiness.Should().OnlyContain(r => r.Id.Length > 0);
        created.Refs.Should().OnlyContain(r => r.Id.Length > 0);
        created.Scenarios.Should().OnlyContain(s => s.Id.Length > 0 && s.Steps.All(step => step.Id.Length > 0));
        created.AcceptanceCriteria.Select(c => c.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task A_list_replacement_keeps_the_id_of_an_entry_whose_id_came_back()
    {
        // The whole reason ids are stable: evidence written against a criterion must survive an edit of its
        // neighbours (increment C2 records that evidence; this is the guarantee it will rest on).
        var created = await _fixture.CreateAsync(Authored());
        var keep = created.AcceptanceCriteria[0];

        var updated = await Board.UpdateAsync(
            created.Id,
            new WorkItemPatch
            {
                AcceptanceCriteria = Optional<IReadOnlyList<AcceptanceCriterion>>.Of(
                [
                    keep,
                    new AcceptanceCriterion(string.Empty, "It survives a reload."),
                ]),
            },
            created.Version,
            LocalBoardFixture.User);

        updated.Outcome.Should().Be(TrackerOutcome.Ok, string.Join("; ", updated.Errors.Select(e => e.Code)));
        updated.Value.AcceptanceCriteria[0].Id.Should().Be(keep.Id, "an entry passed back keeps its id");
        updated.Value.AcceptanceCriteria[0].Text.Should().Be(keep.Text);
        updated.Value.AcceptanceCriteria[1].Id.Should().NotBe(keep.Id).And.NotBeEmpty("a new entry gets a new id");
        updated.Value.AcceptanceCriteria.Should().HaveCount(2, "the list is replaced whole, not merged");
    }

    [Fact]
    public async Task A_person_blocker_is_cleared_rather_than_deleted()
    {
        var created = await _fixture.CreateAsync(Authored());
        var blocker = created.PersonBlockers[0];

        var cleared = await Board.UpdateAsync(
            created.Id,
            new WorkItemPatch
            {
                PersonBlockers = Optional<IReadOnlyList<PersonBlocker>>.Of(
                    [blocker with { ClearedUtc = new DateTimeOffset(2026, 9, 16, 9, 0, 0, TimeSpan.Zero) }]),
            },
            created.Version,
            LocalBoardFixture.User);

        cleared.Outcome.Should().Be(TrackerOutcome.Ok);
        cleared.Value.PersonBlockers.Should().ContainSingle().Which.Id.Should().Be(blocker.Id);
        cleared.Value.PersonBlockers[0].ClearedUtc.Should().NotBeNull("the history of what the work waited on stays");
        cleared.Value.ActivePersonBlockers.Should().BeEmpty();
    }

    [Fact]
    public async Task An_agent_layer_only_change_is_a_real_change_that_bumps_the_version()
    {
        // It has to be: an event diff that could not see the acceptance list would make a patch that edits
        // only the acceptance criteria write nothing at all, silently.
        var created = await _fixture.CreateAsync(Authored());

        var updated = await Board.UpdateAsync(
            created.Id,
            new WorkItemPatch { NextAction = Optional<string?>.Of("Ship the thumbnail.") },
            created.Version,
            LocalBoardFixture.User);

        updated.Outcome.Should().Be(TrackerOutcome.Ok);
        updated.Value.Version.Should().Be(created.Version + 1);
        updated.Value.NextAction.Should().Be("Ship the thumbnail.");

        var history = await Board.HistoryAsync(created.Id);
        history.Should().Contain(e => e.Operation == WorkItemEventOperation.Updated);
    }

    [Fact]
    public async Task A_patch_that_does_not_mention_a_list_leaves_it_alone()
    {
        var created = await _fixture.CreateAsync(Authored());

        var updated = await Board.UpdateAsync(
            created.Id,
            new WorkItemPatch { Title = "Paste an image into the chat and the editor" },
            created.Version,
            LocalBoardFixture.User);

        updated.Value.AcceptanceCriteria.Should().HaveCount(2);
        updated.Value.Scenarios.Should().ContainSingle();
        updated.Value.Refs.Should().HaveCount(3);
    }

    [Fact]
    public async Task An_item_authored_without_an_agent_layer_reads_back_empty_rather_than_null()
    {
        var plain = await _fixture.CreateAsync(LocalBoardFixture.NewTask());

        var read = (await Board.GetAsync(plain.Id))!;

        read.AcceptanceCriteria.Should().BeEmpty();
        read.Readiness.Should().BeEmpty();
        read.PersonBlockers.Should().BeEmpty();
        read.Refs.Should().BeEmpty();
        read.ScopePatterns.Should().BeEmpty();
        read.Scenarios.Should().BeEmpty();
        read.Ambiguity.Should().BeNull();
        read.ChangeBudget.IsEmpty.Should().BeTrue();
        read.NextAction.Should().BeNull();
    }

    [Fact]
    public async Task A_refused_authoring_rule_leaves_the_stored_layer_untouched()
    {
        var created = await _fixture.CreateAsync(Authored());

        var refused = await Board.UpdateAsync(
            created.Id,
            new WorkItemPatch
            {
                ScopePatterns = Optional<IReadOnlyList<ScopePattern>>.Of(
                    [new ScopePattern(string.Empty, ScopeMode.Allowed, "/etc/**")]),
            },
            created.Version,
            LocalBoardFixture.User);

        refused.Outcome.Should().Be(TrackerOutcome.Invalid);
        refused.Errors.Should().ContainSingle(e => e.Code == WorkItemRules.Codes.ScopePatternNotRelative);

        var read = (await Board.GetAsync(created.Id))!;
        read.ScopePatterns.Should().HaveCount(2, "a refusal writes nothing");
        read.Version.Should().Be(created.Version);
    }
}
