using Fravashi.Domain;

namespace Fravashi.Launcher;

/// <summary>
/// Backlog 218: notices when the host's DNS servers have drifted from what the studio container was last
/// started with, confirms the container's resolver is ACTUALLY broken before touching anything, and restarts
/// that SAME container (not a fresh one) once it is. A plain restart is enough because <c>fravashi stop</c> then
/// <c>fravashi</c> already proved it that night: <c>LauncherApp.EnsureContainer</c> starts the EXISTING container
/// rather than recreating it when nothing else baked into it is stale, and that start alone refreshed its
/// resolver from the host. Polled periodically by the resident fravashi.exe (<c>DesktopApplicationContext</c>, which
/// carries no logic of its own); every decision here is otherwise exercised directly against a fake
/// <see cref="IContainerRuntime"/> and an injected clock in <c>Fravashi.Launcher.Tests</c>.
/// </summary>
public sealed class StudioDnsRecovery(
    IContainerRuntime runtime, LauncherConfigStore configStore, Action<string> log, TimeSpan? debounce = null, Action<string>? onRestarted = null)
{
    /// <summary>How often the resident process should call <see cref="Poll"/>.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The fixed name <see cref="IContainerRuntime.CanResolve"/> is asked about: the real host the agent CLI
    /// calls (api.anthropic.com), the same host the incident that filed backlog 218 measured failing to
    /// resolve. Fixed and never built from configuration text, so nothing reaching this class can turn it
    /// into an argument-injection surface on the <c>exec</c> call behind <c>CanResolve</c>.
    /// </summary>
    public const string AgentApiHost = "api.anthropic.com";

    /// <summary>What the person and the agent are told when a restart actually happens (backlog 218).</summary>
    public const string RestartExplanation =
        $"{ProductName.Short} restarted its studio because this PC's network changed and the studio could no longer look up addresses.";

    private readonly HostDnsWatch _watch = new(debounce ?? HostDnsWatch.DefaultDebounce);

    // 1 while a Poll is in progress. The resident process calls Poll from a System.Threading.Timer, which fires on
    // schedule whether or not the previous callback has returned, and a restart (a resolve probe, a stop, a start) can
    // take longer than one interval; HostDnsWatch is not built for two callers at once.
    private int _polling;

    /// <summary>
    /// Only wslc snapshots the host's DNS at container START and never refreshes it while the container keeps
    /// running (measured, backlog 218). Docker and Podman run their own embedded DNS proxy that forwards each
    /// query to the host's CURRENT resolvers rather than a resolv.conf snapshot - not measured on this
    /// machine (the dev host has carried only wslc since ADR 0029), so this is deliberately narrow rather than
    /// assumed safe for them too; widen it here if that assumption is ever found wrong.
    /// </summary>
    public static bool AppliesTo(string runtimeName) => string.Equals(runtimeName, RuntimeNames.Wslc, StringComparison.Ordinal);

    /// <summary>
    /// One observation. Does nothing when there is no container to recover (none configured, or not running -
    /// a start already gets fresh DNS for free, handled by <c>LauncherApp.EnsureContainer</c>) or when no
    /// baseline has ever been recorded (adopts the current servers as the baseline instead of guessing the
    /// container is already stale).
    /// <para>
    /// A held drift does NOT restart on its own: the host's DNS servers can change again - the owner
    /// reconnecting a VPN after this item was filed changed them a SECOND time, to a set the container could
    /// still resolve through - so the container is asked directly (<see cref="IContainerRuntime.CanResolve"/>)
    /// before anything is touched. Still resolving adopts the new baseline quietly; only an actual failure
    /// restarts it.
    /// </para>
    /// <para>
    /// Never throws, and a call made while another is still running returns at once. The caller is a timer callback,
    /// where an exception ends the whole resident process (every window and the bridge with it), and the runtime's stop
    /// and start throw when the engine refuses or does not answer in time. A failure is logged and the watch starts its
    /// window again, so the next attempt comes one full debounce later rather than on every poll.
    /// </para>
    /// </summary>
    public void Poll(IReadOnlyList<string> currentDnsServers, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(currentDnsServers);

        if (Interlocked.Exchange(ref _polling, 1) == 1)
        {
            return;
        }

        try
        {
            PollOnce(currentDnsServers, now);
        }
        catch (Exception ex)
        {
            _watch.Reset();
            log($"Could not recover the studio container after a host network change: {ex.Message}. "
                + $"It is tried again shortly; `{ProductName.Command} stop` then `{ProductName.Command}` also recovers it.");
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private void PollOnce(IReadOnlyList<string> currentDnsServers, DateTimeOffset now)
    {
        var config = configStore.Load();
        if (config is null || !runtime.ContainerRunning(config.ContainerName))
        {
            return;
        }

        if (string.IsNullOrEmpty(config.DnsServersBaked))
        {
            configStore.Save(config with { DnsServersBaked = HostDnsServers.Signature(currentDnsServers) });
            _watch.Reset();
            return;
        }

        var baseline = HostDnsServers.Parse(config.DnsServersBaked);
        if (_watch.Evaluate(baseline, currentDnsServers, now) != DnsDriftDecision.Act)
        {
            return;
        }

        var newBaseline = HostDnsServers.Signature(currentDnsServers);
        if (runtime.CanResolve(config.ContainerName, AgentApiHost))
        {
            log($"Container '{config.ContainerName}' still resolves {AgentApiHost} after the host's DNS servers "
                + $"changed (was [{string.Join(", ", baseline)}], now [{string.Join(", ", currentDnsServers)}]); "
                + "adopting the new baseline without restarting.");
            configStore.Save(config with { DnsServersBaked = newBaseline });
            _watch.Reset();
            return;
        }

        log($"Container '{config.ContainerName}' can no longer resolve {AgentApiHost} after a host network change "
            + $"(was [{string.Join(", ", baseline)}], now [{string.Join(", ", currentDnsServers)}]); "
            + "restarting it so it can resolve names again. Your data is kept.");
        RestartReasonFile.Write(config.DataDir, RestartExplanation, log);
        runtime.StopContainer(config.ContainerName);
        runtime.StartContainer(config.ContainerName);
        configStore.Save(config with { DnsServersBaked = newBaseline });
        _watch.Reset();
        onRestarted?.Invoke(RestartExplanation);
    }
}
