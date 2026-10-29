using Fravashi.Application.Network;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.Sessions;

/// <summary>The page a session's browser holds now, as the agent and the person see it.</summary>
/// <param name="Status">The HTTP status of the last navigation's main response, when there was one.</param>
/// <param name="ConsoleErrors">The page's console errors since it was opened, newest last, capped.</param>
/// <param name="FailedRequests">Requests that failed or were refused by the studio's policy, newest last, capped.</param>
public sealed record BrowserPageState(
    string Url,
    string Title,
    int? Status,
    IReadOnlyList<string> ConsoleErrors,
    IReadOnlyList<string> FailedRequests);

/// <summary>
/// What a browser act did: the page after it, or the sentence that says why it did not happen. <paramref name="Network"/>
/// is set when the reason was the network rather than the page (backlog 233), and the sentence is then its own.
/// </summary>
public sealed record BrowserOutcome(BrowserPageState? Page, string? Refusal = null, BrowserNetworkFailure? Network = null)
{
    public bool Ok => Refusal is null;

    public static BrowserOutcome Refused(string why) => new(null, why);

    /// <summary>A navigation that failed on the network, with the page as it is after it.</summary>
    public static BrowserOutcome NetworkFailed(BrowserPageState? page, BrowserNetworkFailure network)
    {
        ArgumentNullException.ThrowIfNull(network);
        return new(page, network.Sentence, network);
    }
}

/// <summary>
/// A navigation the session's browser could not complete because of the network (backlog 233, plan @59), told apart
/// from a page that answered with an error. Opening a page is a read, so repeating it is safe; a click, fill or key
/// press (<paramref name="Act"/>) may have submitted a form, so after one of those the outcome is unknown and the
/// agent is told to look before repeating it, whatever the kind says, because the act also ran the page's own scripts.
/// </summary>
/// <param name="Failure">Slice A's kind and its plain words.</param>
/// <param name="Host">The host that could not be reached.</param>
/// <param name="Act">The act that started the navigation ("click", "fill", "key press"), or null for opening a page.</param>
/// <param name="TimedOutAfter">Set when nothing failed outright and the page simply did not load in this time.</param>
public sealed record BrowserNetworkFailure(NetworkFailure Failure, string Host, string? Act = null, TimeSpan? TimedOutAfter = null)
{
    /// <summary>True when what the act sent may have arrived, so it must not simply be repeated.</summary>
    public bool OutcomeUnknown => Act is not null;

    /// <summary>True when doing it again cannot do anything twice: opening a page.</summary>
    public bool RetrySafe => Act is null;

    /// <summary>The plain sentence the agent and the pane read.</summary>
    public string Sentence => Act is not null
        ? $"The {Act} was made, but the page it led to could not be loaded: could not reach {Host} ({Reason}). This is the "
          + $"network, not the page. Whether the site received what the {Act} sent is unknown, so do not repeat it yet: look "
          + "first (browser_read, or browser_open on the page) to see what it shows now."
        : TimedOutAfter is { } after
            ? $"{Host} did not finish loading in {after.TotalSeconds:0} seconds. That is the network or a slow site, not an "
              + "error the page answered with. Opening a page is only a read, so it is safe to try again in a minute; the "
              + "session's browser is still open."
            : $"Could not reach {Host} ({Reason}). This is the network, not the page. Opening a page is only a read, so it "
              + "is safe to try again in a minute; the session's browser is still open.";

    /// <summary>The words in brackets: slice A's, or "no answer in N seconds" for a timeout.</summary>
    public string Reason => TimedOutAfter is { } after ? $"no answer in {after.TotalSeconds:0} seconds" : Failure.Words;
}

