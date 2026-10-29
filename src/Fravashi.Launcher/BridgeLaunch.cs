namespace Fravashi.Launcher;

/// <summary>
/// The host bridge's container-facing coordinates, computed by fravashi.exe before the launch: the URL
/// a container can reach the bridge on (the WSL gateway address, per the 2026-08-01 spike) and the
/// per-install token. Injected into the studio container as env; the launcher records the URL in
/// launcher.json and recreates the container when a reboot moves the WSL subnet.
/// </summary>
/// <param name="AppRunnerUrl">Backlog 87: the app runner beside the bridge, when fravashi.exe started one.</param>
/// <param name="AppRunnerToken">Its own token, never the bridge's.</param>
/// <param name="UpgradeUrl">Backlog 157: the upgrade operation beside the bridge, when fravashi.exe started one.</param>
/// <param name="UpgradeToken">Its own token, neither the bridge's nor the app runner's.</param>
public sealed record BridgeLaunch(
    string Url,
    string Token,
    string? AppRunnerUrl = null,
    string? AppRunnerToken = null,
    string? UpgradeUrl = null,
    string? UpgradeToken = null);
