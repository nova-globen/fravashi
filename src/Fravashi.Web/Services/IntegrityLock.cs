namespace Fravashi.Web.Services;

/// <summary>
/// The path policy for the fail-closed integrity lock (F3 / ADR 0007 #9). When the integrity self-check has failed
/// under enforcement, every page request is redirected to <c>/integrity-failed</c> - except the lock page itself,
/// the liveness probe, the Blazor framework/SignalR endpoints, and the Error page (backlog 281: a
/// thrown lock-page request is re-executed to StudioError.PagePath, which must stay reachable or the person gets
/// redirected straight back to the page that threw), so the loud page renders (and <c>/healthz</c> can still
/// report) in the brief window before the app terminates. This lock sits ABOVE the critical-update lock and the
/// VPN gate: an integrity failure is the strongest signal and wins over everything. Extracted from the Program.cs
/// middleware so the decision is unit-tested. A static asset is let through by <see cref="StudioGates"/>, which asks
/// <see cref="StaticAssetRoutes"/> (backlog 283), never by the shape of a path.
/// </summary>
public static class IntegrityLock
{
    /// <summary>The lock page every non-exempt request is redirected to while integrity has failed closed.</summary>
    public const string FailedPath = "/integrity-failed";

    /// <summary>True when a request path stays reachable even under a fail-closed integrity lock.</summary>
    public static bool IsExempt(string path) =>
        path.StartsWith(FailedPath, StringComparison.OrdinalIgnoreCase) ||
        StudioError.IsPagePath(path) ||
        path.StartsWith("/healthz", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/_", StringComparison.Ordinal);
}
