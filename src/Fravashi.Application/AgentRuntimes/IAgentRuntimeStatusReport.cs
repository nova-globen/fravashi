using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.AgentRuntimes;

/// <summary>
/// The in-memory record of the most recent update-on-launch pass, written by the update service and read by the
/// UI and the runtime-status endpoint. A singleton, so the update-on-launch result the app produced at container
/// start is visible to every circuit - the mechanism behind the wizard's "updated / up to date / failed" badge.
/// </summary>
public interface IAgentRuntimeStatusReport
{
    /// <summary>True while an update-on-launch pass is running.</summary>
    bool InProgress { get; }

    /// <summary>When the last pass finished, or null if none has run this process.</summary>
    DateTimeOffset? LastCompletedUtc { get; }

    /// <summary>Marks the start of an update pass.</summary>
    void BeginRun();

    /// <summary>Marks the end of an update pass.</summary>
    void EndRun();

    /// <summary>Records (or replaces) the latest result for one runtime.</summary>
    void Record(AgentRuntimeUpdateResult result);

    /// <summary>The latest recorded result per runtime, if any.</summary>
    AgentRuntimeUpdateResult? Latest(AgentTool tool);

    /// <summary>A snapshot of every recorded result.</summary>
    IReadOnlyList<AgentRuntimeUpdateResult> Snapshot();
}
