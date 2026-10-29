namespace Fravashi.Web.Services;

/// <summary>
/// The path policy for the critical-update lock (F1 / ADR 0006 / D03-2). When a critical product update is
/// pending, every page request is redirected to <c>/update-required</c> - except the lock page itself, the
/// liveness probe, the Blazor framework/SignalR endpoints, and the Error page (backlog 281: a
/// thrown lock-page request is re-executed to StudioError.PagePath, which must stay reachable or the person gets
/// redirected straight back to the page that threw), which must stay reachable so the lock renders and clears
/// itself. Extracted from the Program.cs middleware so the decision is unit-tested. A static asset is let through by
/// <see cref="StudioGates"/>, which asks <see cref="StaticAssetRoutes"/> (backlog 283), never by the shape of a path.
/// </summary>
public static class CriticalUpdateLock
{
    /// <summary>The lock page every non-exempt request is redirected to while a critical update is pending.</summary>
    public const string UpdatePath = "/update-required";

    /// <summary>True when a request path stays reachable even under a critical lock.</summary>
    public static bool IsExempt(string path) =>
        path.StartsWith(UpdatePath, StringComparison.OrdinalIgnoreCase) ||
        StudioError.IsPagePath(path) ||
        path.StartsWith("/healthz", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/_", StringComparison.Ordinal);
}
