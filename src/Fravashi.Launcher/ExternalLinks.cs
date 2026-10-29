namespace Fravashi.Launcher;

/// <summary>
/// Which links the studio window hands to the person's own browser. A page that opens a NEW window (a link with a
/// target, <c>window.open</c>) got a bare WebView window, which holds none of the person's sign-ins: the Claude sign-in
/// link made the owner sign in to claude.ai again inside Fravashi, and the page then sat in a window of its own (seen
/// 2026-10-02). Such a link now opens in the default browser.
/// <para>
/// Narrow on purpose. Only http and https leave (never <c>file:</c>, a custom protocol or <c>javascript:</c>), never the
/// studio's own address (a browser is refused there by ADR 0017's window-only gate), and only for NEW windows: the
/// studio's own sign-in moves the main window to the identity provider and back, and that stays in the window, where
/// the session it brings back belongs.
/// </para>
/// </summary>
public static class ExternalLinks
{
    /// <summary>True when a new window asked for <paramref name="requested"/> should open in the person's browser.</summary>
    public static bool OpensInBrowser(string? requested, string studioUrl)
    {
        if (!Uri.TryCreate(requested, UriKind.Absolute, out var target)
            || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        return !Uri.TryCreate(studioUrl, UriKind.Absolute, out var studio)
            || !string.Equals(target.Host, studio.Host, StringComparison.OrdinalIgnoreCase)
            || target.Port != studio.Port;
    }
}
