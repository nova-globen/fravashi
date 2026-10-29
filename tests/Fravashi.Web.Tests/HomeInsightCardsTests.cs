using Bunit;
using Fravashi.Application.Git;
using Fravashi.Application.Persistence;
using Fravashi.Application.Sessions;
using Fravashi.Application.Telemetry;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Git;
using Fravashi.Domain.Telemetry;
using Fravashi.Infrastructure.Bridge;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Web.Components.Dashboard;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 36: the home surface's four insight cards. The item's standing rule is that an insight
/// computed from data we do not have is worse than no card, so every test here SEEDS a store and
/// asserts the card against what was seeded, and the empty cases assert that a card with nothing to
/// say says so rather than showing a zero.
/// </summary>
public class HomeInsightCardsTests
{
    /// <summary>Every card's clock, so "the last thirty days" is a fixed range rather than today's.</summary>
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private sealed class OneWorkspaceStore(string? id, string name) : IWorkspaceStore
    {
        public IReadOnlyList<WorkspaceRecord> Recent(int max = 10) => [];

        public WorkspaceRecord? Get(string lookup) =>
            id is not null && lookup == id ? new WorkspaceRecord(id, name, @"C:\repo", Now) : null;

        public void Touch(string workspaceId, string workspaceName, string rootPath)
        {
        }

        public void Remove(string workspaceId)
        {
        }
    }

    private static (BunitContext Ctx, RecordingSessionStore Store) Context(
        IProductEventRecorder? telemetry = null,
        IProductEventStore? events = null,
        IGitService? git = null,
        string? activeWorkspaceId = null,
        string workspaceName = "repo")
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var values = new Dictionary<string, string?>();
        if (activeWorkspaceId is not null)
        {
            values[BridgeWorkspaceContext.ActiveIdKey] = activeWorkspaceId;
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        ctx.Services.AddSingleton(configuration);
        ctx.Services.AddSingleton(new BridgeWorkspaceContext(configuration));
        ctx.Services.AddSingleton<IWorkspaceStore>(new OneWorkspaceStore(activeWorkspaceId, workspaceName));
        ctx.Services.AddLogging();
        var store = ctx.Services.AddSessionSurface();
        ctx.Services.AddInsightCards(Now, telemetry, events, git);
        return (ctx, store);
    }

    /// <summary>A session that started at <paramref name="started"/>, with the usage it ended up costing.</summary>
    private static AgentSessionId Seed(
        RecordingSessionStore store,
        DateTimeOffset started,
        decimal costUsd = 0m,
        DateTimeOffset? ended = null,
        int turns = 0,
        string workspace = "w1")
    {
        var id = AgentSessionId.New();
        store.Save(new SessionRecord(
            id, workspace, AgentTool.Claude, "A conversation", "cli-1",
            ended is null ? AgentSessionState.Running : AgentSessionState.Completed, started, ended));

        if (costUsd > 0m)
        {
            store.Append(id, [new StatsUpdated(started, new AgentStatsSnapshot(10, 20, 0, costUsd, 0, 0, turns))]);
        }

        for (var i = 0; i < turns; i++)
        {
            store.Append(id, [new TurnCompleted(started.AddMinutes(i), "done", false, "completed")]);
        }

        return id;
    }

    private static GitCommit Commit(DateTimeOffset when, string author) =>
        new(Guid.NewGuid().ToString("N"), "abc1234", "a change", author, when);

    // ---- Card 1: agent spend -------------------------------------------------------------------