/// <summary>
/// Which element an act is aimed at, the way a person would name it: by its accessible role and name (a button called
/// "Save"), by its visible text, by the label of a form field, or, as a last resort, by a CSS selector. Exactly one of
/// the four kinds is used; the first one given wins in that order.
/// </summary>
public sealed record BrowserTarget(string? Role = null, string? Name = null, string? Text = null, string? Label = null, string? Selector = null)
{
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Role) && string.IsNullOrWhiteSpace(Text)
        && string.IsNullOrWhiteSpace(Label) && string.IsNullOrWhiteSpace(Selector);

    /// <summary>How the target reads in an answer.</summary>
    public string Describe() =>
        !string.IsNullOrWhiteSpace(Role) ? $"the {Role}" + (string.IsNullOrWhiteSpace(Name) ? string.Empty : $" named \"{Name}\"")
        : !string.IsNullOrWhiteSpace(Text) ? $"the element with the text \"{Text}\""
        : !string.IsNullOrWhiteSpace(Label) ? $"the field labelled \"{Label}\""
        : $"the element matching {Selector}";
}

/// <summary>How a page is read back: its visible text, or its accessibility outline (roles, names and states).</summary>
public enum BrowserReadMode
{
    Text,
    Outline,
}

/// <summary>
/// A headless browser per agent session (backlog 159): the agent opens pages, reads them, acts on them and takes
/// screenshots through studio tools, and a person watches the same page in the session pane and can click and type in
/// it, which is how a sign-in a site needs is done by the person and never by the agent. One browser context per
/// session, in memory, closed when the session's process ends or after a long idle; nothing is written to the copy.
/// </summary>
public interface IAgentBrowser
{
    /// <summary>Whether a browser can run in this studio at all; when not, <see cref="UnavailableReason"/> says why.</summary>
    bool IsAvailable { get; }

    /// <summary>Why no browser can run here, or null when one can.</summary>
    string? UnavailableReason { get; }

    /// <summary>Raised with the session whenever its page changes, for the pane.</summary>
    event Action<AgentSessionId>? Changed;

    /// <summary>The session's page now, or null when it has none open.</summary>
    BrowserPageState? Current(AgentSessionId session);

    /// <summary>Opens a URL in the session's page, creating the page when there is none. Only http and https.</summary>
    Task<BrowserOutcome> OpenAsync(AgentSessionId session, string url, CancellationToken cancellationToken = default);

    /// <summary>The page's text or accessibility outline, cut at <paramref name="max"/> characters.</summary>
    Task<(BrowserOutcome Outcome, string? Content, bool Truncated)> ReadAsync(
        AgentSessionId session, BrowserReadMode mode, int max, CancellationToken cancellationToken = default);

    /// <summary>Clicks the one element the target names; refuses when none or several match.</summary>
    Task<BrowserOutcome> ClickAsync(AgentSessionId session, BrowserTarget target, CancellationToken cancellationToken = default);

    /// <summary>Replaces the value of the one field the target names, and presses Enter after when asked.</summary>
    Task<BrowserOutcome> FillAsync(
        AgentSessionId session, BrowserTarget target, string text, bool submit, CancellationToken cancellationToken = default);

    /// <summary>Presses a key (Enter, Tab, Escape, ArrowDown, or a combination such as Control+A) on the focused element.</summary>
    Task<BrowserOutcome> PressAsync(AgentSessionId session, string key, CancellationToken cancellationToken = default);

    /// <summary>A JPEG of the viewport, or of the whole page cut at a height the model can read.</summary>
    Task<(BrowserOutcome Outcome, byte[]? Jpeg)> ScreenshotAsync(
        AgentSessionId session, bool fullPage, CancellationToken cancellationToken = default);

    /// <summary>Closes the session's page and forgets everything it held, cookies included.</summary>
    Task CloseAsync(AgentSessionId session);

    /// <summary>For the pane: the current viewport as a small JPEG, or null when there is no page.</summary>
    Task<byte[]?> FrameAsync(AgentSessionId session, CancellationToken cancellationToken = default);

    /// <summary>For the pane: a person's click at viewport coordinates.</summary>
    Task<BrowserOutcome> ClickAtAsync(AgentSessionId session, double x, double y, CancellationToken cancellationToken = default);

    /// <summary>For the pane: what a person types, into whatever has focus. Never recorded anywhere.</summary>
    Task<BrowserOutcome> TypeTextAsync(AgentSessionId session, string text, CancellationToken cancellationToken = default);
}
