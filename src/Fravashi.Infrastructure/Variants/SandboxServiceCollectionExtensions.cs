using Fravashi.Application.Sandbox;
using Fravashi.Application.Variants;
using Fravashi.Application.Vpn;
using Fravashi.Infrastructure.Sandbox;
using Fravashi.Infrastructure.Vpn;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Fravashi.Infrastructure.Variants;

/// <summary>
/// Registers the F4 / I1 / F5 services: the honest sandbox-posture reader (<see cref="ISandboxPosture"/>), the
/// resolved release-variant feature switches (<see cref="IFeatureFlags"/>), and the VPN-connectivity seam the
/// VPN-required gate consults (<see cref="IVpnConnectivity"/>). Call AFTER <c>AddUpdateNotifications</c> - the
/// feature flags read the variant from <see cref="Application.Updates.IProductInfo"/> - and after persistence
/// (the posture reader needs <c>DataDirectory</c>).
/// </summary>
public static class SandboxServiceCollectionExtensions
{
    public static IServiceCollection AddSandboxHardening(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // F4: the app-side reader of the launcher-written container-hardening posture (honest active/unavailable).
        services.AddSingleton<ISandboxPosture, FileSandboxPosture>();

        // I1: the resolved variant feature switches (compiled defaults + image-env overrides).
        services.AddSingleton<IFeatureFlags, EnvironmentFeatureFlags>();

        // F5 (ADR 0008 - VPN6): when the launcher started a VPN sidecar it injects Vpn:ControlServerUrl (and
        // Vpn:Enabled). In that case the real sidecar probe backs IVpnConnectivity and a background service keeps
        // it fresh. Otherwise the honest "not connected" placeholder is used (a full-variant install has no tunnel).
        var vpnConfigured =
            !string.IsNullOrWhiteSpace(configuration[SidecarVpnConnectivity.ControlUrlKey]) ||
            string.Equals(configuration["Vpn:Enabled"], "true", StringComparison.OrdinalIgnoreCase);

        if (vpnConfigured)
        {
            services.AddHttpClient();
            services.AddSingleton<SidecarVpnConnectivity>();
            services.AddSingleton<IVpnConnectivity>(sp => sp.GetRequiredService<SidecarVpnConnectivity>());
            services.AddHostedService(sp => new VpnConnectivityRefreshService(sp.GetRequiredService<SidecarVpnConnectivity>()));
        }
        else
        {
            services.AddSingleton<IVpnConnectivity, UnconfiguredVpnConnectivity>();
        }

        return services;
    }
}