    [Fact]
    public void Spend_sums_the_sessions_of_each_day_and_states_its_period()
    {
        var (ctx, store) = Context();
        using var _ = ctx;
        Seed(store, Now.AddDays(-1), costUsd: 1.50m);
        Seed(store, Now.AddDays(-1), costUsd: 0.25m);
        Seed(store, Now.AddDays(-10), costUsd: 2.00m);

        var cut = ctx.Render<AgentSpendCard>();

        // The period is the card's own claim about what it covers: thirty days ending today, UTC.
        Assert.Equal("16 Aug to 14 Sep 2026, UTC", cut.Find("[data-testid='insight-spend-period']").TextContent.Trim());
        var total = cut.Find("[data-testid='insight-spend-total']").TextContent;
        Assert.Contains("$3.75", total);
        Assert.Contains("over 3 sessions", total);
        // The busiest DAY is 4 Sep at $2.00: two sessions on 13 Sep come to $1.75 between them, and the
        // figure is about days, not about the single most expensive session.
        Assert.Contains("$2.00", total);
    }

    [Fact]
    public void Spend_draws_one_slot_per_day_including_the_days_nothing_happened()
    {
        var (ctx, store) = Context();
        using var _ = ctx;
        Seed(store, Now.AddDays(-1), costUsd: 1.50m);

        var cut = ctx.Render<AgentSpendCard>();

        var chart = cut.Find("[data-testid='insight-spend-bars']");
        var slots = chart.QuerySelectorAll("g");
        Assert.Equal(AgentSpendCard.DayCount, slots.Length);
        // An idle day still occupies its slot and says so, rather than compressing the month.
        Assert.Contains("13 Sep: $1.50 over 1 session", chart.InnerHtml);
        Assert.Contains("12 Sep: no sessions", chart.InnerHtml);

        // The geometry itself, because a chart nothing looks at is exactly where a silent wrong number
        // lives: the one day with spend is the tallest bar there can be, and an idle day draws nothing.
        // The coordinates are also the invariant check: on this host's Swedish culture a default-rendered
        // 1.5 would be "1,5", which SVG reads as two numbers.
        var busiest = slots[^2].QuerySelector(".insight-bar-primary");
        Assert.NotNull(busiest);
        Assert.Equal("60", busiest!.GetAttribute("height"));
        Assert.Equal("4", busiest.GetAttribute("y"));
        Assert.Equal("1.5", slots[0].QuerySelector(".insight-bar-primary")!.GetAttribute("x"));
        Assert.Equal("0", slots[^3].QuerySelector(".insight-bar-primary")!.GetAttribute("height"));
    }

    [Fact]
    public void Spend_says_nothing_yet_rather_than_zero_when_the_period_is_empty()
    {
        var (ctx, store) = Context();
        using var _ = ctx;
        // Real sessions, all of them older than the window: the card must not report their money.
        Seed(store, Now.AddDays(-45), costUsd: 9.99m);

        var cut = ctx.Render<AgentSpendCard>();

        Assert.Contains("Nothing yet", cut.Find("[data-testid='insight-spend-empty']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='insight-spend-total']"));
        Assert.DoesNotContain("9.99", cut.Markup);
    }

    // ---- Card 2: throughput --------------------------------------------------------------------

    [Fact]
    public void Throughput_counts_sessions_started_and_turns_completed_over_a_fortnight()
    {
        var (ctx, store) = Context();
        using var _ = ctx;
        Seed(store, Now.AddDays(-2), turns: 3);
        Seed(store, Now.AddDays(-2), turns: 1);
        Seed(store, Now.AddDays(-13), turns: 2);
        // Outside the fortnight: neither its session nor its turns may appear.
        Seed(store, Now.AddDays(-20), turns: 7);

        var cut = ctx.Render<ThroughputCard>();

        Assert.Equal("1 Sep to 14 Sep 2026, UTC",
            cut.Find("[data-testid='insight-throughput-period']").TextContent.Trim());
        var total = cut.Find("[data-testid='insight-throughput-total']").TextContent;
        Assert.Contains("3", total);
        Assert.Contains("sessions started", total);
        Assert.Contains("6", total);
        Assert.Contains("turns completed", total);

        var chart = cut.Find("[data-testid='insight-throughput-bars']");
        Assert.Equal(ThroughputCard.DayCount, chart.QuerySelectorAll("g").Length);
        Assert.Contains("12 Sep: 2 started, 4 turns completed", chart.InnerHtml);
    }

