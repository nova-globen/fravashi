namespace Fravashi.Web.Services;

/// <summary>
/// The auth-first sign-in gate's path policy (plan Phase 3, decision 9). The gate enforces when
/// the owner's IdP is configured (<c>IdpSessionState.Required</c>) and the session is not valid;
/// every non-exempt request then redirects to the sign-in page. Exemptions keep reachable:
/// liveness, the status APIs, the OIDC callback (or the sign-in could never complete), the Blazor
/// framework/SignalR endpoints, every upstream lock page, the Error page (backlog
/// 281: a thrown sign-in-page request is re-executed to StudioError.PagePath, which must stay
/// reachable or the person gets redirected straight back to the page that threw), and the page itself.
/// A static asset is let through by <see cref="StudioGates"/>, which asks <see cref="StaticAssetRoutes"/>
/// (backlog 283), never by the shape of a path.
/// </summary>
public static class SignInGate
{
    /// <summary>The page every non-exempt request is redirected to while signed out.</summary>
    public const string GatePath = "/sign-in";

    /// <summary>True when a request path stays reachable even while the gate is closed.</summary>
    public static bool IsExempt(string path) =>
        path.StartsWith(GatePath, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/auth/callback", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/update-required", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/integrity-failed", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/secure-connection", StringComparison.OrdinalIgnoreCase) ||
        StudioError.IsPagePath(path) ||
        path.StartsWith("/healthz", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/_", StringComparison.Ordinal);

    /// <summary>
    /// Whether a gated request may be served. A session that has EXPIRED but can be refreshed is refreshed here,
    /// once, before anybody is sent to the sign-in page. Measured in the installed v0.9.31 on 2026-09-23: the refresh
    /// loop runs every five minutes and a tick landed seven seconds before the session entered its refresh window,
    /// so for three minutes every page redirected to <c>/sign-in</c> although the refresh token was good. A person
    /// should never be asked to sign in when the studio could have renewed the session itself.
    /// <para>
    /// Backlog 234: <paramref name="signedIn"/> is <c>IdpSessionState.Usable</c>, which is also true for one hour past
    /// the session's expiry while its refresh could not reach the identity provider, so a person inside that grace is
    /// admitted without the identity provider being asked; and <paramref name="refresh"/> does not call it while the
    /// grace's backoff holds, so no page load waits on a dead network. A refusal, or the hour running out, closes the
    /// gate exactly as before.
    /// </para>
    /// </summary>
    public static async Task<bool> AdmitsAsync(
        Func<bool> required, Func<bool> signedIn, Func<CancellationToken, Task<bool>> refresh, CancellationToken cancellationToken)
    {
        if (!required() || signedIn())
        {
            return true;
        }

        return await refresh(cancellationToken).ConfigureAwait(false) && signedIn();
    }
}
