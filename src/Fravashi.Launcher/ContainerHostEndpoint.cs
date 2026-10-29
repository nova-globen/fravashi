namespace Fravashi.Launcher;

/// <summary>
/// Where fravashi.exe's listeners (the bridge, the app runner, the upgrade operation) bind, and the host name a container
/// is given to reach them. It depends on the engine, because each engine's containers see the host differently.
/// </summary>
/// <param name="BindAddresses">The addresses to listen on; loopback is always one of them.</param>
/// <param name="ContainerFacingHost">The host name or address the container's URLs use.</param>
public sealed record ContainerHostEndpoint(IReadOnlyList<string> BindAddresses, string ContainerFacingHost)
{
    /// <summary>
    /// wslc's own name for the host, which it forwards to the host's LOOPBACK: a service bound to 127.0.0.1 alone
    /// answered through it from the studio image as uid 1000 (measured on the dev host, wslc 3.0.1, 2026-10-04). It is
    /// in the container's /etc/hosts; glibc resolves it, musl does not (microsoft/WSL#41769), and the studio image
    /// is glibc.
    /// </summary>
    public const string WslcHostAlias = "host.wslc.internal";

    /// <summary>The loopback address every listener binds.</summary>
    public const string Loopback = "127.0.0.1";

    /// <summary>
    /// The endpoint for <paramref name="runtimeName"/>. On wslc the listeners bind loopback ONLY and containers use
    /// <see cref="WslcHostAlias"/> (decided 2026-10-04): nothing else on the machine, no WSL distro and no other
    /// VM, can reach them, and the address never moves, so a reboot cannot strand a container on an old one. Docker and
    /// Podman keep loopback plus the WSL gateway address (spike 2026-08-01: <c>host.containers.internal</c> resolves
    /// but does not connect there). Null when a container would have no way in (no WSL address on Docker or Podman).
    /// </summary>
    public static ContainerHostEndpoint? For(string runtimeName, Func<string?> wslAddress)
    {
        ArgumentNullException.ThrowIfNull(wslAddress);
        if (string.Equals(runtimeName, RuntimeNames.Wslc, StringComparison.OrdinalIgnoreCase))
        {
            return new ContainerHostEndpoint([Loopback], WslcHostAlias);
        }

        return wslAddress() is { Length: > 0 } address
            ? new ContainerHostEndpoint([Loopback, address], address)
            : null;
    }

    /// <summary>The URLs to listen on for <paramref name="port"/>.</summary>
    public IReadOnlyList<string> ListenUrls(int port) => [.. BindAddresses.Select(a => $"http://{a}:{port}")];

    /// <summary>The URL a container uses for <paramref name="port"/>.</summary>
    public string ContainerUrl(int port) => $"http://{ContainerFacingHost}:{port}";
}