    [Fact]
    public void Throughput_says_nothing_yet_when_the_fortnight_is_empty()
    {
        var (ctx, store) = Context();
        using var _ = ctx;
        Seed(store, Now.AddDays(-30), turns: 4);

        var cut = ctx.Render<ThroughputCard>();

        Assert.Contains("Nothing yet", cut.Find("[data-testid='insight-throughput-empty']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='insight-throughput-bars']"));
    }

    // ---- Card 3: where time goes ---------------------------------------------------------------

    [Fact]
    public void Time_reports_the_median_and_longest_finished_session()
    {
        var (ctx, store) = Context();
        using var _ = ctx;
        var start = Now.AddDays(-3);
        Seed(store, start, ended: start.AddMinutes(10));
        Seed(store, start, ended: start.AddMinutes(30));
        Seed(store, start, ended: start.AddHours(2));
        // Still running, so it has no duration yet and must not lengthen either figure.
        Seed(store, start);

        var cut = ctx.Render<TimeSpentCard>();

        Assert.Equal("15 Aug to 14 Sep 2026, UTC", cut.Find("[data-testid='insight-time-period']").TextContent.Trim());
        var figures = cut.Find("[data-testid='insight-time-sessions']").TextContent;
        Assert.Contains("30m 0s", figures);
        Assert.Contains("2h 0m", figures);
        Assert.Contains("Across 3 finished sessions", cut.Markup);
    }

    [Fact]
    public void Time_says_approval_wait_is_not_measured_while_product_events_are_off()
    {
        // The recorded events are there, but the switch is off, so the card may not read them: what it
        // owes the reader is the reason the figure is missing, not a figure from somewhere else.
        var (ctx, store) = Context(
            telemetry: new StubProductEventRecorder(enabled: false),
            events: new StubProductEventStore(ProductEvent.ApprovalResolved(
                Now.AddDays(-1), "w1", ProductEventOutcome.Approved, TimeSpan.FromSeconds(42))));
        using var _ = ctx;
        var start = Now.AddDays(-1);
        Seed(store, start, ended: start.AddMinutes(5));

        var cut = ctx.Render<TimeSpentCard>();

        Assert.Contains("product events are switched off",
            cut.Find("[data-testid='insight-time-approvals-off']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='insight-time-approvals']"));
        Assert.DoesNotContain("42s", cut.Markup);
    }

    [Fact]
    public void Time_points_at_diagnostics_where_it_lives_now_not_the_retired_environment_tab()
    {
        // Work item #361: #357 retired Settings > Environment, and Diagnostics is under Advanced now.
        var (ctx, _) = Context(telemetry: new StubProductEventRecorder(enabled: false));
        using var _ctx = ctx;

        var note = ctx.Render<TimeSpentCard>().Find("[data-testid='insight-time-approvals-off']");

        Assert.DoesNotContain("Environment", note.TextContent);
        var link = note.QuerySelector("[data-testid='insight-time-diagnostics-link']");
        Assert.NotNull(link);
        Assert.Equal("/settings/diagnostics", link!.GetAttribute("href"));
        Assert.Equal("Settings › Advanced › Diagnostics", link.TextContent.Trim());
    }

    [Fact]
    public void Time_reports_the_median_approval_wait_once_product_events_are_on()
    {
        var (ctx, store) = Context(
            telemetry: new StubProductEventRecorder(enabled: true),
            events: new StubProductEventStore(
                ProductEvent.ApprovalResolved(Now.AddDays(-1), "w1", ProductEventOutcome.Approved, TimeSpan.FromSeconds(4)),
                ProductEvent.ApprovalResolved(Now.AddDays(-2), "w1", ProductEventOutcome.Approved, TimeSpan.FromSeconds(20)),
                ProductEvent.ApprovalResolved(Now.AddDays(-3), "w1", ProductEventOutcome.DeniedByUser, TimeSpan.FromSeconds(90)),
                // Older than the window, and a different kind: neither may reach the median.
                ProductEvent.ApprovalResolved(Now.AddDays(-40), "w1", ProductEventOutcome.Approved, TimeSpan.FromHours(9)),
                ProductEvent.SessionStarted(Now.AddDays(-1), "w1", TimeSpan.FromMinutes(7))));
        using var _ = ctx;
        var start = Now.AddDays(-1);
        Seed(store, start, ended: start.AddMinutes(5));

        var cut = ctx.Render<TimeSpentCard>();

        var figures = cut.Find("[data-testid='insight-time-approvals']").TextContent;
        Assert.Contains("20s", figures);
        Assert.Contains("1m 30s", figures);
        Assert.Contains("Across 3 answered tool calls", cut.Markup);
        Assert.DoesNotContain("9h", cut.Markup);
    }

