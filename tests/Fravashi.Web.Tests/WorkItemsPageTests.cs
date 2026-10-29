using Bunit;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.WorkItems;
using Fravashi.Web.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 38: the board, the list and the detail view, driven over the REAL local board rather than a stubbed
/// tracker. That matters for the two things this page promises. A drop has to be a domain transition, which a
/// stub would happily fake, and a refusal has to be the model's own sentence, which a stub would have to invent
/// and could then let drift from what the domain actually says.
/// </summary>
public sealed class WorkItemsPageTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-tests", Guid.NewGuid().ToString("N"));

    private readonly SqliteConnectionFactory _factory;
    private readonly ITracker _board;
    private readonly LocalTrackerResolver _resolver;
    private readonly WorkItemChanges _changes = new();
    private readonly Fravashi.Infrastructure.Wiki.SqliteWikiLibrary _wiki;

    public WorkItemsPageTests()
    {
        Directory.CreateDirectory(_directory);
        _factory = new SqliteConnectionFactory(
            new SqliteStoreOptions(Path.Combine(_directory, "test.db"), null, Pooling: false));
        SqliteSchema.EnsureCreated(_factory);
        _resolver = new LocalTrackerResolver(_factory, TimeProvider.System);
        _board = _resolver.For(ProjectId.Default);
        _wiki = new Fravashi.Infrastructure.Wiki.SqliteWikiLibrary(_factory, TimeProvider.System, Fravashi.Domain.Projects.ProjectId.Default);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp folder.
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Fixture
    // ---------------------------------------------------------------------------------------------

    /// <summary>The provider this project follows in a test (backlog 47); null means none, as before.</summary>
    private Fravashi.Application.Mirroring.IProviderGateway? _follows;

    /// <summary>The board's status mapping for a test; null is an install that mapped nothing, as before.</summary>
    private Fravashi.Application.Onboarding.BoardStatusMapping? _mapping;

    /// <summary>The one sync the page and the test's own setup share, so a mirrored item really is mirrored.</summary>
    private Fravashi.Application.Mirroring.ProviderSync? _sync;

    /// <summary>The studio's attention counts for a test (backlog 244); null leaves them unwired, as before.</summary>
    private Fravashi.Application.Sessions.ISessionAttention? _attention;

    private BunitContext Context(string url)
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton<ITrackerResolver>(_resolver);
        ctx.Services.AddSingleton(_sync ??= TestProviderSync.Over(_factory, _follows, _mapping));
        // Backlog 121: a page follows its window's project; these tests have one project.
        ctx.Services.AddSingleton<Fravashi.Application.Projects.ICurrentProject>(Fravashi.Application.Projects.SingleProject.Instance);
        ctx.Services.AddSingleton<Fravashi.Application.Projects.IProjectStore>(new OneProjectStore());
        ctx.Services.AddSingleton<IWorkItemChanges>(_changes);
        // Backlog 39: a description is markdown whose references link, so the page needs the same resolver
        // and renderer the wiki uses.
        // Backlog 121: a page asks for its project's wiki, and what holds an id asks for the by-id view.
        ctx.Services.AddSingleton<Fravashi.Application.Wiki.IWikiLibraries>(new OneWiki(_wiki));
        ctx.Services.AddSingleton<Fravashi.Application.Wiki.IWikiDocuments>(_wiki);
        // Backlog 209: the page reads what the approved design says about its own version through the same
        // gate the agent tool calls.
        ctx.Services.AddSingleton(new Fravashi.Application.Wiki.DesignApproval(_resolver, new OneWiki(_wiki)));
        ctx.Services.AddSingleton(new Fravashi.Infrastructure.Wiki.WikiReferenceResolver(_resolver, new OneWiki(_wiki)));
        ctx.Services.AddSingleton(new Fravashi.Infrastructure.Rendering.WikiMarkdownRenderer());
        // Backlog 82: the board says in one line whether it is a copy of anything, so it needs the
        // mirroring sidecar. With no provider configured every read here is empty, which is the answer.
        ctx.Services.AddSingleton<Fravashi.Application.Mirroring.IMirrorStore>(
            new Fravashi.Infrastructure.Mirroring.SqliteMirrorStore(_factory));
        ctx.Services.AddSingleton(TimeProvider.System);
        // Work item #368: the board's Releases section and the list's "Released in" filter read the project's releases.
        ctx.Services.AddSingleton<Fravashi.Application.Releases.IReleaseStores>(Releases);
        ctx.Services.AddSingleton(new Fravashi.Application.Releases.ReleaseRecorder(Releases, _resolver, new OneWiki(_wiki), _changes));
        if (_attention is not null)
        {
            ctx.Services.AddSingleton(_attention);
        }

        ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo(url);
        return ctx;
    }

    private async Task<WorkItem> CreateAsync(
        string title,
        WorkItemStatus status = WorkItemStatus.Ready,
        WorkItemFlags? flags = null,
        IReadOnlyList<AcceptanceCriterion>? criteria = null,
        string? nextAction = null)
    {
        var created = await _board.CreateAsync(
            new NewWorkItem
            {
                Level = WorkItemLevel.Task,
                Title = title,
                Description = "The problem and the outcome.",
                Status = status,
                Resolution = status switch
                {
                    WorkItemStatus.Done => WorkItemResolution.Completed,
                    WorkItemStatus.Cancelled => WorkItemResolution.WontDo,
                    _ => null,
                },
                Flags = flags ?? WorkItemFlags.None,
                AcceptanceCriteria = criteria ?? [],
                NextAction = nextAction,
            },
            MutationContext.User);

        Assert.Equal(TrackerOutcome.Ok, created.Outcome);
        return created.Value;
    }

    private async Task<WorkItem> ReloadAsync(WorkItem item) => (await _board.GetAsync(item.Id))!;

    private Fravashi.Infrastructure.Releases.SqliteReleaseStores Releases =>
        _releases ??= new Fravashi.Infrastructure.Releases.SqliteReleaseStores(_factory, TimeProvider.System);

    private Fravashi.Infrastructure.Releases.SqliteReleaseStores? _releases;

    /// <summary>Records a release of <paramref name="items"/> straight through the store, as saving one from the dialog does.</summary>
    private async Task ReleaseAsync(string version, params WorkItem[] items)
    {
        var saved = await Releases.For(ProjectId.Default).CreateAsync(new Fravashi.Application.Releases.NewRelease
        {
            Version = version,
            Tag = version,
            Notes = "# Release " + version,
            Items = [.. items.Select(i => i.Id)],
        });
        Assert.True(saved.Saved, saved.Message);
    }

    // ---------------------------------------------------------------------------------------------
    // Releases (work item #368)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_released_done_item_is_archived_off_the_done_column_and_an_unreleased_one_stays()
    {
        var shipped = await CreateAsync("Shipped in the release", WorkItemStatus.Done);
        await CreateAsync("Waiting for the next release", WorkItemStatus.Done);
        await ReleaseAsync("v0.9.97", shipped);

        using var ctx = Context("http://localhost/board");
        var cut = ctx.Render<WorkItemsPage>();

        cut.WaitForAssertion(() =>
        {
            var done = cut.Find("[data-testid=board-column-done]");
            Assert.Contains("Waiting for the next release", done.TextContent);
            Assert.DoesNotContain("Shipped in the release", done.TextContent);
            Assert.Contains("1 Done item not yet released", cut.Find("[data-testid=board-releasable-count]").TextContent);
        });
    }

    [Fact]
    public async Task The_list_finds_an_archived_item_by_its_release_and_by_search_but_not_by_default()
    {
        var shipped = await CreateAsync("Shipped in the release", WorkItemStatus.Done);
        await CreateAsync("Waiting for the next release", WorkItemStatus.Done);
        await ReleaseAsync("v0.9.97", shipped);

        using var ctx = Context("http://localhost/workitems");
        var cut = ctx.Render<WorkItemsPage>();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=board-list-row]")));

        await cut.InvokeAsync(() => cut.Find("[data-testid=board-released-in]")
            .Change(new ChangeEventArgs { Value = "v0.9.97" }));
        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("[data-testid=board-list-row]");
            Assert.Single(rows);
            Assert.Contains("Shipped in the release", rows[0].TextContent);
            Assert.Contains("v0.9.97", rows[0].TextContent);
        });

        await cut.InvokeAsync(() => cut.Find("[data-testid=board-released-in]")
            .Change(new ChangeEventArgs { Value = string.Empty }));
        await cut.InvokeAsync(() => cut.Find("[data-testid=board-search]").Input("Shipped"));
        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("[data-testid=board-list-row]");
            Assert.Single(rows);
            Assert.Contains("Shipped in the release", rows[0].TextContent);
        });
    }

    [Fact]
    public async Task An_archived_item_opens_by_its_number_and_says_which_release_carried_it()
    {
        var shipped = await CreateAsync("Shipped in the release", WorkItemStatus.Done);
        await ReleaseAsync("v0.9.97", shipped);

        using var ctx = Context($"http://localhost/workitems/{shipped.Number}");
        var cut = ctx.Render<WorkItemsPage>(p => p.Add(x => x.Number, shipped.Number));

        cut.WaitForAssertion(() =>
        {
            var chip = cut.Find("[data-testid=work-item-released-in]");
            Assert.Contains("Released in v0.9.97", chip.TextContent);
            Assert.Equal("/releases?version=v0.9.97", chip.GetAttribute("href"));
        });
    }

    // ---------------------------------------------------------------------------------------------
    // The list
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_list_shows_every_item_and_the_search_narrows_it_by_title()
    {
        await CreateAsync("Wire the approval prompt");
        await CreateAsync("Rename the workspace column");

        using var ctx = Context("http://localhost/workitems");
        var cut = ctx.Render<WorkItemsPage>();

        Assert.Equal(2, cut.FindAll("[data-testid=board-list-row]").Count);

        await cut.InvokeAsync(() => cut.Find("[data-testid=board-search]").Input("approval"));

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("[data-testid=board-list-row]");
            Assert.Single(rows);
            Assert.Contains("Wire the approval prompt", rows[0].TextContent);
        });
    }

    [Fact]
    public async Task Unticking_a_status_removes_that_column_from_the_list()
    {
        await CreateAsync("Ready work");
        await CreateAsync("Finished work", WorkItemStatus.Done);

        using var ctx = Context("http://localhost/workitems");
        var cut = ctx.Render<WorkItemsPage>();

        Assert.Equal(2, cut.FindAll("[data-testid=board-list-row]").Count);

        await cut.InvokeAsync(() => cut.Find("[data-testid=board-status-done]")
            .TriggerEvent("onchange", new ChangeEventArgs { Value = false }));

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("[data-testid=board-list-row]");
            Assert.Single(rows);
            Assert.Contains("Ready work", rows[0].TextContent);
        });
    }

    // ---------------------------------------------------------------------------------------------
    // The board
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_board_puts_each_item_in_the_column_of_its_canonical_status()
    {
        await CreateAsync("Ready work");
        await CreateAsync("Running work", WorkItemStatus.InProgress);

        using var ctx = Context("http://localhost/board");
        var cut = ctx.Render<WorkItemsPage>();

        // Every canonical status is a column, whether or not anything is in it: a board that hides empty
        // columns hides where work is allowed to go next.
        Assert.Equal(WorkItemStatuses.All.Count, cut.FindAll(".board-column").Count);

        Assert.Contains("Ready work", cut.Find("[data-testid=board-column-ready]").TextContent);
        Assert.Contains("Running work", cut.Find("[data-testid=board-column-in_progress]").TextContent);
        Assert.DoesNotContain("Running work", cut.Find("[data-testid=board-column-ready]").TextContent);
    }

    [Fact]
    public async Task A_card_dropped_on_another_column_is_transitioned_in_the_tracker()
    {
        var item = await CreateAsync("Ready work");

        using var ctx = Context("http://localhost/board");
        var cut = ctx.Render<WorkItemsPage>();

        await cut.InvokeAsync(() =>
        {
            cut.Find("[data-testid=board-card]").TriggerEvent("ondragstart", new DragEventArgs());
            cut.Find("[data-testid=board-column-in_progress]").TriggerEvent("ondrop", new DragEventArgs());
        });

        cut.WaitForAssertion(() =>
            Assert.Contains("Ready work", cut.Find("[data-testid=board-column-in_progress]").TextContent));

        var stored = await ReloadAsync(item);
        Assert.Equal(WorkItemStatus.InProgress, stored.Status);
    }

    // ---- backlog 47 increment 5 and backlog 48: what a drop means on the provider ------------------------

    /// <summary>An item that is really on the provider, which is the only kind a move is sent about.</summary>
    private async Task<WorkItem> MirroredAsync(string title)
    {
        var item = await CreateAsync(title);
        await _sync!.SendAsync(ProjectId.Default, item.Id);
        await _sync.SyncNowAsync(ProjectId.Default);
        return await ReloadAsync(item);
    }

    [Fact]
    public async Task A_drop_into_a_column_the_provider_names_one_way_is_queued_without_asking()
    {
        _follows = new AcceptingProvider();
        _mapping = new Fravashi.Application.Onboarding.BoardStatusMapping("jira",
            [new Fravashi.Application.Onboarding.StatusMappingEntry("In Progress", "Doing")]);
        using var ctx = Context("http://localhost/board");
        await MirroredAsync("Unambiguous work");
        var cut = ctx.Render<WorkItemsPage>();

        await cut.InvokeAsync(() =>
        {
            cut.Find("[data-testid=board-card]").TriggerEvent("ondragstart", new DragEventArgs());
            cut.Find("[data-testid=board-column-in_progress]").TriggerEvent("ondrop", new DragEventArgs());
        });

        cut.WaitForAssertion(() =>
            Assert.Contains("'Doing'", cut.Find("[data-testid=board-move-queued]").TextContent));
        Assert.Empty(cut.FindAll("[data-testid=board-move-ask]"));
    }

    [Fact]
    public async Task A_drop_into_a_column_the_provider_names_three_ways_asks_which_state_is_meant()
    {
        // Backlog 48's acceptance, on the surface it names: our statuses are a projection, so dropping a card
        // in Done can mean any of somebody's finished states, and the product does not pick one quietly.
        _follows = new AcceptingProvider();
        _mapping = new Fravashi.Application.Onboarding.BoardStatusMapping("jira",
        [
            new Fravashi.Application.Onboarding.StatusMappingEntry("Done", "Shipped"),
            new Fravashi.Application.Onboarding.StatusMappingEntry("Done", "Closed"),
        ]);
        using var ctx = Context("http://localhost/board");
        var item = await MirroredAsync("Finished somehow");
        var cut = ctx.Render<WorkItemsPage>();

        await cut.InvokeAsync(() =>
        {
            cut.Find("[data-testid=board-card]").TriggerEvent("ondragstart", new DragEventArgs());
            cut.Find("[data-testid=board-column-done]").TriggerEvent("ondrop", new DragEventArgs());
        });

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=board-move-ask]")));
        var offered = cut.FindAll("[data-testid=board-move-choice]").Select(e => e.TextContent.Trim()).ToList();
        Assert.Equal(["Shipped", "Closed"], offered);
        Assert.Equal(WorkItemStatus.Done, (await ReloadAsync(item)).Status);

        await cut.InvokeAsync(() => cut.FindAll("[data-testid=board-move-choice]")[1].Click());

        cut.WaitForAssertion(() =>
            Assert.Contains("'Closed'", cut.Find("[data-testid=board-move-queued]").TextContent));
        Assert.Empty(cut.FindAll("[data-testid=board-move-ask]"));
    }

    [Fact]
    public async Task Deciding_later_leaves_the_move_here_and_sends_nothing()
    {
        // The question can be walked away from. The move on this board stands, nothing is queued, and the
        // next sync raises the same question on the sync page, so nothing is lost by not answering now.
        _follows = new AcceptingProvider();
        _mapping = new Fravashi.Application.Onboarding.BoardStatusMapping("jira",
        [
            new Fravashi.Application.Onboarding.StatusMappingEntry("Done", "Shipped"),
            new Fravashi.Application.Onboarding.StatusMappingEntry("Done", "Closed"),
        ]);
        using var ctx = Context("http://localhost/board");
        var item = await MirroredAsync("Thought about later");
        var cut = ctx.Render<WorkItemsPage>();

        await cut.InvokeAsync(() =>
        {
            cut.Find("[data-testid=board-card]").TriggerEvent("ondragstart", new DragEventArgs());
            cut.Find("[data-testid=board-column-done]").TriggerEvent("ondrop", new DragEventArgs());
        });
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=board-move-ask]")));

        await cut.InvokeAsync(() => cut.Find("[data-testid=board-move-later]").Click());

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=board-move-ask]")));
        Assert.Empty(cut.FindAll("[data-testid=board-move-queued]"));
        Assert.Equal(WorkItemStatus.Done, (await ReloadAsync(item)).Status);
    }

    [Fact]
    public async Task A_drop_on_a_board_that_follows_no_provider_says_nothing_about_one()
    {
        var item = await CreateAsync("Local only");

        using var ctx = Context("http://localhost/board");
        var cut = ctx.Render<WorkItemsPage>();

        await cut.InvokeAsync(() =>
        {
            cut.Find("[data-testid=board-card]").TriggerEvent("ondragstart", new DragEventArgs());
            cut.Find("[data-testid=board-column-in_progress]").TriggerEvent("ondrop", new DragEventArgs());
        });

        cut.WaitForAssertion(() =>
            Assert.Equal(WorkItemStatus.InProgress, ReloadAsync(item).GetAwaiter().GetResult().Status));
        Assert.Empty(cut.FindAll("[data-testid=board-move-ask]"));
        Assert.Empty(cut.FindAll("[data-testid=board-move-queued]"));
    }

    [Fact]
    public async Task A_refused_drop_shows_the_refusal_and_puts_the_card_back_where_the_tracker_says_it_is()
    {
        // The reachable refusal on a board is a stale version: two views of the same project, one of them
        // moved the item first. A card that sprang back with no explanation would be the worst outcome, so
        // the page says what happened and reloads rather than swallowing it.
        var item = await CreateAsync("Contested work");

        using var ctx = Context("http://localhost/board");
        var cut = ctx.Render<WorkItemsPage>();

        var elsewhere = await _board.TransitionAsync(
            item.Id, WorkItemStatus.InReview, null, null, null, item.Version, MutationContext.User);
        Assert.Equal(TrackerOutcome.Ok, elsewhere.Outcome);

        await cut.InvokeAsync(() =>
        {
            cut.Find("[data-testid=board-card]").TriggerEvent("ondragstart", new DragEventArgs());
            cut.Find("[data-testid=board-column-in_progress]").TriggerEvent("ondrop", new DragEventArgs());
        });

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=board-error]")));

        Assert.Equal(WorkItemStatus.InReview, (await ReloadAsync(item)).Status);
        Assert.Contains("Contested work", cut.Find("[data-testid=board-column-in_review]").TextContent);
        Assert.DoesNotContain("Contested work", cut.Find("[data-testid=board-column-in_progress]").TextContent);
    }

    [Fact]
    public async Task A_card_says_whether_it_can_start_and_the_sidebar_lists_what_can()
    {
        await CreateAsync("Free to start", criteria: [new AcceptanceCriterion(string.Empty, "It works.")]);
        await CreateAsync("Still in triage", WorkItemStatus.Triage);

        using var ctx = Context("http://localhost/board");
        var cut = ctx.Render<WorkItemsPage>();

        Assert.Single(cut.FindAll("[data-testid=board-card-startable]"));
        Assert.Single(cut.FindAll("[data-testid=board-card-held]"));

        var startable = cut.FindAll("[data-testid=board-startable-item]");
        Assert.Single(startable);
        Assert.Contains("Free to start", startable[0].TextContent);
    }

    [Theory]
    [InlineData(WorkItemStatus.Triage)]
    [InlineData(WorkItemStatus.Backlog)]
    [InlineData(WorkItemStatus.Ready)]
    public async Task A_card_that_has_not_started_says_whether_it_can_start(WorkItemStatus status)
    {
        // Work item #363: the readiness line is news only before the work starts.
        await CreateAsync("Not started", status);

        using var ctx = Context("http://localhost/board");
        var cut = ctx.Render<WorkItemsPage>();

        var card = cut.Find("[data-testid=board-card]");
        Assert.Single(card.QuerySelectorAll("[data-testid=board-card-startable], [data-testid=board-card-held]"));
    }

    [Theory]
    [InlineData(WorkItemStatus.InProgress)]
    [InlineData(WorkItemStatus.InReview)]
    [InlineData(WorkItemStatus.Done)]
    [InlineData(WorkItemStatus.Cancelled)]
    public async Task A_card_that_has_started_or_finished_has_no_readiness_line(WorkItemStatus status)
    {
        // Work item #363, seen on the board on 2026-10-23: every In Review and Done card said "It is not Ready yet."
        await CreateAsync("Past starting", status);

        using var ctx = Context("http://localhost/board");
        var cut = ctx.Render<WorkItemsPage>();

        var card = cut.Find("[data-testid=board-card]");
        Assert.Empty(card.QuerySelectorAll("[data-testid=board-card-startable], [data-testid=board-card-held]"));
        Assert.DoesNotContain("Ready yet", card.TextContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WorkItemStatus.Triage, "board-card-triage")]
    [InlineData(WorkItemStatus.Backlog, "board-card-backlog")]
    [InlineData(WorkItemStatus.Ready, "board-card-ready")]
    [InlineData(WorkItemStatus.InProgress, "board-card-in_progress")]
    [InlineData(WorkItemStatus.InReview, "board-card-in_review")]
    [InlineData(WorkItemStatus.Done, "board-card-done")]
    [InlineData(WorkItemStatus.Cancelled, "board-card-cancelled")]
    public async Task A_card_carries_its_status_as_the_class_its_accent_is_drawn_from(WorkItemStatus status, string expected)
    {
        // Work item #363: the accent is the item's status, not one green for every card.
        await CreateAsync("Coloured by status", status);

        using var ctx = Context("http://localhost/board");
        var cut = ctx.Render<WorkItemsPage>();

        var classes = cut.Find("[data-testid=board-card]").ClassList;
        Assert.Contains("board-card", classes);
        Assert.Contains(expected, classes);
        Assert.Single(classes, c => c.StartsWith("board-card-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_item_created_elsewhere_appears_without_anyone_reloading_the_page()
    {
        // Backlog 41's acceptance: an agent works through the studio's tool server and the USER SEES the change
        // appear on the board. The signal carries no payload, so the page re-reads through the port.
        using var ctx = Context("http://localhost/board");
        var cut = ctx.Render<WorkItemsPage>();

        Assert.Empty(cut.FindAll("[data-testid=board-card]"));

        await CreateAsync("Written by an agent");
        await cut.InvokeAsync(() => _changes.Announce(ProjectId.Default));

        cut.WaitForAssertion(() =>
            Assert.Contains("Written by an agent", cut.Find("[data-testid=board-column-ready]").TextContent));
    }

    // ---------------------------------------------------------------------------------------------
    // The detail view
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_detail_view_shows_the_agent_layer_and_the_recorded_evidence()
    {
        var item = await CreateAsync(
            "Wire the approval prompt",
            criteria: [new AcceptanceCriterion(string.Empty, "A refused call is shown, not swallowed.")],
            nextAction: "Draw the prompt.");

        var reloaded = await ReloadAsync(item);
        var recorded = await _board.RecordEvidenceAsync(
            reloaded.Id,
            new NewEvidence
            {
                Kind = EvidenceKind.TestRun,
                Result = EvidenceResult.Pass,
                Summary = "The whole suite is green.",
                Criterion = reloaded.AcceptanceCriteria[0].Id,
            },
            MutationContext.User);
        Assert.Equal(TrackerOutcome.Ok, recorded.Outcome);

        using var ctx = Context($"http://localhost/workitems/{item.Number}");
        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Number));

        Assert.Contains(
            "A refused call is shown, not swallowed.",
            cut.Find("[data-testid=work-item-criterion]").TextContent);
        Assert.Contains("Draw the prompt.", cut.Find("[data-testid=work-item-next-action-text]").TextContent);
        Assert.Contains("The whole suite is green.", cut.Find("[data-testid=work-item-evidence]").TextContent);
        Assert.Contains("can start now", cut.Find("[data-testid=work-item-verdict]").TextContent);
    }

    [Theory]
    [InlineData(WorkItemStatus.InProgress)]
    [InlineData(WorkItemStatus.Done)]
    [InlineData(WorkItemStatus.Cancelled)]
    public async Task An_item_that_has_started_or_finished_is_not_asked_whether_it_can_start(WorkItemStatus status)
    {
        // Seen on the imported backlog in v0.9.33: every Done item said "It is not Ready yet."
        var item = await CreateAsync("Past starting", status);
        using var ctx = Context($"http://localhost/workitems/{item.Number}");

        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Number));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=work-item-detail]")));
        Assert.Empty(cut.FindAll("[data-testid=work-item-verdict]"));
    }

    [Fact]
    public async Task The_version_chip_shows_the_items_version_number()
    {
        // Seen in the installed v0.9.31 on 2026-09-23: the chip said "v@Item.Version", because Razor reads
        // "v@Item" as an email address and prints it literally. No test looked at the chip's text until then.
        var item = await CreateAsync("Versioned");
        using var ctx = Context($"http://localhost/workitems/{item.Number}");

        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Number));

        cut.WaitForAssertion(() => Assert.Equal($"v{item.Version}", cut.Find("[data-testid=work-item-version]").TextContent.Trim()));
    }

    // ---- backlog 47: sending an item to the project's provider ---------------------------------------

    [Fact]
    public async Task With_no_provider_followed_an_item_says_nothing_about_one()
    {
        var item = await CreateAsync("Local only");
        using var ctx = Context($"http://localhost/workitems/{item.Number}");

        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Number));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=work-item-detail]")));
        Assert.Empty(cut.FindAll("[data-testid=work-item-provider]"));
    }

    [Fact]
    public async Task A_board_that_follows_a_provider_and_has_sent_nothing_says_so_rather_than_that_there_is_none()
    {
        // Found on the imported Fravashi project in v0.9.33: the board said "there is no provider behind it"
        // while every item on it offered Send to Jira.
        _follows = new AcceptingProvider();
        await CreateAsync("Not sent yet");
        using var ctx = Context("http://localhost/board");

        var cut = ctx.Render<WorkItemsPage>();

        cut.WaitForAssertion(() => Assert.Equal(
            "This board follows Jira, and nothing on it has been sent there or imported from there yet.",
            cut.Find("[data-testid=board-provider-line]").TextContent.Trim()));
    }

    [Fact]
    public async Task A_board_that_stopped_following_its_provider_is_the_source_of_truth_not_its_worst_old_record()
    {
        // Seen after this repository's project was set to follow no provider (backlog 142): the board line reported a
        // tombstoned item on Azure DevOps as if the board still followed it.
        var item = await CreateAsync("Once on a provider");
        await new Fravashi.Infrastructure.Mirroring.SqliteMirrorStore(_factory).TombstoneAsync(
            ProjectId.Default, item.Id, "Azure DevOps no longer has 154.", DateTimeOffset.UtcNow);
        using var ctx = Context("http://localhost/board");

        var cut = ctx.Render<WorkItemsPage>();

        cut.WaitForAssertion(() => Assert.StartsWith(
            Fravashi.Domain.Mirroring.MirrorFreshness.Local.Sentence,
            cut.Find("[data-testid=board-provider-line]").TextContent.Trim()));
        Assert.DoesNotContain("154", cut.Find("[data-testid=board-provider-line]").TextContent);
    }

    [Fact]
    public async Task A_board_that_follows_no_provider_says_it_is_the_source_of_truth()
    {
        await CreateAsync("Local only");
        using var ctx = Context("http://localhost/board");

        var cut = ctx.Render<WorkItemsPage>();

        cut.WaitForAssertion(() => Assert.Equal(
            Fravashi.Domain.Mirroring.MirrorFreshness.Local.Sentence,
            cut.Find("[data-testid=board-provider-line]").TextContent.Trim()));
    }

    [Fact]
    public async Task An_item_can_be_sent_once_and_the_page_then_says_it_is_waiting()
    {
        var provider = new AcceptingProvider();
        _follows = provider;
        var item = await CreateAsync("Send me");
        using var ctx = Context($"http://localhost/workitems/{item.Number}");
        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Number));
        cut.WaitForAssertion(() => Assert.Equal("Not on Jira.", cut.Find("[data-testid=work-item-provider-line]").TextContent.Trim()));

        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-send]").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("Waiting to be sent to Jira", cut.Find("[data-testid=work-item-provider-line]").TextContent);
            Assert.Empty(cut.FindAll("[data-testid=work-item-send]"));
        });
        Assert.Empty(provider.Created);   // sending only queues; Sync now is what sends
    }

    [Fact]
    public async Task A_reference_written_in_a_description_is_a_working_link()
    {
        // Backlog 39: the syntax has to behave the same wherever it is written, or it is a rule people
        // learn on one surface and are surprised by on the other.
        var target = await CreateAsync("The thing referred to");
        var item = await _board.CreateAsync(
            new NewWorkItem
            {
                Level = WorkItemLevel.Task,
                Title = "The one that refers",
                Description = $"This finishes what #{target.Number} started, unlike #4321.",
            },
            MutationContext.User);
        Assert.Equal(TrackerOutcome.Ok, item.Outcome);

        using var ctx = Context($"http://localhost/workitems/{item.Value.Number}");
        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Value.Number));

        var description = cut.Find("[data-testid=work-item-description-text]").InnerHtml;
        Assert.Contains($"href=\"/workitems/{target.Number}\"", description);
        Assert.Contains("wiki-ref-broken", description);
    }

    [Fact]
    public async Task An_approved_design_is_shown_on_the_work_item_as_a_link_into_the_wiki()
    {
        // Backlog 54: the approved design is the one thing the verifier checks the built result against,
        // so it is shown on its own rather than buried in the reference list as a number.
        var item = await CreateAsync("Redesign the welcome page");
        var design = await _wiki.CreateAsync(new Fravashi.Application.Wiki.NewWikiItem
        {
            Type = Fravashi.Domain.Wiki.WikiItemType.Draft,
            Title = "The new welcome page",
            Body = $"This is the design for #{item.Number}.",
        });
        Assert.True(design.IsValid);

        var approval = new Fravashi.Application.Wiki.DesignApproval(_resolver, new OneWiki(_wiki));
        Assert.True((await approval.ApproveAsync(ProjectId.Default, design.Value.Number, item.Number)).Approved);

        using var ctx = Context($"http://localhost/workitems/{item.Number}");
        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Number));

        var shown = cut.Find("[data-testid=work-item-approved-design]");
        Assert.Contains("The new welcome page", shown.TextContent);

        await cut.InvokeAsync(() => shown.Click());
        var nav = ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        Assert.EndsWith($"/wiki/{design.Value.Number}", nav.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_item_with_no_approved_design_shows_no_such_section()
    {
        var item = await CreateAsync("Nothing approved");

        using var ctx = Context($"http://localhost/workitems/{item.Number}");
        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Number));

        Assert.Empty(cut.FindAll("[data-testid=work-item-approved-design]"));
    }

    // ---- backlog 209: the approved design is pinned to its version ------------------------------------

    [Fact]
    public async Task The_approved_design_says_what_version_it_was_approved_at()
    {
        var item = await CreateAsync("Redesign the welcome page");
        var design = await _wiki.CreateAsync(new Fravashi.Application.Wiki.NewWikiItem
        {
            Type = Fravashi.Domain.Wiki.WikiItemType.Design,
            Title = "The new welcome page",
            Body = $"For #{item.Number}.",
        });

        var approval = new Fravashi.Application.Wiki.DesignApproval(_resolver, new OneWiki(_wiki));
        Assert.True((await approval.ApproveAsync(ProjectId.Default, design.Value.Number, item.Number)).Approved);

        using var ctx = Context($"http://localhost/workitems/{item.Number}");
        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Number));

        Assert.Equal("Approved at version 1.", cut.Find("[data-testid=work-item-approved-design-version]").TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid=work-item-approved-design-changed]"));
    }

    [Fact]
    public async Task When_the_design_moves_on_the_item_says_so_and_a_person_can_reapprove_it()
    {
        var item = await CreateAsync("Redesign the welcome page");
        var design = await _wiki.CreateAsync(new Fravashi.Application.Wiki.NewWikiItem
        {
            Type = Fravashi.Domain.Wiki.WikiItemType.Design,
            Title = "The new welcome page",
            Body = $"For #{item.Number}.",
        });

        var approval = new Fravashi.Application.Wiki.DesignApproval(_resolver, new OneWiki(_wiki));
        Assert.True((await approval.ApproveAsync(ProjectId.Default, design.Value.Number, item.Number)).Approved);

        // The host edits the approved design after approval, exactly as backlog 209 was opened for.
        var edited = await _wiki.UpdateAsync(
            design.Value.Id,
            new Fravashi.Application.Wiki.WikiItemEdit { Body = $"Nine changes later. For #{item.Number}." },
            design.Value.Version);
        Assert.True(edited.IsValid);

        using var ctx = Context($"http://localhost/workitems/{item.Number}");
        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Number));

        cut.WaitForAssertion(() =>
            Assert.Contains("changed since", cut.Find("[data-testid=work-item-approved-design-changed]").TextContent));

        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-reapprove-design]").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(
                "Approved at version 2.",
                cut.Find("[data-testid=work-item-approved-design-version]").TextContent.Trim());
            Assert.Empty(cut.FindAll("[data-testid=work-item-approved-design-changed]"));
        });
    }

    [Fact]
    public async Task A_reference_that_predates_pinning_shows_no_version_line_and_no_changed_since()
    {
        var item = await CreateAsync("Redesign the welcome page");
        var design = await _wiki.CreateAsync(new Fravashi.Application.Wiki.NewWikiItem
        {
            Type = Fravashi.Domain.Wiki.WikiItemType.Design,
            Title = "The new welcome page",
            Body = $"For #{item.Number}.",
        });

        // An approved-design reference the way one looked before backlog 209: no version at all.
        var updated = await _board.UpdateAsync(
            item.Id,
            new WorkItemPatch
            {
                Refs = Optional<IReadOnlyList<ArtifactRef>>.Of(
                [
                    new ArtifactRef(
                        string.Empty,
                        ArtifactRefRole.ApprovedDesign,
                        ArtifactRefKind.Design,
                        design.Value.Number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Label: design.Value.Title),
                ]),
            },
            item.Version,
            MutationContext.User);
        Assert.Equal(TrackerOutcome.Ok, updated.Outcome);

        using var ctx = Context($"http://localhost/workitems/{item.Number}");
        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Number));

        cut.WaitForAssertion(() => cut.Find("[data-testid=work-item-approved-design]"));
        Assert.Empty(cut.FindAll("[data-testid=work-item-approved-design-version]"));
        Assert.Empty(cut.FindAll("[data-testid=work-item-approved-design-changed]"));
    }

    [Fact]
    public async Task A_link_reads_from_both_ends_and_each_end_opens_the_other()
    {
        var blocker = await CreateAsync("The dependency");
        var blocked = await CreateAsync("The dependent");

        var linked = await _board.LinkAsync(
            blocker.Id, WorkItemLinkType.Blocks, blocked.Id, MutationContext.User);
        Assert.Equal(TrackerOutcome.Ok, linked.Outcome);

        // Requested 2026-10-22 (related items sidebar): each reading is its own group in the sidebar, and a row is
        // a real link to the other end.
        using (var ctx = Context($"http://localhost/workitems/{blocker.Number}"))
        {
            var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, blocker.Number));
            var row = cut.Find("[data-testid=work-item-related-blocks] [data-testid=work-item-link]");
            Assert.Contains("The dependent", row.TextContent);
            Assert.Equal($"/workitems/{blocked.Number}", row.GetAttribute("href"));
        }

        using var back = Context($"http://localhost/workitems/{blocked.Number}");
        var other = back.Render<WorkItemsPage>(p => p.Add(c => c.Number, blocked.Number));
        var backRow = other.Find("[data-testid=work-item-related-blocked_by] [data-testid=work-item-link]");
        Assert.Contains("The dependency", backRow.TextContent);
        Assert.Equal($"/workitems/{blocker.Number}", backRow.GetAttribute("href"));
    }

    // ---- requested 2026-10-22 (related items sidebar) ----------------------------------------------

    [Theory]
    [InlineData(WorkItemLinkType.BlockedBy)]
    [InlineData(WorkItemLinkType.Blocks)]
    [InlineData(WorkItemLinkType.Relates)]
    [InlineData(WorkItemLinkType.Duplicates)]
    [InlineData(WorkItemLinkType.DuplicatedBy)]
    [InlineData(WorkItemLinkType.Supersedes)]
    [InlineData(WorkItemLinkType.SupersededBy)]
    public async Task Every_link_kind_is_listed_in_the_sidebar_under_its_own_heading_with_the_other_items_status(
        WorkItemLinkType reading)
    {
        var item = await CreateAsync("The item being read");
        var other = await CreateAsync("The other end", WorkItemStatus.InProgress);
        var linked = await _board.LinkAsync(item.Id, reading, other.Id, MutationContext.User);
        Assert.Equal(TrackerOutcome.Ok, linked.Outcome);

        using var ctx = Context($"http://localhost/workitems/{item.Number}");
        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Number));

        var group = cut.Find($"[data-testid=work-item-related-{StableNames.ToName(reading)}]");
        var link = group.QuerySelector("[data-testid=work-item-link]")!;
        Assert.Equal($"/workitems/{other.Number}", link.GetAttribute("href"));
        Assert.Contains($"#{other.Number} The other end", link.TextContent);
        Assert.Equal("In progress", group.QuerySelector("[data-testid=work-item-related-status]")!.TextContent.Trim());
        Assert.Empty(cut.FindAll("[data-testid=work-item-related-empty]"));

        // Exactly one row: the same edge is not listed under its other reading as well.
        Assert.Single(cut.FindAll("[data-testid=work-item-related] [data-testid=work-item-link]"));
    }

    [Fact]
    public async Task The_parent_and_the_children_are_listed_in_the_sidebar_and_each_links_to_its_item()
    {
        var parent = await CreateHierarchyItemAsync("The increment", WorkItemLevel.Increment, null);
        var child = await CreateHierarchyItemAsync("A task under it", WorkItemLevel.Task, parent.Id);

        using (var ctx = Context($"http://localhost/workitems/{child.Number}"))
        {
            var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, child.Number));
            var up = cut.Find("[data-testid=work-item-related-parent] [data-testid=work-item-link]");
            Assert.Equal($"/workitems/{parent.Number}", up.GetAttribute("href"));
            Assert.Contains("The increment", up.TextContent);
        }

        using var down = Context($"http://localhost/workitems/{parent.Number}");
        var page = down.Render<WorkItemsPage>(p => p.Add(c => c.Number, parent.Number));
        var children = page.Find("[data-testid=work-item-related-children]");
        Assert.Contains("Children (1)", children.TextContent);
        var link = children.QuerySelector("[data-testid=work-item-link]")!;
        Assert.Equal($"/workitems/{child.Number}", link.GetAttribute("href"));
        Assert.Contains("A task under it", link.TextContent);
    }

    [Fact]
    public async Task The_plan_and_every_other_related_wiki_document_are_listed_with_working_links_plan_first()
    {
        var item = await CreateAsync("Planned work");

        // Created first so it has the lower number: the plan is still listed above it.
        var report = await _wiki.CreateAsync(new Fravashi.Application.Wiki.NewWikiItem
        {
            Type = Fravashi.Domain.Wiki.WikiItemType.Report,
            Title = "What the trial showed",
            Body = $"Seen while working on #{item.Number}.",
        });
        var plan = await _wiki.CreateAsync(new Fravashi.Application.Wiki.NewWikiItem
        {
            Type = Fravashi.Domain.Wiki.WikiItemType.Plan,
            Title = "How the work is done",
            Body = "Steps.",
            Links = [new Fravashi.Domain.Wiki.WikiLink(
                Fravashi.Domain.Wiki.WikiLinkKind.WorkItem,
                item.Number.ToString(System.Globalization.CultureInfo.InvariantCulture))],
        });
        Assert.True(report.IsValid);
        Assert.True(plan.IsValid);

        using var ctx = Context($"http://localhost/workitems/{item.Number}");
        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Number));

        var documents = cut.FindAll("[data-testid=work-item-related-wiki] [data-testid=work-item-related-wiki-item]");
        var relations = cut.FindAll("[data-testid=work-item-related-wiki] [data-testid=work-item-related-wiki-relation]");
        Assert.Equal(2, documents.Count);
        Assert.Equal($"/wiki/{plan.Value.Number}", documents[0].GetAttribute("href"));
        Assert.Contains($"@{plan.Value.Number} How the work is done", documents[0].TextContent);
        Assert.Equal("plan", relations[0].TextContent.Trim());
        Assert.Equal($"/wiki/{report.Value.Number}", documents[1].GetAttribute("href"));
        Assert.Equal("report, mentions this item", relations[1].TextContent.Trim());
    }

    [Fact]
    public async Task A_document_the_item_names_in_its_agent_layer_is_listed_with_its_role()
    {
        var item = await CreateAsync("Redesign the welcome page");
        var design = await _wiki.CreateAsync(new Fravashi.Application.Wiki.NewWikiItem
        {
            Type = Fravashi.Domain.Wiki.WikiItemType.Design,
            Title = "The new welcome page",
            Body = "No reference back to the item.",
        });
        var updated = await _board.UpdateAsync(
            item.Id,
            new WorkItemPatch
            {
                Refs = Optional<IReadOnlyList<ArtifactRef>>.Of(
                [
                    new ArtifactRef(
                        string.Empty,
                        ArtifactRefRole.ApprovedDesign,
                        ArtifactRefKind.Design,
                        design.Value.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ]),
            },
            item.Version,
            MutationContext.User);
        Assert.Equal(TrackerOutcome.Ok, updated.Outcome);

        using var ctx = Context($"http://localhost/workitems/{item.Number}");
        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Number));

        var document = cut.Find("[data-testid=work-item-related-wiki] [data-testid=work-item-related-wiki-item]");
        Assert.Equal($"/wiki/{design.Value.Number}", document.GetAttribute("href"));
        Assert.Contains("The new welcome page", document.TextContent);
        Assert.Equal(
            "approved design",
            cut.Find("[data-testid=work-item-related-wiki] [data-testid=work-item-related-wiki-relation]").TextContent.Trim());
    }

    [Fact]
    public async Task A_linked_conversation_is_listed_in_the_sidebar_and_links_to_its_session()
    {
        var item = await CreateAsync("Worked on in a conversation");
        var session = new Fravashi.Domain.AgentSessions.AgentSessionId(Guid.NewGuid());

        _attention = new OneSessionAttention(item.Id, session);

        using var ctx = Context($"http://localhost/workitems/{item.Number}");
        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Number));

        var link = cut.Find("[data-testid=work-item-sessions] [data-testid=work-item-session-link]");
        Assert.Equal($"/sessions/{session.Value:D}", link.GetAttribute("href"));
        Assert.Contains(session.Value.ToString("D")[..8], link.TextContent);
        Assert.Contains("Conversations (1)", cut.Find("[data-testid=work-item-sessions]").TextContent);
    }

    [Fact]
    public async Task Recorded_evidence_is_listed_in_the_sidebar_and_jumps_to_its_full_record()
    {
        var item = await CreateAsync("Checked work");
        var recorded = await _board.RecordEvidenceAsync(
            item.Id,
            new NewEvidence { Kind = EvidenceKind.TestRun, Result = EvidenceResult.Pass, Summary = "The suite is green." },
            MutationContext.User);
        Assert.Equal(TrackerOutcome.Ok, recorded.Outcome);

        using var ctx = Context($"http://localhost/workitems/{item.Number}");
        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Number));

        var entry = cut.Find("[data-testid=work-item-related-evidence] [data-testid=work-item-related-evidence-entry]");
        Assert.Contains("The suite is green.", entry.TextContent);
        var href = entry.GetAttribute("href")!;
        Assert.StartsWith($"/workitems/{item.Number}#work-item-evidence-", href, StringComparison.Ordinal);

        // The anchor names the full record in the item's Evidence section, so the jump lands somewhere.
        var anchor = href[(href.IndexOf('#', StringComparison.Ordinal) + 1)..];
        Assert.Contains("The suite is green.", cut.Find($"[id='{anchor}']").TextContent);
    }

    [Fact]
    public async Task An_item_with_nothing_related_says_so_in_the_sidebar_rather_than_showing_a_blank_strip()
    {
        var item = await CreateAsync("On its own");

        using var ctx = Context($"http://localhost/workitems/{item.Number}");
        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Number));

        cut.WaitForAssertion(() => Assert.Contains(
            "Nothing is related to this item yet",
            cut.Find("[data-testid=work-item-related] [data-testid=work-item-related-empty]").TextContent));
        Assert.Empty(cut.FindAll("[data-testid=work-item-related] [data-testid=work-item-link]"));
    }

    private async Task<WorkItem> CreateHierarchyItemAsync(string title, WorkItemLevel level, WorkItemId? parent)
    {
        var created = await _board.CreateAsync(
            new NewWorkItem
            {
                Level = level,
                Title = title,
                Description = "The problem and the outcome.",
                Parent = parent,
            },
            MutationContext.User);
        Assert.Equal(TrackerOutcome.Ok, created.Outcome);
        return created.Value;
    }

    /// <summary>The studio's attention counts with exactly one conversation linked to one item.</summary>
    private sealed class OneSessionAttention(
        WorkItemId item, Fravashi.Domain.AgentSessions.AgentSessionId session) : Fravashi.Application.Sessions.ISessionAttention
    {
        public event Action<Fravashi.Domain.AgentSessions.AgentSessionId>? Changed
        {
            add { }
            remove { }
        }

        public void Record(Fravashi.Application.Sessions.AttentionEntry entry)
        {
        }

        public Fravashi.Application.Sessions.SessionAttention For(Fravashi.Domain.AgentSessions.AgentSessionId id) =>
            Fravashi.Application.Sessions.SessionAttention.Empty;

        public Fravashi.Application.Sessions.WorkItemAttention ForItem(WorkItemId id) =>
            new(id == item
                ? [new Fravashi.Application.Sessions.LinkedSessionAttention(session, Fravashi.Application.Sessions.SessionAttention.Empty)]
                : []);

        public void Forget(Fravashi.Domain.AgentSessions.AgentSessionId id)
        {
        }
    }

    [Fact]
    public async Task The_status_control_on_the_detail_view_performs_the_transition()
    {
        var item = await CreateAsync("Ready work");

        using var ctx = Context($"http://localhost/workitems/{item.Number}");
        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Number));

        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-transition]").Change("in_progress"));

        cut.WaitForAssertion(() =>
            Assert.Contains("In progress", cut.Find("[data-testid=work-item-status]").TextContent));

        Assert.Equal(WorkItemStatus.InProgress, (await ReloadAsync(item)).Status);
    }

    // ---------------------------------------------------------------------------------------------
    // Evidence on Done (requested 2026-10-22)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Done_without_evidence_is_refused_naming_the_criterion_and_a_reason_moves_it_and_is_shown()
    {
        var item = await CreateAsync(
            "Finish me", WorkItemStatus.InReview, criteria: [new AcceptanceCriterion(string.Empty, "The export works.")]);

        using var ctx = Context($"http://localhost/workitems/{item.Number}");
        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Number));

        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-transition]").Change("done"));

        // The refusal is the tracker's own sentence naming the criterion, and the prompt sits under it.
        cut.WaitForAssertion(() => Assert.Contains("'The export works.'", cut.Find("[data-testid=board-error]").TextContent));
        Assert.Equal(WorkItemStatus.InReview, (await ReloadAsync(item)).Status);
        Assert.True(cut.Find("[data-testid=board-done-reason-move]").HasAttribute("disabled"), "a blank reason is no reason");

        await cut.InvokeAsync(() => cut.Find("[data-testid=board-done-reason-text]")
            .Input("Checked by hand; the dialog cannot be driven by a test."));
        await cut.InvokeAsync(() => cut.Find("[data-testid=board-done-reason-move]").Click());

        cut.WaitForAssertion(() => Assert.Contains("Done", cut.Find("[data-testid=work-item-status]").TextContent));
        Assert.Empty(cut.FindAll("[data-testid=board-done-reason]"));
        Assert.Empty(cut.FindAll("[data-testid=board-error]"));
        Assert.Equal(WorkItemStatus.Done, (await ReloadAsync(item)).Status);
        cut.WaitForAssertion(() => Assert.Contains(
            cut.FindAll("[data-testid=work-item-evidence]"),
            e => e.TextContent.Contains("Moved to Done without evidence", StringComparison.Ordinal)
                && e.TextContent.Contains("Checked by hand", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Cancelling_the_reason_prompt_leaves_the_item_where_it_was()
    {
        var item = await CreateAsync(
            "Finish me", WorkItemStatus.InReview, criteria: [new AcceptanceCriterion(string.Empty, "The export works.")]);

        using var ctx = Context("http://localhost/board");
        var cut = ctx.Render<WorkItemsPage>();

        await cut.InvokeAsync(() =>
        {
            cut.Find("[data-testid=board-card]").TriggerEvent("ondragstart", new DragEventArgs());
            cut.Find("[data-testid=board-column-done]").TriggerEvent("ondrop", new DragEventArgs());
        });

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=board-done-reason]")));
        Assert.Contains("Finish me", cut.Find("[data-testid=board-column-in_review]").TextContent);

        await cut.InvokeAsync(() => cut.Find("[data-testid=board-done-reason-cancel]").Click());

        Assert.Empty(cut.FindAll("[data-testid=board-done-reason]"));
        Assert.Equal(WorkItemStatus.InReview, (await ReloadAsync(item)).Status);
        Assert.Empty(await _board.EvidenceAsync(item.Id));
    }

    // ---------------------------------------------------------------------------------------------
    // Record evidence against a criterion (follow-up filed 2026-10-22)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_person_records_evidence_on_a_criterion_and_it_is_marked_and_done_then_needs_no_reason()
    {
        var item = await CreateAsync(
            "Finish me",
            WorkItemStatus.InReview,
            criteria: [new AcceptanceCriterion(string.Empty, "The page loads."), new AcceptanceCriterion(string.Empty, "The export works.")]);

        using var ctx = Context($"http://localhost/workitems/{item.Number}");
        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Number));

        Assert.Empty(cut.FindAll("[data-testid=work-item-criterion-evidence]"));
        Assert.Equal(2, cut.FindAll("[data-testid=work-item-criterion-record]").Count);

        // The second criterion: Fail first is a real outcome, then the form is opened again for the first.
        await cut.InvokeAsync(() => cut.FindAll("[data-testid=work-item-criterion-record]")[1].Click());
        Assert.True(cut.Find("[data-testid=work-item-evidence-save]").HasAttribute("disabled"), "a blank summary proves nothing");
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-evidence-result]").Change("Fail"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-evidence-summary]").Input("The export dialog threw."));
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-evidence-reference]").Input("docs/evidences/export.log"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-evidence-save]").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-testid=work-item-evidence-form]"));
            var rows = cut.FindAll("[data-testid=work-item-criterion-row]");
            Assert.Empty(rows[0].QuerySelectorAll("[data-testid=work-item-criterion-evidence]"));
            Assert.Contains("Evidence: fail", rows[1].QuerySelector("[data-testid=work-item-criterion-evidence]")!.TextContent);
        });

        var stored = Assert.Single(await _board.EvidenceAsync(item.Id));
        var reloaded = await ReloadAsync(item);
        Assert.Equal(reloaded.AcceptanceCriteria[1].Id, stored.Criterion);
        Assert.Equal(EvidenceResult.Fail, stored.Result);
        Assert.Equal("docs/evidences/export.log", stored.ArtefactRef);
        Assert.Equal(WorkItemActorKind.User, stored.Actor!.Kind);
        Assert.Contains(cut.FindAll("[data-testid=work-item-evidence]"), e => e.TextContent.Contains("(docs/evidences/export.log)", StringComparison.Ordinal));

        // The criterion's text is still the criterion alone, whatever is beside it.
        Assert.Equal(
            ["The page loads.", "The export works."],
            cut.FindAll("[data-testid=work-item-criterion]").Select(e => e.TextContent.Trim()));

        await cut.InvokeAsync(() => cut.FindAll("[data-testid=work-item-criterion-record]")[0].Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-evidence-summary]").Input("Opened it in the window."));
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-evidence-save]").Click());
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-testid=work-item-criterion-evidence]").Count));
        Assert.Contains("Evidence: pass", cut.FindAll("[data-testid=work-item-criterion-evidence]")[0].TextContent);

        // Every criterion has evidence now, so the move to Done lands without asking for a reason.
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-transition]").Change("done"));
        cut.WaitForAssertion(() => Assert.Contains("Done", cut.Find("[data-testid=work-item-status]").TextContent));
        Assert.Empty(cut.FindAll("[data-testid=board-done-reason]"));
        Assert.Equal(WorkItemStatus.Done, (await ReloadAsync(item)).Status);
    }

    [Fact]
    public async Task Cancelling_the_evidence_form_records_nothing()
    {
        var item = await CreateAsync(
            "Finish me", WorkItemStatus.InReview, criteria: [new AcceptanceCriterion(string.Empty, "The page loads.")]);

        using var ctx = Context($"http://localhost/workitems/{item.Number}");
        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Number));

        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-criterion-record]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-evidence-summary]").Input("Not saved."));
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-evidence-cancel]").Click());

        Assert.Empty(cut.FindAll("[data-testid=work-item-evidence-form]"));
        Assert.Empty(await _board.EvidenceAsync(item.Id));
    }

    // ---------------------------------------------------------------------------------------------
    // Autopilot mark (ADR 0031 decision 1, backlog 298)
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Marking_a_ready_item_shows_the_chip_and_the_board_card_badge()
    {
        var item = await CreateAsync("Ready work");

        using var ctx = Context($"http://localhost/workitems/{item.Number}");
        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Number));

        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-mark-autopilot]").Click());

        cut.WaitForAssertion(() => cut.Find("[data-testid=work-item-autopilot-marked]"));
        Assert.True((await ReloadAsync(item)).MarkedForAutopilot);

        using var board = Context("http://localhost/board");
        var boardCut = board.Render<WorkItemsPage>();
        boardCut.WaitForAssertion(() => boardCut.Find("[data-testid=board-card-autopilot]"));
    }

    [Fact]
    public async Task The_mark_button_is_disabled_on_a_non_ready_item()
    {
        var item = await CreateAsync("Running work", WorkItemStatus.InProgress);

        using var ctx = Context($"http://localhost/workitems/{item.Number}");
        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Number));

        Assert.NotNull(cut.Find("[data-testid=work-item-mark-autopilot]").GetAttribute("disabled"));
    }

    [Fact]
    public async Task Unmarking_clears_the_chip()
    {
        var item = await CreateAsync("Ready work");

        using var ctx = Context($"http://localhost/workitems/{item.Number}");
        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Number));

        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-mark-autopilot]").Click());
        cut.WaitForAssertion(() => cut.Find("[data-testid=work-item-unmark-autopilot]"));

        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-unmark-autopilot]").Click());

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid=work-item-autopilot-marked]")));
        Assert.False((await ReloadAsync(item)).MarkedForAutopilot);
    }

    // ---------------------------------------------------------------------------------------------
    // Authoring
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Creating_an_item_with_no_description_shows_the_refusal_under_that_field()
    {
        using var ctx = Context("http://localhost/workitems");
        var cut = ctx.Render<WorkItemsPage>();

        cut.Find("[data-testid=new-work-item]").Click();
        cut.Find("[data-testid=work-item-title]").Input("A title with nothing behind it");
        cut.Find("[data-testid=work-item-save]").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll("[data-testid=work-item-description-error]"));
            Assert.NotEmpty(cut.FindAll("[data-testid=work-item-form]"));
        });

        Assert.Empty(cut.FindAll("[data-testid=board-list-row]"));
    }

    [Fact]
    public async Task Creating_an_item_with_a_description_stores_it_and_opens_it()
    {
        using var ctx = Context("http://localhost/workitems");
        var cut = ctx.Render<WorkItemsPage>();

        await cut.InvokeAsync(() => cut.Find("[data-testid=new-work-item]").Click());
        await cut.InvokeAsync(() =>
        {
            cut.Find("[data-testid=work-item-title]").Input("Pin the CLI version");
            cut.Find("[data-testid=work-item-description]").Input("Because a rebuild must not move it.");
        });
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-save]").Click());

        var page = await _board.QueryAsync(new WorkItemQuery());
        var stored = Assert.Single(page.Items);
        Assert.Equal("Pin the CLI version", stored.Title);

        var nav = ctx.Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        Assert.EndsWith($"/workitems/{stored.Number}", nav.Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Editing_an_item_saves_the_new_title_through_the_port()
    {
        var item = await CreateAsync("The old title");

        using var ctx = Context($"http://localhost/workitems/{item.Number}");
        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Number));

        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-edit]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-title]").Input("The new title"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-save]").Click());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=work-item-detail]")));

        Assert.Equal("The new title", (await ReloadAsync(item)).Title);
    }

    [Fact]
    public async Task The_suggested_model_and_effort_are_shown_and_edited_on_the_item_page()
    {
        // Requested 2026-10-22 (model and effort per work item): Default is shown until somebody sets one.
        var item = await CreateAsync("Suggests a run");

        using var ctx = Context($"http://localhost/workitems/{item.Number}");
        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Number));

        Assert.Equal("Default", cut.Find("[data-testid=work-item-suggested-model-text]").TextContent.Trim());
        Assert.Equal("Default", cut.Find("[data-testid=work-item-suggested-effort-text]").TextContent.Trim());

        // Work item #356: two clean label and value rows, not a pre-wrap sentence whose source indentation showed.
        var pairs = cut.Find("[data-testid=work-item-suggested-pairs]");
        Assert.Equal("DL", pairs.TagName);
        Assert.Equal(["Model", "Effort"], pairs.QuerySelectorAll("dt").Select(dt => dt.TextContent.Trim()).ToArray());

        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-edit]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-suggested-model]").Change("sonnet"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-suggested-effort]").Change("max"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-save]").Click());

        cut.WaitForAssertion(() =>
            Assert.Equal("sonnet", cut.Find("[data-testid=work-item-suggested-model-text]").TextContent.Trim()));
        Assert.Equal("max", cut.Find("[data-testid=work-item-suggested-effort-text]").TextContent.Trim());

        var saved = await ReloadAsync(item);
        Assert.Equal(Fravashi.Domain.AgentSessions.AgentModelAlias.Sonnet, saved.SuggestedModel);
        Assert.Equal(Fravashi.Domain.AgentSessions.AgentEffort.Max, saved.SuggestedEffort);

        // And back to Default, which is null and passes nothing.
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-edit]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-suggested-effort]").Change(string.Empty));
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-save]").Click());

        cut.WaitForAssertion(() =>
            Assert.Equal("Default", cut.Find("[data-testid=work-item-suggested-effort-text]").TextContent.Trim()));
        var cleared = await ReloadAsync(item);
        Assert.Null(cleared.SuggestedEffort);
        Assert.Equal(Fravashi.Domain.AgentSessions.AgentModelAlias.Sonnet, cleared.SuggestedModel);
    }

    [Fact]
    public async Task Editing_an_item_to_a_blank_description_shows_the_refusal_and_keeps_the_form_open()
    {
        var item = await CreateAsync("Keeps its description");

        using var ctx = Context($"http://localhost/workitems/{item.Number}");
        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Number));

        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-edit]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-description]").Input(string.Empty));
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-save]").Click());

        cut.WaitForAssertion(() =>
            Assert.NotEmpty(cut.FindAll("[data-testid=work-item-description-error]")));

        Assert.Equal("The problem and the outcome.", (await ReloadAsync(item)).Description);
    }

    // ---- backlog 178: a person writes the acceptance criteria -----------------------------------------

    private const string NoDoneTest = "It has no acceptance criteria, so there is no done-test.";

    [Fact]
    public async Task Creating_an_item_with_a_criterion_stores_it_and_the_verdict_stops_asking_for_one()
    {
        using var ctx = Context("http://localhost/workitems");
        var cut = ctx.Render<WorkItemsPage>();

        await cut.InvokeAsync(() => cut.Find("[data-testid=new-work-item]").Click());
        await cut.InvokeAsync(() =>
        {
            cut.Find("[data-testid=work-item-title]").Input("Pin the CLI version");
            cut.Find("[data-testid=work-item-description]").Input("Because a rebuild must not move it.");
            cut.Find("[data-testid=work-item-status]").Change("ready");
        });
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-criterion-add]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-criterion-input]").Input("A rebuild installs the pin."));
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-save]").Click());

        var stored = Assert.Single((await _board.QueryAsync(new WorkItemQuery())).Items);
        var full = (await _board.GetAsync(stored.Id))!;
        Assert.Equal(["A rebuild installs the pin."], full.AcceptanceCriteria.Select(c => c.Text));
        Assert.False(string.IsNullOrEmpty(full.AcceptanceCriteria[0].Id));

        // The page opens the new item; the verdict there is the tracker's, read after the save.
        using var detail = Context($"http://localhost/workitems/{stored.Number}");
        var page = detail.Render<WorkItemsPage>(p => p.Add(c => c.Number, stored.Number));
        page.WaitForAssertion(() =>
        {
            var verdict = page.Find("[data-testid=work-item-verdict]").TextContent;
            Assert.Contains("This can start now.", verdict);
            Assert.DoesNotContain(NoDoneTest, verdict);
        });
    }

    [Fact]
    public async Task Editing_adds_edits_and_removes_criteria_and_an_unchanged_one_keeps_its_id()
    {
        var item = await CreateAsync(
            "Three criteria",
            criteria:
            [
                new AcceptanceCriterion(string.Empty, "Kept as it is."),
                new AcceptanceCriterion(string.Empty, "Worded badly."),
                new AcceptanceCriterion(string.Empty, "No longer wanted."),
            ]);
        var before = (await ReloadAsync(item)).AcceptanceCriteria;

        using var ctx = Context($"http://localhost/workitems/{item.Number}");
        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Number));

        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-edit]").Click());
        Assert.Equal(
            ["Kept as it is.", "Worded badly.", "No longer wanted."],
            cut.FindAll("[data-testid=work-item-criterion-input]").Select(e => e.GetAttribute("value")));

        await cut.InvokeAsync(() => cut.FindAll("[data-testid=work-item-criterion-input]")[1].Input("Worded well."));
        await cut.InvokeAsync(() => cut.FindAll("[data-testid=work-item-criterion-remove]")[2].Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-criterion-add]").Click());
        await cut.InvokeAsync(() => cut.FindAll("[data-testid=work-item-criterion-input]")[2].Input("Newly added."));
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-save]").Click());

        cut.WaitForAssertion(() => Assert.Equal(
            ["Kept as it is.", "Worded well.", "Newly added."],
            cut.FindAll("[data-testid=work-item-criterion]").Select(e => e.TextContent.Trim())));

        var after = (await ReloadAsync(item)).AcceptanceCriteria;
        Assert.Equal(["Kept as it is.", "Worded well.", "Newly added."], after.Select(c => c.Text));
        Assert.Equal(before[0].Id, after[0].Id);
        Assert.Equal(before[1].Id, after[1].Id);
        Assert.DoesNotContain(after, c => c.Id == before[2].Id);
    }

    [Fact]
    public async Task A_blank_criterion_is_refused_in_the_domains_words_and_nothing_is_stored()
    {
        var item = await CreateAsync("Has one", criteria: [new AcceptanceCriterion(string.Empty, "It works.")]);

        using var ctx = Context($"http://localhost/workitems/{item.Number}");
        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Number));

        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-edit]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-criterion-add]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-save]").Click());

        cut.WaitForAssertion(() => Assert.Contains(
            "a blank one is noise",
            cut.Find("[data-testid=work-item-criteria-error]").TextContent));
        Assert.NotEmpty(cut.FindAll("[data-testid=work-item-form]"));
        Assert.Equal(["It works."], (await ReloadAsync(item)).AcceptanceCriteria.Select(c => c.Text));
    }

    [Fact]
    public async Task Removing_a_criterion_that_evidence_points_at_is_refused_in_the_stores_words()
    {
        var item = await CreateAsync("Proven", criteria: [new AcceptanceCriterion(string.Empty, "It works.")]);
        var reloaded = await ReloadAsync(item);
        var recorded = await _board.RecordEvidenceAsync(
            reloaded.Id,
            new NewEvidence
            {
                Kind = EvidenceKind.TestRun,
                Result = EvidenceResult.Pass,
                Summary = "Green.",
                Criterion = reloaded.AcceptanceCriteria[0].Id,
            },
            MutationContext.User);
        Assert.Equal(TrackerOutcome.Ok, recorded.Outcome);

        using var ctx = Context($"http://localhost/workitems/{item.Number}");
        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Number));

        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-edit]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-criterion-remove]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-save]").Click());

        cut.WaitForAssertion(() => Assert.Contains(
            "has evidence recorded against it",
            cut.Find("[data-testid=work-item-criteria-error]").TextContent));
        Assert.Single((await ReloadAsync(item)).AcceptanceCriteria);
    }

    [Fact]
    public async Task Removing_the_last_criterion_brings_the_verdicts_advisory_back()
    {
        var item = await CreateAsync("Loses its test", criteria: [new AcceptanceCriterion(string.Empty, "It works.")]);

        using var ctx = Context($"http://localhost/workitems/{item.Number}");
        var cut = ctx.Render<WorkItemsPage>(p => p.Add(c => c.Number, item.Number));
        Assert.DoesNotContain(NoDoneTest, cut.Find("[data-testid=work-item-verdict]").TextContent);

        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-edit]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-criterion-remove]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=work-item-save]").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll("[data-testid=work-item-no-criteria]"));
            Assert.Contains(NoDoneTest, cut.Find("[data-testid=work-item-verdict]").TextContent);
        });
        Assert.Empty((await ReloadAsync(item)).AcceptanceCriteria);
    }
}
