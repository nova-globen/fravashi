using System.Collections.Concurrent;

namespace Fravashi.Infrastructure.Autopilot;

/// <summary>
/// Keeps two ticks of the same autopilot run from ever running at once (#309's acceptance), with no queuing:
/// a run still mid-tick is simply skipped for this cycle rather than having a second tick pile up behind it,
/// which is also what keeps a slow or crashed tick from making the host spin.
/// </summary>
public sealed class AutopilotTickGate
{
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();

    /// <summary>True, and holds the gate, when no tick of this run is in flight right now. False otherwise.</summary>
    public bool TryEnter(Guid runId) => _inFlight.TryAdd(runId, 0);

    /// <summary>Releases the gate for this run, so its next tick may enter.</summary>
    public void Exit(Guid runId) => _inFlight.TryRemove(runId, out _);
}