    [Fact]
    public void Time_says_nothing_yet_when_no_session_finished_in_the_period()
    {
        var (ctx, store) = Context();
        using var _ = ctx;
        Seed(store, Now.AddDays(-2));

        var cut = ctx.Render<TimeSpentCard>();

        Assert.Contains("Nothing yet", cut.Find("[data-testid='insight-time-empty']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='insight-time-sessions']"));
    }

    // ---- Card 4: repository health -------------------------------------------------------------

    [Fact]
    public void Repository_counts_both_windows_and_the_distinct_authors()
    {
        var git = new FakeGitService
        {
            Branch = "main",
            Commits =
            [
                Commit(Now.AddDays(-1), "Ada"),
                Commit(Now.AddDays(-3), "Ada"),
                Commit(Now.AddDays(-6), "Grace"),
                Commit(Now.AddDays(-20), "Alan"),
                Commit(Now.AddDays(-40), "Edsger"),
            ],
        };
        var (ctx, _store) = Context(git: git, activeWorkspaceId: "w1", workspaceName: "Fravashi");
        using var _ = ctx;

        var cut = ctx.Render<RepositoryHealthCard>();

        Assert.Equal("15 Aug to 14 Sep 2026, UTC", cut.Find("[data-testid='insight-repo-period']").TextContent.Trim());
        var figures = cut.Find("[data-testid='insight-repo-figures']").TextContent;
        Assert.Contains("Commits, last 7 days", figures);
        Assert.Contains("Commits, last 30 days", figures);
        Assert.Contains("Authors, last 30 days", figures);
        // Three in the last week (Ada twice, Grace), four in the month, three authors in the month.
        var values = cut.FindAll("[data-testid='insight-repo-figures'] .insight-value")
            .Select(node => node.TextContent.Trim()).ToList();
        Assert.Equal(new[] { "3", "4", "3" }, values);
        Assert.Contains("Fravashi, on main", cut.Find("[data-testid='insight-repo-name']").TextContent);
        Assert.Equal(new[] { RepositoryHealthCard.CommitCap }, git.RequestedMax);
    }

    [Fact]
    public void Repository_says_there_is_no_folder_open_rather_than_reporting_on_nothing()
    {
        var (ctx, _store) = Context(git: new FakeGitService { Available = false });
        using var _ = ctx;

        var cut = ctx.Render<RepositoryHealthCard>();

        Assert.Contains("No folder is open", cut.Find("[data-testid='insight-repo-unavailable']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='insight-repo-figures']"));
        Assert.Empty(cut.FindAll("[data-testid='insight-repo-period']"));
    }

    [Fact]
    public void Repository_says_the_open_folder_is_not_a_repository()
    {
        var (ctx, _store) = Context(
            git: new FakeGitService { Available = false }, activeWorkspaceId: "w1", workspaceName: "notes");
        using var _ = ctx;

        var cut = ctx.Render<RepositoryHealthCard>();

        Assert.Contains("notes is open, but it is not a git repository",
            cut.Find("[data-testid='insight-repo-unavailable']").TextContent);
    }

