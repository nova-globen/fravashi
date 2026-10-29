using Fravashi.Domain;

namespace Fravashi.Web.Services;

/// <summary>
/// The studio UI is window-only (ADR 0017): every browsable request must come from the studio window
/// shell, which brands its WebView2 user agent with <see cref="UserAgentMarker"/> and, when the
/// launcher provided one, the per-install access token (<c>tk/&lt;token&gt;</c>, env
/// <c>FRAVASHI_WINDOW_TOKEN</c>). A plain browser gets a static refusal page; there is deliberately
/// no browser mode. Machine endpoints stay open: liveness, the status API the launcher/container
/// polls, and the OAuth callback (whose redirect must land regardless of what initiated it).
/// This is a product boundary for the single-user studio, not a cryptographic one; the real
/// perimeter remains localhost binding and the container.
/// </summary>
public static class WindowOnlyGate
{
    public const string UserAgentMarker = "FravashiWindow";

    public const string TokenConfigKey = "FRAVASHI_WINDOW_TOKEN";

    public static bool IsExempt(string path) =>
        path.StartsWith("/healthz", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/auth/callback", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the request's user agent proves the studio window shell (and its token, when required).</summary>
    public static bool IsAllowed(string? userAgent, string? requiredToken)
    {
        if (string.IsNullOrEmpty(userAgent) || !userAgent.Contains(UserAgentMarker, StringComparison.Ordinal))
        {
            return false;
        }

        return string.IsNullOrEmpty(requiredToken)
            || userAgent.Contains($"tk/{requiredToken}", StringComparison.Ordinal);
    }

    /// <summary>The static refusal page (no Blazor: framework assets are themselves behind the gate).</summary>
    public const string BlockedHtml = $$"""
        <!DOCTYPE html>
        <html lang="en">
        <head><meta charset="utf-8"><title>{{ProductName.Short}}</title>
        <style>
        body { font-family: system-ui, sans-serif; background: #0b0c0f; color: #d8dbe2;
               display: grid; place-items: center; min-height: 100vh; margin: 0; }
        main { max-width: 30rem; padding: 2rem; text-align: center; }
        h1 { font-size: 1.2rem; } p { color: #9aa0ab; font-size: .95rem; line-height: 1.5; }
        code { background: #1a1c22; padding: .1rem .4rem; border-radius: 4px; }
        </style></head>
        <body><main>
        <h1>{{ProductName.Short}} opens in its own window</h1>
        <p>This studio is not served to browsers. Start it with the <code>{{ProductName.Command}}</code> launcher
        (or the tray), which opens the {{ProductName.Short}} window.</p>
        </main></body></html>
        """;
}
