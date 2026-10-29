using Microsoft.Extensions.Hosting;

namespace Fravashi.Infrastructure.Vpn;

/// <summary>
/// Keeps <see cref="SidecarVpnConnectivity"/> fresh (F5 / ADR 0008 - VPN6): a short initial delay so the sidecar
/// tunnel has a moment to come up, then a periodic re-probe. The VPN-required gate and the integrity surface read
/// the cached state synchronously; this service is the only writer. Registered only in VPN mode.
/// </summary>
public sealed class VpnConnectivityRefreshService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(15);

    private readonly SidecarVpnConnectivity _vpn;
    private readonly TimeProvider _clock;

    public VpnConnectivityRefreshService(SidecarVpnConnectivity vpn, TimeProvider? clock = null)
    {
        _vpn = vpn;
        _clock = clock ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, _clock, stoppingToken);
            await _vpn.RefreshAsync(stoppingToken);

            using var timer = new PeriodicTimer(Period, _clock);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await _vpn.RefreshAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
