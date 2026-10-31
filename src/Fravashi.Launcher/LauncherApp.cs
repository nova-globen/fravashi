using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Fravashi.Launcher.Backup;
using Fravashi.Launcher.Integrity;
using Fravashi.Launcher.Releases;
using Fravashi.Launcher.Vpn;
using Fravashi.Domain;

namespace Fravashi.Launcher;

/// <summary>
/// Orchestrates the launcher's actions. The default run ensures the container runtime is present, loads the
/// image if needed, creates-or-starts the container with the data volume mounted on the reserved port, opens
/// the browser, and - on Windows - brings up the notification-area tray. The container is left running on exit
/// (ADR 0003 V3). The <c>fravashi</c> verbs (stop, status, website, onboarding, logs, exit) act directly on the
/// runtime / launcher.json, so they work whether or not the tray is running. The runtime is
/// runtime-agnostic (ADR 0009): whichever engine the detector selected - wslc, docker, or podman - is driven
/// through <see cref="IContainerRuntime"/>. All side effects (runtime, browser, port probe, tray, logging) are
/// injected, so the flow is unit-testable with fakes.
/// </summary>
public sealed class LauncherApp
{
    private readonly IContainerRuntime _runtime;
    private readonly LauncherConfigStore _configStore;
    private readonly string _launcherDirectory;
    private readonly Action<string> _log;
    private readonly Action<string> _openBrowser;
    private readonly Func<int, bool> _isPortFree;
    private readonly ITrayControl _tray;
    private readonly string? _containerNameOverride;
    private readonly Func<string, IReleaseManifestSource> _manifestSourceFactory;
    private readonly IReleaseAssetDownloader _assetDownloader;
    private readonly Func<string, bool> _confirm;
    private readonly Func<string> _defaultDataDir;
    private readonly Func<VpnConfig, bool> _vpnConnectedProbe;
    private readonly TimeSpan _vpnPollInterval;
    private readonly int _vpnReadyAttempts;
    private readonly Action<LaunchPhase> _phase;
    private readonly BridgeLaunch? _bridge;
    private readonly IResidentUpgrade? _residentUpgrade;
    private readonly Func<string, StudioHealth?> _studioHealth;
    private readonly TimeProvider _clock;

    /// <summary>
    /// The one place a studio container is created and what it baked is recorded (backlog 160). Held here and
    /// handed to the updater, so <c>fravashi update</c> creates the container this launcher's start would have.
    /// </summary>
    private readonly StudioContainer _container;

    public LauncherApp(
        IContainerRuntime runtime,
        LauncherConfigStore configStore,
        string launcherDirectory,
        Action<string>? log = null,
        Action<string>? openBrowser = null,
        Func<int, bool>? isPortFree = null,
        ITrayControl? tray = null,
        string? containerNameOverride = null,
        Func<string, IReleaseManifestSource>? manifestSourceFactory = null,
        IReleaseAssetDownloader? assetDownloader = null,
        Func<string, bool>? confirm = null,
        Func<string>? defaultDataDir = null,
        Func<VpnConfig, bool>? vpnConnectedProbe = null,
        TimeSpan? vpnPollInterval = null,
        int vpnReadyAttempts = 20,
        Action<LaunchPhase>? phase = null,
        BridgeLaunch? bridge = null,
        Func<string, StudioHealth?>? studioHealth = null,
        IResidentUpgrade? residentUpgrade = null,
        TimeProvider? clock = null)
    {
        // The clock an update check is recorded and worded on (backlog 230).
        _clock = clock ?? TimeProvider.System;
        // Null unless the console verb hands one in: an update is handed to a running fravashi.exe only from a console,
        // and never from a test, which would otherwise reach the real fravashi.exe on the machine it runs on.
        _residentUpgrade = residentUpgrade;
        _runtime = runtime;
        _configStore = configStore;
        _launcherDirectory = launcherDirectory;
        _log = log ?? Console.WriteLine;
        _openBrowser = openBrowser ?? (url => BrowserLauncher.Open(url, _log));
        _isPortFree = isPortFree ?? PortSelector.IsTcpPortFree;
        _tray = tray ?? TrayControlFactory.Create();
        _containerNameOverride = containerNameOverride;
        _manifestSourceFactory = manifestSourceFactory ?? (url => new HttpReleaseManifestSource(url, log: _log));
        _assetDownloader = assetDownloader ?? new HttpReleaseAssetDownloader(log: _log);
        _confirm = confirm ?? DefaultConfirm;
        // Overridable so tests never write records/posture into the real per-user data directory.
        _defaultDataDir = defaultDataDir ?? DefaultDataDir;
        // Default VPN readiness probe: the sidecar container is running. The deeper tunnel confirmation is the
        // studio's own gate (IVpnConnectivity → /secure-connection), so the launcher never blocks indefinitely.
        _vpnConnectedProbe = vpnConnectedProbe ?? (vpn => _runtime.ContainerRunning(vpn.ContainerName));
        _vpnPollInterval = vpnPollInterval ?? TimeSpan.FromSeconds(1.5);
        _vpnReadyAttempts = Math.Max(1, vpnReadyAttempts);
        _phase = phase ?? (_ => { });
        _bridge = bridge;
        _container = new StudioContainer(runtime, configStore, bridge);
        // The restore verb's proof that the product came back up ON the restored database. Injectable so
        // the restore flow is testable without a studio; the default waits up to two minutes, because a
        // restored database can have migrations to run before the first health answer.
        _studioHealth = studioHealth ??
            (url => StudioHealthProbe.Poll(url, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(2)));
    }

    /// <summary>The default launch: ensure the container is running, open the browser, and (Windows) the tray.</summary>
    /// <remarks>Kept as the public entry point the tests drive; the <c>fravashi</c> default verb calls it.</remarks>
    public int Run()
    {
        var exit = EnsureRunning(out var config);
        if (exit != ExitCodes.Success)
        {
            return exit;
        }

        // Product-update check (ADR 0006 / T5): fold in the installed version, evaluate the manifest, and write
        // update-state.json so the app can lock (critical) or banner (available). The container is already up
        // here; a critical update is enforced by the app lock + `fravashi update`, so a locked app is unmistakable
        // and recoverable only by updating. Never blocks the launch on a missing/unreachable channel.
        _phase(LaunchPhase.CheckingUpdates);
        var effective = FoldInstalledRelease(config!);
        AnnounceUpdate(EvaluateUpdate(effective), effective);

        // The studio is window-only (ADR 0017): the URL is an internal detail between the launcher and
        // the studio window shell, never shown to the user (a browser would only see the refusal page).
        var url = LauncherUrls.Base(effective.Port);
        _log($"{ProductName.Short} is running; the {ProductName.Short} window is opening (data: {effective.DataDir}). "
            + "The container keeps running after the window closes.");
        _openBrowser(url);
        return ExitCodes.Success;
    }

    /// <summary>
    /// Ensures the container is running (Docker present, image loaded, container created-or-started) and opens
    /// the onboarding / settings re-entry URL. Used by <c>fravashi onboarding</c> and the tray "Onboarding" item.
    /// </summary>
    public int Onboarding()
    {
        var exit = EnsureRunning(out var config);
        if (exit != ExitCodes.Success)
        {
            return exit;
        }

        var url = LauncherUrls.Onboarding(config!.Port);
        _log($"Opening onboarding / settings in the {ProductName.Short} window ...");
        _openBrowser(url);
        return ExitCodes.Success;
    }

    /// <summary>
    /// Stops the container. The port stays reserved in launcher.json, so a later <c>fravashi</c> reuses it. This is
    /// distinct from <c>fravashi exit</c>, which only closes the tray and leaves the container running.
    /// </summary>
    public int Stop()
    {
        if (_runtime.Probe() is var state && state != RuntimeAvailability.Available)
        {
            _log(RuntimeUnavailableMessage(state));
            return ExitCodes.RuntimeUnavailable;
        }

        var config = LoadConfig();
        if (config is null)
        {
            _log($"{ProductName.Short} has not been started yet (no launcher.json), so there is nothing to stop.");
            return ExitCodes.Success;
        }

        if (!_runtime.ContainerExists(config.ContainerName) || !_runtime.ContainerRunning(config.ContainerName))
        {
            _log($"{ProductName.Short} is already stopped. Start it again with `{ProductName.Command}`.");
            return ExitCodes.Success;
        }

        _runtime.StopAndConfirm(config.ContainerName, _log);
        _log($"{ProductName.Short} is stopped (container '{config.ContainerName}'). Your data and reserved port {config.Port} are kept. Start it again with `{ProductName.Command}`.");
        return ExitCodes.Success;
    }

    /// <summary>
    /// The product's current state as a value rather than as printed text (backlog 15). <see cref="Status"/>
    /// answers the same question for a human reading a console; the tray menu and the studio window's
    /// recovery page need it as data, so they stop assuming the container is always up.
    /// <para>
    /// A missing launcher.json is definitive whatever the runtime says, so it is asked first. A runtime
    /// that does not answer is its own state and never collapsed into "stopped": we genuinely do not know.
    /// </para>
    /// </summary>
    public ProductRunState ProductState()
    {
        var config = LoadConfig();
        if (config is null)
        {
            return ProductRunState.NeverStarted;
        }

        if (!_runtime.IsAvailable())
        {
            return ProductRunState.RuntimeUnavailable;
        }

        if (!_runtime.ContainerExists(config.ContainerName))
        {
            return ProductRunState.NotCreated;
        }

        return _runtime.ContainerRunning(config.ContainerName)
            ? ProductRunState.Running
            : ProductRunState.Stopped;
    }

