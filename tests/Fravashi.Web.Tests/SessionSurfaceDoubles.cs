using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Insights;
using Fravashi.Infrastructure.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace Fravashi.Web.Tests;

/// <summary>
/// The session seams a page that renders a session surface needs. The home surface renders the
/// recent-conversations card (backlog 34), so every Home test needs them even when it is testing
/// something else entirely.
/// </summary>
internal static class SessionSurfaceDoubles
{
    /// <summary>
    /// Registers a real <see cref="LiveSessionRegistry"/> over an in-memory store and a runner that
    /// never emits anything, so nothing starts and the card reads exactly the rows the test wrote.
    /// </summary>
    public static RecordingSessionStore AddSessionSurface(
        this IServiceCollection services, RecordingSessionStore? store = null)
    {
        var sessions = store ?? new RecordingSessionStore();
        services.AddSingleton<ISessionStore>(sessions);
        services.AddSingleton<IAgentSessionRunner>(new SilentSessionRunner());
        services.AddSingleton<IAgentApprovalBroker>(new AgentApprovalBroker(TimeSpan.FromSeconds(5)));
        services.AddSingleton(sp => new LiveSessionRegistry(
            sp.GetRequiredService<IAgentSessionRunner>(),
            sp.GetRequiredService<ISessionStore>(),
            sp.GetRequiredService<IAgentApprovalBroker>()));
        return sessions;
    }
}

/// <summary>An in-memory <see cref="ISessionStore"/> that also records what was deleted through it.</summary>
internal sealed class RecordingSessionStore : ISessionStore
{
    private readonly List<SessionRecord> _records = [];

    public List<AgentSessionId> Deleted { get; } = [];

    public IReadOnlyList<SessionRecord> Records => _records;

    public void Add(SessionRecord record) => Save(record);

    public void Save(SessionRecord record)
    {
        _records.RemoveAll(r => r.Id == record.Id);
        _records.Add(record);
    }

    public SessionRecord? Find(AgentSessionId id) => _records.FirstOrDefault(r => r.Id == id);

    public IReadOnlyList<SessionRecord> ForWorkspace(string workspaceId, int max = 50) =>
        [.. _records.Where(r => r.WorkspaceId == workspaceId).OrderByDescending(r => r.StartedUtc).Take(max)];

    public IReadOnlyList<SessionRecord> Recent(int max = 10) =>
        [.. _records.OrderByDescending(r => r.StartedUtc).Take(max)];

    /// <summary>
    /// Persists exactly what the real store persists: the events that have a kind, and the latest
    /// cumulative usage snapshot folded into the session's summary. Backlog 36 reads both of those back
    /// as aggregates, so a seeded store that threw events away could not stand in for one.
    /// </summary>
    public void Append(AgentSessionId id, IEnumerable<AgentEvent> events)
    {
        foreach (var e in events)
        {
            if (e is StatsUpdated stats)
            {
                _usage[id] = stats.Stats;
            }

            if (AgentEventJson.KindOf(e) is not null)
            {
                _transcripts.TryAdd(id, []);
                _transcripts[id].Add(e);
            }
        }
    }

    public IReadOnlyList<AgentEvent> Transcript(AgentSessionId id) =>
        _transcripts.TryGetValue(id, out var events) ? [.. events] : [];

    public void Delete(AgentSessionId id)
    {
        Deleted.Add(id);
        _records.RemoveAll(r => r.Id == id);
        _transcripts.Remove(id);
        _usage.Remove(id);
    }

    public SessionUsageTotals ReadUsageTotals(DateTimeOffset dayStartUtc, DateTimeOffset monthStartUtc) =>
        SessionUsageTotals.Empty;

    /// <summary>Backlog 36, keyed by the day a session STARTED, exactly as the SQLite store keys it.</summary>
    public IReadOnlyList<SessionDay> ReadSessionDays(DateOnly fromDayUtc, DateOnly toDayUtc) =>
        [.. _records
            .Select(r => (Day: DateOnly.FromDateTime(r.StartedUtc.UtcDateTime), Record: r))
            .Where(row => row.Day >= fromDayUtc && row.Day <= toDayUtc)
            .GroupBy(row => row.Day)
            .OrderBy(group => group.Key)
            .Select(group => new SessionDay(
                group.Key,
                group.Sum(row => _usage.TryGetValue(row.Record.Id, out var usage) ? usage.CostUsd : 0m),
                group.Count()))];

    /// <summary>Backlog 36: one count per persisted <see cref="TurnCompleted"/>, by the day it landed.</summary>
    public IReadOnlyList<DayCount> ReadTurnDays(DateOnly fromDayUtc, DateOnly toDayUtc) =>
        [.. _transcripts.Values
            .SelectMany(events => events)
            .OfType<TurnCompleted>()
            .Select(e => DateOnly.FromDateTime(e.Timestamp.UtcDateTime))
            .Where(day => day >= fromDayUtc && day <= toDayUtc)
            .GroupBy(day => day)
            .OrderBy(group => group.Key)
            .Select(group => new DayCount(group.Key, group.Count()))];

    /// <summary>Backlog 36: only sessions that started in the window AND have ended carry a duration.</summary>
    public DurationStats ReadSessionDurations(DateTimeOffset fromUtc) =>
        DurationStats.From(_records
            .Where(r => r.StartedUtc >= fromUtc && r.EndedUtc is not null)
            .Select(r => r.EndedUtc!.Value - r.StartedUtc));

    private readonly Dictionary<AgentSessionId, List<AgentEvent>> _transcripts = [];
    private readonly Dictionary<AgentSessionId, AgentStatsSnapshot> _usage = [];
}

/// <summary>A runner that never produces an event: nothing in these tests is a live conversation.</summary>
internal sealed class SilentSessionRunner : IAgentSessionRunner
{
    public IAsyncEnumerable<AgentEvent> RunAsync(
        AgentSessionRequest request, CancellationToken cancellationToken = default) => Empty();

    public Task<bool> SendAsync(AgentSessionId sessionId, string text, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public bool Stop(AgentSessionId sessionId) => true;

    public bool ChangeModel(AgentSessionId sessionId, string? model) => true;

    private static async IAsyncEnumerable<AgentEvent> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }
}
