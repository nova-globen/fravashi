using Fravashi.Application.Network;
using Fravashi.Application.Sessions;

namespace Fravashi.Infrastructure.Browser;

/// <summary>
/// Tells a navigation that failed on the NETWORK from everything else the agent browser can meet (backlog 233, plan
/// @59), in slice A's kinds (<see cref="NetworkFailure"/>). Chromium names a network failure with a <c>net::ERR_*</c>
/// code, both in Playwright's exception text (<c>net::ERR_NAME_NOT_RESOLVED at http://...</c>) and in a failed
/// request's own text; the texts it reads were measured in the studio container on 2026-10-07 and are kept as fixtures
/// (<c>Browser/Fixtures/playwright-network-errors.json</c> in Infrastructure.Tests).
/// <para>
/// The same never-left rule as slice A: a name error and a refusal prove nothing was sent; a reset, an empty response
/// or Chromium's own read timeout come with no phase, so they may have left. A code that is not about the network
/// (<c>ERR_ABORTED</c>, <c>ERR_BLOCKED_BY_CLIENT</c> from the studio's own policy, a certificate error) is not one.
/// </para>
/// </summary>
public static class BrowserNetworkErrors
{
    private static readonly (string Code, NetworkFailureKind Kind)[] Codes =
    [
        ("ERR_NAME_NOT_RESOLVED", NetworkFailureKind.NameNotResolved),
        ("ERR_NAME_RESOLUTION_FAILED", NetworkFailureKind.NameNotResolved),
        ("ERR_CONNECTION_REFUSED", NetworkFailureKind.Refused),
        ("ERR_INTERNET_DISCONNECTED", NetworkFailureKind.Unreachable),
        ("ERR_ADDRESS_UNREACHABLE", NetworkFailureKind.Unreachable),
        ("ERR_NETWORK_ACCESS_DENIED", NetworkFailureKind.Unreachable),
        ("ERR_CONNECTION_TIMED_OUT", NetworkFailureKind.TimedOutConnecting),
        ("ERR_CONNECTION_FAILED", NetworkFailureKind.ResetBeforeSend),
        ("ERR_PROXY_CONNECTION_FAILED", NetworkFailureKind.ResetBeforeSend),
        ("ERR_CONNECTION_RESET", NetworkFailureKind.LeftWithNoAnswer),
        ("ERR_CONNECTION_CLOSED", NetworkFailureKind.LeftWithNoAnswer),
        ("ERR_CONNECTION_ABORTED", NetworkFailureKind.LeftWithNoAnswer),
        ("ERR_EMPTY_RESPONSE", NetworkFailureKind.LeftWithNoAnswer),
        ("ERR_TIMED_OUT", NetworkFailureKind.LeftWithNoAnswer),
        ("ERR_NETWORK_CHANGED", NetworkFailureKind.LeftWithNoAnswer),
    ];

    /// <summary>
    /// The network failure a Chromium text names (an exception message or a failed request's text), or
    /// <see cref="NetworkFailure.None"/> when it names none.
    /// </summary>
    public static NetworkFailure Classify(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return NetworkFailure.None;
        }

        var at = text.IndexOf("net::", StringComparison.Ordinal);
        if (at < 0)
        {
            return NetworkFailure.None;
        }

        var start = at + "net::".Length;
        var end = start;
        while (end < text.Length && (char.IsAsciiLetterUpper(text[end]) || char.IsAsciiDigit(text[end]) || text[end] == '_'))
        {
            end++;
        }

        var code = text[start..end];
        foreach (var (known, kind) in Codes)
        {
            if (string.Equals(code, known, StringComparison.Ordinal))
            {
                return new(kind, code);
            }
        }

        return NetworkFailure.None;
    }

    /// <summary>
    /// What a failed <c>browser_open</c> was: a network failure the exception names, or Playwright's own navigation
    /// timeout (a <see cref="TimeoutException"/>, measured as <c>Timeout 30000ms exceeded.</c>), which is the network or
    /// a slow site and is said as exactly that. Null for anything else, which keeps the browser's own words.
    /// </summary>
    public static BrowserNetworkFailure? OfOpen(Exception exception, string url, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var host = HostOf(url);
        if (exception is TimeoutException)
        {
            return new(new NetworkFailure(NetworkFailureKind.LeftWithNoAnswer, "navigation timeout"), host, TimedOutAfter: timeout);
        }

        var failure = Classify(exception.Message);
        return failure.IsNetwork ? new(failure, host) : null;
    }

    /// <summary>
    /// What a click, fill or key press left behind when the navigation it started failed. Neither the act nor the load
    /// wait throws then (measured: a form posting to a name that does not resolve clicks and "loads" Chromium's error
    /// page), so the evidence is the main frame's failed navigation request together with the error page now showing.
    /// Both are needed: Chromium reports an ABANDONED navigation's failure late, during whatever comes next (measured), and
    /// that alone must not make a click look like it failed.
    /// </summary>
    /// <param name="act">The act's name as the sentence says it.</param>
    /// <param name="failedUrl">The main frame's navigation that failed since the act began, or null.</param>
    /// <param name="failedText">That request's failure text (<c>net::ERR_NAME_NOT_RESOLVED</c>).</param>
    /// <param name="urlAfter">The page's address after the act.</param>
    public static BrowserNetworkFailure? OfAct(string act, string? failedUrl, string? failedText, string? urlAfter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(act);
        if (failedUrl is null || urlAfter is null || !urlAfter.StartsWith("chrome-error:", StringComparison.Ordinal))
        {
            return null;
        }

        var failure = Classify(failedText);
        return failure.IsNetwork ? new(failure, HostOf(failedUrl), act) : null;
    }

    /// <summary>The host of an address, or the address itself when it has none.</summary>
    public static string HostOf(string url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : url?.Trim() ?? string.Empty;
}
