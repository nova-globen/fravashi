using Fravashi.Application.Integrity;
using Fravashi.Application.Persistence;
using Fravashi.Application.Updates;
using Fravashi.Application.Variants;
using Fravashi.Application.Vpn;
using Fravashi.Infrastructure.Onboarding.Auth;

namespace Fravashi.Web.Services;

/// <summary>
/// One redirecting full-page gate: the page it sends a person to, which paths stay reachable while it is closed (the
/// gate's own static policy class), and whether it is closed right now.
/// </summary>
public sealed record StudioGate(
    string Name,
    string Target,
    Func<string, bool> IsExempt,
    Func<IServiceProvider, CancellationToken, Task<bool>> IsClosedAsync);

/// <summary>
/// Backlog 282 (plan @100): the five redirecting full-page gates, in the order Program.cs has always run them
/// (CLAUDE.md, "Program.cs middleware gate order"), as ONE decision that two places ask. The HTTP middleware asks it for
/// every request, and <c>Components/GateGuard.razor</c> asks it for every in-app navigation inside a live circuit,
/// which never makes an HTTP request and so never met the middleware: measured on v0.9.74, a click on the activity rail
/// from <c>/integrity-failed</c> rendered <c>/wiki</c> with no document load. The path policies stay in their own static
/// classes, unchanged; what moved here from Program.cs's inline lambdas is each gate's closed condition, verbatim, so
/// neither asker holds a copy of any rule. The window-only gate (ADR 0017) is not here: it decides on the request's user
/// agent, which a circuit-side navigation does not have, and it stays Program.cs's outermost middleware.
/// <para>
/// Each gate resolves its services only when the path is not exempt from it, as the middleware always did, so a gate
/// that is not asked never touches the store it reads.
/// </para>
/// </summary>
public static class StudioGates
{
    /// <summary>The gates, most authoritative first. The first closed gate a path is not exempt from wins.</summary>
    public static IReadOnlyList<StudioGate> InOrder { get; } =
    [
        // Fail-closed integrity lock (F3 / ADR 0007 #9): a genuine mismatch between the running shape and the
        // launcher-recorded expected shape while enforcing. The strongest signal, so it wins over everything.
        // Report-only mode never sets IsFailClosed, so a host-dev / Development / unprovisioned run is never locked.
        new StudioGate(
            "integrity",
            IntegrityLock.FailedPath,
            IntegrityLock.IsExempt,
            (services, _) => Task.FromResult(
                services.GetRequiredService<IIntegrityStatusProvider>().Current.IsFailClosed)),

        // Critical-update lock (F1 / ADR 0006 / D03-2): the launcher marked the pending product update CRITICAL from
        // the signed manifest. Before the onboarding gate, so a critical lock wins even mid-onboarding. Non-critical
        // updates are never locked (they show a dismissible banner).
        new StudioGate(
            "critical-update",
            CriticalUpdateLock.UpdatePath,
            CriticalUpdateLock.IsExempt,
            (services, _) => Task.FromResult(
                services.GetRequiredService<IProductUpdateStatus>().Current.IsLocked)),

        // VPN-required startup gate (F5 / I1 / ADR 0008): in a variant that requires a VPN, nothing is served until
        // the tunnel is confirmed. Inert in the full variant. You must be on the tunnel before configuring anything.
        new StudioGate(
            "vpn",
            VpnGate.GatePath,
            VpnGate.IsExempt,
            (services, _) => Task.FromResult(
                services.GetRequiredService<IFeatureFlags>().Flags.VpnRequired
                && !services.GetRequiredService<IVpnConnectivity>().Current.IsConnected)),

        // Auth-first sign-in gate (plan Phase 3, decision 9): identity before configuration. Unconfigured installs
        // skip it entirely. Backlog 226: the only gate that consults a verification run; no path was exempted.
        new StudioGate(
            "sign-in",
            SignInGate.GatePath,
            SignInGate.IsExempt,
            async (services, cancellationToken) =>
            {
                var session = services.GetRequiredService<IdpSessionState>();
                var verificationRun = services.GetRequiredService<VerificationRun>();
                return !await SignInGate.AdmitsAsync(
                    () => verificationRun.SignInRequired(session.Required),
                    () => session.Usable,
                    ct => services.GetRequiredService<IdpAuthService>().RefreshIfNeededAsync(ct),
                    cancellationToken).ConfigureAwait(false);
            }),

        // First-run gate (ADR 0003 V5): until onboarding is complete the studio shell is not browsable. Once
        // complete, nothing is redirected (Settings can re-open the wizard).
        new StudioGate(
            "onboarding",
            OnboardingGate.GatePath,
            OnboardingGate.IsExempt,
            (services, _) => Task.FromResult(
                !services.GetRequiredService<IOnboardingState>().IsComplete)),
    ];

    /// <summary>
    /// Where a request or navigation for <paramref name="path"/> must be sent instead, or null when every gate lets it
    /// through. <paramref name="services"/> is the request's services for the middleware and the circuit's for the
    /// guard; both resolve the same registrations.
    /// <para>
    /// Backlog 283: a static asset the app really serves passes every gate, asked here once and of
    /// <see cref="StaticAssetRoutes"/>, where each policy used to end in <c>Path.HasExtension</c> and so let through any
    /// page whose route ends in a file name (<c>/code/README.md</c>). The policies hold no asset rule of their own.
    /// </para>
    /// </summary>
    public static async Task<string?> RedirectAsync(string path, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(services);
        if (services.GetRequiredService<StaticAssetRoutes>().Serves(path))
        {
            return null;
        }

        foreach (var gate in InOrder)
        {
            if (!gate.IsExempt(path) && await gate.IsClosedAsync(services, cancellationToken).ConfigureAwait(false))
            {
                return gate.Target;
            }
        }

        return null;
    }
}
