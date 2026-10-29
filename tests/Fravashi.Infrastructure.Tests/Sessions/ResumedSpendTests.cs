using System.Threading.Channels;
using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Backlog 62 after resume-in-place: a conversation resumed today from one started yesterday (or last month)
/// spends TODAY. The day and month ceilings read those totals, so spending has to be counted in the window it
/// was incurred in, not in the window the conversation happened to start in, or a resumed conversation is a
/// way around both ceilings. Driven through the registry over the real SQLite store, with a clock the test moves.
/// </summary>
public sealed class ResumedSpendTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-resumed-spend-" + Guid.NewGuid().ToString("N"));

    private readonly SqliteSessionStore _store;
    private readonly AgentApprovalBroker _broker = new(TimeSpan.FromSeconds(5));
    private readonly PerRunRunner _runner = new();
    private DateTimeOffset _now;

    public ResumedSpendTests()
    {
        Directory.CreateDirectory(_directory);
        var factory = new SqliteConnectionFactory(
            new SqliteStoreOptions(Path.Combine(_directory, "fravashi.db"), Pooling: false));
        SqliteSchema.EnsureCreated(factory);
        _store = new SqliteSessionStore(factory);
    }

    public void Dispose()
    {
        _broker.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A runner whose every run gets its own event channel, so a resumed run is a new stream.</summary>
    private sealed class PerRunRunner : IAgentSessionRunner
    {
        private Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>();

        public IAsyncEnumerable<AgentEvent> RunAsync(AgentSessionRequest request, CancellationToken cancellationToken = default)
        {
            _events = Channel.CreateUnbounded<AgentEvent>();
            Runs++;
            return _events.Reader.ReadAllAsync(cancellationToken);
        }

        public Task<bool> SendAsync(AgentSessionId sessionId, string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public bool Stop(AgentSessionId sessionId) => true;

        public bool ChangeModel(AgentSessionId sessionId, string? model) => true;

        public int Runs { get; private set; }

        public void Emit(AgentEvent e) => _events.Writer.TryWrite(e);

        public void Finish(DateTimeOffset at)
        {
            Emit(new SessionEnded(at, AgentSessionState.Completed, null));
            _events.Writer.TryComplete();
        }
    }

    private static async Task Eventually(Func<bool> condition)
    {
        // A DEADLINE, not a fixed number of attempts. The budgets here used to be 2, 5, 10 and 15
        // seconds with no principle behind the numbers, and the 2 second ones failed on CI: run 103
        // and run 107 on 2026-09-19 went red on commits that changed only markdown, because the
        // runner was a shared host that also ran production, and a contended machine needs longer to
        // reach a turn boundary than this dev host does. A generous ceiling costs nothing when the
        // condition is met quickly, since the loop exits the moment it is true; it only matters on a
        // machine slow enough that the old limit was measuring the machine rather than the behaviour.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(10); // poll interval inside a wait helper (backlog 107)
        }

        condition().Should().BeTrue();
    }

    private static StatsUpdated Spent(DateTimeOffset at, decimal cost, long input, long output) =>
        new(at, new AgentStatsSnapshot(input, output, 0, cost, 0, 0, 1));

    /// <summary>Spends $1.00 on the first day, ends, then resumes after <paramref name="later"/> and spends $0.50 more.</summary>
    private async Task<(LiveSessionRegistry Registry, AgentSessionId Id)> SpendAcrossAResume(
        DateTimeOffset first, DateTimeOffset later)
    {
        _now = first;
        var registry = new LiveSessionRegistry(_runner, _store, _broker, clock: () => _now);
        var session = registry.Start(new SessionStartRequest("w1", "/workspaces/w1", "the first thing"));
        await Eventually(() => _runner.Runs == 1);
        _runner.Emit(new SessionRegistered(first, "cli-1", null));
        _runner.Emit(Spent(first, 1.00m, 100, 200));
        _runner.Finish(first);
        await Eventually(() => _store.Find(session.Id)?.State == AgentSessionState.Completed);

        _now = later;
        var runsBefore = _runner.Runs;
        registry.Resume(session.Id, "/workspaces/w1");
        await Eventually(() => _runner.Runs > runsBefore);
        // The resumed PROCESS counts from zero: $0.50 of its own, which is the conversation's $1.50.
        _runner.Emit(Spent(later, 0.50m, 10, 20));
        await Eventually(() => session.Stats.CostUsd == 1.50m);
        await Eventually(() => registry.UsageTotals().AllTime.CostUsd == 1.50m);
        return (registry, session.Id);
    }

    [Fact]
    public async Task Spending_after_a_resume_that_crosses_midnight_counts_toward_today_not_yesterday()
    {
        var (registry, _) = await SpendAcrossAResume(
            new DateTimeOffset(2026, 9, 14, 23, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 15, 1, 0, 0, TimeSpan.Zero));
        using var _ = registry;

        var totals = registry.UsageTotals();

        totals.Today.CostUsd.Should().Be(0.50m, "the $0.50 was spent today, the $1.00 yesterday");
        totals.Today.TotalTokens.Should().Be(30);
        totals.Today.Sessions.Should().Be(1, "a conversation that spent today is part of today");
        totals.Month.CostUsd.Should().Be(1.50m);
        totals.AllTime.CostUsd.Should().Be(1.50m);
    }

    [Fact]
    public async Task Spending_after_a_resume_that_crosses_a_month_counts_toward_this_month_not_last()
    {
        var (registry, _) = await SpendAcrossAResume(
            new DateTimeOffset(2026, 9, 30, 23, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 1, 1, 0, 0, TimeSpan.Zero));
        using var _ = registry;

        var totals = registry.UsageTotals();

        totals.Today.CostUsd.Should().Be(0.50m);
        totals.Month.CostUsd.Should().Be(0.50m, "last month's $1.00 is not this month's");
        totals.Month.TotalTokens.Should().Be(30);
        totals.AllTime.CostUsd.Should().Be(1.50m);
    }

    [Fact]
    public async Task The_daily_ceiling_refuses_a_run_once_a_resumed_conversation_has_spent_it_today()
    {
        _now = new DateTimeOffset(2026, 9, 14, 23, 0, 0, TimeSpan.Zero);
        var (registry, _) = await SpendAcrossAResume(_now, _now.AddHours(2));
        using var _r = registry;
        using var limited = new LiveSessionRegistry(
            new PerRunRunner(), _store, _broker, clock: () => _now,
            limits: new FixedLimits(new AgentSpendLimits(PerSessionUsd: null, PerDayUsd: 0.40m, PerMonthUsd: null)));

        limited.SpendRefusal().Should().NotBeNull("today's $0.50 is over a $0.40 day");
    }

    [Fact]
    public async Task The_days_list_puts_a_resumed_conversations_spending_on_the_day_it_happened()
    {
        var (registry, _) = await SpendAcrossAResume(
            new DateTimeOffset(2026, 9, 14, 23, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 15, 1, 0, 0, TimeSpan.Zero));
        using var _ = registry;

        var days = _store.ReadSessionDays(new DateOnly(2026, 9, 14), new DateOnly(2026, 9, 15));

        days.Select(d => (d.Day, d.CostUsd)).Should().Equal(
            (new DateOnly(2026, 9, 14), 1.00m),
            (new DateOnly(2026, 9, 15), 0.50m));
    }

    private sealed class FixedLimits(AgentSpendLimits limits) : IAgentSpendLimitSource
    {
        public AgentSpendLimits Current() => limits;
    }
}
