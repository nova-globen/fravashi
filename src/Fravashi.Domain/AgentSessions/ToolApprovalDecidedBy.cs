namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// WHO answered a parked tool call. That is a separate question from WHAT the answer was, and the
/// transcript needs both: a denial the user made and a denial the studio imposed on their behalf (a
/// timeout, an abandoned request, a released session, a shutdown) are the same "not approved" and mean
/// completely different things.
/// <para>
/// Backlog 114: rendering every non-approval as "You denied" told an owner they had refused a call they
/// had never been asked about, and they reasonably read that as the approval gate having been bypassed.
/// An approval surface that misattributes its own decisions destroys the trust the gate depends on.
/// </para>
/// <para>
/// <see cref="Studio"/> is 0 on purpose, so the fallback everywhere (an unset value, a transcript row
/// written before this distinction existed) is the NEUTRAL one. Blaming the user for a refusal the
/// studio made is the worse error of the two, so nothing may reach <see cref="User"/> by default.
/// </para>
/// </summary>
public enum ToolApprovalDecidedBy
{
    /// <summary>The studio decided this one: an auto-allow, a timeout, a release, a shutdown.</summary>
    Studio = 0,

    /// <summary>The user answered it in the studio's approval prompt.</summary>
    User = 1,
}