    [Fact]
    public void Repository_says_nothing_yet_when_the_month_holds_no_commit()
    {
        var git = new FakeGitService { Commits = [Commit(Now.AddDays(-90), "Ada")] };
        var (ctx, _store) = Context(git: git, activeWorkspaceId: "w1", workspaceName: "Fravashi");
        using var _ = ctx;

        var cut = ctx.Render<RepositoryHealthCard>();

        Assert.Contains("Nothing yet", cut.Find("[data-testid='insight-repo-empty']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='insight-repo-figures']"));
    }

    [Fact]
    public void Repository_reports_the_month_as_a_floor_when_the_log_cap_is_full()
    {
        // Every commit the log returned is inside the window, so there may be more the cap cut off and
        // the card may not state the total as if it knew it.
        var git = new FakeGitService
        {
            Commits = [.. Enumerable.Range(0, RepositoryHealthCard.CommitCap)
                .Select(i => Commit(Now.AddDays(-1).AddMinutes(-i), "Ada"))],
        };
        var (ctx, _store) = Context(git: git, activeWorkspaceId: "w1");
        using var _ = ctx;

        var cut = ctx.Render<RepositoryHealthCard>();

        var values = cut.FindAll("[data-testid='insight-repo-figures'] .insight-value")
            .Select(node => node.TextContent.Trim()).ToList();
        Assert.Equal("500+", values[1]);
        Assert.Contains("a floor rather than a total", cut.Find("[data-testid='insight-repo-name']").TextContent);
    }

    [Fact]
    public void Repository_says_it_could_not_read_rather_than_showing_zeros()
    {
        var git = new FakeGitService { LogFailure = new InvalidOperationException("bridge is gone") };
        var (ctx, _store) = Context(git: git, activeWorkspaceId: "w1");
        using var _ = ctx;

        var cut = ctx.Render<RepositoryHealthCard>();

        Assert.Contains("could not be read", cut.Find("[data-testid='insight-repo-unavailable']").TextContent);
        Assert.Empty(cut.FindAll("[data-testid='insight-repo-figures']"));
    }

    // ---- The acceptance's other half: updating without a restart -------------------------------

    [Fact]
    public void A_card_rereads_its_store_when_the_registry_reports_a_change()
    {
        // "Updates without a restart" is half the item's acceptance, and the registry's event is the
        // prompt half of it: a conversation finishing in this window must move the card.
        var (ctx, store) = Context();
        using var _ = ctx;
        var cut = ctx.Render<AgentSpendCard>();
        Assert.NotNull(cut.Find("[data-testid='insight-spend-empty']"));

        Seed(store, Now.AddDays(-1), costUsd: 4.00m);
        ctx.Services.GetRequiredService<LiveSessionRegistry>().Delete(AgentSessionId.New());

        cut.WaitForAssertion(() =>
            Assert.Contains("$4.00", cut.Find("[data-testid='insight-spend-total']").TextContent));
    }

    [Fact]
    public void Every_card_states_the_period_it_covers()
    {
        // The acceptance names this explicitly, and a period label is the kind of thing that goes
        // missing in a redesign, so all four are held at once rather than only where it is convenient.
        var (ctx, store) = Context(
            git: new FakeGitService { Commits = [Commit(Now.AddDays(-1), "Ada")] },
            activeWorkspaceId: "w1");
        using var _ = ctx;
        var start = Now.AddDays(-1);
        Seed(store, start, costUsd: 1m, ended: start.AddMinutes(4), turns: 1);

        var periods = new[]
        {
            ctx.Render<AgentSpendCard>().Find("[data-testid='insight-spend-period']").TextContent,
            ctx.Render<ThroughputCard>().Find("[data-testid='insight-throughput-period']").TextContent,
            ctx.Render<TimeSpentCard>().Find("[data-testid='insight-time-period']").TextContent,
            ctx.Render<RepositoryHealthCard>().Find("[data-testid='insight-repo-period']").TextContent,
        };

        foreach (var period in periods.Select(p => p.Trim()))
        {
            Assert.EndsWith("2026, UTC", period);
            Assert.Contains(" to ", period);
        }
    }
}
