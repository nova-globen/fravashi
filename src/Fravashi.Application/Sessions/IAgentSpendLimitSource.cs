using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.Sessions;

/// <summary>
/// Where the owner's spend ceilings come from (backlog 62). Read on EVERY decision rather than captured
/// once: the limits live in the settings table, the Settings page reloads configuration when it saves
/// them, and a ceiling the owner just lowered has to bite the next turn rather than the next restart.
/// </summary>
public interface IAgentSpendLimitSource
{
    /// <summary>The ceilings as they stand right now.</summary>
    AgentSpendLimits Current();

    /// <summary>A source with no ceilings, for a wiring that does not configure them at all.</summary>
    public static IAgentSpendLimitSource None { get; } = new NoLimits();

    private sealed class NoLimits : IAgentSpendLimitSource
    {
        public AgentSpendLimits Current() => AgentSpendLimits.None;
    }
}
