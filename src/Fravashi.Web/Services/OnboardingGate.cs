namespace Fravashi.Web.Services;

/// <summary>
/// The path policy for the first-run onboarding gate (ADR 0003 V5). Until onboarding is complete, every page
/// request is redirected to <see cref="GatePath"/> - except the wizard itself, every other gate's own page (so a
/// critical lock, the VPN gate or a sign-in requirement still wins even mid-onboarding), the liveness probe, the
/// status API, the Blazor framework/SignalR endpoints, and the Error page (backlog 281,
/// <see cref="StudioError.IsPagePath"/>; without that last one a thrown onboarding-page request would redirect
/// back to <see cref="GatePath"/> instead of answering with the 500 item 281 asks for). Extracted from the
/// Program.cs middleware so the decision is unit-tested, the same as the other four gates. A static asset is let
/// through by <see cref="StudioGates"/>, which asks <see cref="StaticAssetRoutes"/> (backlog 283), never by the
/// shape of a path.
/// </summary>
public static class OnboardingGate
{
    /// <summary>The wizard every non-exempt request is redirected to until onboarding is complete.</summary>
    public const string GatePath = "/onboarding";

    /// <summary>True when a request path stays reachable even while onboarding is incomplete.</summary>
    public static bool IsExempt(string path) =>
        path.StartsWith(GatePath, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/sign-in", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/auth/callback", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/update-required", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/integrity-failed", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/secure-connection", StringComparison.OrdinalIgnoreCase) ||
        StudioError.IsPagePath(path) ||
        path.StartsWith("/healthz", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/_", StringComparison.Ordinal);
}
