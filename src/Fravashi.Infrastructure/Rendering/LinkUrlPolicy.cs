using System.Text;

namespace Fravashi.Infrastructure.Rendering;

/// <summary>
/// The ONE place that decides which URL a rendered link or image may carry (backlog 286).
/// <para>
/// Every markdown surface in the studio is inserted into the window as a MarkupString, so a link's target
/// is live in the studio's own origin: <c>[x](javascript:...)</c> ran script there on a single click
/// (measured 2026-10-10, recorded on item #286). Raw HTML was already disabled; link targets
/// were not filtered at all.
/// </para>
/// <para>
/// The rule is an allowlist: http, https and mailto, a fragment, and a URL with no scheme (relative to the
/// studio). Anything else (javascript:, vbscript:, data:, file:, a custom scheme) is refused. The scheme is
/// read the way a browser's URL parser reads it, because the browser is what acts on it: leading and
/// trailing C0 controls and spaces are stripped, ASCII tab and newline are removed ANYWHERE, and the scheme
/// is an ASCII letter followed by letters, digits, <c>+</c>, <c>-</c> and <c>.</c> up to a colon, case
/// ignored. Reading it any other way is how <c>java&amp;#9;script:</c> gets through: Markdig decodes the
/// entity into a tab, and the browser drops the tab.
/// </para>
/// </summary>
public static class LinkUrlPolicy
{
    private static readonly HashSet<string> AllowedSchemes =
        new(StringComparer.Ordinal) { "http", "https", "mailto" };

    /// <summary>Whether a rendered link or image may carry this URL as its href or src.</summary>
    public static bool IsAllowed(string? url) =>
        SchemeOf(url) is not { } scheme || AllowedSchemes.Contains(scheme);

    /// <summary>
    /// The URL's scheme in lower case as a browser would read it, or null when it has none (a relative URL,
    /// a fragment, an empty string).
    /// </summary>
    public static string? SchemeOf(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        var cleaned = new StringBuilder(url.Length);
        foreach (var c in url)
        {
            if (c is not ('\t' or '\n' or '\r'))
            {
                cleaned.Append(c);
            }
        }

        var start = 0;
        var end = cleaned.Length;
        while (start < end && cleaned[start] <= ' ')
        {
            start++;
        }

        while (end > start && cleaned[end - 1] <= ' ')
        {
            end--;
        }

        if (start == end || !char.IsAsciiLetter(cleaned[start]))
        {
            return null;
        }

        var at = start + 1;
        while (at < end && (char.IsAsciiLetterOrDigit(cleaned[at]) || cleaned[at] is '+' or '-' or '.'))
        {
            at++;
        }

        return at < end && cleaned[at] == ':'
            ? cleaned.ToString(start, at - start).ToLowerInvariant()
            : null;
    }
}
