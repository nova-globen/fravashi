using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Application.Wiki;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Projects;
using Fravashi.Domain.Wiki;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Tests.Persistence;
using Fravashi.Infrastructure.Wiki;
using Fravashi.Infrastructure.WorkItems;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Backlog 85's readiness, over the real stores: a gate that only checks a link exists is cleared by
/// binding to a cancelled item and an empty plan, and this is where that stops being true. Also the
/// quick-change path, which is the counterweight: neither is a bypass, and both produce real artifacts.
/// </summary>
public sealed class GateReadinessTests : IDisposable
{
    private readonly SqliteTestContext _store = new();
    private readonly AgentSessionId _session = AgentSessionId.New();
    private readonly LocalTrackerResolver _trackers;
    private readonly ITracker _board;
    private readonly SqliteWikiLibrary _wiki;
    private readonly SqliteSessionWorkItems _work;
    private readonly SqliteSessionPlans _plans;
    private readonly SqliteSessionChangedFiles _changed;
    private readonly SessionGovernance _governance;

    public GateReadinessTests()
    {
        _trackers = new LocalTrackerResolver(_store.Factory, TimeProvider.System);
        _board = _trackers.For(ProjectId.Default);
        _wiki = new SqliteWikiLibrary(_store.Factory, TimeProvider.System, Fravashi.Domain.Projects.ProjectId.Default);
        _work = new SqliteSessionWorkItems(_store.Factory, TimeProvider.System);
        _plans = new SqliteSessionPlans(_store.Factory, TimeProvider.System);
        _changed = new SqliteSessionChangedFiles(_store.Factory, TimeProvider.System);
        _governance = new SessionGovernance(_work, _plans, _trackers, _wiki, _changed);
    }

    public void Dispose() => _store.Dispose();

    // ---- readiness -----------------------------------------------------------------------------

    [Fact]
    public async Task Binding_to_a_cancelled_item_does_not_clear_the_gate()
    {
        var item = await ItemAsync("Abandoned work", WorkItemStatus.Cancelled);
        _work.Link(_session, item.Id, ProjectId.Default, "user");
        await BindPlanAsync(item.Number);

        Verdict().Should().Be(WorkGateVerdict.WorkNotWorkable);
    }

    [Fact]
    public async Task Binding_to_a_finished_item_does_not_clear_it_either()
    {
        var item = await ItemAsync("Finished work", WorkItemStatus.Done);
        _work.Link(_session, item.Id, ProjectId.Default, "user");
        await BindPlanAsync(item.Number);

        Verdict().Should().Be(WorkGateVerdict.WorkNotWorkable);
    }

    [Fact]
    public async Task One_open_item_among_several_is_enough_to_be_working()
    {
        var open = await ItemAsync("Still going");
        var closed = await ItemAsync("Finished", WorkItemStatus.Done);
        _work.Link(_session, open.Id, ProjectId.Default, "user");
        _work.Link(_session, closed.Id, ProjectId.Default, "user");
        await BindPlanAsync(open.Number);

        Verdict().Should().Be(WorkGateVerdict.Linked);
    }

    [Fact]
    public async Task An_empty_plan_does_not_clear_the_gate()
    {
        var item = await ItemAsync("The work");
        _work.Link(_session, item.Id, ProjectId.Default, "user");
        var plan = await PlanAsync("A plan in name only", body: "   ");
        _plans.Link(_session, plan.Id, "user");

        Verdict().Should().Be(WorkGateVerdict.PlanIsEmpty);
    }

    [Fact]
    public async Task A_plan_for_other_work_does_not_cover_this_work()
    {
        var item = await ItemAsync("The work");
        var other = await ItemAsync("Something else");
        _work.Link(_session, item.Id, ProjectId.Default, "user");
        var plan = await PlanAsync("A plan for something else", body: $"This covers #{other.Number}.");
        _plans.Link(_session, plan.Id, "user");

        Verdict().Should().Be(WorkGateVerdict.PlanDoesNotCover);
    }

