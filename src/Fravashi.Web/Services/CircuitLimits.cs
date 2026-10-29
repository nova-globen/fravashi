namespace Fravashi.Web.Services;

/// <summary>
/// The size of one message the browser may send the studio over its circuit (backlog 176).
/// <para>
/// SignalR's default is 32 KB, and a textarea bound with <c>@oninput</c> sends its WHOLE value on every keystroke,
/// JSON inside JSON, so every quote, backslash and newline in it is escaped twice. At 20,000 characters a work item's
/// description passed that limit: the server closed the connection, the page reconnected without the edit, and a person
/// typing into it was disconnected on every key (seen 2026-09-30 on six of this repository's own items, one
/// "Connection disconnected" in the window log per item). The Code page never met it because it streams file content
/// (<c>IJSStreamReference</c>); a form field cannot.
/// </para>
/// <para>
/// Raised rather than streamed: the studio is one person's, behind the window-only gate (ADR 0017), so a large message
/// costs memory for one circuit and nothing else, and every text field keeps the plain binding it already has.
/// </para>
/// </summary>
public static class CircuitLimits
{
    public const long MaximumReceiveMessageBytes = 4 * 1024 * 1024;
}
