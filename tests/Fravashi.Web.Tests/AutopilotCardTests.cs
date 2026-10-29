using Bunit;
using Fravashi.Application.AgentSync;
using Fravashi.Application.Autopilot;
using Fravashi.Application.Persistence;
using Fravashi.Application.Sessions;
using Fravashi.Application.Wiki;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Autopilot;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.AgentRuntimes;
using Fravashi.Infrastructure.Autopilot;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Wiki;
using Fravashi.Infrastructure.WorkItems;
using Fravashi.Web.Components.Dashboard;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 303 (ADR 0031 decisions 13 and 14), moved by work item #358 from the retired autopilot page onto Home's
/// Autopilot card and Recent dispatches card: over a real local board and run store so what is
/// shown here is what a real dispatch and a real stop would really do. Every region over a fake run; the
/// building, review and delivery seams are never called by a stop, so each is a throwing stub here.
/// #313's real-browser tier lives in its own class, <see cref="AutopilotCardLiveTests"/>: see
/// <see cref="AutopilotCardTestHarness"/> for why.
/// </summary>
public sealed class AutopilotCardTests : AutopilotCardTestHarness
{
    [Fact]
    public async Task With_no_run_the_card_explains_the_autopilot_and_lists_the_marked_items()
    {
        var marked = await CreateItemAsync("Build the marked thing");
        await MarkAsync(marked);
        await CreateItemAsync("Build the unmarked thing");

        using var ctx = Context();
        var cut = ctx.Render<AutopilotCard>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("unattended", cut.Find("[data-testid=autopilot-explain]").TextContent);
            var item = Assert.Single(cut.FindAll("[data-testid=autopilot-marked-item]"));
            Assert.Contains("Build the marked thing", item.TextContent);
            Assert.Equal($"/workitems/{marked.Number}", item.GetAttribute("href"));
            Assert.Equal("1 marked", cut.Find("[data-testid=autopilot-marked-count]").TextContent);
        });
        Assert.Empty(cut.FindAll("[data-testid=autopilot-queue]"));
        // The form is the dialog's, not the card's: nothing is dispatched from the card's own body.
        Assert.Empty(cut.FindAll("[data-testid=autopilot-dispatch-form]"));
    }

    [Fact]
    public void The_explanation_is_clamped_until_More_opens_it()
    {
        // Work item #363: two lines and a real toggle (not hover only), named for assistive technology.
        using var ctx = Context();
        var cut = ctx.Render<AutopilotCard>();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=autopilot-explain-toggle]")));
        var toggle = cut.Find("[data-testid=autopilot-explain-toggle]");
        Assert.Equal("false", toggle.GetAttribute("aria-expanded"));
        Assert.Equal("autopilot-explain", toggle.GetAttribute("aria-controls"));
        Assert.DoesNotContain("is-open", cut.Find("[data-testid=autopilot-explain]").ClassList);

        cut.InvokeAsync(() => cut.Find("[data-testid=autopilot-explain-toggle]").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("is-open", cut.Find("[data-testid=autopilot-explain]").ClassList);
            Assert.Equal("true", cut.Find("[data-testid=autopilot-explain-toggle]").GetAttribute("aria-expanded"));
            Assert.Equal("Less", cut.Find("[data-testid=autopilot-explain-toggle]").TextContent.Trim());
        });
    }

    [Fact]
    public async Task Dispatch_opens_the_dialog_with_the_dispatch_form_and_dispatches_the_marked_items()
    {
        var item = await CreateItemAsync("Build the thing");
        await MarkAsync(item);

        using var ctx = Context();
        var cut = ctx.Render<AutopilotCard>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=autopilot-marked-item]")));

        await cut.InvokeAsync(() => cut.Find("[data-testid=autopilot-dispatch-open]").Click());

        // Exactly the options the retired autopilot page's "Dispatch autopilot" section had: it is the same component.
        var dialog = cut.Find("[data-testid=autopilot-dialog]");
        Assert.Equal("dialog", dialog.GetAttribute("role"));
        Assert.NotNull(dialog.QuerySelector("[data-testid=autopilot-dispatch-form]"));
        foreach (var field in new[]
        {
            "autopilot-money-per-item", "autopilot-money-per-run", "autopilot-failed-reviews", "autopilot-time-per-item",
            "autopilot-no-progress", "autopilot-concurrency-override", "autopilot-missing-scenario", "autopilot-person-wait",
            "autopilot-builder-model", "autopilot-operator-model", "autopilot-judge-key-input",
        })
        {
            Assert.NotNull(dialog.QuerySelector($"[data-testid={field}]"));
        }

        await cut.InvokeAsync(() => cut.Find("[data-testid=autopilot-money-per-item]").Change("7.5"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=autopilot-dispatch-submit]").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll("[data-testid=autopilot-dialog]"));
            Assert.Contains($"#{item.Number}", cut.Find("[data-testid=autopilot-queue-item]").TextContent);
        });
        var open = Assert.Single(await _runs.OpenRunsAsync());
        Assert.Equal(7.5m, open.Settings.Limits.MoneyPerItemUsd);
        Assert.Equal([item.Id], open.Queue.Select(q => q.Item));
    }

    [Fact]
    public async Task The_dialog_closes_without_dispatching()
    {
        using var ctx = Context();
        var cut = ctx.Render<AutopilotCard>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=autopilot-dispatch-open]")));

        await cut.InvokeAsync(() => cut.Find("[data-testid=autopilot-dispatch-open]").Click());
        await cut.InvokeAsync(() => cut.Find("[data-testid=autopilot-dialog-close]").Click());

        Assert.Empty(cut.FindAll("[data-testid=autopilot-dialog]"));
        Assert.Empty(await _runs.OpenRunsAsync());
    }

    [Fact]
    public async Task Recent_dispatches_lists_every_run_with_its_state_items_and_times()
    {
        var item = await CreateItemAsync("Build the thing");
        var past = AutopilotRun.Start(
            Guid.NewGuid(), ProjectId.Default, AutopilotDispatchSettings.Default, [item.Id], DateTimeOffset.UtcNow.AddHours(-3));
        past = past with { State = AutopilotRunState.Completed, LastProgressUtc = DateTimeOffset.UtcNow.AddHours(-2) };
        await _runs.SaveAsync(past);
        var open = AutopilotRun.Start(
            Guid.NewGuid(), ProjectId.Default, AutopilotDispatchSettings.Default, [item.Id, WorkItemIdOf("other")], DateTimeOffset.UtcNow);
        await _runs.SaveAsync(open);

        using var ctx = Context();
        var cut = ctx.Render<RecentDispatchesCard>();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("[data-testid=recent-dispatch-row]");
            Assert.Equal(2, rows.Count);
            // Newest started first.
            Assert.Equal("Running", rows[0].QuerySelector("[data-testid=recent-dispatch-state]")!.TextContent);
            Assert.Contains("2 items", rows[0].TextContent);
            Assert.Contains("Still running", rows[0].QuerySelector("[data-testid=recent-dispatch-ended]")!.TextContent);
            Assert.Equal("Completed", rows[1].QuerySelector("[data-testid=recent-dispatch-state]")!.TextContent);
            Assert.Contains("1 item", rows[1].TextContent);
            Assert.Contains("Ended", rows[1].QuerySelector("[data-testid=recent-dispatch-ended]")!.TextContent);
        });
        Assert.Equal("2", cut.Find("[data-testid=recent-dispatches-count]").TextContent);
    }

    [Fact]
    public void Recent_dispatches_says_so_when_there_are_none()
    {
        using var ctx = Context();
        var cut = ctx.Render<RecentDispatchesCard>();

        Assert.NotEmpty(cut.FindAll("[data-testid=recent-dispatches-empty]"));
    }

    private static WorkItemId WorkItemIdOf(string value) => new(value);

    private async Task MarkAsync(WorkItem item)
    {
        var marked = await _board.UpdateAsync(
            item.Id, new WorkItemPatch { MarkedForAutopilot = Optional<bool>.Of(true) }, item.Version, MutationContext.User);
        Assert.True(marked.IsOk, string.Join("; ", marked.Errors.Select(e => e.Message)));
    }

    [Fact]
    public async Task An_open_run_shows_the_queue_in_board_order_with_each_items_stage()
    {
        var item = await CreateItemAsync("Build the thing");
        var run = AutopilotRun.Start(
            Guid.NewGuid(), ProjectId.Default, AutopilotDispatchSettings.Default, [item.Id], DateTimeOffset.UtcNow,
            baseBranch: "main", baseWorkspaceId: "w1");
        run = run.With(run.Queue[0] with { Stage = AutopilotItemStage.Building, StartedUtc = DateTimeOffset.UtcNow });
        await _runs.SaveAsync(run);

        using var ctx = Context();
        var cut = ctx.Render<AutopilotCard>();

        cut.WaitForAssertion(() =>
        {
            var row = cut.Find("[data-testid=autopilot-queue-item]");
            Assert.Contains($"#{item.Number}", row.TextContent);
            Assert.Contains("Build the thing", row.TextContent);
            Assert.Contains("Building", row.TextContent);
        });
    }

    [Fact]
    public async Task An_open_run_shows_the_running_item_with_no_session_yet()
    {
        var item = await CreateItemAsync("Build the thing");
        var run = AutopilotRun.Start(
            Guid.NewGuid(), ProjectId.Default, AutopilotDispatchSettings.Default, [item.Id], DateTimeOffset.UtcNow,
            baseBranch: "main", baseWorkspaceId: "w1");
        run = run.With(run.Queue[0] with { Stage = AutopilotItemStage.Building, StartedUtc = DateTimeOffset.UtcNow });
        await _runs.SaveAsync(run);

        using var ctx = Context();
        var cut = ctx.Render<AutopilotCard>();

        cut.WaitForAssertion(() =>
        {
            var region = cut.Find("[data-testid=autopilot-running-item]");
            Assert.Contains($"#{item.Number}", region.TextContent);
            Assert.NotEmpty(cut.FindAll("[data-testid=autopilot-running-item-no-session]"));
        });
    }

    /// <summary>
    /// #308 host note point 4: a capacity refusal's reason survives a reload (through the real SQLite store,
    /// not only the in-memory tick result), and the Autopilot card shows it.
    /// </summary>
    [Fact]
    public async Task An_open_run_with_an_item_waiting_for_capacity_shows_the_reason_after_a_reload()
    {
        var item = await CreateItemAsync("Build the thing");
        var run = AutopilotRun.Start(
            Guid.NewGuid(), ProjectId.Default, AutopilotDispatchSettings.Default, [item.Id], DateTimeOffset.UtcNow,
            baseBranch: "main", baseWorkspaceId: "w1");
        run = run.With(run.Queue[0] with
        {
            Stage = AutopilotItemStage.Building,
            StartedUtc = DateTimeOffset.UtcNow,
            WaitingForCapacityReason = "The concurrency cap is reached: 1 of 1 session(s) are already running against this repository.",
        });
        await _runs.SaveAsync(run);

        using var ctx = Context();
        var cut = ctx.Render<AutopilotCard>();

        cut.WaitForAssertion(() =>
        {
            var reason = cut.Find("[data-testid=autopilot-running-item-waiting-capacity]");
            Assert.Contains("concurrency cap is reached", reason.TextContent);
        });
    }

    /// <summary>#308: the Autopilot card shows every item the run is actively working, not merely the first.</summary>
    [Fact]
    public async Task An_open_run_with_two_active_items_shows_a_running_block_for_each()
    {
        var first = await CreateItemAsync("Build the first thing");
        var second = await CreateItemAsync("Build the second thing");
        var run = AutopilotRun.Start(
            Guid.NewGuid(), ProjectId.Default, AutopilotDispatchSettings.Default, [first.Id, second.Id],
            DateTimeOffset.UtcNow, baseBranch: "main", baseWorkspaceId: "w1");
        run = run.With(run.Queue[0] with { Stage = AutopilotItemStage.Building, StartedUtc = DateTimeOffset.UtcNow });
        run = run.With(run.Queue[1] with { Stage = AutopilotItemStage.Building, StartedUtc = DateTimeOffset.UtcNow });
        await _runs.SaveAsync(run);

        using var ctx = Context();
        var cut = ctx.Render<AutopilotCard>();

        cut.WaitForAssertion(() =>
        {
            var blocks = cut.FindAll("[data-testid=autopilot-running-item-block]");
            Assert.Equal(2, blocks.Count);
            Assert.Contains(blocks, b => b.TextContent.Contains($"#{first.Number}"));
            Assert.Contains(blocks, b => b.TextContent.Contains($"#{second.Number}"));
        });
    }

    [Fact]
    public async Task An_open_run_with_a_bound_session_renders_the_live_session_pane()
    {
        var item = await CreateItemAsync("Build the thing");
        var run = AutopilotRun.Start(
            Guid.NewGuid(), ProjectId.Default, AutopilotDispatchSettings.Default, [item.Id], DateTimeOffset.UtcNow,
            baseBranch: "main", baseWorkspaceId: "w1");
        run = run.With(run.Queue[0] with { Stage = AutopilotItemStage.Building, StartedUtc = DateTimeOffset.UtcNow });
        await _runs.SaveAsync(run);

        using var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(5));
        using var registry = new LiveSessionRegistry(new SilentSessionRunner(), new RecordingSessionStore(), broker);
        var session = registry.Start(new SessionStartRequest("w1", "/workspaces/w1", "build the thing"));
        var builderSessions = new AutopilotBuilderSessions(broker);
        builderSessions.Bind(item.Id, session.Id);

        using var ctx = Context(builderSessions);
        // The session pane SessionPane renders (SessionGovernanceStrip, SessionBrowserPane,
        // NodeVersionBanner, SessionTranscript, SessionComposer) needs its own dependency set, the same
        // shape SessionsUiTests builds its own harness from.
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var configuration = new ConfigurationBuilder().Build();
        ctx.Services.AddSingleton(registry);
        ctx.Services.AddSingleton<IWorkspaceStore>(new FakeWorkspaceStore());
        ctx.Services.AddSingleton<ISessionWorkItems>(new SqliteSessionWorkItems(_factory, TimeProvider.System));
        ctx.Services.AddSingleton<ISessionPlans>(new SqliteSessionPlans(_factory, TimeProvider.System));
        var wiki = new SqliteWikiLibrary(_factory, TimeProvider.System, ProjectId.Default);
        ctx.Services.AddSingleton<IWikiDocuments>(wiki);
        ctx.Services.AddSingleton<ISessionGovernance>(new SessionGovernance(
            new SqliteSessionWorkItems(_factory, TimeProvider.System),
            new SqliteSessionPlans(_factory, TimeProvider.System),
            _resolver,
            wiki,
            new SqliteSessionChangedFiles(_factory, TimeProvider.System)));
        ctx.Services.AddSingleton(new WorkspaceNodeAdvisor(
            new NodePathProvisioner(configuration), new FakeNodeCommandRunner()));
        ctx.Services.AddSingleton(new NodeProvisionCoordinator(new FakeNodeProvisioner()));
        ctx.Services.AddSingleton<IAgentBrowser>(new FakeAgentBrowser());
        ctx.Services.AddSingleton(new Fravashi.Infrastructure.Rendering.MarkdownPreviewRenderer());
        ctx.Services.AddLogging();

        var cut = ctx.Render<AutopilotCard>();

        cut.WaitForAssertion(() =>
        {
            var region = cut.Find("[data-testid=autopilot-running-item]");
            Assert.NotEmpty(region.QuerySelectorAll("[data-testid=session-pane]"));
            Assert.Empty(cut.FindAll("[data-testid=autopilot-running-item-no-session]"));
        });
    }

    /// <summary>
    /// #313: two running items, both with a bound session, the second ALSO waiting for capacity (an item
    /// can keep a session from an earlier turn while its next one waits). Each item's own heading, its own
    /// capacity sentence (when it has one), its own pane and its own limit list stay inside its own block,
    /// in that order, and never pick up the other item's number: the DOM structure a stacked, readable
    /// layout needs, which the real-browser tier then proves is actually rendered that way.
    /// </summary>
    [Fact]
    public async Task Two_running_items_each_keep_their_own_heading_pane_and_limits_in_order()
    {
        var first = await CreateItemAsync("Build the first thing");
        var second = await CreateItemAsync("Build the second thing");
        // No BaseWorkspaceId: each item's pane then resolves ITS OWN session's own workspace (AutopilotCard's
        // RunningWorkspaceId fallback), which is what lets the two sessions below sit in different
        // workspaces rather than colliding on the registry's one-live-session-per-workspace rule.
        var run = AutopilotRun.Start(
            Guid.NewGuid(), ProjectId.Default, AutopilotDispatchSettings.Default, [first.Id, second.Id], DateTimeOffset.UtcNow);
        run = run.With(run.Queue[0] with { Stage = AutopilotItemStage.Building, StartedUtc = DateTimeOffset.UtcNow });
        run = run.With(run.Queue[1] with
        {
            Stage = AutopilotItemStage.Building,
            StartedUtc = DateTimeOffset.UtcNow,
            WaitingForCapacityReason = "The concurrency cap is reached: 1 of 1 session(s) are already running against this repository.",
        });
        await _runs.SaveAsync(run);

        using var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(5));
        using var registry = new LiveSessionRegistry(new SilentSessionRunner(), new RecordingSessionStore(), broker);
        var session = registry.Start(new SessionStartRequest("w1", "/workspaces/w1", "build the first thing"));
        var secondSession = registry.Start(new SessionStartRequest("w2", "/workspaces/w2", "build the second thing"));
        var builderSessions = new AutopilotBuilderSessions(broker);
        builderSessions.Bind(first.Id, session.Id);
        builderSessions.Bind(second.Id, secondSession.Id);

        using var ctx = Context(builderSessions);
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var configuration = new ConfigurationBuilder().Build();
        ctx.Services.AddSingleton(registry);
        ctx.Services.AddSingleton<IWorkspaceStore>(new FakeWorkspaceStore());
        ctx.Services.AddSingleton<ISessionWorkItems>(new SqliteSessionWorkItems(_factory, TimeProvider.System));
        ctx.Services.AddSingleton<ISessionPlans>(new SqliteSessionPlans(_factory, TimeProvider.System));
        var wiki = new SqliteWikiLibrary(_factory, TimeProvider.System, ProjectId.Default);
        ctx.Services.AddSingleton<IWikiDocuments>(wiki);
        ctx.Services.AddSingleton<ISessionGovernance>(new SessionGovernance(
            new SqliteSessionWorkItems(_factory, TimeProvider.System),
            new SqliteSessionPlans(_factory, TimeProvider.System),
            _resolver,
            wiki,
            new SqliteSessionChangedFiles(_factory, TimeProvider.System)));
        ctx.Services.AddSingleton(new WorkspaceNodeAdvisor(
            new NodePathProvisioner(configuration), new FakeNodeCommandRunner()));
        ctx.Services.AddSingleton(new NodeProvisionCoordinator(new FakeNodeProvisioner()));
        ctx.Services.AddSingleton<IAgentBrowser>(new FakeAgentBrowser());
        ctx.Services.AddSingleton(new Fravashi.Infrastructure.Rendering.MarkdownPreviewRenderer());
        ctx.Services.AddLogging();

        var cut = ctx.Render<AutopilotCard>();

        cut.WaitForAssertion(() =>
        {
            // FindAll returns document order, so the first item's block coming first here is itself part
            // of what "after that item's own session pane" (acceptance bullet 1) means across blocks.
            var blocks = cut.FindAll("[data-testid=autopilot-running-item-block]");
            Assert.Equal(2, blocks.Count);

            var firstBlock = blocks[0];
            var secondBlock = blocks[1];
            Assert.Contains($"#{first.Number}", firstBlock.TextContent);
            Assert.Contains($"#{second.Number}", secondBlock.TextContent);
            Assert.DoesNotContain($"#{second.Number}", firstBlock.TextContent);
            Assert.DoesNotContain($"#{first.Number}", secondBlock.TextContent);

            AssertOwnChildrenInOrder(firstBlock, hasPane: true, hasCapacitySentence: false);
            AssertOwnChildrenInOrder(secondBlock, hasPane: true, hasCapacitySentence: true);
        });
    }

    /// <summary>
    /// Children of one running block, as the block itself owns them: the label first, then its own
    /// capacity sentence (when it is waiting) and its own pane (when it has one), then the limit list
    /// (itself three separate lines, in order), each a direct child so none of it can be another item's
    /// content rendered in the wrong place.
    /// </summary>
    private static void AssertOwnChildrenInOrder(AngleSharp.Dom.IElement block, bool hasPane, bool hasCapacitySentence)
    {
        var children = block.Children.ToArray();
        var labelIndex = Array.FindIndex(children, c => c.ClassList.Contains("autopilot-running-item-label"));
        var listIndex = Array.FindIndex(children, c => c.ClassList.Contains("autopilot-list"));
        Assert.True(labelIndex >= 0, "the block has its own heading");
        Assert.True(listIndex > labelIndex, "the block's own limit list comes after its own heading");

        var list = children[listIndex];
        var lines = list.Children.ToArray();
        var moneyIndex = Array.FindIndex(lines, c => c.GetAttribute("data-testid") == "autopilot-limit-money-per-item");
        var reviewsIndex = Array.FindIndex(lines, c => c.GetAttribute("data-testid") == "autopilot-limit-failed-reviews");
        var timeIndex = Array.FindIndex(lines, c => c.GetAttribute("data-testid") == "autopilot-limit-time-per-item");
        Assert.True(moneyIndex >= 0 && reviewsIndex >= 0 && timeIndex >= 0, "the block's own limit list has all three of its own lines");
        Assert.True(moneyIndex < reviewsIndex && reviewsIndex < timeIndex, "the block's own three limit lines are separate elements in order");

        var capacityIndex = Array.FindIndex(children, c => c.GetAttribute("data-testid") == "autopilot-running-item-waiting-capacity");
        if (hasCapacitySentence)
        {
            Assert.True(capacityIndex > labelIndex && capacityIndex < listIndex, "the block's own capacity sentence sits between its own heading and its own limit list");
        }
        else
        {
            Assert.Equal(-1, capacityIndex);
        }

        var paneIndex = Array.FindIndex(children, c => c.GetAttribute("data-testid") == "autopilot-running-item-pane");
        if (hasPane)
        {
            Assert.True(paneIndex > labelIndex && paneIndex < listIndex, "the block's own pane sits between its own heading and its own limit list");
            if (hasCapacitySentence)
            {
                Assert.True(capacityIndex < paneIndex, "the block's own capacity sentence sits before its own pane");
            }
        }
        else
        {
            Assert.Equal(-1, paneIndex);
        }
    }

    /// <summary>
    /// Commit review's finding on this slice's first attempt: a session genuinely building the item must
    /// never be reported as "no session has started" just because the run recorded no workspace (a run
    /// dispatched before #310's fields existed, or one whose workspace could not be reached at dispatch) AND
    /// the live registry does not know this session either. The honest sentence is its own region, distinct
    /// from both "no session yet" and the live pane.
    /// </summary>
    [Fact]
    public async Task A_bound_session_whose_run_names_no_workspace_says_so_honestly()
    {
        var item = await CreateItemAsync("Build the thing");
        var run = AutopilotRun.Start(
            Guid.NewGuid(), ProjectId.Default, AutopilotDispatchSettings.Default, [item.Id], DateTimeOffset.UtcNow);
        run = run.With(run.Queue[0] with { Stage = AutopilotItemStage.Building, StartedUtc = DateTimeOffset.UtcNow });
        await _runs.SaveAsync(run);

        var builderSessions = new AutopilotBuilderSessions(new FakeBroker());
        builderSessions.Bind(item.Id, AgentSessionId.New());

        using var ctx = Context(builderSessions);
        var cut = ctx.Render<AutopilotCard>();

        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll("[data-testid=autopilot-running-item-no-workspace]"));
            Assert.Empty(cut.FindAll("[data-testid=autopilot-running-item-no-session]"));
            Assert.Empty(cut.FindAll("[data-testid=session-pane]"));
        });
    }

    [Fact]
    public async Task An_open_run_shows_the_limits_used_so_far_against_the_dispatchs_settings()
    {
        var item = await CreateItemAsync("Build the thing");
        var run = AutopilotRun.Start(
            Guid.NewGuid(), ProjectId.Default, AutopilotDispatchSettings.Default, [item.Id], DateTimeOffset.UtcNow,
            baseBranch: "main", baseWorkspaceId: "w1");
        run = run.With(run.Queue[0] with
        {
            Stage = AutopilotItemStage.Building,
            StartedUtc = DateTimeOffset.UtcNow,
            SpentUsd = 3.5m,
            FailedReviews = 1,
        });
        await _runs.SaveAsync(run);

        using var ctx = Context();
        var cut = ctx.Render<AutopilotCard>();

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("$3.50 of $15.00", cut.Find("[data-testid=autopilot-limit-money-per-item]").TextContent);
            Assert.Contains("$3.50 of $100.00", cut.Find("[data-testid=autopilot-limit-money-per-run]").TextContent);
            Assert.Contains("1 of 3", cut.Find("[data-testid=autopilot-limit-failed-reviews]").TextContent);
            Assert.Contains("of 120 min", cut.Find("[data-testid=autopilot-limit-time-per-item]").TextContent);
            Assert.Contains("of 30 min", cut.Find("[data-testid=autopilot-limit-no-progress]").TextContent);
        });
    }

    /// <summary>
    /// #321's own acceptance: an item whose commits failed review as many times as the dispatch's own limit
    /// allows is stopped by the limit, and Recent dispatches shows that figure. The actual stopping, through real
    /// retries, is proven at the engine level by
    /// AutopilotEngineTests.A_build_turn_the_reviewer_refused_before_a_commit_landed_counts_against_the_ceiling_too;
    /// this test is the display half, over the state the engine really persists once that fires: Settle stops
    /// the item on the SAME tick its count reaches the ceiling (never leaving FailedReviews at the limit on an
    /// item still shown as running, which `autopilot-limit-failed-reviews` only ever renders for), and a single-item
    /// run concludes once its only item is no longer open, so what a person actually sees is the run's own
    /// history entry, naming the count and the limit, the same wording
    /// History_lists_a_past_runs_stop_with_its_reason already proves is rendered.
    /// </summary>
    [Fact]
    public async Task An_item_stopped_at_its_own_failed_review_ceiling_shows_the_count_and_the_limit_in_history()
    {
        var item = await CreateItemAsync("Build the thing");
        var settings = AutopilotDispatchSettings.Default with
        {
            Limits = AutopilotLimits.Default with { FailedCommitReviewsPerItem = 2 },
        };
        var run = AutopilotRun.Start(
            Guid.NewGuid(), ProjectId.Default, settings, [item.Id], DateTimeOffset.UtcNow.AddHours(-1));
        var stop = new AutopilotStop(
            AutopilotStopKind.FailedCommitReviews, AutopilotStopScope.Item, "failed commit reviews per item",
            "2 failed review(s), limit 2", DateTimeOffset.UtcNow, item.Id);
        run = run.With(run.Queue[0] with { Stage = AutopilotItemStage.Stopped, FailedReviews = 2 });
        run = run.WithStop(stop) with { State = AutopilotRunState.Stopped };
        await _runs.SaveAsync(run);

        using var ctx = Context();
        var cut = ctx.Render<RecentDispatchesCard>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=recent-dispatch]")));

        await cut.InvokeAsync(() => cut.Find("[data-testid=recent-dispatch]").Click());

        cut.WaitForAssertion(() =>
            Assert.Contains("2 failed review(s), limit 2", cut.Find("[data-testid=recent-dispatch-stop]").TextContent));
    }

    /// <summary>
    /// #321 (fourth pass, commit review's own finding): an item-scoped limit can stop ONE item while the
    /// RUN itself stays open (a sibling item still has work to do), and a stopped item leaves the "Running
    /// items" block entirely, so without this a person watching an open, multi-item run would see neither
    /// the figure nor the stop until the WHOLE run eventually ends. Rendered on the open run itself now,
    /// beside its other running totals, the same wording the run's own eventual History entry would show.
    /// </summary>
    [Fact]
    public async Task An_item_stopped_at_its_own_ceiling_shows_the_count_and_the_limit_while_the_run_is_still_open()
    {
        var first = await CreateItemAsync("Build the first thing");
        var second = await CreateItemAsync("Build the second thing");
        var settings = AutopilotDispatchSettings.Default with
        {
            Limits = AutopilotLimits.Default with { FailedCommitReviewsPerItem = 2 },
        };
        var run = AutopilotRun.Start(
            Guid.NewGuid(), ProjectId.Default, settings, [first.Id, second.Id], DateTimeOffset.UtcNow,
            baseBranch: "main", baseWorkspaceId: "w1");
        var stop = new AutopilotStop(
            AutopilotStopKind.FailedCommitReviews, AutopilotStopScope.Item, "failed commit reviews per item",
            "2 failed review(s), limit 2", DateTimeOffset.UtcNow, first.Id);
        run = run.With(run.Queue[0] with { Stage = AutopilotItemStage.Stopped, FailedReviews = 2 });
        run = run.WithStop(stop);

        Assert.Equal(AutopilotRunState.Running, run.State); // the second item still has work to do
        await _runs.SaveAsync(run);

        using var ctx = Context();
        var cut = ctx.Render<AutopilotCard>();

        cut.WaitForAssertion(() =>
            Assert.Contains("2 failed review(s), limit 2", cut.Find("[data-testid=autopilot-run-stop]").TextContent));
    }

    [Fact]
    public async Task Stop_now_ends_the_run_and_leaves_the_item_stopped()
    {
        var item = await CreateItemAsync("Build the thing");
        var run = AutopilotRun.Start(
            Guid.NewGuid(), ProjectId.Default, AutopilotDispatchSettings.Default, [item.Id], DateTimeOffset.UtcNow,
            baseBranch: "main", baseWorkspaceId: "w1");
        run = run.With(run.Queue[0] with { Stage = AutopilotItemStage.Building, StartedUtc = DateTimeOffset.UtcNow });
        await _runs.SaveAsync(run);

        using var ctx = Context();
        var cut = ctx.Render<AutopilotCard>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=autopilot-stop-now]")));

        await cut.InvokeAsync(() => cut.Find("[data-testid=autopilot-stop-now]").Click());

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=autopilot-idle]")));
        var stopped = await _runs.GetAsync(run.Id);
        Assert.Equal(AutopilotRunState.Stopped, stopped!.State);
        Assert.Equal(AutopilotItemStage.Stopped, stopped.Queue[0].Stage);
    }

    [Fact]
    public async Task Stop_after_this_item_sets_the_flag_and_leaves_the_run_open()
    {
        var item = await CreateItemAsync("Build the thing");
        var run = AutopilotRun.Start(
            Guid.NewGuid(), ProjectId.Default, AutopilotDispatchSettings.Default, [item.Id], DateTimeOffset.UtcNow,
            baseBranch: "main", baseWorkspaceId: "w1");
        run = run.With(run.Queue[0] with { Stage = AutopilotItemStage.Building, StartedUtc = DateTimeOffset.UtcNow });
        await _runs.SaveAsync(run);

        using var ctx = Context();
        var cut = ctx.Render<AutopilotCard>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=autopilot-stop-after-item]")));

        await cut.InvokeAsync(() => cut.Find("[data-testid=autopilot-stop-after-item]").Click());

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-testid=autopilot-stop-after-item]").GetAttribute("disabled")));
        var updated = await _runs.GetAsync(run.Id);
        Assert.True(updated!.StopAfterCurrentItem);
        Assert.Equal(AutopilotRunState.Running, updated.State);
    }

    [Fact]
    public async Task History_lists_a_past_runs_stop_with_its_reason()
    {
        var item = await CreateItemAsync("Build the thing");
        var past = AutopilotRun.Start(
            Guid.NewGuid(), ProjectId.Default, AutopilotDispatchSettings.Default, [item.Id], DateTimeOffset.UtcNow.AddHours(-1));
        var stop = new AutopilotStop(
            AutopilotStopKind.TimePerItem, AutopilotStopScope.Item, "time per item", "125 min elapsed, limit 120 min",
            DateTimeOffset.UtcNow, item.Id, "a note for the person reading the history");
        past = past.WithStop(stop) with { State = AutopilotRunState.Stopped };
        await _runs.SaveAsync(past);

        using var ctx = Context();
        var cut = ctx.Render<RecentDispatchesCard>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=recent-dispatch]")));
        Assert.Empty(cut.FindAll("[data-testid=recent-dispatch-detail]"));

        await cut.InvokeAsync(() => cut.Find("[data-testid=recent-dispatch]").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Contains($"#{item.Number} Build the thing", cut.Find("[data-testid=recent-dispatch-item]").TextContent);
            var row = cut.Find("[data-testid=recent-dispatch-stop]");
            Assert.Contains("time per item", row.TextContent);
            Assert.Contains("125 min elapsed, limit 120 min", row.TextContent);
            Assert.Contains("a note for the person reading the history", row.TextContent);
        });
    }
}