    /// <summary>Prints container state, port, URL, data dir, and image version.</summary>
    public int Status()
    {
        var loaded = LoadConfig();
        if (loaded is null)
        {
            _log($"{ProductName.Short} has not been started yet. Run `{ProductName.Command}` to start it.");
            return ExitCodes.Success;
        }

        var config = FoldInstalledRelease(loaded);
        string state;
        string image;
        var running = false;
        if (!_runtime.IsAvailable())
        {
            state = $"unknown ({_runtime.Name} is not available)";
            image = config.Image;
        }
        else if (!_runtime.ContainerExists(config.ContainerName))
        {
            state = "not created";
            image = _runtime.ImageVersion(config.Image) is { } v ? $"{config.Image} - {v}" : $"{config.Image} (not loaded)";
        }
        else
        {
            running = _runtime.ContainerRunning(config.ContainerName);
            state = running ? "running" : "stopped";
            image = _runtime.ImageVersion(config.Image) is { } v ? $"{config.Image} - {v}" : config.Image;
        }

        _log($"{ProductName.Full} status:");
        _log($"  Runtime   : {_runtime.Name}");
        _log($"  Container : {config.ContainerName} ({state})");
        _log($"  Port      : {config.Port}");
        _log($"  Data dir  : {config.DataDir}");
        if (DataMoveFile.Read(config.DataDir) is { } moveWords)
        {
            // The rename's data-folder move (DataFolderMove): why the data is still in the old folder, or that both
            // folders hold an install. Said for as long as it applies.
            _log($"  Data move : {moveWords}");
        }

        _log($"  Image     : {image}");
        _log($"  Version   : {EffectiveVersion(config)} ({EffectiveVariant(config)})");
        _log($"  Website   : {LauncherDefaults.ResolveWebsiteUrl(config.WebsiteUrl)}");
        if (config.Broker?.BaseUrl is { Length: > 0 } brokerUrl)
        {
            _log($"  Broker    : {brokerUrl} (credential broker - ADR 0010)");
        }

        // Refresh + report the applied hardening posture (also rewrites sandbox-posture.json for the app).
        var plan = ContainerHardening.Plan(_runtime.Name, ImageStripped(config));
        WriteSandboxPosture(config);
        var active = plan.Controls.Count(c => c.Status == HardeningStatus.Active);

        // The count is a PLAN worked out from the runtime's name. It is only a fact about something while a
        // container is running under it, so with none running it is said in the future tense. Seen after a
        // reboot with the engine down: "unknown (podman is not available)" and then "8/8 protections active".
        _log((running
                ? $"  Sandbox   : {active}/{plan.Controls.Count} protections active on {_runtime.Name}"
                : $"  Sandbox   : {active}/{plan.Controls.Count} protections will apply on {_runtime.Name} (the container is not running)") +
            (plan.StrongerRuntimeRecommended ? " (Docker/Podman enforce more - see the Sandbox posture view)" : string.Empty));

        // F3 tamper-evidence line: the pinned digest + whether the loaded image still matches it (ADR 0007).
        _log($"  Integrity : {DescribeIntegrity(config)}");

        // Refresh the update state (also updates update-state.json so the app's lock stays current) and report it.
        var availability = EvaluateUpdate(config);
        _log($"  Update    : {availability.Kind switch
        {
            UpdateKind.Critical => $"REQUIRED - {availability.TargetVersion} (run `{ProductName.Command} update`)",
            UpdateKind.Available => $"available - {availability.TargetVersion} (run `{ProductName.Command} update`)",
            UpdateKind.Unknown => $"unknown - {CouldNotCheckWords(availability, config)}",
            _ => "up to date",
        }}");

        // Backlog 242: whether the manifest that answered is the release key's, and so whether anything above came from it.
        _log($"  Manifest  : {UpdateChannelWords.Manifest(availability)}");

        // Backlog 252: what the running studio found when it asked fravashi.exe's bridge from inside the container. Said
        // only while the container runs (a stopped studio's last finding is stale) and only when there is a problem.
        if (running && BridgeReachFile.Read(config.DataDir) is { } reach)
        {
            _log($"  Bridge    : {reach}");
        }

        return ExitCodes.Success;
    }

    /// <summary>
    /// What to tell the person when the container was started but the studio never answered on its port from Windows
    /// (backlog 252), or null when the launcher has nothing to add. Only Podman has a known cause (a ROOTFUL machine
    /// publishes no port Windows can reach), and only while the container is running; Podman is asked, and an engine
    /// that cannot say is said as that.
    /// </summary>
    public string? StudioUnansweredAdvice()
    {
        try
        {
            if (!string.Equals(_runtime.Name, RuntimeNames.Podman, StringComparison.Ordinal)
                || LoadConfig() is not { } config
                || !_runtime.ContainerRunning(config.ContainerName))
            {
                return null;
            }

            return PodmanWords.StudioUnanswered(config.Port, _runtime.RunsRootless());
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            _log($"Could not ask the engine why the studio does not answer: {ex.Message}");
            return null;
        }
    }

    /// <summary>Opens the product website (launcher.json <c>websiteUrl</c>, default <see cref="LauncherDefaults.WebsiteUrl"/>).</summary>
    public int Website()
    {
        var config = LoadConfig();
        var url = LauncherDefaults.ResolveWebsiteUrl(config?.WebsiteUrl);
        _log($"Opening {url}");
        _openBrowser(url);
        return ExitCodes.Success;
    }

    /// <summary>
    /// Sets (or clears) the credential-broker endpoint in launcher.json (ADR 0010, the <c>corp-broker</c> tier). This
    /// is the <c>fravashi broker &lt;url&gt;</c> helper that closes the gap where a brokered studio launched via
    /// <c>fravashi</c> had no way to receive its broker URL. Pass a URL to set it, or <c>off</c>/<c>none</c>/empty to
    /// clear it. The change takes effect on the next container (re)creation - the launcher recreates the container on
    /// the next <c>fravashi</c> run so the new <c>Broker__BaseUrl</c> is applied. Carries only the endpoint, never a
    /// secret.
    /// </summary>
    public int SetBroker(string? url)
    {
        var config = LoadConfig();
        if (config is null)
        {
            _log($"{ProductName.Short} has not been started yet (no launcher.json). Run `{ProductName.Command}` once first, then `{ProductName.Command} broker <url>`.");
            return ExitCodes.Success;
        }

        var trimmed = url?.Trim();
        var clearing = string.IsNullOrEmpty(trimmed)
            || string.Equals(trimmed, "off", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "none", StringComparison.OrdinalIgnoreCase);

        if (!clearing && !Uri.TryCreate(trimmed, UriKind.Absolute, out _))
        {
            _log($"'{url}' is not an absolute URL. Give a broker endpoint like `{ProductName.Command} broker https://broker.example:8080`, or `{ProductName.Command} broker off` to clear it.");
            return ExitCodes.UsageError;
        }

        LauncherConfig updated;
        if (clearing)
        {
            updated = config with { Broker = null };
            _log($"Cleared the credential-broker endpoint. Recreate the container (`{ProductName.Command} stop` then `{ProductName.Command}`) to apply.");
        }
        else
        {
            var existingRef = config.Broker?.UserTokenSecretRef;
            updated = config with { Broker = new BrokerLaunchConfig { BaseUrl = trimmed, UserTokenSecretRef = existingRef } };
            _log($"Set the credential-broker endpoint to {trimmed}. Recreate the container (`{ProductName.Command} stop` then `{ProductName.Command}`) to apply.");
            _log("Note: the broker holds the org key (ADR 0011); this device only points the CLI at that endpoint and mints a per-user token.");
        }

        _configStore.Save(updated);
        return ExitCodes.Success;
    }

    /// <summary>Tails the container logs (nice-to-have).</summary>
    public int Logs(int tail)
    {
        if (_runtime.Probe() is var state && state != RuntimeAvailability.Available)
        {
            _log(RuntimeUnavailableMessage(state));
            return ExitCodes.RuntimeUnavailable;
        }

        var config = LoadConfig();
        if (config is null || !_runtime.ContainerExists(config.ContainerName))
        {
            _log($"No {ProductName.Short} container exists yet. Run `{ProductName.Command}` to start it.");
            return ExitCodes.Success;
        }

        _log(_runtime.GetLogs(config.ContainerName, tail));
        return ExitCodes.Success;
    }

