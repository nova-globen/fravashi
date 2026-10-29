using System.Collections.Concurrent;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.AgentRuntimes;

/// <summary>
/// Thread-safe in-memory <see cref="IAgentRuntimeStatusReport"/>. A singleton, so the update-on-launch results
/// produced at container start are readable by every circuit and the runtime-status endpoint.
/// </summary>
public sealed class InMemoryAgentRuntimeStatusReport : IAgentRuntimeStatusReport
{
    private readonly ConcurrentDictionary<AgentTool, AgentRuntimeUpdateResult> _results = new();
    private volatile bool _inProgress;
    private long _lastCompletedTicks;

    public bool InProgress => _inProgress;

    public DateTimeOffset? LastCompletedUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastCompletedTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public void BeginRun() => _inProgress = true;

    public void EndRun()
    {
        _inProgress = false;
        Interlocked.Exchange(ref _lastCompletedTicks, DateTimeOffset.UtcNow.UtcTicks);
    }

    public void Record(AgentRuntimeUpdateResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _results[result.Tool] = result;
    }

    public AgentRuntimeUpdateResult? Latest(AgentTool tool) =>
        _results.TryGetValue(tool, out var result) ? result : null;

    public IReadOnlyList<AgentRuntimeUpdateResult> Snapshot() =>
        AgentRuntimeCatalog.Tools
            .Where(_results.ContainsKey)
            .Select(t => _results[t])
            .ToList();
}
