namespace Fravashi.Domain.AgentSessions;

/// <summary>Where a tool call's path lands, relative to the session's contained copy.</summary>
public enum AgentTargetContainment
{
    /// <summary>Every reading of the path stays inside the copy.</summary>
    Inside,

    /// <summary>
    /// At least one reading of the path leaves the copy. This is the fact a user answering an approval
    /// must not be able to miss, so nothing may render a call carrying it without saying so.
    /// </summary>
    Outside,

    /// <summary>
    /// The studio could not work out where it lands (no copy registered, an unresolvable value, a field
    /// the tool did not send as text). That is NOT the same as inside and is never shown as if it were.
    /// </summary>
    Undetermined,
}

/// <summary>
/// The path an approval is really about, resolved, for the human answering it.
/// <para>
/// It exists because the one-line subject on the prompt is CAPPED, and a capped path is a different
/// path: a <c>file_path</c> of 500 characters that resolves to the Data Protection key ring reads, at
/// 200 characters, as an ordinary folder deep inside the workspace copy. The user approved what they
/// were shown and the CLI wrote what it was sent. So the resolved destination travels with the request
/// and the prompt shows it, in full, next to the capped subject rather than instead of it.
/// </para>
/// <para>
/// Every string here is FOR DISPLAY and may have been shortened (middle-out, so the tail that names the
/// destination always survives). <see cref="Containment"/> was decided on the RAW value before any of
/// that, the same rule the read scope has always followed: nothing shortened may reach a decision.
/// </para>
/// </summary>
/// <param name="Field">The input field the path came from ("file_path", "notebook_path", "path").</param>
/// <param name="Requested">What the tool asked for, as it sent it.</param>
/// <param name="Canonical">
/// Where the operating system says that really lands, or null when the studio could not tell.
/// </param>
/// <param name="Collapsed">
/// Where it lands when the calling program collapses <c>..</c> as TEXT first and the OS then follows
/// what that spells, given ONLY when it disagrees with <paramref name="Canonical"/>. That is the order
/// a real opener works in, and the disagreement between the two is how a symlink inside the copy hides
/// a destination outside it: an as-written walk can stay in the copy while the collapsed spelling names
/// a link that leaves it.
/// </param>
/// <param name="Containment">Whether that destination is inside the session's contained copy.</param>
public sealed record AgentToolTarget(
    string Field,
    string Requested,
    string? Canonical,
    string? Collapsed,
    AgentTargetContainment Containment);