    /// <summary>
    /// What the container last printed, or null when there is nothing to read (no config, no container,
    /// or an engine that will not answer). Distinct from the <c>logs</c> verb, which PRINTS: the window's
    /// recovery page needs the text back so it can say why the studio is not answering, and a page that
    /// throws while explaining a failure is worse than the failure.
    /// </summary>
    public string? ContainerTail(int tail = 40)
    {
        try
        {
            var config = LoadConfig();
            if (config is null || !_runtime.ContainerExists(config.ContainerName))
            {
                return null;
            }

            var text = _runtime.GetLogs(config.ContainerName, tail);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex)
        {
            _log($"Could not read the container log for the recovery page: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Closes the tray process if one is running, leaving the container as-is. Distinct from <c>fravashi stop</c>:
    /// exit affects only the tray icon; the product keeps running and stays reachable at its URL and via the
    /// other verbs.
    /// </summary>
    public int Exit()
    {
        if (_tray.SignalExit())
        {
            _log($"Closed the {ProductName.Short} tray. The product keeps running - use `{ProductName.Command} stop` to stop the container.");
        }
        else
        {
            _log($"No {ProductName.Short} tray is running. (The container, if any, is unaffected - `{ProductName.Command} stop` stops it.)");
        }

        return ExitCodes.Success;
    }

    /// <summary>What the update channel offers this install now (backlog 157), for the resident upgrade operation.</summary>
    /// <returns>The installed version, the newer version offered or null, whether it is critical, and why not in words.</returns>
    public (string Installed, string? Offered, bool Critical, string? Said) CheckForUpgrade()
    {
        if (_runtime.Probe() is var state && state != RuntimeAvailability.Available)
        {
            return ("unknown", null, false, RuntimeUnavailableMessage(state));
        }

        if (LoadConfig() is not { } loaded)
        {
            return ("unknown", null, false, $"{ProductName.Short} has not been started yet, so there is nothing to upgrade.");
        }

        var config = FoldInstalledRelease(loaded);
        var availability = EvaluateUpdate(config);
        return availability.Kind switch
        {
            UpdateKind.None => (DisplayVersion(config), null, false, $"{ProductName.Short} {DisplayVersion(config)} is the newest release the update channel offers."),
            // Backlog 230: no offer can be installed without a manifest, and "newest" would be a guess.
            UpdateKind.Unknown => (DisplayVersion(config), null, false, CouldNotCheckWords(availability, config)),
            _ => (DisplayVersion(config), availability.TargetVersion, availability.Kind == UpdateKind.Critical, null),
        };
    }

    /// <summary>
    /// Applies the update the channel offers, without a console prompt, when it is exactly <paramref name="version"/>
    /// (backlog 157): the person approved that version in the studio, and a channel that moved since must not install
    /// another. Runs in the resident fravashi.exe, whose launcher holds the bridge, so the new container gets it.
    /// </summary>
    /// <returns>Whether the container now runs the new version, and what was said.</returns>
    public (bool Applied, string Said) ApplyUpgrade(string version)
    {
        var lines = new List<string>();
        var (installed, offered, _, said) = CheckForUpgrade();
        if (offered is null)
        {
            return (false, said ?? $"Nothing newer than {installed} is offered.");
        }

        if (!string.Equals(offered, version, StringComparison.Ordinal))
        {
            return (false, $"The update channel now offers {offered}, not {version}; nothing was installed.");
        }

        var config = FoldInstalledRelease(LoadConfig()!);
        var availability = EvaluateUpdate(config);
        var applied = CreateUpdater(config, line =>
        {
            lines.Add(line);
            _log(line);
        }).ApplyAsync(config, availability).GetAwaiter().GetResult();

        return (applied, lines.LastOrDefault() ?? (applied ? $"Updated to {version}." : "The update did not complete."));
    }

    /// <summary>
    /// The product self-update (ADR 0006 / D03-7): evaluate the manifest and, if newer, download + verify + swap
    /// the image + migrate on the same port/volume. An <b>Available</b> update is confirmed; a <b>Critical</b> one
    /// is required and applied without an opt-out. Degrades honestly when no channel is configured.
    /// </summary>
    public int Update()
    {
        if (_runtime.Probe() is var state && state != RuntimeAvailability.Available)
        {
            _log(RuntimeUnavailableMessage(state));
            return ExitCodes.RuntimeUnavailable;
        }

        var loaded = LoadConfig();
        if (loaded is null)
        {
            _log($"{ProductName.Short} has not been started yet. Run `{ProductName.Command}` first, then `{ProductName.Command} update`.");
            return ExitCodes.Success;
        }

        var config = FoldInstalledRelease(loaded);
        var availability = EvaluateUpdate(config);
        switch (availability.Kind)
        {
            case UpdateKind.None:
                _log($"{ProductName.Short} is up to date (version {DisplayVersion(config)}).");
                return ExitCodes.Success;

            case UpdateKind.Unknown:
                _log(CouldNotCheckWords(availability, config));
                return ExitCodes.UpdateChannelUnreachable;

            case UpdateKind.Critical:
                _log($"A REQUIRED update to {ProductName.Short} {availability.TargetVersion} is available (installed {DisplayVersion(config)}). Installing it now - it cannot be skipped.");
                return ApplyUpdate(config, availability);

            default:
                _log($"An update to {ProductName.Short} {availability.TargetVersion} is available (installed {DisplayVersion(config)}).");
                if (!_confirm("Install it now? [y/N] "))
                {
                    _log($"Update cancelled. Run `{ProductName.Command} update` when you're ready; the current version keeps running.");
                    return ExitCodes.Success;
                }

                return ApplyUpdate(config, availability);
        }
    }

    /// <summary>
    /// Applies a confirmed update: through the running fravashi.exe when one answers (backlog 157), because it holds the
    /// host bridge and the app runner and the container it recreates keeps them; otherwise here, as before, where the
    /// next start then recreates the container with the bridge (backlog 160).
    /// </summary>
    private int ApplyUpdate(LauncherConfig config, UpdateAvailability availability)
    {
        if (availability.TargetVersion is { } target && _residentUpgrade?.TryApply(target, _log) is { } handed)
        {
            _log(handed.Said);
            return handed.Applied ? ExitCodes.Success : ExitCodes.ImageMissing;
        }

        return CreateUpdater(config).ApplyAsync(config, availability).GetAwaiter().GetResult()
            ? ExitCodes.Success
            : ExitCodes.ImageMissing;
    }

    /// <summary>
    /// Re-pins launcher.json's <c>pinnedImageDigest</c> to the CURRENT local image digest: the recovery the
    /// integrity refusal names when a legitimate LOCAL image change (a reinstall, a manual retag, or a fresh local
    /// bundle load) moved the tag without going through <c>fravashi update</c>'s remote path. It is deliberately NOT the
    /// remote update path (<c>fravashi update</c> stays as-is): with no manifest configured that path only 404s and
    /// changes nothing local, which is exactly how the owner stayed stuck (backlog 101).
    /// <para>
    /// Tamper-evidence is PRESERVED. The gate still refuses to START silently on a mismatch
    /// (<see cref="PinVerifyAndProvision"/>); recovery is a deliberate, digest-DISPLAYING, owner-CONFIRMED act at the
    /// console. That is exactly the trust boundary the pin already crosses unconfirmed on first run (line 644) and on
    /// a verified remote update (<c>ProductUpdater.ApplyAsync</c>): an owner physically at the console, shown both
    /// digests and typing "y", is authenticating the change, whereas a background/automated image swap has no console
    /// and cannot answer the prompt. A silent auto-repin would defeat the pin; this is never silent. It is a
    /// host-launcher action only (the container/app cannot rewrite launcher.json), so nothing inside the sandbox can
    /// re-pin itself. A no-op when the digests already match.
    /// </para>
    /// </summary>
    public int Repin()
    {
        if (_runtime.Probe() is var state && state != RuntimeAvailability.Available)
        {
            _log(RuntimeUnavailableMessage(state));
            return ExitCodes.RuntimeUnavailable;
        }

        var config = LoadConfig();
        if (config is null)
        {
            _log($"{ProductName.Short} has not been started yet. Run `{ProductName.Command}` first; the first start pins the image on its own.");
            return ExitCodes.Success;
        }

        var resolved = _runtime.ResolveImageDigest(config.Image);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            _log($"Could not resolve the '{config.Image}' image digest on this runtime, so there is nothing to " +
                $"re-pin. Make sure the image is loaded (`{ProductName.Command} status`), then try again.");
            return ExitCodes.Success;
        }

        // No pin yet: this is the ordinary trust-on-first-use case, which never overrides anything, so it pins
        // without a prompt, the same posture the first-run launch takes (PinVerifyAndProvision, line 644).
        if (string.IsNullOrWhiteSpace(config.PinnedImageDigest))
        {
            config = config with { PinnedImageDigest = resolved };
            _configStore.Save(config);
            ProvisionIntegrity(config, resolved, null);
            _log($"Integrity: pinned the image digest {resolved}.");
            return ExitCodes.Success;
        }

        if (ImageIds.Same(config.PinnedImageDigest, resolved))
        {
            _log($"The pinned digest already matches the loaded image ({Short(resolved)}); nothing to re-pin.");
            return ExitCodes.Success;
        }

        // Show BOTH digests before changing anything (acceptance: names old and new), then require an explicit
        // console confirmation. Fail closed if not confirmed, the same posture as the update opt-in at line 393.
        _log($"Re-pinning the local {ProductName.Short} image digest:");
        _log($"    old (pinned)   : {config.PinnedImageDigest}");
        _log($"    new (resolved) : {resolved}");
        _log($"This tells {ProductName.Short} to TRUST the image now under the tag. Only do this if YOU just changed it (a " +
            "reinstall, a retag, or a local build). If you did not, this may be a tampered image; answer N.");
        if (!_confirm("Re-pin to the new digest? [y/N] "))
        {
            _log($"Re-pin cancelled. The pinned digest is unchanged; {ProductName.Short} still refuses to start until it matches.");
            return ExitCodes.Success;
        }

        config = config with { PinnedImageDigest = resolved };
        _configStore.Save(config);
        // Re-provision the expected-shape record so the app-side integrity-record.json matches the new digest too.
        ProvisionIntegrity(config, resolved, null);
        _log($"Re-pinned the image digest to {resolved}. Start {ProductName.Short} with `{ProductName.Command}`.");
        return ExitCodes.Success;
    }

    /// <summary>
    /// Restores a backup written by the studio (backlog 79). This half is the LAUNCHER's because it needs
    /// the studio stopped: the process that owns the open database cannot be the one that replaces it.
    /// <para>
    /// The order is the whole design. Everything that can refuse refuses FIRST, with the install untouched:
    /// the runtime has to answer (otherwise the container could be stopped and not started again), the
    /// archive has to verify in full (every recorded SHA256, every path proven to stay inside the data
    /// directory, a schema version this build knows), and the owner has to confirm. Only then is the
    /// container stopped and the current database, key ring and agent sessions MOVED - never deleted - into
    /// <c>&lt;data dir&gt;/backups/pre-restore-&lt;stamp&gt;/</c>.
    /// </para>
    /// <para>
    /// After the swap the product is started and asked for its health, and what is reported is the schema
    /// version THE STUDIO opened the restored database at, not the one the manifest claimed. A failure
    /// after the swap names the folder holding the previous state in the same breath, because at that point
    /// the useful sentence is not what went wrong but where the owner's data is. On a machine that had
    /// nothing to move aside it says THAT instead, and how much of the archive had already landed: naming
    /// a pre-restore folder that holds nothing would be the one inaccuracy this message cannot afford.
    /// </para>
    /// </summary>
    public int Restore(string? archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            _log($"Usage: {ProductName.Command} restore <backup.zip>");
            _log($"Backups are written from {ProductName.Short} under Settings > Data > Backup and restore, which shows the path.");
            return ExitCodes.UsageError;
        }

        if (_runtime.Probe() is var state && state != RuntimeAvailability.Available)
        {
            _log(RuntimeUnavailableMessage(state));
            return ExitCodes.RuntimeUnavailable;
        }

        // The archive is checked BEFORE anything is looked up or written. On a machine that has never run
        // Fravashi the configuration is created further down, and it has to be created only after this: a
        // refused backup says "Nothing has been changed", and writing a launcher.json and reserving a port
        // first would make that sentence false on exactly the machine where it matters most.
        var archive = Path.GetFullPath(archivePath);
        var inspection = BackupArchive.Inspect(archive);
        if (!inspection.Ok)
        {
            _log($"This backup will not be restored: {inspection.Problem}");
            _log("Nothing has been changed.");
            return ExitCodes.BackupRefused;
        }

        // A machine that has never run Fravashi is the case a backup exists FOR: the old machine is gone and
        // this one is new (backlog 79, found on a real clean machine on 2026-09-23). This used to refuse and
        // send the owner through a full first launch, which builds an empty install only for the restore to
        // move it aside. Nothing is written yet: the configuration is created once the owner has said yes.
        var config = LoadConfig();
        var dataDir = config?.DataDir ?? _defaultDataDir();

        var manifest = inspection.Manifest!;
        var preRestoreFolder = Path.Combine(dataDir, BackupRestore.BackupsFolderName);
        _log($"Backup   : {archive}");
        _log($"Taken    : {manifest.CreatedUtc} by {ProductName.Short} {manifest.ProductVersion} ({manifest.ProductVariant})");
        _log($"Contents : {manifest.Files.Count.ToString(CultureInfo.InvariantCulture)} file(s), database schema " +
            $"{manifest.SchemaVersion.ToString(CultureInfo.InvariantCulture)} " +
            $"(this {ProductName.Short} knows up to {BackupManifest.KnownSchemaVersion.ToString(CultureInfo.InvariantCulture)})");
        _log("Verified : every file in it matched its recorded SHA256.");

        foreach (var provider in manifest.Providers)
        {
            _log($"  provider: {provider.Kind} '{provider.Id}' ({provider.Status})" +
                (provider.HasCredential ? " with a stored credential" : " with no stored credential"));
        }

        foreach (var note in manifest.Notes)
        {
            _log($"  note: {note}");
        }

        _log(config is null
            ? $"{ProductName.Short} has not been run on this machine yet, so the backup becomes this machine's data, in {dataDir}."
            : $"This replaces the database, key ring and agent sessions in {dataDir}. The current ones " +
              $"are MOVED into {preRestoreFolder} and never deleted.");
        if (!_confirm("Restore this backup? [y/N] "))
        {
            _log("Restore cancelled. Nothing has been changed.");
            return ExitCodes.Success;
        }

        if (config is null)
        {
            // Exactly what a first launch does for configuration (reserve a port from the registered list, the
            // default data directory, the runtime), and nothing it does beyond that: the studio is started below,
            // on the restored data, by the same EnsureRunning a launch uses.
            try
            {
                config = CreateInitialConfig();
            }
            catch (InvalidOperationException ex)
            {
                // Every port the identity provider has registered is busy (backlog item 7).
                _log(ex.Message);
                _log("Nothing has been restored.");
                return ExitCodes.PortsUnavailable;
            }
        }

        Directory.CreateDirectory(config.DataDir);

        if (_runtime.ContainerExists(config.ContainerName) && _runtime.ContainerRunning(config.ContainerName))
        {
            _log($"Stopping {ProductName.Short} (container '{config.ContainerName}') so the database can be replaced ...");
            _runtime.StopAndConfirm(config.ContainerName, _log);
        }

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var swap = BackupRestore.Apply(archive, manifest, config.DataDir, stamp);
        var written = swap.Restored.Count.ToString(CultureInfo.InvariantCulture);
        if (!swap.Ok)
        {
            // Three states, and the difference between them is the whole value of this message: the
            // previous state is aside, or there was none and some of the archive is already on disk, or
            // nothing has happened at all. Naming the wrong one sends the owner looking for their data in
            // a folder that does not hold it.
            _log($"The restore stopped: {swap.Problem}");
            if (swap.PreviousStateMoved)
            {
                _log($"YOUR PREVIOUS DATA IS NOT LOST. It is in {swap.PreRestoreFolder}: move its contents back " +
                    $"into {config.DataDir} to return to where you were, then run `{ProductName.Command}`.");
                if (swap.Restored.Count > 0)
                {
                    _log($"{written} file(s) of the backup had already been written into {config.DataDir} " +
                        "before it stopped, so overwrite those as you move the previous ones back.");
                }
            }
            else if (swap.Restored.Count > 0)
            {
                _log($"There was nothing of yours in {config.DataDir} to move aside, so nothing was replaced, " +
                    $"but {written} file(s) of the backup were written into it before the restore stopped. " +
                    $"This install is PART RESTORED: fix the problem above and run the same `{ProductName.Command} restore` again.");
            }
            else
            {
                _log($"Nothing had been moved yet, so this install is exactly as it was. Run `{ProductName.Command}` to start it.");
            }

            return ExitCodes.RestoreIncomplete;
        }

        _log($"Restored {written} file(s).");
        _log(swap.PreviousStateMoved
            ? $"The state this replaced is kept in {swap.PreRestoreFolder}."
            : $"There was nothing in {config.DataDir} to replace, so nothing was kept aside.");

        // Said once here rather than rebuilt at each exit below, and empty when nothing was moved, because
        // a sentence about the previous state is worse than no sentence when there is no previous state.
        var keptState = swap.PreviousStateMoved
            ? $" The state this restore replaced is in {swap.PreRestoreFolder}."
            : string.Empty;

        var exit = EnsureRunning(out var started);
        if (exit != ExitCodes.Success)
        {
            _log($"The restored data is in place, but {ProductName.Short} could not be started; the problem is above." +
                keptState);
            return exit;
        }

        var health = _studioHealth(LauncherUrls.Base(started!.Port));
        if (health is null || !health.Ok)
        {
            _log($"{ProductName.Short} was started but did not report itself healthy in time, so this restore is NOT " +
                $"verified. Check `{ProductName.Command} logs`. The restored data is in place." + keptState);
            return ExitCodes.RestoreIncomplete;
        }

        if (health.SchemaVersion is { } version)
        {
            _log($"{ProductName.Short} is back up on the restored database and reports schema version " +
                $"{version.ToString(CultureInfo.InvariantCulture)} " +
                $"(the backup carried {manifest.SchemaVersion.ToString(CultureInfo.InvariantCulture)}).");
        }
        else
        {
            _log($"{ProductName.Short} is back up on the restored database. This image does not report its schema version, " +
                $"so the only version known here is the {manifest.SchemaVersion.ToString(CultureInfo.InvariantCulture)} " +
                "the backup recorded.");
        }

        return ExitCodes.Success;
    }

    private int EnsureRunning(out LauncherConfig? config)
    {
        config = null;
        _phase(LaunchPhase.CheckingRuntime);
        if (!EnsureRuntimeAnswers())
        {
            return ExitCodes.RuntimeUnavailable;
        }

        LauncherConfig effective;
        var portMoved = false;
        try
        {
            var stored = LoadConfig();
            effective = stored is null ? CreateInitialConfig() : ReconcileStudioPort(stored, out portMoved);
        }
        catch (InvalidOperationException ex)
        {
            // Every port the identity provider has registered is busy (backlog item 7): say which ones and stop,
            // rather than start on a port sign-in would then be refused on.
            _log(ex.Message);
            return ExitCodes.PortsUnavailable;
        }

        Directory.CreateDirectory(effective.DataDir);

        _phase(LaunchPhase.PreparingImage);
        if (!EnsureImage(effective))
        {
            return ExitCodes.ImageMissing;
        }

        // The installed image says which version it is, and that has to be known BEFORE the expected shape below is
        // written: the studio's integrity check compares the container's FRAVASHI_VERSION with it, and a record carrying
        // the previous install's version made the first start after every install fail closed (seen 2026-10-02).
        // Backlog 251: the same goes for its variant, and a container created against a record naming another variant
        // was started against that expectation, so it is recreated (below) rather than restarted.
        var recordedVariant = EffectiveVariant(effective);
        effective = FoldInstalledRelease(effective);
        var variantCorrected = !string.Equals(recordedVariant, EffectiveVariant(effective), StringComparison.Ordinal);

        // F5 (ADR 0008): when VPN mode is on, bring up the VPN sidecar FIRST - the structural kill switch the studio
        // joins - and refuse on a runtime that cannot run it (wslc) or a swapped sidecar image. Returns the netns to
        // join, the control-server URL the studio probes, and the sidecar's integrity component (VPN4).
        if (effective.Vpn?.Enabled == true)
        {
            _phase(LaunchPhase.StartingVpn);
        }

        if (!EnsureVpnSidecar(ref effective, out var vpnSidecar, out var vpnRefuse))
        {
            return vpnRefuse;
        }

        // F3 tamper-evidence (ADR 0007): pin+verify the image digest and record the expected shape BEFORE the
        // container is (re)created, so the app's integrity service verifies against what the launcher just did. A
        // genuine digest mismatch refuses the launch loudly. A run that cannot resolve a digest degrades to no
        // enforcement (the app runs report-only) rather than blocking - do-no-harm. The VPN sidecar (when present)
        // is recorded as a second component.
        _phase(LaunchPhase.VerifyingIntegrity);
        if (!PinVerifyAndProvision(ref effective, vpnSidecar, out var integrity))
        {
            return ExitCodes.IntegrityRefused;
        }

        _phase(LaunchPhase.PreparingContainer);
        EnsureContainer(effective, integrity, portMoved, variantCorrected ? recordedVariant : null);
        WriteSandboxPosture(effective);
        config = effective;
        return ExitCodes.Success;
    }

    /// <summary>
    /// Makes the runtime answer, starting its machine when that is what is missing (item 6). The owner's
    /// post-reboot case is Podman installed, its machine not auto-started, so the probe fails and the launch
    /// used to end at "install a container runtime" for a runtime that is plainly installed.
    /// <para>
    /// A missing CLI is reported as before. An installed-but-silent engine gets ONE start attempt, announced
    /// through the <see cref="LaunchPhase.StartingRuntime"/> phase so the splash names the slow step, followed
    /// by ONE re-probe: <c>podman machine start</c> returns only once the connection is up, so a success that
    /// still does not answer is a real failure and is reported as one rather than retried behind the splash.
    /// Every runtime call underneath is deadline-bounded, so this path cannot hang the launch.
    /// </para>
    /// </summary>
    private bool EnsureRuntimeAnswers()
    {
        var state = _runtime.Probe();
        if (state == RuntimeAvailability.Available)
        {
            return true;
        }

        if (state == RuntimeAvailability.CliMissing)
        {
            _log(RuntimeNotInstalledMessage);
            return false;
        }

        _log($"The '{_runtime.Name}' runtime is installed but its engine is not answering; trying to start it ...");
        _phase(LaunchPhase.StartingRuntime);
        var start = _runtime.TryStartMachine();
        _log(start.Message);
        if (!start.Started)
        {
            return false;
        }

        if (_runtime.Probe() == RuntimeAvailability.Available)
        {
            return true;
        }

        _log($"The '{_runtime.Name}' machine started but the engine still does not answer. Check it with " +
            $"`{_runtime.Name} version`, then run `{ProductName.Command}` again.");
        return false;
    }

    /// <summary>
    /// F5 / ADR 0008 (VPN1 - VPN4, VPN6): when the config enables VPN, start the tiny sidecar FIRST and prepare the
    /// studio to join its network namespace (the structural kill switch). Refuses (a) on a runtime that cannot run a
    /// VPN sidecar - <c>wslc</c> has no <c>--cap-add</c>/<c>--device</c> (ADR 0009 matrix), so the VPN variant needs
    /// Docker/Podman - and (b) on a sidecar image whose digest no longer matches the pinned one (VPN4, fail-closed).
    /// Waits (bounded, best-effort) for the sidecar to be ready, then proceeds regardless: the studio's own gate
    /// holds at <c>/secure-connection</c> until the tunnel is confirmed, so the launcher never blocks indefinitely.
    /// A no-op returning success when VPN is off. Returns false only to refuse (via <paramref name="refuseCode"/>).
    /// </summary>
    private bool EnsureVpnSidecar(ref LauncherConfig config, out IntegrityComponent? sidecar, out int refuseCode)
    {
        sidecar = null;
        refuseCode = ExitCodes.Success;

        var vpn = config.Vpn;
        if (vpn is null || !vpn.Enabled)
        {
            return true;
        }

        if (!_runtime.SupportsVpnSidecar)
        {
            _log("");
            _log("  ****************************  VPN RUNTIME UNSUPPORTED  ****************************");
            _log($"  VPN mode is enabled, but the '{_runtime.Name}' runtime cannot run a VPN sidecar - it does not");
            _log("  accept --cap-add or --device, which a VPN client needs (see ADR 0009's capability matrix).");
            _log($"  Install Docker or Podman and pin it in launcher.json (\"runtime\": \"docker\"), then run `{ProductName.Command}`");
            _log("  again. Refusing to start the studio without the tunnel it is required to run through.");
            _log("  ********************************************************************************");
            _log("");
            refuseCode = ExitCodes.VpnUnsupportedRuntime;
            return false;
        }

        // Start (or recreate) the sidecar. Docker/Podman auto-pull the registry image on `run`.
        if (!_runtime.ContainerExists(vpn.ContainerName) || !_runtime.ContainerRunning(vpn.ContainerName))
        {
            _runtime.RemoveContainer(vpn.ContainerName); // clear any stale stopped sidecar (idempotent).
            _log($"Starting the VPN sidecar '{vpn.ContainerName}' ({vpn.Image}); the studio will join its network namespace ...");
            _runtime.RunVpnSidecar(VpnSidecarPlan.BuildRunSpec(vpn, config.Port, LauncherDefaults.ContainerPort));
        }
        else
        {
            _log($"VPN sidecar '{vpn.ContainerName}' is already running.");
        }

        // VPN4: pin + verify the sidecar image digest (trust-on-first-use). A swapped sidecar image → refuse: the
        // studio is about to trust its entire network path to this container's netns.
        var sidecarDigest = _runtime.ResolveImageDigest(vpn.Image);
        if (!string.IsNullOrWhiteSpace(vpn.PinnedImageDigest) && !string.IsNullOrWhiteSpace(sidecarDigest) &&
            !string.Equals(vpn.PinnedImageDigest, sidecarDigest, StringComparison.OrdinalIgnoreCase))
        {
            _log("");
            _log("  ****************************  VPN SIDECAR INTEGRITY FAILED  ****************************");
            _log($"  The VPN sidecar image '{vpn.Image}' digest does NOT match the pinned digest.");
            _log($"    expected (pinned): {vpn.PinnedImageDigest}");
            _log($"    found  (resolved): {sidecarDigest}");
            _log("  Refusing to start - the container the studio routes all traffic through has been altered.");
            _log("  ********************************************************************************");
            _log("");
            refuseCode = ExitCodes.IntegrityRefused;
            return false;
        }

        if (string.IsNullOrWhiteSpace(vpn.PinnedImageDigest) && !string.IsNullOrWhiteSpace(sidecarDigest))
        {
            vpn = vpn with { PinnedImageDigest = sidecarDigest };
            config = config with { Vpn = vpn };
            _configStore.Save(config);
            _log($"Integrity: pinned the VPN sidecar image digest {sidecarDigest}.");
        }

        if (WaitForVpnReady(vpn))
        {
            _log("VPN sidecar is up; starting the studio joined to its tunnel.");
        }
        else
        {
            _log("The VPN tunnel is not confirmed yet - starting the studio anyway; it will hold at the " +
                "waiting-for-connection page until the tunnel comes up (no traffic leaves without it).");
        }

        // The netns to join and the control-server URL are NOT returned: they are derived from the configuration
        // where the container is created (backlog 160), so no creation path can leave the studio outside the
        // tunnel by forgetting to pass them.
        sidecar = string.IsNullOrWhiteSpace(sidecarDigest)
            ? null
            : VpnSidecarPlan.BuildIntegrityComponent(vpn, sidecarDigest);
        return true;
    }

    /// <summary>Bounded, best-effort wait for the sidecar to be ready (default: the container is running).</summary>
    private bool WaitForVpnReady(VpnConfig vpn)
    {
        for (var attempt = 0; attempt < _vpnReadyAttempts; attempt++)
        {
            if (_vpnConnectedProbe(vpn))
            {
                return true;
            }

            if (_vpnPollInterval > TimeSpan.Zero)
            {
                Thread.Sleep(_vpnPollInterval);
            }
        }

        return false;
    }

    /// <summary>
    /// F3 digest pin/verify + expected-shape provisioning (ADR 0007 #6/#8). Resolves the loaded image's content
    /// digest; on a first run pins it (trust-on-first-use, honestly degrading until the signed releases repo gives
    /// an authoritative digest); on a later run refuses if the resolved digest differs from the pinned one (the
    /// tamper signal - a re-pin only happens through <c>fravashi update</c>). When a digest is available it loads the
    /// host-side HMAC key, writes the signed expected-shape record into <c>/data</c>, and returns the run-spec env
    /// fields (key + digest) the container is created with. Returns false only on a genuine mismatch (refuse).
    /// </summary>
    private bool PinVerifyAndProvision(ref LauncherConfig config, IntegrityComponent? sidecar, out IntegrityRunFields integrity)
    {
        integrity = IntegrityRunFields.None;

        var resolved = _runtime.ResolveImageDigest(config.Image);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            _log("Integrity: could not resolve the image digest on this runtime - the app will run in report-only " +
                "integrity mode (no fail-closed enforcement) until a digest can be pinned.");
            return true;
        }

        // The same image under another engine's spelling of its id is the same image (ImageIds).
        if (!string.IsNullOrWhiteSpace(config.PinnedImageDigest) && !ImageIds.Same(config.PinnedImageDigest, resolved))
        {
            _log("");
            _log("  ****************************  INTEGRITY CHECK FAILED  ****************************");
            _log($"  The '{config.Image}' image digest does NOT match the pinned digest.");
            _log($"    expected (pinned): {config.PinnedImageDigest}");
            _log($"    found  (resolved): {resolved}");
            _log($"  {ProductName.Short} is REFUSING to start. If YOU just changed the image (a reinstall, a retag, or a local");
            _log($"  build), run `{ProductName.Command} repin`: it re-pins the local image to the new digest after showing you both.");
            _log("  Otherwise the image may have been altered - reinstall from a trusted release.");
            _log("  ********************************************************************************");
            _log("");
            return false;
        }

        if (string.IsNullOrWhiteSpace(config.PinnedImageDigest))
        {
            config = config with { PinnedImageDigest = resolved };
            _configStore.Save(config);
            _log($"Integrity: pinned the image digest {resolved}.");
        }

        integrity = ProvisionIntegrity(config, resolved, sidecar);
        return true;
    }