    [Fact]
    public async Task A_plan_covers_work_it_names_in_prose_or_in_a_typed_link()
    {
        var item = await ItemAsync("The work");
        _work.Link(_session, item.Id, ProjectId.Default, "user");

        // Said in prose.
        var byProse = await PlanAsync("Plan one", body: $"This covers #{item.Number}.");
        _plans.Link(_session, byProse.Id, "user");
        Verdict().Should().Be(WorkGateVerdict.Linked);

        // And said as a typed link, with prose that names nothing.
        _plans.Unlink(_session, byProse.Id);
        var byLink = await PlanAsync(
            "Plan two",
            body: "How it will be done.",
            links: [new WikiLink(WikiLinkKind.WorkItem, item.Number.ToString(System.Globalization.CultureInfo.InvariantCulture))]);
        _plans.Link(_session, byLink.Id, "user");

        Verdict().Should().Be(WorkGateVerdict.Linked);
    }

    // ---- scope and budget ------------------------------------------------------------------------

    [Fact]
    public async Task A_change_outside_the_declared_scope_is_refused()
    {
        var item = await ItemAsync(
            "Scoped work",
            scope: [new ScopePattern(AgentLayerEntry.NewId(), ScopeMode.Allowed, "src/**")]);
        _work.Link(_session, item.Id, ProjectId.Default, "user");
        await BindPlanAsync(item.Number);

        Verdict("src/a.cs").Should().Be(WorkGateVerdict.Linked);
        Verdict("tests/a.cs").Should().Be(WorkGateVerdict.OutOfScope);
    }

    [Fact]
    public async Task An_item_that_declares_no_scope_forbids_nowhere()
    {
        var item = await ItemAsync("Unscoped work");
        _work.Link(_session, item.Id, ProjectId.Default, "user");
        await BindPlanAsync(item.Number);

        Verdict("anywhere/at/all.cs").Should().Be(WorkGateVerdict.Linked);
    }

    [Fact]
    public async Task A_change_past_the_declared_budget_is_refused_and_a_repeat_of_one_already_made_is_not()
    {
        var item = await ItemAsync("Budgeted work", budget: 2);
        _work.Link(_session, item.Id, ProjectId.Default, "user");
        await BindPlanAsync(item.Number);

        Verdict("a.cs").Should().Be(WorkGateVerdict.Linked);
        _governance.RecordApprovedChange(_session, "a.cs");
        _governance.RecordApprovedChange(_session, "b.cs");

        // Two files are already changed, so a third is over, and touching one of the two again is not.
        Verdict("c.cs").Should().Be(WorkGateVerdict.OverChangeBudget);
        Verdict("a.cs").Should().Be(WorkGateVerdict.Linked);
    }

    [Fact]
    public async Task Recording_the_same_file_twice_costs_one()
    {
        var item = await ItemAsync("Budgeted work", budget: 1);
        _work.Link(_session, item.Id, ProjectId.Default, "user");
        await BindPlanAsync(item.Number);

        _governance.RecordApprovedChange(_session, "a.cs");
        _governance.RecordApprovedChange(_session, "a.cs");

        Verdict("a.cs").Should().Be(WorkGateVerdict.Linked);
        Verdict("b.cs").Should().Be(WorkGateVerdict.OverChangeBudget);
    }

    [Fact]
    public async Task A_path_the_studio_could_not_resolve_is_not_counted_and_is_not_out_of_scope()
    {
        var item = await ItemAsync(
            "Scoped work",
            scope: [new ScopePattern(AgentLayerEntry.NewId(), ScopeMode.Allowed, "src/**")],
            budget: 1);
        _work.Link(_session, item.Id, ProjectId.Default, "user");
        await BindPlanAsync(item.Number);

        // A call that names nowhere cannot be outside somewhere.
        Verdict(null).Should().Be(WorkGateVerdict.Linked);
        _governance.RecordApprovedChange(_session, null);
        _changed.Count(_session).Should().Be(0);
    }

    [Fact]
    public async Task An_unreadable_store_holds_the_gate_rather_than_opening_it()
    {
        var item = await ItemAsync("The work");
        _work.Link(_session, item.Id, ProjectId.Default, "user");
        await BindPlanAsync(item.Number);

        // A tracker whose database is gone: the facts cannot be assembled, so nothing is granted.
        var broken = new SessionGovernance(
            _work,
            _plans,
            new LocalTrackerResolver(new BrokenFactory(), TimeProvider.System),
            _wiki,
            _changed);

        broken.Facts(_session, "src/a.cs").Should().Be(WorkGateFacts.None);
    }

