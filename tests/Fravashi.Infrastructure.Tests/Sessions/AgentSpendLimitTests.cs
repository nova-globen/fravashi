using System.Threading.Channels;
using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Backlog 62: a spend limit that an unattended run actually stops at. The per-session ceiling is the
/// CLI's own (<c>ClaudeSessionRunnerTests</c> holds the flag); these are the two the studio has to
/// enforce itself, because a day and a month are sums across sessions that no single invocation sees.
/// <para>
/// The registry is driven against a REAL SQLite store rather than a stub, because the figure the guard
/// compares is the one the store folds out of each session's usage snapshot, and a stub would let the
/// aggregation and the refusal drift apart without anyone noticing.
/// </para>
/// </summary>
public class AgentSpendLimitTests : IDisposable
{
    /// <summary>Mid-afternoon on 2026-09-13 UTC: a day well inside its month, and neither boundary.</summary>
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 15, 0, 0, TimeSpan.Zero);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-spend-" + Guid.NewGuid().ToString("N"));

    private readonly SqliteSessionStore _store;
    private readonly AgentApprovalBroker _broker = new(TimeSpan.FromSeconds(5));
    private readonly ScriptedRunner _runner = new();
    private readonly MutableLimits _limits = new();
    private readonly LiveSessionRegistry _registry;

    public AgentSpendLimitTests()
    {
        Directory.CreateDirectory(_directory);
        var factory = new SqliteConnectionFactory(
            new SqliteStoreOptions(Path.Combine(_directory, "fravashi.db"), Pooling: false));
        SqliteSchema.EnsureCreated(factory);
        _store = new SqliteSessionStore(factory);
        _registry = new LiveSessionRegistry(
            _runner, _store, _broker, clock: () => Now, limits: _limits);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _registry.Dispose();
        _broker.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- reading the limits out of settings -------------------------------------------------

    [Theory]
    [InlineData("5", 5)]
    [InlineData("2.50", 2.50)]
    [InlineData(" 12 ", 12)]
    public void A_stored_amount_becomes_a_limit(string stored, decimal expected) =>
        ConfigurationAgentSpendLimits.Read(stored).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("five")]
    [InlineData("2,50")]
    public void Anything_that_is_not_an_amount_above_zero_is_no_limit_at_all(string? stored)
    {
        // Including "2,50": the rows are parsed invariantly, and this host runs a Swedish culture where
        // a comma is the decimal separator. Reading it culturally would make the same stored row mean
        // 2.50 here and 250 on the owner's next machine.
        ConfigurationAgentSpendLimits.Read(stored).Should().BeNull();
    }

    [Fact]
    public void The_limits_are_read_fresh_from_configuration_on_every_ask()
    {
        // A ceiling saved in Settings has to bite the next turn, not the next restart, so the source may
        // not capture what it read the first time.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ConfigurationAgentSpendLimits.PerDayKey] = "10",
            })
            .Build();
        var source = new ConfigurationAgentSpendLimits(configuration);

        source.Current().Should().Be(new AgentSpendLimits(null, 10m, null));

        configuration[ConfigurationAgentSpendLimits.PerDayKey] = "20";

        source.Current().PerDayUsd.Should().Be(20m);
    }

    // ---- the guard, over the aggregation --------------------------------------------------

    [Fact]
    public void Spend_is_summed_over_the_utc_day_and_the_utc_calendar_month()
    {
        // Three sessions: one last month, one earlier this month, one today. What the guard compares is
        // the sum of the windows each session's START falls in, and the two windows must not be the same
        // number: conflating them is what would let a day limit be enforced against a month's spend.
        Seed("last month", Now.AddDays(-20), 4.00m);
        Seed("this month", Now.AddDays(-3), 2.00m);
        Seed("today, earlier", Now.AddHours(-6), 0.50m);
        Seed("today, a minute ago", Now.AddMinutes(-1), 0.25m);

        var totals = _registry.UsageTotals();

        totals.Today.CostUsd.Should().Be(0.75m);
        totals.Today.Sessions.Should().Be(2);
        totals.Month.CostUsd.Should().Be(2.75m);
        totals.Month.Sessions.Should().Be(3);
        totals.AllTime.CostUsd.Should().Be(6.75m);
        totals.AllTime.Sessions.Should().Be(4);
    }

    [Fact]
    public void A_session_that_started_before_midnight_utc_is_not_todays_spend()
    {
        // The boundary is the first instant of the UTC day, inclusive. A session a minute earlier belongs
        // to yesterday, however recently it was still running.
        var midnight = new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero);
        Seed("just before midnight", midnight.AddMinutes(-1), 9.00m);
        Seed("just after midnight", midnight, 1.00m);

        _registry.UsageTotals().Today.CostUsd.Should().Be(1.00m);
    }

    [Fact]
    public void No_limit_means_no_refusal_however_much_has_been_spent()
    {
        Seed("expensive", Now, 10_000m);

        _registry.SpendRefusal().Should().BeNull();
    }

    [Fact]
    public void Spending_below_a_limit_is_not_a_refusal()
    {
        Seed("today", Now, 4.99m);
        _limits.Value = new AgentSpendLimits(null, 5m, null);

        _registry.SpendRefusal().Should().BeNull();
    }

    [Fact]
    public void Reaching_a_limit_exactly_is_reaching_it()
    {
        // ">= " rather than "> ": a $5 limit with $5 spent is spent, and letting one more turn through
        // would be the studio deciding the number meant something else.
        Seed("today", Now, 5m);
        _limits.Value = new AgentSpendLimits(null, 5m, null);

        _registry.SpendRefusal()!.Period.Should().Be(AgentSpendPeriod.Day);
    }

    [Fact]
    public void The_day_is_reported_first_when_both_windows_are_over()
    {
        // Both are true, and the day is the one that clears soonest, which is what the reader most needs.
        Seed("today", Now, 20m);
        _limits.Value = new AgentSpendLimits(null, 5m, 10m);

        _registry.SpendRefusal()!.Period.Should().Be(AgentSpendPeriod.Day);
    }

    // ---- what the user is told ------------------------------------------------------------

    [Fact]
    public void Starting_a_session_over_the_daily_limit_is_refused_naming_the_limit_and_the_spend()
    {
        Seed("today", Now, 12.34m);
        _limits.Value = new AgentSpendLimits(null, 10m, null);

        var refusal = Assert.Throws<InvalidOperationException>(
            () => _registry.Start(new SessionStartRequest("w1", "/workspaces/w1", "go")));

        refusal.Message.Should().Be(
            "Agent spending has reached the daily limit: $12.34 spent today (UTC) against a $10.00 limit. "
            + "Change it in Settings > Agents > Spending and limits.");

        // Refused means refused: nothing was created, so nothing is listed and no CLI was asked to run.
        _registry.Sessions.Should().BeEmpty();
        _runner.LastRequest.Should().BeNull();
    }

    [Fact]
    public void Starting_a_session_over_the_monthly_limit_is_refused_naming_the_limit_and_the_spend()
    {
        Seed("earlier this month", Now.AddDays(-5), 99.50m);
        _limits.Value = new AgentSpendLimits(null, null, 50m);

        var refusal = Assert.Throws<InvalidOperationException>(
            () => _registry.Start(new SessionStartRequest("w1", "/workspaces/w1", "go")));

        refusal.Message.Should().Be(
            "Agent spending has reached the monthly limit: $99.50 spent this month (UTC) against a $50.00 "
            + "limit. Change it in Settings > Agents > Spending and limits.");
    }

    [Fact]
    public async Task Sending_a_turn_over_a_limit_is_refused_with_the_same_sentence()
    {
        // A new turn is new spending, so the running session refuses it exactly as a new session is
        // refused. The refusal has to be a throw rather than a false: false already means "there is no
        // session to send to", and the user would be told nothing at all.
        var session = _registry.Start(new SessionStartRequest("w1", "/workspaces/w1", "go"));
        await Eventually(() => session.IsRunning);

        Seed("today", Now, 7m);
        _limits.Value = new AgentSpendLimits(null, 6m, null);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _registry.SendAsync(session.Id, "another turn"));

        refusal.Message.Should().Be(
            "Agent spending has reached the daily limit: $7.00 spent today (UTC) against a $6.00 limit. "
            + "Change it in Settings > Agents > Spending and limits.");
        _runner.Sent.Should().BeEmpty("the turn must never reach the CLI");
    }

    // ---- an unattended run stops when it hits one ------------------------------------------

    [Fact]
    public async Task A_running_session_is_stopped_at_the_next_turn_boundary_and_says_why()
    {
        // The acceptance criterion: an unattended run STOPS. Refusing the next typed turn is not enough,
        // because nobody is typing. The boundary is the honest place for it, since mid-turn there is work
        // in flight that stopping would throw away.
        var session = _registry.Start(new SessionStartRequest("w1", "/workspaces/w1", "go"));
        await Eventually(() => session.IsRunning);

        _limits.Value = new AgentSpendLimits(null, 2m, null);

        // The turn's own cost arrives on the result line before the turn completes, exactly as the CLI
        // reports it, so the figure the guard compares includes the turn that just finished.
        _runner.Emit(new StatsUpdated(Now, new AgentStatsSnapshot(100, 200, 0, 3.50m, 0, 0, 1)));
        _runner.Emit(new TurnCompleted(Now, "done", false, "completed"));

        await Eventually(() => _runner.Stopped, "the run should have been stopped at the turn boundary");

        session.Transcript.OfType<MessageEmitted>()
            .Where(m => m.Role == TranscriptRole.System)
            .Select(m => m.Text)
            .Should().Contain(
                "Agent spending has reached the daily limit: $3.50 spent today (UTC) against a $2.00 limit. "
                + "Change it in Settings > Agents > Spending and limits. This session has been stopped.");
    }

    [Fact]
    public async Task A_run_inside_its_limits_is_left_alone_at_the_turn_boundary()
    {
        // The control for the test above: the boundary check must not end a session that has room, which
        // would be a far worse defect than the one this item fixes.
        var session = _registry.Start(new SessionStartRequest("w1", "/workspaces/w1", "go"));
        await Eventually(() => session.IsRunning);

        _limits.Value = new AgentSpendLimits(null, 100m, 100m);

        _runner.Emit(new StatsUpdated(Now, new AgentStatsSnapshot(100, 200, 0, 3.50m, 0, 0, 1)));
        _runner.Emit(new TurnCompleted(Now, "done", false, "completed"));

        await Eventually(() => session.Transcript.OfType<TurnCompleted>().Any());
        _runner.Stopped.Should().BeFalse();
    }

    // ---- the per-session ceiling reaches the CLI -------------------------------------------

    [Fact]
    public async Task A_started_session_carries_the_per_session_ceiling_to_the_runner()
    {
        _limits.Value = new AgentSpendLimits(7.5m, null, null);

        _registry.Start(new SessionStartRequest("w1", "/workspaces/w1", "go"));

        await Eventually(() => _runner.LastRequest is not null);
        _runner.LastRequest!.MaxBudgetUsd.Should().Be(7.5m);
    }

    [Fact]
    public async Task A_started_session_carries_no_ceiling_when_none_is_configured()
    {
        _registry.Start(new SessionStartRequest("w1", "/workspaces/w1", "go"));

        await Eventually(() => _runner.LastRequest is not null);
        _runner.LastRequest!.MaxBudgetUsd.Should().BeNull();
    }

    // ---- helpers ---------------------------------------------------------------------------

    /// <summary>A finished session in the store, with a cost, started when the caller says.</summary>
    private void Seed(string title, DateTimeOffset started, decimal costUsd)
    {
        var id = AgentSessionId.New();
        _store.Save(new SessionRecord(
            id, "w1", AgentTool.Claude, title, null, AgentSessionState.Completed, started, started));
        _store.Append(id, [new StatsUpdated(started, new AgentStatsSnapshot(10, 20, 0, costUsd, 0, 0, 1))]);
    }

    private static async Task Eventually(Func<bool> condition, string because = "")
    {
        // A DEADLINE, not a fixed number of attempts. The budgets here used to be 2, 5, 10 and 15
        // seconds with no principle behind the numbers, and the 2 second ones failed on CI: run 103
        // and run 107 on 2026-09-19 went red on commits that changed only markdown, because the
        // runner was a shared host that also ran production, and a contended machine needs longer to
        // reach a turn boundary than this dev host does. A generous ceiling costs nothing when the
        // condition is met quickly, since the loop exits the moment it is true; it only matters on a
        // machine slow enough that the old limit was measuring the machine rather than the behaviour.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        condition().Should().BeTrue(because);
    }

    /// <summary>Limits the test moves between calls, which is how a saved setting behaves.</summary>
    private sealed class MutableLimits : IAgentSpendLimitSource
    {
        public AgentSpendLimits Value { get; set; } = AgentSpendLimits.None;

        public AgentSpendLimits Current() => Value;
    }

    /// <summary>A runner the test drives by hand, one event at a time.</summary>
    private sealed class ScriptedRunner : IAgentSessionRunner
    {
        private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>();

        public AgentSessionRequest? LastRequest { get; private set; }

        public List<string> Sent { get; } = [];

        public bool Stopped { get; private set; }

        public IAsyncEnumerable<AgentEvent> RunAsync(
            AgentSessionRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return _events.Reader.ReadAllAsync(cancellationToken);
        }

        public Task<bool> SendAsync(AgentSessionId sessionId, string text, CancellationToken cancellationToken = default)
        {
            Sent.Add(text);
            return Task.FromResult(true);
        }

        public bool Stop(AgentSessionId sessionId)
        {
            Stopped = true;
            _events.Writer.TryWrite(new SessionEnded(Now, AgentSessionState.Cancelled, null));
            _events.Writer.TryComplete();
            return true;
        }

        public bool ChangeModel(AgentSessionId sessionId, string? model) => true;

        public void Emit(AgentEvent e) => _events.Writer.TryWrite(e);
    }
}
