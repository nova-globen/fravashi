using Fravashi.Domain.AgentSessions;

namespace Fravashi.Web.Components.Sessions;

/// <summary>
/// How a conversation's outcome is worded, in ONE place: the sessions sidebar card and the home
/// surface's recent-conversations card (backlog 34) both label the same states, and two copies of the
/// mapping would drift the first time a state was reworded in one of them.
/// </summary>
public static class SessionStateText
{
    public static string Label(AgentSessionState state) => state switch
    {
        AgentSessionState.Running => "running",
        AgentSessionState.Completed => "done",
        AgentSessionState.Failed => "failed",
        AgentSessionState.Cancelled => "stopped",
        _ => "pending",
    };

    /// <summary>The state's class suffix, so the same colour dot means the same thing on both cards.</summary>
    public static string CssSuffix(AgentSessionState state) => state.ToString().ToLowerInvariant();
}