    // ---- the quick-change path --------------------------------------------------------------------

    [Fact]
    public async Task A_quick_change_creates_real_artifacts_and_clears_the_gate_the_ordinary_way()
    {
        var quick = new QuickChange(_trackers, new OneWiki(_wiki), _work, _plans);

        var started = await quick.StartAsync(_session, ProjectId.Default, "Fix the typo in the welcome heading");

        started.Should().NotBeNull();
        Verdict("src/a.cs").Should().Be(WorkGateVerdict.Linked, "nothing was skipped; the artifacts exist");

        // A real item, at Ready, with a done-check on it.
        var item = (await _board.GetAsync(started!.Item.Id))!;
        item.Status.Should().Be(WorkItemStatus.Ready);
        item.AcceptanceCriteria.Should().ContainSingle();

        // A real plan that covers it, findable in the wiki like any other document.
        var plan = (await _wiki.GetByNumberAsync(started.Plan.Number))!;
        plan.Type.Should().Be(WikiItemType.Plan);
        plan.BodyReferences.Should().Contain(new WikiReference(WikiReferenceKind.WorkItem, item.Number));
    }

    [Fact]
    public async Task Everything_a_quick_change_makes_says_it_was_made_that_way()
    {
        // A quick change that looked exactly like a considered one would quietly lower the standard of
        // everything on the board, which is what item 85's note warns about.
        var quick = new QuickChange(_trackers, new OneWiki(_wiki), _work, _plans);

        var started = await quick.StartAsync(_session, ProjectId.Default, "Fix the typo");

        started!.Item.Description.Should().Contain(QuickChange.Marker);
        started.Plan.Body.Should().Contain(QuickChange.Marker);
        started.Plan.Title.Should().StartWith("Quick change:");
    }

    [Fact]
    public async Task A_quick_change_with_nothing_said_is_refused()
    {
        var quick = new QuickChange(_trackers, new OneWiki(_wiki), _work, _plans);

        (await quick.StartAsync(_session, ProjectId.Default, "   ")).Should().BeNull();
        _work.HasWork(_session).Should().BeFalse();
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private WorkGateVerdict Verdict(string? path = "src/a.cs") =>
        AgentWorkGate.Check("Edit", readOnly: false, _governance.Facts(_session, path));

    private async Task<WorkItem> ItemAsync(
        string title,
        WorkItemStatus status = WorkItemStatus.Ready,
        IReadOnlyList<ScopePattern>? scope = null,
        int? budget = null)
    {
        var created = await _board.CreateAsync(
            new NewWorkItem
            {
                Level = WorkItemLevel.Task,
                Title = title,
                Description = "Why it exists.",
                Status = status,
                Resolution = status switch
                {
                    WorkItemStatus.Done => WorkItemResolution.Completed,
                    WorkItemStatus.Cancelled => WorkItemResolution.WontDo,
                    _ => null,
                },
                ScopePatterns = scope ?? [],
                ChangeBudget = budget is { } max ? new ChangeBudget(max) : ChangeBudget.None,
            },
            MutationContext.User);

        created.Outcome.Should().Be(TrackerOutcome.Ok, string.Join("; ", created.Errors.Select(e => e.Code)));
        return created.Value;
    }

    private async Task<WikiItem> PlanAsync(
        string title, string body, IReadOnlyList<WikiLink>? links = null)
    {
        var created = await _wiki.CreateAsync(new NewWikiItem
        {
            Type = WikiItemType.Plan,
            Title = title,
            Body = body,
            Links = links ?? [],
        });

        created.IsValid.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Code)));
        return created.Value;
    }

    private async Task BindPlanAsync(int covers)
    {
        var plan = await PlanAsync("The plan", $"This covers #{covers}.");
        _plans.Link(_session, plan.Id, "user");
    }

    /// <summary>A factory whose connections open onto nothing, so every read throws.</summary>
    private sealed class BrokenFactory : Fravashi.Infrastructure.Persistence.ISqliteConnectionFactory
    {
        public Microsoft.Data.Sqlite.SqliteConnection Open() =>
            throw new InvalidOperationException("This store is not available.");
    }
}