    /// <summary>
    /// Loads the HMAC key and writes the signed expected-shape record into <c>/data</c>, returning the run-spec env
    /// fields. Best-effort: a write failure downgrades to report-only rather than blocking the launch.
    /// </summary>
    private IntegrityRunFields ProvisionIntegrity(LauncherConfig config, string digest, IntegrityComponent? sidecar)
    {
        try
        {
            var keyStore = new IntegrityKeyStore(_configStore.ConfigDirectory);
            var key = keyStore.LoadOrCreate();
            var record = IntegrityProvisioner.BuildRecord(config, _runtime.Name, digest, sidecar);
            new IntegrityRecordStore(config.DataDir).Write(record, key);
            return new IntegrityRunFields(Convert.ToBase64String(key), digest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"Integrity: could not record the expected shape ({ex.Message}); running in report-only mode.");
            return IntegrityRunFields.None;
        }
    }

    /// <summary>
    /// Records the applied container-hardening posture (F4 / ADR 0007) into the data directory so the app can
    /// DISPLAY the honest truth (assertion-gap rule). Derived from the same <see cref="HardeningPlan"/> that
    /// builds the run args, so the displayed protections match the enforced ones exactly. Best-effort: a write
    /// failure never blocks the launch.
    /// </summary>
    private void WriteSandboxPosture(LauncherConfig config)
    {
        try
        {
            new SandboxPostureStore(config.DataDir).Write(ContainerHardening.Plan(_runtime.Name, ImageStripped(config)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"Could not record the sandbox posture: {ex.Message}");
        }
    }

    /// <summary>Whether the image says its build stripped every privilege-raising binary (ADR 0029, D4).</summary>
    private bool ImageStripped(LauncherConfig config)
    {
        try
        {
            return _runtime.ImageLabel(config.Image, ImageLabels.NoPrivilegeRaisingBinaries) is not null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Loads the persisted config, applying the <c>--name</c> container-name override if set.</summary>
    private LauncherConfig? LoadConfig()
    {
        var config = _configStore.Load();
        if (config is not null && _containerNameOverride is { Length: > 0 })
        {
            config = config with { ContainerName = _containerNameOverride };
        }

        return config;
    }

    /// <summary>
    /// Brings a persisted studio port back inside <see cref="LauncherDefaults.StudioPorts"/> (backlog item 7).
    /// An install that reserved a port before the list existed (or a hand-edited launcher.json) would serve on a
    /// port the identity provider has no redirect URI for, and sign-in would fail with a mismatch the user cannot
    /// fix from inside the app. A port already on the list is KEPT even when the availability probe says it is
    /// busy: on a normal restart the thing holding it is our own running container.
    /// </summary>
    /// <param name="changed">True when the port was moved, so the container's baked port mapping is stale.</param>
    private LauncherConfig ReconcileStudioPort(LauncherConfig config, out bool changed)
    {
        changed = false;
        if (LauncherDefaults.StudioPorts.Contains(config.Port))
        {
            return config;
        }

        var port = PortSelector.SelectFirstFree(LauncherDefaults.StudioPorts, _isPortFree);
        // The sentence about which ports sign in comes from the list itself (LauncherDefaults), never from a
        // copy written by hand: the copy that used to stand here named three ports the provider refuses.
        _log($"Host port {config.Port} is not one {ProductName.Short} can sign in on, so the studio moves to {port}. "
            + $"{LauncherDefaults.SignInPortsSentence} Your data is kept.");
        changed = true;
        var reconciled = config with { Port = port };
        _configStore.Save(reconciled);
        return reconciled;
    }

    private LauncherConfig CreateInitialConfig()
    {
        // Backlog item 7: the studio port comes from the short registered list, never a random free port. The
        // sign-in redirect URI follows the served port and the identity provider refuses an unregistered one.
        var port = PortSelector.SelectFirstFree(LauncherDefaults.StudioPorts, _isPortFree);
        var config = new LauncherConfig
        {
            Port = port,
            DataDir = _defaultDataDir(),
            ContainerName = _containerNameOverride is { Length: > 0 } name ? name : LauncherDefaults.ContainerName,
        };
        config = config with { Runtime = _runtime.Name };
        _configStore.Save(config);
        _log($"First run: reserved host port {port}, container runtime '{_runtime.Name}'.");
        return config;
    }

    private bool EnsureImage(LauncherConfig config)
    {
        if (_runtime.ImageExists(config.Image))
        {
            return true;
        }

        var tarPath = Path.Combine(_launcherDirectory, LauncherDefaults.ImageTarFileName);
        if (File.Exists(tarPath))
        {
            _log($"Loading the {ProductName.Short} image from {tarPath} ...");
            var loaded = _runtime.LoadImage(tarPath);

            // Backlog 190: the tarball names its image localhost/fravashi:latest, which on wslc and Docker is not
            // config.Image. Without this the next step looked for an image that was not there (and Docker would
            // have tried to PULL one by that name).
            LoadedImageName.Answer(_runtime, loaded, config.Image, _log);
            if (!_runtime.ImageExists(config.Image))
            {
                _log($"Loaded {tarPath}, but '{config.Image}' still names no image"
                    + (loaded.Count > 0 ? $" (the load named {string.Join(", ", loaded)})" : "")
                    + ". Re-run the installer.");
                return false;
            }

            return true;
        }

        _log($"The image '{config.Image}' is not present and no image tarball was found next to the launcher ({tarPath}). Re-run the installer.");
        return false;
    }

    /// <summary>Starts recording the fingerprint for a container that is being kept (an upgraded install).</summary>
    private void RecordBridgeTokenFingerprint(LauncherConfig config)
    {
        var current = StudioContainer.Fingerprint(_bridge?.Token);
        if (!string.Equals(config.BridgeTokenFingerprint ?? "", current, StringComparison.Ordinal))
        {
            _configStore.Save((_configStore.Load() ?? config) with { BridgeTokenFingerprint = current });
        }
    }

    /// <summary>
    /// Records the host's current DNS servers as the baseline a just-started container carries (backlog
    /// 218), so <see cref="StudioDnsRecovery"/> compares against what is actually true rather than what was
    /// true the last time this container was created or restarted.
    /// </summary>
    /// <summary>Two host paths name the same folder: case and a trailing separator aside.</summary>
    private static bool SamePath(string a, string b) =>
        string.Equals(a.Trim().TrimEnd('\\', '/'), b.Trim().TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private void RecordDnsBaked(LauncherConfig config)
    {
        var current = HostDnsServers.Signature(HostDnsServers.Resolve());
        if (!string.Equals(config.DnsServersBaked ?? "", current, StringComparison.Ordinal))
        {
            _configStore.Save((_configStore.Load() ?? config) with { DnsServersBaked = current });
        }
    }

    /// <param name="previousVariant">
    /// The variant launcher.json recorded before this start corrected it from the image (backlog 251), or null when it
    /// did not move. The existing container was created and started against an expected shape naming that variant.
    /// </param>
    private void EnsureContainer(
        LauncherConfig config, IntegrityRunFields integrity, bool portMoved = false, string? previousVariant = null)
    {
        string? supersededImageId = null;
        if (_runtime.ContainerExists(config.ContainerName))
        {
            // Each reason to recreate is DECIDED first and the removal happens once, because a port move used
            // to short-circuit the whole block: installing a new fravashi.exe (a new image under the same tag) onto
            // a launcher.json whose port predates the registered list recreated the container from the current
            // image, correctly, but never noticed the image had moved, so the superseded one was never retired
            // and the log did not mention the swap. That combination is the likely one in the field.

            // A local-bundle reinstall swaps the image under the same tag; merely (re)starting the old
            // container would run the OLD build while the integrity pin points at the new image (the
            // owner hit exactly this, 2026-07-26). When the container's image no longer matches the
            // current one, recreate it: /data is a bind mount and the agent copies live on a named
            // volume, so nothing is lost.
            var containerImage = _runtime.ContainerImageId(config.ContainerName);
            var currentImage = _runtime.ResolveImageDigest(config.Image);
            var imageSuperseded = !string.IsNullOrWhiteSpace(containerImage) && !string.IsNullOrWhiteSpace(currentImage)
                && !string.Equals(TrimDigest(containerImage), TrimDigest(currentImage), StringComparison.OrdinalIgnoreCase);
            // What a container created now would carry, asked of the one place that creates them and records
            // what it baked (backlog 160), so the comparison here and that record cannot drift apart.
            var wanted = _container.Baked(config);

            // The WSL subnet moved (reboot) or the bridge was turned on/off: the env baked into the container
            // is stale, and env cannot change on a running container.
            var bridgeStale = !string.Equals(config.BridgeUrl ?? "", wanted.BridgeUrl, StringComparison.OrdinalIgnoreCase);
            // The TOKEN is baked in the same way and the URL says nothing about it (backlog 135): a rotated
            // token left the studio calling the bridge with the old one, and no repository would open. Only a
            // RECORDED fingerprint that differs counts. A launcher.json from before fingerprints existed is
            // not evidence of a rotation, so it is recorded below and the container is left alone.
            var tokenRotated = config.BridgeTokenFingerprint is { Length: > 0 } recorded
                && !string.Equals(recorded, wanted.BridgeTokenFingerprint, StringComparison.Ordinal);

            // The BROKER endpoint is baked in the same way (backlog 138). `fravashi broker <url>` told the owner to
            // run `fravashi stop` and then `fravashi`, which restarted the same container with the old environment: the
            // setting never arrived, on 2026-09-21, until the container was removed by hand.
            var brokerChanged = !string.Equals(config.BrokerBaked ?? "", wanted.BrokerSignature, StringComparison.Ordinal);

            // The APP RUNNER's endpoint and token are baked the same way (backlog 87), and a new setting added to
            // this comparison is the only way it ever reaches an existing container (CLAUDE.md, baked settings).
            var appRunnerChanged = !string.Equals(config.AppRunnerBaked ?? "", wanted.AppRunnerSignature, StringComparison.Ordinal);

            // The UPGRADE operation's endpoint and token likewise (backlog 157).
            var upgradeChanged = !string.Equals(config.UpgradeBaked ?? "", wanted.UpgradeSignature, StringComparison.Ordinal);

            // The NETWORK the studio runs on is fixed at creation too (backlog 270). A rootless Podman container from
            // before this recorded none and runs in pasta mode, which the app runner cannot join to a run.
            var networkChanged = !string.Equals(config.StudioNetworkBaked ?? "", wanted.StudioNetwork, StringComparison.Ordinal);

            // The DATA DIRECTORY the container bind-mounts is fixed at creation too. The move to %LOCALAPPDATA%\Fravashi
            // (DataFolderMove) rewrites DataDir and records the old mount here, so the container is recreated on the
            // new path rather than started on a folder that is gone. Only a RECORDED path that differs counts: a
            // launcher.json from before the field existed is not evidence of a move.
            var dataDirMoved = config.DataDirBaked is { Length: > 0 } mounted
                && !SamePath(mounted, wanted.DataDir);

            // The VARIANT the expected shape names was just corrected from the image (backlog 251). The record is
            // rewritten on every start, but this container was created and run against the other variant's, which is
            // what stopped it; it starts fresh against the corrected one.
            var variantChanged = previousVariant is not null;

            if (imageSuperseded)
            {
                _log($"Container '{config.ContainerName}' was created from an older image ({Short(containerImage)}); "
                    + $"recreating it from the current one ({Short(currentImage)}). Your data is kept.");
                supersededImageId = containerImage;
            }

            if (portMoved)
            {
                // The studio port was just brought back onto the registered list (backlog item 7). A container's
                // published port is baked at creation, so restarting this one would keep serving the old port and
                // sign-in would still be refused.
                _log($"Container '{config.ContainerName}' publishes the previous host port; recreating it on "
                    + $"{config.Port}. Your data is kept.");
            }
            else if (!imageSuperseded && bridgeStale)
            {
                _log($"Container '{config.ContainerName}' carries a stale bridge endpoint; recreating it with the current one. "
                    + "Your data is kept.");
            }
            else if (!imageSuperseded && tokenRotated)
            {
                _log($"Container '{config.ContainerName}' carries a host-bridge token that has since been rotated; "
                    + "recreating it with the current one. Your data is kept.");
            }
            else if (!imageSuperseded && brokerChanged)
            {
                _log($"Container '{config.ContainerName}' was created with a different credential-broker setting; "
                    + "recreating it with the current one. Your data is kept.");
            }
            else if (!imageSuperseded && appRunnerChanged)
            {
                _log($"Container '{config.ContainerName}' was created with a different app-runner endpoint; "
                    + "recreating it with the current one. Your data is kept.");
            }
            else if (!imageSuperseded && upgradeChanged)
            {
                _log($"Container '{config.ContainerName}' was created with a different upgrade endpoint; "
                    + "recreating it with the current one. Your data is kept.");
            }
            else if (!imageSuperseded && networkChanged)
            {
                _log(wanted.StudioNetwork.Length > 0
                    ? $"Container '{config.ContainerName}' was created on a network the app runner cannot join it from; "
                      + $"recreating it on '{wanted.StudioNetwork}'. Your data is kept."
                    : $"Container '{config.ContainerName}' was created on the network '{config.StudioNetworkBaked}'; "
                      + "recreating it on the engine's default. Your data is kept.");
            }
            else if (!imageSuperseded && dataDirMoved)
            {
                _log($"Container '{config.ContainerName}' mounts the data directory's old place ({config.DataDirBaked}); "
                    + $"recreating it on {config.DataDir}. Your data is kept.");
            }
            else if (!imageSuperseded && variantChanged)
            {
                _log($"Container '{config.ContainerName}' was created expecting the {previousVariant} variant, but the image "
                    + $"is {EffectiveVariant(config)}; recreating it. Your data is kept.");
            }

            if (!portMoved && !imageSuperseded && !bridgeStale && !tokenRotated && !brokerChanged && !appRunnerChanged && !upgradeChanged
                && !networkChanged && !dataDirMoved && !variantChanged)
            {
                if (_runtime.ContainerRunning(config.ContainerName))
                {
                    _log($"Container '{config.ContainerName}' is already running.");
                }
                else
                {
                    // Stopped, about to start: safe to normalize /data (nothing holds the DB open) so a container
                    // created before the Podman ownership fix does not restart into a read-only database.
                    NormalizeDataDirForPodman(config);
                    _log($"Starting existing container '{config.ContainerName}' ...");
                    _runtime.StartContainer(config.ContainerName);
                    // Backlog 218: a start refreshes the container's DNS resolver from the host for free (on
                    // wslc), so this is a fresh, true baseline for StudioDnsRecovery to compare against while
                    // it keeps running - not recorded on the "already running" branch above, where nothing
                    // refreshed.
                    RecordDnsBaked(config);
                }

                RecordBridgeTokenFingerprint(config);
                return;
            }

            // /data is a bind mount and the agent copies live on a named volume, so nothing is lost.
            _runtime.RemoveContainer(config.ContainerName);
        }

        // Creating a new container: normalize /data first (no container holds it open) so the hardened non-root
        // Podman app can write the bind-mounted DB + Data Protection keys (see PodmanDataDirNormalizer).
        NormalizeDataDirForPodman(config);

        // F5: in VPN mode the studio joins the sidecar's netns (NetworkContainer) instead of publishing its own
        // port, and is told where the sidecar's control server lives so its connectivity probe can reach it.
        var netns = StudioContainer.NetnsContainer(config);
        _log(netns is { Length: > 0 }
            ? $"Creating container '{config.ContainerName}' joined to the VPN sidecar '{netns}' netns ..."
            : $"Creating container '{config.ContainerName}' on port {config.Port} ...");

        // Backlog 160: the ONE place that creates a studio container and records what it baked, so a start and
        // a product update cannot inject different environments.
        _container.Create(config, integrity.KeyBase64, integrity.Digest);

        // Housekeeping for the recreate path: the image the removed container ran is superseded (the tag
        // already moved to the freshly loaded image), and without this an install accumulates one dangling
        // <none> image per reinstall (the 2026-07-28 disk-full incident). Only an image nothing names any
        // more goes: one somebody tagged on purpose, a backup for instance, is kept (backlog 165).
        if (supersededImageId is { Length: > 0 })
        {
            SupersededImage.Retire(_runtime, supersededImageId, _log, Short(supersededImageId));
        }
    }

    /// <summary>
    /// Podman-on-Windows rootless bind-mount fix: strips legacy WSL/DrvFs ownership metadata from the data files
    /// so the hardened non-root (uid 1000) container can write <c>/data</c> (see
    /// <see cref="PodmanDataDirNormalizer"/>). Podman-only and once-per-data-dir (marker-gated). Best-effort: a
    /// failure is logged and the launch proceeds - if the app then reports a read-only database the log points to
    /// the docs. Called only when no container holds the data dir open (before create / before start).
    /// </summary>
    private void NormalizeDataDirForPodman(LauncherConfig config)
    {
        if (!string.Equals(_runtime.Name, RuntimeNames.Podman, StringComparison.Ordinal)
            || PodmanDataDirNormalizer.AlreadyNormalized(config.DataDir))
        {
            return;
        }

        try
        {
            var normalized = PodmanDataDirNormalizer.Normalize(config.DataDir);
            if (normalized > 0)
            {
                _log($"Prepared {normalized} data file(s) for the rootless Podman bind mount " +
                    "(ownership metadata stripped so the non-root app can write /data).");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"Could not normalize the Podman data directory ({ex.Message}). If the app reports a " +
                "read-only database, see docs/environment.md.");
        }
    }

    /// <summary>
    /// Folds the installer's <c>installed-release.json</c> (version + variant) into launcher.json on first run
    /// (D03-T4d): the install scripts cannot safely pre-write launcher.json - the port/data-dir are chosen on the
    /// first <c>fravashi</c> run - so the version/variant land in a sibling marker the launcher merges in here. Once
    /// launcher.json carries a version this is a no-op. Never fails the caller.
    /// </summary>
    private LauncherConfig FoldInstalledRelease(LauncherConfig config)
    {
        // The installed IMAGE says which release it is (its org.fravashi.version label), and it is the truth about
        // what runs: nothing writes installed-release.json, so an install left the version unknown and the start
        // offered the very release it had just installed (seen with v0.9.44, 2026-10-02), and a reinstall of a newer
        // image never moved a version recorded earlier. It wins over anything recorded.
        // Its VARIANT likewise (backlog 251): a corp-broker image installed by hand was recorded as "full", the expected
        // shape said FRAVASHI_VARIANT=full, and the studio's integrity check stopped the container ten seconds after it
        // started. An image with no variant label (older builds) keeps what was recorded, then "full".
        var imageVersion = TryImageReleaseVersion(config);
        var imageVariant = TryImageVariant(config);
        var versionMoved = imageVersion is not null && !string.Equals(imageVersion, config.Version, StringComparison.Ordinal);
        var variantMoved = imageVariant is not null && !string.Equals(imageVariant, config.Variant, StringComparison.Ordinal);
        if (versionMoved || variantMoved)
        {
            var version = versionMoved ? imageVersion : config.Version;
            var variant = imageVariant ?? EffectiveVariant(config);
            RecordRelease(config, version, variant);
            if (versionMoved)
            {
                _log($"Recorded installed version {imageVersion} (read from the installed image) in launcher.json.");
            }

            if (variantMoved)
            {
                _log(string.IsNullOrWhiteSpace(config.Variant)
                    ? $"Recorded installed variant {imageVariant} (read from the installed image) in launcher.json."
                    : $"Recorded installed variant {imageVariant} (read from the installed image) in launcher.json, which said {config.Variant}.");
            }

            config = config with { Version = version, Variant = variant };
        }

        if (!string.IsNullOrWhiteSpace(config.Version))
        {
            return config;
        }

        try
        {
            var markerPath = Path.Combine(_configStore.ConfigDirectory, "installed-release.json");
            if (!File.Exists(markerPath))
            {
                return config;
            }

            var marker = JsonSerializer.Deserialize<InstalledReleaseMarker>(
                File.ReadAllText(markerPath), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (marker is null || string.IsNullOrWhiteSpace(marker.Version))
            {
                return config;
            }

            var folded = config with
            {
                Version = marker.Version,
                // A variant the image states beats the marker's, as it beats launcher.json's.
                Variant = imageVariant
                    ?? (string.IsNullOrWhiteSpace(marker.Variant) ? LauncherDefaults.DefaultVariant : marker.Variant),
            };
            RecordRelease(config, folded.Version!, folded.Variant!);
            _log($"Recorded installed version {folded.Version} ({folded.Variant}) in launcher.json.");
            return folded;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return config;
        }
    }

    /// <summary>
    /// Writes the installed version onto what is SAVED now, never the caller's copy: a start hands over the configuration
    /// it loaded before it created the container, and creating it saved what it baked, so saving the caller's copy wiped
    /// those records (seen installing v0.9.46: the upgrade endpoint's record was lost and the next start would have
    /// recreated the container for nothing). The caller keeps its own copy, with the version applied, for what it does next.
    /// </summary>
    private void RecordRelease(LauncherConfig fallback, string? version, string variant)
    {
        var saved = _configStore.Load() ?? fallback;
        _configStore.Save(saved with { Version = version, Variant = variant });
    }

    private ProductUpdater CreateUpdater(LauncherConfig config, Action<string>? log = null)
    {
        // One channel per host: launcher.json's manifestUrl when an owner set one, else the build's.
        var source = _manifestSourceFactory(
            string.IsNullOrWhiteSpace(config.ManifestUrl) ? LauncherDefaults.ManifestUrl : config.ManifestUrl!);
        // Backlog 160: the updater is handed THIS launcher's container creator, so an update inside the resident
        // fravashi.exe keeps the bridge and the app runner it holds, and an update from the console verb (which holds
        // neither) records their absence, which is what makes the next start see the container as stale.
        return new ProductUpdater(
            source, _runtime, _assetDownloader, _configStore, _container, log ?? _log, clock: _clock);
    }

    /// <summary>
    /// Evaluates the manifest and, when it was read, writes update-state.json. A channel that could not be checked,
    /// or a check that threw, is <see cref="UpdateKind.Unknown"/>, never "nothing newer" (backlog 230).
    /// </summary>
    private UpdateAvailability EvaluateUpdate(LauncherConfig config)
    {
        try
        {
            return CreateUpdater(config).EvaluateAsync(config).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log($"Update check skipped: {ex.Message}");
            var now = _clock.GetUtcNow();
            return UpdateAvailability.Unknown(new UpdateCheckFailure(ex.Message, now, now));
        }
    }

    private string CouldNotCheckWords(UpdateAvailability availability, LauncherConfig config) =>
        UpdateChannelWords.CouldNotCheck(
            availability.CouldNotCheck ?? new UpdateCheckFailure("no reason was recorded", _clock.GetUtcNow(), _clock.GetUtcNow()),
            DisplayVersion(config),
            _clock);

    private void AnnounceUpdate(UpdateAvailability availability, LauncherConfig config)
    {
        switch (availability.Kind)
        {
            case UpdateKind.Unknown:
                _log(CouldNotCheckWords(availability, config));
                break;
            case UpdateKind.Critical:
                _log($"A REQUIRED update to {ProductName.Short} {availability.TargetVersion} is available. {ProductName.Short} is locked until you run `{ProductName.Command} update`.");
                break;
            case UpdateKind.Available:
                _log($"An update to {ProductName.Short} {availability.TargetVersion} is available - run `{ProductName.Command} update` to install it.");
                break;
        }
    }

    /// <summary>Describes the F3 digest-pin state for <c>fravashi status</c> (resolved vs pinned).</summary>
    private string DescribeIntegrity(LauncherConfig config)
    {
        if (!_runtime.IsAvailable())
        {
            return "unknown (runtime unavailable)";
        }

        var resolved = _runtime.ResolveImageDigest(config.Image);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            return "report-only (image digest not resolvable on this runtime)";
        }

        if (string.IsNullOrWhiteSpace(config.PinnedImageDigest))
        {
            return $"unpinned - will pin {resolved} on next start";
        }

        return ImageIds.Same(config.PinnedImageDigest, resolved)
            ? $"digest pinned + matches ({Short(resolved)})"
            : $"MISMATCH - pinned {Short(config.PinnedImageDigest)} but loaded {Short(resolved)} (run `{ProductName.Command} repin` to re-pin the local image, or reinstall from a trusted release)";
    }

    private static string Short(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest))
        {
            return "";
        }

        var hex = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest["sha256:".Length..] : digest;
        return $"sha256:{hex[..Math.Min(12, hex.Length)]}";
    }

    /// <summary>Engines report ids with and without the <c>sha256:</c> prefix; compare the bare hex.</summary>
    private static string TrimDigest(string digest) =>
        digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest["sha256:".Length..] : digest;

    private static string DisplayVersion(LauncherConfig config) =>
        string.IsNullOrWhiteSpace(config.Version) ? "unknown" : config.Version!;

    /// <summary>The installed image's own release version, or null when the engine or the label cannot say.</summary>
    private string? TryImageReleaseVersion(LauncherConfig config)
    {
        try
        {
            return _runtime.IsAvailable() && _runtime.ImageReleaseVersion(config.Image) is { Length: > 0 } version
                ? version.Trim()
                : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>The installed image's own release variant, or null when the engine or the label cannot say (backlog 251).</summary>
    private string? TryImageVariant(LauncherConfig config)
    {
        try
        {
            return _runtime.IsAvailable() && _runtime.ImageVariant(config.Image) is { } variant
                && !string.IsNullOrWhiteSpace(variant)
                ? variant.Trim()
                : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>The variant launcher.json records, or <see cref="LauncherDefaults.DefaultVariant"/> when it records none.</summary>
    private static string EffectiveVariant(LauncherConfig config) =>
        string.IsNullOrWhiteSpace(config.Variant) ? LauncherDefaults.DefaultVariant : config.Variant!;

    /// <summary>
    /// The version to show in <c>fravashi status</c>: launcher.json's recorded version when set, else the version
    /// baked into the loaded image (the <c>org.fravashi.version</c> label), else "unknown". This keeps status
    /// from reporting "unknown" for a running container whose image plainly carries its version - the common
    /// case before <c>installed-release.json</c> has been folded into launcher.json.
    /// </summary>
    private string EffectiveVersion(LauncherConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.Version))
        {
            return config.Version!;
        }

        if (_runtime.IsAvailable() && _runtime.ImageReleaseVersion(config.Image) is { Length: > 0 } fromImage)
        {
            return fromImage;
        }

        return "unknown";
    }

    private static bool DefaultConfirm(string prompt)
    {
        Console.Write(prompt);
        var line = Console.ReadLine();
        return line is not null && line.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record InstalledReleaseMarker(string? Version, string? Variant, string? ImageRef);

    /// <summary>The F3 integrity env fields a freshly-created container is given: the HMAC key + pinned digest.</summary>
    /// <param name="KeyBase64">Base64 HMAC key, or null when integrity is not provisioned (report-only).</param>
    /// <param name="Digest">The pinned image digest, or null when it could not be resolved.</param>
    private readonly record struct IntegrityRunFields(string? KeyBase64, string? Digest)
    {
        /// <summary>No integrity provisioning - the container runs report-only.</summary>
        public static readonly IntegrityRunFields None = new(null, null);
    }

    /// <summary>Also the log-folder anchor for fravashi.exe's and the tray's entry points, hence public.</summary>
    public static string DefaultDataDir() => ProductHome.DataDir(ProductHome.Current);

    private string RuntimeNotInstalledMessage =>
        $"No container runtime was found (looking for '{_runtime.Name}'). Install a supported runtime - WSLC on " +
        $"Windows, or Docker / Podman on Windows, Linux, or macOS - start it, and run {ProductName.Short} again.";

    /// <summary>
    /// The refusal message for a verb that does NOT start anything (stop / logs / update): it only reports.
    /// An installed engine that is not answering must not be told to install a runtime it already has.
    /// </summary>
    private string RuntimeUnavailableMessage(RuntimeAvailability state) => state == RuntimeAvailability.EngineNotResponding
        ? $"The '{_runtime.Name}' runtime is installed but its engine is not answering. Run `{ProductName.Command}` to have {ProductName.Short} " +
            "try to start it, or start it yourself, then try this command again."
        : RuntimeNotInstalledMessage;
}

/// <summary>Launcher process exit codes.</summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int UsageError = 1;

    /// <summary>No usable container runtime (wslc / docker / podman) was found. Historically "DockerUnavailable".</summary>
    public const int RuntimeUnavailable = 2;
    public const int ImageMissing = 3;

    /// <summary>The F3 digest pin check failed - the resolved image digest did not match the pinned one (ADR 0007).</summary>
    public const int IntegrityRefused = 4;

    /// <summary>
    /// VPN mode is enabled but the detected runtime cannot run a VPN sidecar (wslc has no <c>--cap-add</c>/<c>--device</c>,
    /// ADR 0009 matrix). The VPN-required variant needs Docker or Podman (F5 / ADR 0008).
    /// </summary>
    public const int VpnUnsupportedRuntime = 5;

    /// <summary>
    /// Every host port the studio is allowed to serve on (<see cref="LauncherDefaults.StudioPorts"/>) is busy on
    /// this machine, so no port could be reserved (backlog item 7). The list is fixed because the identity
    /// provider matches the sign-in redirect URI exactly, port included; the log names the ports.
    /// </summary>
    public const int PortsUnavailable = 6;

    /// <summary>
    /// <c>fravashi restore</c> refused the archive (backlog 79): a bad or altered file, a hash that did not
    /// match, a path that left the data directory, or a backup from a NEWER Fravashi. Nothing was touched.
    /// </summary>
    public const int BackupRefused = 7;

    /// <summary>
    /// <c>fravashi restore</c> got past the refusals and then did not finish: the swap stopped part way, or the
    /// product did not come back up healthy on the restored data. The log names the pre-restore folder
    /// holding the state that was replaced.
    /// </summary>
    public const int RestoreIncomplete = 8;

    /// <summary>
    /// <c>fravashi update</c> could not check the update channel (backlog 230), so whether a newer release exists is
    /// unknown. Nothing was installed, and what the last successful check recorded (a required update included) stands.
    /// </summary>
    public const int UpdateChannelUnreachable = 9;

    /// <summary>
    /// <c>fravashi uninstall</c> (backlog 375) removed what it could, but a step failed (said on its own line): something
    /// of the install is left, and the data is kept if its copy did not complete.
    /// </summary>
    public const int UninstallIncomplete = 10;
}
