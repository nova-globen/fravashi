using System.Collections.Concurrent;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// <see cref="ISessionAttemptBases"/> held in memory (#315). Never persisted on purpose: only an autopilot
/// builder sets one, for a session it has just started, and a restart gives a resumed item a FRESH session
/// through that same start path, which sets it again.
/// </summary>
public sealed class InMemorySessionAttemptBases : ISessionAttemptBases
{
    private readonly ConcurrentDictionary<AgentSessionId, string> _bases = new();

    /// <inheritdoc />
    public string? For(AgentSessionId session) => _bases.TryGetValue(session, out var baseBranch) ? baseBranch : null;

    /// <inheritdoc />
    public void Set(AgentSessionId session, string baseBranch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);
        _bases[session] = baseBranch;
    }
}
