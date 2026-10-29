using Bunit;
using Fravashi.Application.Persistence;
using Fravashi.Application.Sessions;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Wiki;
using Fravashi.Infrastructure.WorkItems;
using Fravashi.Web.Components.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 44's other half: the user can SEE what a conversation is working on and change it. Without this
/// the gate would be a rule only the agent can explain, and a user whose session was refused would have no
/// way to say "yes, it is this item" except by asking the agent to do it for them.
/// </summary>
public sealed class SessionWorkStripTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-tests", Guid.NewGuid().ToString("N"));

    private readonly SqliteConnectionFactory _factory;
    private readonly LocalTrackerResolver _trackers;
    private readonly ITracker _board;
    private readonly SqliteSessionWorkItems _work;
    private readonly SqliteSessionPlans _plans;
    private readonly SqliteWikiLibrary _wiki;
    private readonly SessionGovernance _governance;
    private readonly SqliteSessionRoles _roles;
    private readonly SqliteWorkspaceTrust _trust;
    private readonly SqliteWorkspaceStore _workspaces;
    private readonly AgentSessionId _session = AgentSessionId.New();

    public SessionWorkStripTests()
    {
        Directory.CreateDirectory(_directory);
        _factory = new SqliteConnectionFactory(
            new SqliteStoreOptions(Path.Combine(_directory, "test.db"), null, Pooling: false));
        SqliteSchema.EnsureCreated(_factory);
        _trackers = new LocalTrackerResolver(_factory, TimeProvider.System);
        _board = _trackers.For(ProjectId.Default);
        _work = new SqliteSessionWorkItems(_factory, TimeProvider.System);
        _plans = new SqliteSessionPlans(_factory, TimeProvider.System);
        _wiki = new SqliteWikiLibrary(_factory, TimeProvider.System, Fravashi.Domain.Projects.ProjectId.Default);
        _roles = new SqliteSessionRoles(_factory);
        _trust = new SqliteWorkspaceTrust(_factory);
        _workspaces = new SqliteWorkspaceStore(_factory);
        _governance = new SessionGovernance(
            _work,
            _plans,
            _trackers,
            _wiki,
            new SqliteSessionChangedFiles(_factory, TimeProvider.System));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private BunitContext Context()
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton<ITrackerResolver>(_trackers);
        // Backlog 121: a page follows its window's project; these tests have one project.
        ctx.Services.AddSingleton<Fravashi.Application.Projects.ICurrentProject>(Fravashi.Application.Projects.SingleProject.Instance);
        ctx.Services.AddSingleton<Fravashi.Application.Projects.IProjectStore>(new OneProjectStore());
        ctx.Services.AddSingleton<ISessionWorkItems>(_work);
        ctx.Services.AddSingleton<ISessionPlans>(_plans);
        // Backlog 121: a page asks for its project's wiki, and what holds an id asks for the by-id view.
        ctx.Services.AddSingleton<Fravashi.Application.Wiki.IWikiLibraries>(new OneWiki(_wiki));
        ctx.Services.AddSingleton<Fravashi.Application.Wiki.IWikiDocuments>(_wiki);
        ctx.Services.AddSingleton<ISessionGovernance>(_governance);
        ctx.Services.AddSingleton(new QuickChange(_trackers, new OneWiki(_wiki), _work, _plans));
        ctx.Services.AddSingleton<ISessionRoles>(_roles);
        ctx.Services.AddSingleton<IWorkspaceTrust>(_trust);
        ctx.Services.AddSingleton<IWorkspaceStore>(_workspaces);
        return ctx;
    }

    private async Task<WorkItem> CreateAsync(string title, WorkItemStatus status = WorkItemStatus.Ready)
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
            },
            MutationContext.User);
        Assert.Equal(TrackerOutcome.Ok, created.Outcome);
        return created.Value;
    }

    private async Task<Fravashi.Domain.Wiki.WikiItem> PlanAsync(string title)
    {
        var created = await _wiki.CreateAsync(new Fravashi.Application.Wiki.NewWikiItem
        {
            Type = Fravashi.Domain.Wiki.WikiItemType.Plan,
            Title = title,
        });
        Assert.True(created.IsValid);
        return created.Value;
    }

    [Fact]
    public void A_session_bound_to_nothing_says_changes_are_held_and_what_is_missing()
    {
        using var ctx = Context();

        var cut = ctx.Render<SessionWorkStrip>(p => p.Add(c => c.Session, _session));

        Assert.Contains("held", cut.Find("[data-testid=session-work-state]").TextContent.ToLowerInvariant());
        Assert.NotEmpty(cut.FindAll("[data-testid=session-work-none]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=session-plan-none]"));

        // The gate's verdict, in a person's words.
        Assert.Contains(
            "not linked to a work item", cut.Find("[data-testid=session-work-missing]").TextContent);
    }

    [Fact]
    public async Task The_strip_names_no_raw_tool_whatever_holds_the_session()
    {
        // Work item #362: the agent's sentence names mcp__ tools; the person reading the strip is not shown them.
        using var ctx = Context();
        var unlinked = ctx.Render<SessionWorkStrip>(p => p.Add(c => c.Session, _session));
        Assert.DoesNotContain("mcp__", unlinked.Markup);

        var item = await CreateAsync("Wire the approval prompt");
        _work.Link(_session, item.Id, ProjectId.Default, "user");
        var noPlan = ctx.Render<SessionWorkStrip>(p => p.Add(c => c.Session, _session));
        Assert.Contains("no plan", noPlan.Find("[data-testid=session-work-missing]").TextContent);
        Assert.DoesNotContain("mcp__", noPlan.Markup);

        // And every verdict the gate can return is worded without one, including any added later.
        foreach (var verdict in Enum.GetValues<WorkGateVerdict>())
        {
            Assert.DoesNotContain("mcp__", WorkGateWordsForPeople.For(verdict) ?? string.Empty);
            Assert.Equal(verdict.SentenceFor() is null, WorkGateWordsForPeople.For(verdict) is null);
        }
    }

    [Fact]
    public async Task A_session_with_work_and_no_plan_says_the_plan_is_what_is_missing()
    {
        var item = await CreateAsync("Wire the approval prompt");
        _work.Link(_session, item.Id, ProjectId.Default, "user");
        using var ctx = Context();

        var cut = ctx.Render<SessionWorkStrip>(p => p.Add(c => c.Session, _session));

        Assert.Contains("held", cut.Find("[data-testid=session-work-state]").TextContent.ToLowerInvariant());
        Assert.Contains("no plan", cut.Find("[data-testid=session-work-missing]").TextContent);
    }

    [Fact]
    public async Task A_session_with_both_says_changes_are_allowed_and_names_nothing_missing()
    {
        var item = await CreateAsync("Wire the approval prompt");
        var plan = await PlanAsync("The plan for it");

        // The plan has to SAY what it covers now (backlog 85): an empty plan naming nothing clears no gate.
        var updated = await _wiki.UpdateAsync(
            plan.Id,
            new Fravashi.Application.Wiki.WikiItemEdit { Body = $"This covers #{item.Number}." },
            plan.Version);
        Assert.True(updated.IsValid);

        _work.Link(_session, item.Id, ProjectId.Default, "user");
        _plans.Link(_session, plan.Id, "user");
        using var ctx = Context();

        var cut = ctx.Render<SessionWorkStrip>(p => p.Add(c => c.Session, _session));

        Assert.Contains("allowed", cut.Find("[data-testid=session-work-state]").TextContent.ToLowerInvariant());
        Assert.Empty(cut.FindAll("[data-testid=session-work-missing]"));

        // Backlog 129: once changes are allowed the strip is ONE line, which still names the item and the plan,
        // and the controls are one click away. Bound like this it used to wrap onto six rows and leave the
        // transcript a single line tall at the minimum window size.
        var summary = cut.Find("[data-testid=session-work-summary]").TextContent;
        Assert.Contains("Wire the approval prompt", summary, StringComparison.Ordinal);
        Assert.Contains("The plan for it", summary, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=session-plan-chip]"));
        Assert.Empty(cut.FindAll("[data-testid=session-work-add]"));

        await cut.InvokeAsync(() => cut.Find("[data-testid=session-work-toggle]").Click());

        Assert.Contains("The plan for it", cut.Find("[data-testid=session-plan-chip]").TextContent);
        Assert.Empty(cut.FindAll("[data-testid=session-work-summary]"));
    }

    [Fact]
    public void A_held_session_opens_the_controls_by_itself_because_that_is_when_a_person_needs_them()
    {
        // Backlog 129's other half. Collapsing must not hide the way out of a refusal: with nothing linked the
        // strip is open, the add selects and the quick-change line are there, and no click was needed.
        using var ctx = Context();

        var cut = ctx.Render<SessionWorkStrip>(p => p.Add(c => c.Session, _session));

        Assert.Contains("held", cut.Find("[data-testid=session-work-state]").TextContent.ToLowerInvariant());
        Assert.NotEmpty(cut.FindAll("[data-testid=session-work-add]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=session-work-quick]"));
        Assert.Empty(cut.FindAll("[data-testid=session-work-summary]"));
    }

    [Fact]
    public async Task The_user_can_put_the_session_under_a_plan_from_the_strip()
    {
        var plan = await PlanAsync("The plan for it");
        using var ctx = Context();

        var cut = ctx.Render<SessionWorkStrip>(p => p.Add(c => c.Session, _session));

        await cut.InvokeAsync(() =>
            cut.Find("[data-testid=session-plan-add]").Change(plan.Number.ToString()));

        cut.WaitForAssertion(() =>
            Assert.Contains("The plan for it", cut.Find("[data-testid=session-plan-chip]").TextContent));

        Assert.True(_plans.HasPlan(_session));
    }

    [Fact]
    public async Task Only_documents_that_are_plans_are_offered_as_plans()
    {
        await PlanAsync("A real plan");
        var created = await _wiki.CreateAsync(new Fravashi.Application.Wiki.NewWikiItem
        {
            Type = Fravashi.Domain.Wiki.WikiItemType.Draft,
            Title = "Only a draft",
        });
        Assert.True(created.IsValid);
        using var ctx = Context();

        var cut = ctx.Render<SessionWorkStrip>(p => p.Add(c => c.Session, _session));

        var options = cut.Find("[data-testid=session-plan-add]").TextContent;
        Assert.Contains("A real plan", options);
        Assert.DoesNotContain("Only a draft", options);
    }

    [Fact]
    public async Task A_held_session_offers_the_quick_change_path_and_it_creates_real_artifacts()
    {
        // Item 85's acceptance: one confirmed step, marked as such, and not a bypass.
        using var ctx = Context();
        var cut = ctx.Render<SessionWorkStrip>(p => p.Add(c => c.Session, _session));

        Assert.NotEmpty(cut.FindAll("[data-testid=session-work-quick]"));

        await cut.InvokeAsync(() =>
            cut.Find("[data-testid=session-quick-summary]").Input("Fix the typo in the heading"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=session-quick-start]").Click());

        cut.WaitForAssertion(() =>
            Assert.Contains("allowed", cut.Find("[data-testid=session-work-state]").TextContent.ToLowerInvariant()));

        Assert.True(_work.HasWork(_session));
        Assert.True(_plans.HasPlan(_session));

        var plans = await _wiki.ListAsync(new Fravashi.Application.Wiki.WikiItemQuery());
        Assert.Contains(plans, p => p.Body.Contains(QuickChange.Marker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_session_that_may_already_change_things_is_not_offered_a_shortcut()
    {
        var item = await CreateAsync("The work");
        var plan = await PlanAsync("The plan");
        var updated = await _wiki.UpdateAsync(
            plan.Id,
            new Fravashi.Application.Wiki.WikiItemEdit { Body = $"This covers #{item.Number}." },
            plan.Version);
        Assert.True(updated.IsValid);

        _work.Link(_session, item.Id, ProjectId.Default, "user");
        _plans.Link(_session, plan.Id, "user");
        using var ctx = Context();

        var cut = ctx.Render<SessionWorkStrip>(p => p.Add(c => c.Session, _session));

        Assert.Empty(cut.FindAll("[data-testid=session-work-quick]"));
    }

    [Fact]
    public async Task A_session_bound_to_a_cancelled_item_still_reads_as_held()
    {
        // Backlog 85: a gate that only checks a link exists is cleared by pointing at abandoned work.
        var item = await CreateAsync("Abandoned", WorkItemStatus.Cancelled);
        _work.Link(_session, item.Id, ProjectId.Default, "user");
        using var ctx = Context();

        var cut = ctx.Render<SessionWorkStrip>(p => p.Add(c => c.Session, _session));

        Assert.Contains("held", cut.Find("[data-testid=session-work-state]").TextContent.ToLowerInvariant());
        Assert.Contains("closed", cut.Find("[data-testid=session-work-missing]").TextContent);
    }

    [Fact]
    public async Task The_role_the_conversation_runs_as_is_shown_and_can_be_changed()
    {
        using var ctx = Context();
        var cut = ctx.Render<SessionWorkStrip>(p => p.Add(c => c.Session, _session));

        var picker = cut.Find("[data-testid=session-role]");
        Assert.Contains("Verifier", picker.TextContent);
        Assert.Contains("Unrestricted", picker.TextContent);

        await cut.InvokeAsync(() => picker.Change(Fravashi.Domain.AgentSessions.AgentRoles.Verifier.Id));

        cut.WaitForAssertion(() =>
            Assert.Equal(
                Fravashi.Domain.AgentSessions.AgentRoles.Verifier,
                _roles.For(_session)));

        // The role's own instructions are on the control, so the person and the agent read the same words.
        cut.WaitForAssertion(() => Assert.Contains(
            "no longer checking anything",
            cut.Find("[data-testid=session-role]").GetAttribute("title")!));
    }

    /// <summary>Work item #356: the trust level moved to the session header, so the strip behind Details no longer has it.</summary>
    [Fact]
    public void The_strip_no_longer_carries_the_trust_picker()
    {
        using var ctx = Context();
        var cut = ctx.Render<SessionWorkStrip>(p => p.Add(c => c.Session, _session).Add(c => c.WorkspaceId, "w1"));

        Assert.Empty(cut.FindAll("[data-testid=session-trust]"));
    }

    [Fact]
    public async Task The_folders_trust_level_is_shown_and_can_be_changed_and_is_absent_without_a_folder()
    {
        // ADR 0028: the level belongs to the FOLDER, so the picker appears only when the strip knows one, and a
        // choice made here is what the approval gate reads on the next call.
        using var ctx = Context();
        var cut = ctx.Render<SessionTrustPicker>(p => p.Add(c => c.WorkspaceId, "w1"));

        var picker = cut.Find("[data-testid=session-trust]");
        Assert.Contains("Nothing (ask)", picker.TextContent);
        Assert.Contains("Edits in the copy", picker.TextContent);
        Assert.Contains("commands, hidden paths and anything outside the copy still ask", picker.InnerHtml);
        Assert.Contains("Every change asks", picker.GetAttribute("title")!);

        await cut.InvokeAsync(() => picker.Change("edits"));

        cut.WaitForAssertion(() => Assert.Equal(WorkspaceTrust.Edits, _trust.For("w1")));
        cut.WaitForAssertion(() => Assert.Contains(
            "still ask",
            cut.Find("[data-testid=session-trust]").GetAttribute("title")!));

        // Another folder is untouched, and a strip that knows no folder offers no picker at all.
        Assert.Equal(WorkspaceTrust.Ask, _trust.For("w2"));
        var bare = ctx.Render<SessionTrustPicker>();
        Assert.Empty(bare.FindAll("[data-testid=session-trust]"));
    }

    /// <summary>
    /// Backlog 312: a worktree never has its own level (AgentApprovalBroker.TrustFor stopped reading it), so
    /// the strip, opened on a worktree's own workspace, shows and changes its REPOSITORY folder's level
    /// instead of a level nothing reads.
    /// </summary>
    [Fact]
    public async Task Opened_on_a_worktree_the_trust_picker_shows_and_changes_the_repositorys_own_level()
    {
        _workspaces.Touch("repo-1", "widget", "/repos/widget");
        _workspaces.Touch("wt-1", "widget-item-1", "/repos/widget.fravashi-worktrees/1");

        using var ctx = Context();
        var cut = ctx.Render<SessionTrustPicker>(p => p.Add(c => c.WorkspaceId, "wt-1"));

        Assert.Contains("Every change asks", cut.Find("[data-testid=session-trust]").GetAttribute("title")!);

        await cut.InvokeAsync(() => cut.Find("[data-testid=session-trust]").Change("edits"));

        cut.WaitForAssertion(() => Assert.Equal(WorkspaceTrust.Edits, _trust.For("repo-1")));
        // The worktree's own id is never where a level is stored.
        Assert.Equal(WorkspaceTrust.Ask, _trust.For("wt-1"));
        cut.WaitForAssertion(() => Assert.Contains(
            "repository folder's own level", cut.Find("[data-testid=session-trust]").GetAttribute("title")!));
    }

    /// <summary>
    /// Backlog 312: a worktree whose repository folder has no workspace record (never opened through the
    /// welcome page) has nowhere to read or write a level, so a choice made here is refused in words rather
    /// than silently accepted and forgotten.
    /// </summary>
    [Fact]
    public async Task A_worktree_whose_repository_has_no_record_refuses_a_chosen_level_in_words()
    {
        _workspaces.Touch("wt-1", "widget-item-1", "/repos/widget.fravashi-worktrees/1");

        using var ctx = Context();
        var cut = ctx.Render<SessionTrustPicker>(p => p.Add(c => c.WorkspaceId, "wt-1"));

        await cut.InvokeAsync(() => cut.Find("[data-testid=session-trust]").Change("edits"));

        cut.WaitForAssertion(() => Assert.Contains(
            "repository folder is not registered", cut.Find("[data-testid=session-trust-refusal]").TextContent));
        cut.WaitForAssertion(() => Assert.Contains(
            "Every change asks", cut.Find("[data-testid=session-trust]").GetAttribute("title")!));
        Assert.Equal(WorkspaceTrust.Ask, _trust.For("wt-1"));
        Assert.Equal(WorkspaceTrust.Ask, _trust.For("w1"));
        Assert.Equal(WorkspaceTrust.Ask, _trust.For("w2"));
    }

    [Fact]
    public async Task The_user_can_start_working_on_an_item_from_the_strip()
    {
        var item = await CreateAsync("Wire the approval prompt");
        using var ctx = Context();

        var cut = ctx.Render<SessionWorkStrip>(p => p.Add(c => c.Session, _session));

        await cut.InvokeAsync(() =>
            cut.Find("[data-testid=session-work-add]").Change(item.Number.ToString()));

        cut.WaitForAssertion(() =>
            Assert.Contains("Wire the approval prompt", cut.Find("[data-testid=session-work-chip]").TextContent));

        // And the gate really opened, read back through the store rather than from the screen.
        Assert.True(_work.HasWork(_session));
        Assert.Equal("user", _work.For(_session)[0].LinkedBy);
    }

    [Fact]
    public async Task The_user_can_stop_working_on_an_item()
    {
        var item = await CreateAsync("Wire the approval prompt");
        _work.Link(_session, item.Id, ProjectId.Default, "user");
        using var ctx = Context();

        var cut = ctx.Render<SessionWorkStrip>(p => p.Add(c => c.Session, _session));
        Assert.Single(cut.FindAll("[data-testid=session-work-chip]"));

        await cut.InvokeAsync(() => cut.Find("[data-testid=session-work-remove]").Click());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=session-work-none]")));
        Assert.False(_work.HasWork(_session));
        Assert.Empty(cut.FindAll("[data-testid=session-work-chip]"));
    }

    [Fact]
    public async Task A_link_the_agent_made_appears_without_the_user_reloading()
    {
        // The agent clears the gate for itself, so the strip has to follow a change it did not make.
        var item = await CreateAsync("Written by an agent");
        using var ctx = Context();

        var cut = ctx.Render<SessionWorkStrip>(p => p.Add(c => c.Session, _session));
        Assert.Empty(cut.FindAll("[data-testid=session-work-chip]"));

        await cut.InvokeAsync(() => _work.Link(_session, item.Id, ProjectId.Default, "session:abc"));

        cut.WaitForAssertion(() =>
            Assert.Contains("Written by an agent", cut.Find("[data-testid=session-work-chip]").TextContent));
    }

    [Fact]
    public async Task A_closed_item_is_not_offered_as_something_to_start()
    {
        await CreateAsync("Still open");
        await CreateAsync("Already finished", WorkItemStatus.Done);
        using var ctx = Context();

        var cut = ctx.Render<SessionWorkStrip>(p => p.Add(c => c.Session, _session));

        var options = cut.Find("[data-testid=session-work-add]").TextContent;
        Assert.Contains("Still open", options);
        Assert.DoesNotContain("Already finished", options);
    }
}
