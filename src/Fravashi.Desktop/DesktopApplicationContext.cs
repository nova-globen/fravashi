using Fravashi.AppRunner;
using Fravashi.Bridge;
using Fravashi.Launcher;
using Microsoft.AspNetCore.Builder;
using Fravashi.Domain;

namespace Fravashi.Desktop;

/// <summary>
/// The GUI lifetime of the merged fravashi.exe: splash while <see cref="LauncherApp"/> runs on a
/// background thread (phases feed the splash), a healthz wait, then the borderless studio window.
/// The container keeps running when the window closes (ADR 0003 V3). `fravashi exit` sets the exit
/// event; a second GUI launch sets the activate event and this instance focuses/reopens instead.
///
/// Starting the product is ONE path (<see cref="EnsureProductAsync"/>), used by the launch, by every
/// tray action that shows the studio, and by a failed navigation's retry (backlog 15). Before that,
/// the tray only navigated a window at a URL, so stopping the product and choosing Open landed on the
/// browser engine's raw connection-refused page inside a window with no address bar. The path is
/// serialized and starts with a short healthz probe, so an already-running studio opens immediately
/// and two windows retrying at once never run two launcher starts against one container.
/// </summary>
internal sealed class DesktopApplicationContext : ApplicationContext, IStudioRecovery, IWorkspaceWindowHost
{
    private readonly ParsedCommand _parsed;
    private readonly string _configPath;
    private readonly LauncherConfigStore _configStore;
    private readonly LauncherLog _launcherLog;
    private readonly SplashForm _splash;
    private readonly SynchronizationContext _ui;
    private readonly EventWaitHandle _exitEvent;
    private readonly EventWaitHandle _activateEvent;
    private readonly RegisteredWaitHandle _exitWait;
    private readonly RegisteredWaitHandle _activateWait;
    private readonly WorkspaceRegistry _workspaces;

    /// <summary>
    /// The sign-in hand-over (backlog 94): what the studio last said about its sign-in, and at most one session the
    /// launcher obtained for it, in memory, handed over once through the bridge.
    /// </summary>
    private readonly IdentityHandover _identity;

    /// <summary>
    /// Every open studio window and the workspace it shows (backlog 29): one window per folder, at most
    /// one unbound (home) window. Re-keyed on every adoption, switch and close, never only at creation.
    /// </summary>
    private readonly WorkspaceWindowMap<MainWindow> _windows = new();

    /// <summary>Serializes the start sequence: one launcher run at a time, whoever asked for it.</summary>
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private WebApplication? _bridgeApp;
    private WebApplication? _appRunnerApp;
    private System.Threading.Timer? _appRunnerSweep;
    private WebApplication? _upgradeApp;
    private System.Threading.Timer? _dnsRecoveryTimer;
    private DesktopTrayIcon? _tray;
    private string? _studioUrl;
    private BridgeLaunch? _bridgeLaunch;
    private BridgeWorkspace? _initialWorkspace;
    private ResidentShutdown? _shutdown;
    private bool _residencyTipShown;

    /// <summary>What the launcher recognised about the last failed start, for the box that reports it (backlog 252).</summary>
    private volatile string? _lastStartExplanation;

    private const int PreferredBridgePort = 9091;

    /// <summary>Backlog 87: clear of the studio's 9090 to 9093 and the bridge's 9091.</summary>
    private const int PreferredAppRunnerPort = 9095;

    /// <summary>Backlog 157: clear of the studio's, the bridge's and the app runner's.</summary>
    private const int PreferredUpgradePort = 9097;

    /// <summary>How long a "is the studio already up" probe waits before we assume it has to be started.</summary>
    private static readonly TimeSpan StudioProbeTimeout = TimeSpan.FromSeconds(2);

    /// <param name="splash">The splash <see cref="Program"/> already showed, before anything slow ran (backlog 379).</param>
    /// <param name="splashAfter">How long after the process started it was on screen, said once in the launcher log.</param>
    public DesktopApplicationContext(
        ParsedCommand parsed, EventWaitHandle exitEvent, EventWaitHandle activateEvent, SplashForm splash, TimeSpan splashAfter)
    {
        _parsed = parsed;
        _exitEvent = exitEvent;
        _activateEvent = activateEvent;
        _configPath = parsed.ConfigPath ?? LauncherConfigStore.DefaultPath;
        _configStore = new LauncherConfigStore(_configPath);
        // Backlog 14: the bridge's grant list lives beside bridge.token so a fresh process still
        // serves the workspace ids the studio's recents hold (the id is a hash of the path).
        _workspaces = new WorkspaceRegistry(new WorkspaceRegistryStore(
            System.IO.Path.Combine(_configStore.ConfigDirectory, WorkspaceRegistryStore.FileName)));
        _identity = new IdentityHandover(System.IO.Path.Combine(_configStore.ConfigDirectory, IdentityHandover.FileName));
        var storedConfig = _configStore.Load();

        // GUI mode has no console; launcher activity goes to <data dir>/logs only (directive
        // 2026-07-26). The window log shares the same folder, handed over in-process now.
        _launcherLog = new LauncherLog(
            string.IsNullOrWhiteSpace(storedConfig?.DataDir) ? LauncherApp.DefaultDataDir() : storedConfig!.DataDir!,
            _ => { });
        WindowLog.Init(_launcherLog.Directory);
        _launcherLog.Line(LaunchTiming.SplashShown(splashAfter));

        _splash = splash;
        // The WinForms context exists once the first control is created (the splash, in Program); capture it for
        // marshalling that outlives the splash.
        _ui = SynchronizationContext.Current!;

        _exitWait = ThreadPool.RegisterWaitForSingleObject(
            _exitEvent, (_, _) => OnUi(ExitApp), null, Timeout.Infinite, executeOnlyOnce: true);
        _activateWait = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent, (_, _) => OnUi(ActivateWindow), null, Timeout.Infinite, executeOnlyOnce: false);

        Task.Run(StartProduct);
    }

    private void OnUi(Action action) => _ui.Post(_ => action(), null);

    private void StartProduct()
    {
        try
        {
            _bridgeLaunch = StartBridge();
            StartDnsRecovery();
            var onboarding = _parsed.Verb == DesktopVerb.Onboarding;
            int exit;

            // The launch takes the same gate as every later start: a second `fravashi` launch arriving
            // mid-startup signals the activate event, and without this its open path would run a
            // second launcher start against the container this one is still creating.
            _startGate.Wait();
            try
            {
                if (SignInPlan(StudioIsServing()) is { } signIn)
                {
                    SignInBeforeStart(signIn);
                }

                exit = RunStartSequence(onboarding);
                if (exit == ExitCodes.Success)
                {
                    FirstRunHint.ShowOnce(_configPath, _launcherLog.Line);
                    // Queued while the gate is still held, so any UI work a waiting tray action posts
                    // lands behind it and finds this window already open.
                    OnUi(() =>
                    {
                        // Backlog 4: the splash fades out above the window that opens under it.
                        _splash.FadeAway();
                        // Plan 2b-ii: a folder launch opens THAT workspace's window; a bare launch
                        // opens the welcome page (recents + open-folder).
                        if (_initialWorkspace is { } workspace)
                        {
                            OpenWorkspaceWindow(workspace);
                        }
                        else
                        {
                            OpenWindow(UrlFor(onboarding ? StudioTarget.Onboarding : StudioTarget.Studio));
                        }
                    });
                }
            }
            finally
            {
                _startGate.Release();
            }

            if (exit != ExitCodes.Success)
            {
                // The launch has no window and no tray to fall back to, so a failure here ends the
                // process; a failure AFTER it is resident only reports (see ReportStartFailure).
                OnUi(() => FailAndExit(exit));
            }
        }
        catch (Exception ex)
        {
            // Backlog 252: an engine failure the launcher recognised is said on its own line and on the screen, not
            // only inside the exception text below it.
            var explained = EngineFailures.ExplanationIn(ex);
            if (explained is not null)
            {
                _launcherLog.Line(explained);
            }

            _launcherLog.Line($"fravashi failed to start: {ex}");
            OnUi(() => FailAndExit(ExitCodes.UsageError, explained));
        }
    }

    /// <summary>
    /// The launcher's own start sequence, splash phases and healthz wait included. Idempotent by
    /// construction (an already-running container is left alone by <c>EnsureContainer</c>), which is
    /// what lets the tray and a failed navigation's retry call the same code as the launch. Runs on a
    /// worker thread; returns an <see cref="ExitCodes"/> value.
    /// </summary>
    private int RunStartSequence(bool onboarding)
    {
        string? opened = null;
        var app = new LauncherApp(
            ContainerRuntimeDetector.Detect(_configStore.Load()),
            _configStore,
            launcherDirectory: AppContext.BaseDirectory,
            log: _launcherLog.Line,
            openBrowser: url => opened = url,
            // The merged exe IS the tray; never spawn the retired tray executable.
            tray: new NoopTrayControl(),
            containerNameOverride: _parsed.ContainerName,
            phase: _splash.SetPhase,
            bridge: _bridgeLaunch);

        var exit = onboarding ? app.Onboarding() : app.Run();
        if (exit != ExitCodes.Success)
        {
            return exit;
        }

        if (opened is null)
        {
            // The launcher reported success without ever naming a URL: refuse to open a window on a
            // guess rather than pretend the studio is somewhere.
            _launcherLog.Line("The launcher reported success but named no studio URL; not opening a window.");
            return ExitCodes.UsageError;
        }

        _splash.SetPhase(LaunchPhase.WaitingForStudio);
        var ready = new StudioReadinessProbe()
            .WaitUntilReadyAsync(LauncherUrls.Base(StudioPort), TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(500))
            .GetAwaiter().GetResult();
        if (!ready)
        {
            _launcherLog.Line(
                "The studio did not answer /healthz within 60s; opening the window anyway (reconnect.js keeps waiting in-page).");

            // Backlog 252: a running container on a ROOTFUL Podman machine never answers from Windows. Asked of the
            // engine, said in the log and on screen; the window still opens, as before.
            if (app.StudioUnansweredAdvice() is { } advice)
            {
                _launcherLog.Line(advice);
                OnUi(() => MessageBox.Show(advice, ProductName.Short, MessageBoxButtons.OK, MessageBoxIcon.Warning));
            }
        }

        else
        {
            // The bridge release: once the studio under the new names answers healthy, the former image tag goes, and once
            // nothing of the former names is left launcher.json says so and later starts ask the engine nothing (#384).
            FormerInstallMigration.RetireFormerImage(_configStore, ContainerRuntimeDetector.Detect, _launcherLog.Line);
        }

        _splash.SetPhase(LaunchPhase.Ready);
        // Always the BASE, never the URL the launcher happened to open: `fravashi onboarding` opens a page
        // URL, and appending "/code?ws=..." to it produced nonsense the moment a workspace window was
        // opened afterwards.
        _studioUrl = LauncherUrls.Base(StudioPort);
        return ExitCodes.Success;
    }

    /// <summary>
    /// Backlog 94 (ADR 0026 as ruled): sign the person in BEFORE the container starts, in their own browser, and
    /// leave the result with the bridge for the studio to take once. Only when the studio would otherwise have to
    /// show its own sign-in page (<see cref="LauncherSignIn.SkipReason"/>), and never fatal: whatever goes wrong,
    /// the start goes on and the studio's page is the fallback. Runs on a worker thread under the start gate.
    /// </summary>
    private SignInCoordinates? SignInPlan(bool studioAnswers)
    {
        var coordinates = _identity.Coordinates();
        if (LauncherSignIn.SkipReason(coordinates is not null, coordinates?.SessionUsable ?? false, studioAnswers) is { } skip)
        {
            _launcherLog.Line($"Sign-in before start: not needed, {skip}.");
            return null;
        }

        return coordinates;
    }

    private void SignInBeforeStart(SignInCoordinates coordinates)
    {
        try
        {
            _splash.SetPhase(LaunchPhase.SigningIn);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var result = new LauncherSignIn(http, url => BrowserLauncher.Open(url, _launcherLog.Line))
                .SignInAsync(
                    new LauncherSignInTarget(coordinates.Issuer, coordinates.ClientId, coordinates.Scope, StudioPort),
                    LauncherSignIn.DefaultBrowserWait,
                    CancellationToken.None,
                    // Logged when the browser REALLY opens, never before: on 2026-09-23 this line was written ahead of
                    // a port check that then refused, and read as a browser that never appeared.
                    () => _launcherLog.Line($"Sign-in before start: opening the browser at {coordinates.Issuer}."))
                .GetAwaiter().GetResult();
            if (result.Session is { } session)
            {
                _identity.Offer(new HandedIdentity(
                    session.Issuer, session.ClientId, session.AccessToken, session.RefreshToken, session.IdToken,
                    session.ExpiresUtc));
                _launcherLog.Line("Sign-in before start: signed in; the studio takes the session once and checks it with the provider.");
            }
            else
            {
                _launcherLog.Line($"Sign-in before start: not completed, {result.Outcome}. The studio will ask for sign-in itself.");
            }
        }
        catch (Exception ex)
        {
            _launcherLog.Line($"Sign-in before start failed ({ex.GetType().Name}: {ex.Message}); the studio will ask for sign-in itself.");
        }
    }

    /// <summary>
    /// Whether the studio is already up, for the sign-in decision. A held studio port counts as up: one healthz request
    /// with a two-second budget, sent moments after fravashi.exe starts, answered "no" for a studio that was running on
    /// 2026-09-23, and the launcher then went on to a sign-in the busy port could never receive.
    /// </summary>
    private bool StudioIsServing() => StudioAnswers() || !PortSelector.IsTcpPortFree(StudioPort);

    /// <summary>The reserved host port; read from launcher.json each time, since a first run picks it.</summary>
    private int StudioPort => _configStore.Load()?.Port ?? LauncherDefaults.PreferredPort;

    private string StudioBase => (_studioUrl ?? LauncherUrls.Base(StudioPort)).TrimEnd('/');

    private string UrlFor(StudioTarget target) => target == StudioTarget.Onboarding
        ? LauncherUrls.Onboarding(StudioPort)
        : $"{StudioBase}/welcome";

    /// <summary>
    /// One short <c>/healthz</c> probe. This is the only honest "is it up" question: a URL says
    /// nothing, a running container may still be booting, and a stopped one has to be started.
    /// </summary>
    private bool StudioAnswers()
    {
        try
        {
            return new StudioReadinessProbe()
                .WaitUntilReadyAsync(LauncherUrls.Base(StudioPort), StudioProbeTimeout, TimeSpan.FromMilliseconds(250))
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _launcherLog.Line($"The studio readiness probe failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Makes the studio answer, starting the product when it does not. Serialized, so a tray Open and a
    /// window retry arriving together run one launcher start between them. Returns
    /// <see cref="ExitCodes.Success"/> when the studio is up; the caller decides how to report a failure.
    /// </summary>
    private Task<int> EnsureProductAsync(bool onboarding) => Task.Run(async () =>
    {
        await _startGate.WaitAsync().ConfigureAwait(false);
        _lastStartExplanation = null;
        try
        {
            if (StudioAnswers())
            {
                return ExitCodes.Success;
            }

            // The splash opens on the step this start really begins with, so its bar never steps backwards.
            var signIn = SignInPlan(studioAnswers: false);
            OnUi(() => ShowStartupSplash(signIn is null ? LaunchPhase.CheckingRuntime : LaunchPhase.SigningIn));
            if (signIn is not null)
            {
                SignInBeforeStart(signIn);
            }

            return RunStartSequence(onboarding);
        }
        catch (Exception ex)
        {
            // Backlog 252: the recognised cause reaches the screen (ReportStartFailure), not only this log.
            _lastStartExplanation = EngineFailures.ExplanationIn(ex);
            if (_lastStartExplanation is { } explained)
            {
                _launcherLog.Line(explained);
            }

            _launcherLog.Line($"fravashi could not start the product: {ex}");
            return ExitCodes.UsageError;
        }
        finally
        {
            _startGate.Release();
            OnUi(() => _splash.Hide());
            _tray?.RefreshState();
        }
    });

    /// <summary>
    /// The one way the studio is opened after launch: the tray's Open / Start / Onboarding items and
    /// the tray double-click. Starts the product first when it is not answering, with the same splash
    /// phases and healthz wait as the launch (backlog 15).
    /// </summary>
    private void OpenStudio(StudioTarget target) => _ = OpenStudioAsync(target);

    private async Task OpenStudioAsync(StudioTarget target)
    {
        var exit = await EnsureProductAsync(target == StudioTarget.Onboarding).ConfigureAwait(false);
        OnUi(() =>
        {
            if (exit != ExitCodes.Success)
            {
                ReportStartFailure(exit);
                return;
            }

            ShowStudio(target);
        });
    }

    /// <summary>
    /// A waiting toast's own click (backlog 304, ADR 0031 decision 12): unlike <see cref="ShowStudio"/>, this
    /// ALWAYS navigates whichever window it focuses to the home page, because that page's waiting list is the
    /// one thing this click is about, and a window already open on another page would otherwise show the
    /// person nothing of what they clicked.
    /// </summary>
    private void OpenWaitingList() => _ = OpenWaitingListAsync();

    private async Task OpenWaitingListAsync()
    {
        var exit = await EnsureProductAsync(onboarding: false).ConfigureAwait(false);
        OnUi(() =>
        {
            if (exit != ExitCodes.Success)
            {
                ReportStartFailure(exit);
                return;
            }

            // The unbound window can never hold a workspace's Code page (that needs ?ws=), so it never has
            // dirty editor buffers and a plain Navigate is safe; a workspace-bound window might, so it goes
            // through the same hot-exit flush CloseWorkspace and SwitchWorkspace already use before a
            // host-driven navigation replaces its page (ADR 0016 addendum).
            if (_windows.Unbound is { IsDisposed: false } unbound)
            {
                unbound.Navigate(WorkspaceNavigation.HomeUrl(StudioBase));
                Focus(unbound);
                return;
            }

            var window = _windows.All.FirstOrDefault(w => !w.IsDisposed);
            if (window is null)
            {
                OpenWindow(WorkspaceNavigation.HomeUrl(StudioBase));
                return;
            }

            _ = window.NavigateAfterFlushAsync(WorkspaceNavigation.HomeUrl(StudioBase));
            Focus(window);
        });
    }

    private void ShowStartupSplash(LaunchPhase first)
    {
        if (_splash.IsDisposed)
        {
            return;
        }

        _splash.SetPhase(first);
        _splash.Show();
        _splash.BringToFront();
        _splash.Activate();
    }

    /// <summary>
    /// A start that failed AFTER fravashi.exe is resident: say so and keep the tray and any open window alive,
    /// unlike <see cref="FailAndExit"/>, which is the launch path's only option.
    /// </summary>
    private void ReportStartFailure(int exitCode)
    {
        _splash.Hide();
        var reason = _lastStartExplanation is { } explained
            ? $"{ProductName.Short} could not be started.\n\n{explained}"
            : $"{ProductName.Short} could not be started (exit code {exitCode}).";
        MessageBox.Show(
            $"{reason}\n\nSee the launcher log for details:\n{_launcherLog.Directory}",
            ProductName.Short, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// <summary>
    /// Starts the host bridge (plan 2a) on the addresses the engine's containers can reach (<see cref="ContainerHostEndpoint"/>):
    /// loopback alone on wslc, which forwards its host name there, and loopback plus the WSL gateway address on Docker
    /// and Podman. Never fatal: a failed bridge start means the studio runs without host workspaces, honestly logged.
    /// Returns the container-facing launch info, or null when a container would have no way in.
    /// </summary>
    private BridgeLaunch? StartBridge()
    {
        try
        {
            var token = BridgeAccessToken.GetOrCreate(_configStore.ConfigDirectory);
            // Before the listener exists, so the very first request already sees the remembered
            // folders (backlog 14). Folders that are gone are pruned, never re-granted.
            var restored = _workspaces.Rehydrate();
            if (restored > 0)
            {
                _launcherLog.Line($"Restored {restored} remembered workspace folder(s).");
            }

            var config = _configStore.Load();
            var port = PortSelector.Select(config?.BridgePort ?? PreferredBridgePort, PortSelector.IsTcpPortFree);
            if (config is not null && config.BridgePort != port)
            {
                _configStore.Save(config with { BridgePort = port });
            }

            var runtime = ContainerRuntimeDetector.Detect(config);
            var endpoint = ContainerHostEndpoint.For(runtime.Name, () => WslHostAddress.Resolve());
            var urls = endpoint?.ListenUrls(port) ?? [$"http://{ContainerHostEndpoint.Loopback}:{port}"];

            _bridgeApp = BridgeApplication.Create(
                new BridgeServerOptions(
                    token, urls, PickFolder: PickFolderOnUiAsync, Identity: _identity, ShowToast: ShowToastAsync),
                _workspaces);
            _bridgeApp.StartAsync().GetAwaiter().GetResult();
            _launcherLog.Line($"Host bridge listening on {string.Join(", ", urls)}.");

            if (_parsed.OpenPath is { Length: > 0 } openPath)
            {
                try
                {
                    _initialWorkspace = _workspaces.Register(openPath);
                    _launcherLog.Line(
                        $"Registered workspace '{_initialWorkspace.Name}' ({_initialWorkspace.RootPath}).");
                }
                catch (WorkspaceUnreachableException)
                {
                    OnUi(() => MessageBox.Show(
                        $"The folder passed to --open cannot be reached right now:\n{openPath}\n\n"
                        + "If it is on a network drive or a removable disk, connect it and try again.",
                        ProductName.Short, MessageBoxButtons.OK, MessageBoxIcon.Warning));
                }
                catch (DirectoryNotFoundException)
                {
                    OnUi(() => MessageBox.Show(
                        $"The folder passed to --open does not exist:\n{openPath}",
                        ProductName.Short, MessageBoxButtons.OK, MessageBoxIcon.Warning));
                }
            }

            if (endpoint is null)
            {
                _launcherLog.Line(
                    "No WSL interface address found; the bridge serves the host only (no container endpoint injected).");
                return null;
            }

            var appRunner = StartAppRunner(endpoint, runtime);
            var upgrade = StartUpgradeHost(endpoint);
            return new BridgeLaunch(
                endpoint.ContainerUrl(port), token, appRunner?.Url, appRunner?.Token, upgrade?.Url, upgrade?.Token);
        }
        catch (Exception ex)
        {
            _launcherLog.Line($"The host bridge could not start: {ex.Message}. Continuing without it.");
            return null;
        }
    }

    /// <summary>
    /// Starts the app runner beside the bridge (backlog 87, ADR 0027): the same addresses, a port of its own and a
    /// token of its own. It drives the engine the launcher uses, and only docker or podman: wslc cannot run the relay
    /// a run needs, so there it is not started and the studio says a scenario's application is unavailable. Never
    /// fatal. Its sweep runs now and every minute, removing whatever of its own is past its deadline, and a run
    /// whose writable layer passed its ceiling.
    /// </summary>
    private (string Url, string Token)? StartAppRunner(ContainerHostEndpoint endpoint, IContainerRuntime runtime)
    {
        try
        {
            var config = _configStore.Load();
            if (runtime.Name is not (RuntimeNames.Podman or RuntimeNames.Docker))
            {
                _launcherLog.Line($"App runner: not started, because the '{runtime.Name}' engine cannot run a run's relay.");
                return null;
            }

            var port = PortSelector.Select(config?.AppRunnerPort ?? PreferredAppRunnerPort, PortSelector.IsTcpPortFree);
            if (config is not null && config.AppRunnerPort != port)
            {
                _configStore.Save(config with { AppRunnerPort = port });
            }

            var token = AppRunnerToken.GetOrCreate(_configStore.ConfigDirectory);
            var runner = new AppRunner.AppRunner(
                new CliEngine(runtime.Name),
                new AppRunnerOptions
                {
                    ScratchDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fravashi-apprunner"),
                    // The studio's own image carries socat, so the relay pulls nothing.
                    RelayImage = config?.Image ?? LauncherDefaults.Image,
                    StudioContainer = config?.ContainerName ?? LauncherDefaults.ContainerName,
                    Owner = "fravashi",
                },
                id => _workspaces.TryGet(id)?.RootPath);
            var urls = endpoint.ListenUrls(port);
            _appRunnerApp = AppRunnerApplication.Create(token, urls, runner);
            _appRunnerApp.StartAsync().GetAwaiter().GetResult();
            _launcherLog.Line($"App runner listening on {string.Join(", ", urls)} ({runtime.Name}).");

            // The bridge release: runs an app runner from before the internal rename left behind carry its former label.
            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (var item in await runner.RemoveFormerLeftoversAsync(CancellationToken.None).ConfigureAwait(false))
                    {
                        _launcherLog.Line($"App runner: could not remove the former {item}; it is tried again at the next start.");
                    }
                }
                catch (Exception ex)
                {
                    _launcherLog.Line($"App runner: the former leftovers could not be listed ({ex.Message}).");
                }
            });

            _appRunnerSweep = new System.Threading.Timer(
                _ =>
                {
                    try
                    {
                        var stuck = runner.SweepAsync(CancellationToken.None).GetAwaiter().GetResult();
                        foreach (var item in stuck)
                        {
                            _launcherLog.Line($"App runner: could not remove {item}; it is reported, not retried in silence.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _launcherLog.Line($"App runner sweep failed: {ex.Message}");
                    }
                },
                null,
                TimeSpan.Zero,
                TimeSpan.FromMinutes(1));
            return (endpoint.ContainerUrl(port), token);
        }
        catch (Exception ex)
        {
            _launcherLog.Line($"The app runner could not start: {ex.Message}. Scenarios that use the application will say so.");
            return null;
        }
    }

    /// <summary>
    /// Starts the upgrade operation beside the bridge (backlog 157): the same addresses, a port and a token of its own.
    /// It checks and applies exactly what <c>fravashi update</c> would, but through THIS launcher, which holds the bridge,
    /// so the replacement studio keeps its workspaces; and it applies under the start gate, so it never races a start.
    /// The studio that asks is the container being replaced, so it gets its answer at once and reads the outcome from
    /// the status after it comes back. Never fatal.
    /// </summary>
    private (string Url, string Token)? StartUpgradeHost(ContainerHostEndpoint endpoint)
    {
        try
        {
            var config = _configStore.Load();
            var port = PortSelector.Select(config?.UpgradePort ?? PreferredUpgradePort, PortSelector.IsTcpPortFree);
            if (config is not null && config.UpgradePort != port)
            {
                // Kept once chosen (its URL is baked into the container), and read by a console `fravashi update`.
                _configStore.Save(config with { UpgradePort = port });
            }

            var token = StudioUpgradeToken.GetOrCreate(_configStore.ConfigDirectory);
            var runner = new StudioUpgradeRunner(
                _ => Task.Run(() =>
                {
                    var (installed, offered, critical, said) = UpgradeLauncher().CheckForUpgrade();
                    return new StudioUpgradeOffer(installed, offered, critical, said);
                }),
                (version, _) => Task.Run(() =>
                {
                    _startGate.Wait();
                    try
                    {
                        var (applied, said) = UpgradeLauncher().ApplyUpgrade(version);
                        return new StudioUpgradeOutcome(applied, said);
                    }
                    finally
                    {
                        _startGate.Release();
                    }
                }),
                log: _launcherLog.Line);
            var urls = endpoint.ListenUrls(port);
            _upgradeApp = StudioUpgradeApplication.Create(token, urls, runner);
            _upgradeApp.StartAsync().GetAwaiter().GetResult();
            _launcherLog.Line($"Upgrade operation listening on {string.Join(", ", urls)}.");
            return (endpoint.ContainerUrl(port), token);
        }
        catch (Exception ex)
        {
            _launcherLog.Line($"The upgrade operation could not start: {ex.Message}. `{ProductName.Command} update` still works from a console.");
            return null;
        }
    }

    /// <summary>
    /// Backlog 218: starts the periodic poll that notices a host DNS change and restarts the studio container
    /// so it recovers without a person running <c>fravashi stop</c>/<c>fravashi</c>. All the logic lives in
    /// <see cref="StudioDnsRecovery"/> (tested in <c>Fravashi.Launcher.Tests</c>); this is wiring only. Only
    /// started when the detected runtime actually needs it (<see cref="StudioDnsRecovery.AppliesTo"/>). Never
    /// fatal: a failure here means a host network change still needs a manual restart, same as before.
    /// </summary>
    private void StartDnsRecovery()
    {
        try
        {
            var runtime = ContainerRuntimeDetector.Detect(_configStore.Load());
            if (!StudioDnsRecovery.AppliesTo(runtime.Name))
            {
                return;
            }

            var recovery = new StudioDnsRecovery(
                runtime,
                _configStore,
                _launcherLog.Line,
                // Backlog 218: the message is the launcher's own decision (StudioDnsRecovery.RestartExplanation);
                // this is wiring only - relaying it to whichever tray icon exists when it actually happens.
                onRestarted: reason => OnUi(() => _tray?.ShowBalloon(reason)));
            _dnsRecoveryTimer = new System.Threading.Timer(
                _ => recovery.Poll(HostDnsServers.Resolve(), DateTimeOffset.UtcNow),
                null,
                StudioDnsRecovery.DefaultPollInterval,
                StudioDnsRecovery.DefaultPollInterval);
        }
        catch (Exception ex)
        {
            _launcherLog.Line($"DNS recovery could not start: {ex.Message}. A host network change will need `{ProductName.Command} stop` then `{ProductName.Command}`.");
        }
    }

    /// <summary>The launcher an upgrade runs through: this fravashi.exe's, holding the bridge, the app runner and this.</summary>
    private LauncherApp UpgradeLauncher() => new(
        ContainerRuntimeDetector.Detect(_configStore.Load()),
        _configStore,
        launcherDirectory: AppContext.BaseDirectory,
        log: _launcherLog.Line,
        openBrowser: _ => { },
        tray: new NoopTrayControl(),
        containerNameOverride: _parsed.ContainerName,
        bridge: _bridgeLaunch);

    /// <summary>
    /// The bridge's folder-picker callback (plan 2b-ii): the dialog must run on the UI thread; the
    /// studio waits on the endpoint until the user picks or cancels.
    /// </summary>
    private Task<string?> PickFolderOnUiAsync()
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        OnUi(() =>
        {
            try
            {
                using var dialog = new FolderBrowserDialog
                {
                    Description = $"Open a folder as a {ProductName.Short} workspace",
                    UseDescriptionForTitle = true,
                    ShowNewFolderButton = true,
                };
                var result = _windows.Unbound is { IsDisposed: false } owner
                    ? dialog.ShowDialog(owner)
                    : dialog.ShowDialog();
                completion.TrySetResult(result == DialogResult.OK ? dialog.SelectedPath : null);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });
        return completion.Task;
    }

    /// <summary>
    /// The bridge's waiting-notification callback (backlog 304, ADR 0031 decision 12): raises the message as a
    /// tray balloon, the same one DNS recovery and the residency tip already use, but with a click of its own
    /// (<see cref="OpenWaitingList"/>) so only THIS balloon's click navigates to the home page; the others
    /// keep doing nothing when clicked. Never fatal; a tray that has not been created yet (no window has ever
    /// opened in this process) has nothing to raise it through.
    /// </summary>
    private Task ShowToastAsync(string message)
    {
        OnUi(() => _tray?.ShowBalloon(message, OpenWaitingList));
        return Task.CompletedTask;
    }

    /// <summary>Opens the unbound (home) window, or focuses the one that is already open.</summary>
    private void OpenWindow(string url)
    {
        _splash.FadeAway();
        if (_windows.Unbound is { IsDisposed: false } existing)
        {
            existing.Navigate(url);
            Focus(existing);
            return;
        }

        var window = CreateWindow(url, workspaceId: null, workspaceName: null);
        _windows.TryAddUnbound(window);
        window.Show();
        _tray ??= new DesktopTrayIcon(_configStore, _launcherLog, OpenStudio, ExitApp);
    }

    /// <summary>Focus-or-create the window for a workspace (never two windows for one folder).</summary>
    private void OpenWorkspaceWindow(BridgeWorkspace workspace)
    {
        _splash.FadeAway();
        if (_windows.OwnerOf(workspace.Id) is { IsDisposed: false } existing)
        {
            Focus(existing);
            return;
        }

        var window = CreateWindow(
            WorkspaceNavigation.WorkspaceUrl(StudioBase, workspace.Id), workspace.Id, workspace.Name);
        _windows.Rebind(window, workspace.Id);
        window.Show();
        _tray ??= new DesktopTrayIcon(_configStore, _launcherLog, OpenStudio, ExitApp);
    }

    private MainWindow CreateWindow(string url, string? workspaceId, string? workspaceName)
    {
        var window = new MainWindow(url, WindowAccessToken.TryRead(_configStore.ConfigDirectory), workspaceId, workspaceName)
        {
            Recovery = this,
            WorkspaceHost = this,
        };
        // Forgotten by identity, wherever the map filed it LAST: keying this on the id the window was
        // created with is exactly the pre-29 bug that stranded adopted windows.
        window.FormClosed += (_, _) =>
        {
            _windows.Remove(window);
            OnAnyWindowClosed();
        };
        return window;
    }

    private static void Focus(MainWindow window)
    {
        if (window.WindowState == FormWindowState.Minimized)
        {
            window.WindowState = FormWindowState.Normal;
        }

        window.Activate();
    }

    /// <summary>The name a caption shows for a workspace id: the host registry's, else the id itself.</summary>
    private string NameOf(string workspaceId) => _workspaces.TryGet(workspaceId)?.Name ?? workspaceId;

    /// <inheritdoc />
    bool IWorkspaceWindowHost.IsShownByAnotherWindow(MainWindow window, string workspaceId) =>
        _windows.OwnerOf(workspaceId) is { IsDisposed: false } owner && !ReferenceEquals(owner, window);

    /// <inheritdoc />
    void IWorkspaceWindowHost.Adopt(MainWindow window, string workspaceId)
    {
        var result = _windows.Rebind(window, workspaceId);
        if (result.Outcome == WindowRebindOutcome.ShownElsewhere)
        {
            // The navigation rule refuses this case before adopting; reaching it means the map moved in
            // between. Keep the invariant (one window per folder) and say so rather than double-file it.
            _launcherLog.Line($"Workspace {workspaceId} is already shown by another window; not re-keying this one.");
            return;
        }

        window.ShowWorkspace(workspaceId, NameOf(workspaceId));
        _launcherLog.Line($"A window adopted workspace '{NameOf(workspaceId)}' ({workspaceId}).");
    }

    /// <inheritdoc />
    void IWorkspaceWindowHost.FocusWorkspace(string workspaceId)
    {
        if (_windows.OwnerOf(workspaceId) is { IsDisposed: false } owner)
        {
            Focus(owner);
        }
    }

    /// <summary>
    /// <see cref="IWorkspaceWindowHost.CloseWorkspace"/>. Deliberately a WINDOW operation only (backlog
    /// 29's decision): the folder stays in the bridge's grant list and workspaces.json, its contained copy
    /// keeps syncing and its agent session keeps running, so reopening it from the recents is instant. A
    /// full close that revokes host access (registry unregister, a bridge DELETE, sync and session
    /// teardown) is out of scope here; none of that machinery exists yet.
    /// </summary>
    void IWorkspaceWindowHost.CloseWorkspace(MainWindow window)
    {
        if (window.IsDisposed)
        {
            return;
        }

        var closed = window.WorkspaceId;
        var result = _windows.Unbind(window);
        switch (result.Outcome)
        {
            case WindowUnbindOutcome.CloseInFavourOf:
                // At most one unbound window: this one closes (hot exit flushes on the way) and the home
                // window that already exists comes forward instead.
                _launcherLog.Line($"Closed workspace {closed}; a home window is already open, so this window closes.");
                window.Close();
                if (result.Survivor is { IsDisposed: false } survivor)
                {
                    Focus(survivor);
                }

                break;

            default:
                _launcherLog.Line($"Closed workspace {closed ?? "(none)"}; the window returns to the home surface.");
                window.ShowWorkspace(null, null);
                _ = window.NavigateAfterFlushAsync(WorkspaceNavigation.HomeUrl(StudioBase));
                break;
        }
    }

    /// <summary><see cref="IWorkspaceWindowHost.SwitchWorkspace"/>.</summary>
    void IWorkspaceWindowHost.SwitchWorkspace(MainWindow window, string workspaceId)
    {
        if (window.IsDisposed)
        {
            return;
        }

        // Only a folder the host has granted: the studio re-registers a recent before asking (backlog 14),
        // so an unknown id here is a stale or forged request, not a folder to open.
        if (_workspaces.TryGet(workspaceId) is not { } workspace)
        {
            _launcherLog.Line($"Switch to workspace {workspaceId} refused: the host has not registered it.");
            return;
        }

        var result = _windows.Rebind(window, workspace.Id);
        switch (result.Outcome)
        {
            case WindowRebindOutcome.ShownElsewhere:
                if (result.Owner is { IsDisposed: false } owner)
                {
                    Focus(owner);
                }

                break;

            case WindowRebindOutcome.Unchanged:
                Focus(window);
                break;

            default:
                _launcherLog.Line($"Switched a window to workspace '{workspace.Name}' ({workspace.RootPath}).");
                window.ShowWorkspace(workspace.Id, workspace.Name);
                _ = window.NavigateAfterFlushAsync(WorkspaceNavigation.WorkspaceUrl(StudioBase, workspace.Id));
                break;
        }
    }

    /// <summary>Registers and opens a folder handed over by a second `fravashi --open` launch.</summary>
    private void OpenWorkspaceFolder(string folderPath)
    {
        try
        {
            var workspace = _workspaces.Register(folderPath);
            _launcherLog.Line($"Opening workspace '{workspace.Name}' ({workspace.RootPath}).");
            OpenWorkspaceWindow(workspace);
        }
        catch (WorkspaceUnreachableException)
        {
            MessageBox.Show(
                $"The folder cannot be reached right now:\n{folderPath}\n\n"
                + "If it is on a network drive or a removable disk, connect it and try again.",
                ProductName.Short, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (DirectoryNotFoundException)
        {
            MessageBox.Show(
                $"The folder does not exist:\n{folderPath}",
                ProductName.Short, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnAnyWindowClosed()
    {
        // The retired tray executable's behavior: the product keeps running, reachable from the tray icon.
        if (!_residencyTipShown)
        {
            _residencyTipShown = true;
            _tray?.ShowBalloon($"{ProductName.Short} keeps running. Reopen it from this tray icon; `{ProductName.Command} stop` stops the container.");
        }
    }

    /// <summary>
    /// Brings the studio to the user once it is known to be running: focus a live window (the bare one
    /// first, then any workspace window) and, for a target that names a page, navigate it there.
    /// <para>
    /// Backlog 15's second half: this used to look ONLY at the bare window field, so with only
    /// workspace windows open every tray Open silently opened a SECOND window on the same studio. It
    /// also no longer navigates for a plain Open: "show me Fravashi" must not throw away the page the
    /// user was on. Onboarding names a destination, so that one still navigates.
    /// </para>
    /// </summary>
    private void ShowStudio(StudioTarget target)
    {
        var window = _windows.All.FirstOrDefault(w => !w.IsDisposed);
        if (window is null)
        {
            OpenWindow(UrlFor(target));
            return;
        }

        if (target == StudioTarget.Onboarding)
        {
            window.Navigate(UrlFor(target));
        }

        if (window.WindowState == FormWindowState.Minimized)
        {
            window.WindowState = FormWindowState.Normal;
        }

        window.Activate();
    }

    /// <summary>
    /// <see cref="IStudioRecovery"/>: what a window's recovery page says. Asks the launcher, so it must
    /// be called off the UI thread (the window awaits it).
    /// </summary>
    StudioUnavailableMessage IStudioRecovery.Describe()
    {
        try
        {
            var app = new LauncherApp(
                ContainerRuntimeDetector.Detect(_configStore.Load()),
                _configStore,
                launcherDirectory: AppContext.BaseDirectory,
                log: _launcherLog.Line,
                tray: new NoopTrayControl(),
                containerNameOverride: _parsed.ContainerName);
            // The tail is read in the two states where the usual message can be wrong: Running, whose
            // "it may still be starting up" goes stale, and Stopped, which since backlog 122 is also
            // where a container that used up its restart attempts ends up, and where "starting it again
            // takes a few seconds" would be inviting the user to watch the same crash. The other states
            // already say something true, and reading logs there would spend a round trip to change
            // nothing.
            var state = app.ProductState();
            var tail = state is ProductRunState.Running or ProductRunState.Stopped
                ? app.ContainerTail()
                : null;
            return StudioUnavailableMessage.For(state, tail);
        }
        catch (Exception ex)
        {
            _launcherLog.Line($"Could not read the product state for the window: {ex.Message}");
            return StudioUnavailableMessage.For(ProductRunState.RuntimeUnavailable);
        }
    }

    /// <summary><see cref="IStudioRecovery"/>: the retry button's action, sharing the launch's one path.</summary>
    async Task<bool> IStudioRecovery.EnsureRunningAsync() =>
        await EnsureProductAsync(onboarding: false).ConfigureAwait(false) == ExitCodes.Success;

    private void ActivateWindow()
    {
        // A second `fravashi --open PATH` hands its folder over via the open-request file (2b-ii).
        var requestPath = System.IO.Path.Combine(_configStore.ConfigDirectory, TrayControlNames.OpenRequestFile);
        try
        {
            if (File.Exists(requestPath))
            {
                var folder = File.ReadAllText(requestPath).Trim();
                File.Delete(requestPath);
                if (folder.Length > 0)
                {
                    OpenWorkspaceFolder(folder);
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fall through to a plain focus.
        }

        // A workspace window first, then the home window: the order this focus has always used.
        if (_windows.All.OrderBy(w => ReferenceEquals(w, _windows.Unbound)).FirstOrDefault(w => !w.IsDisposed) is { } anyWindow)
        {
            Focus(anyWindow);

            // A window being open says nothing about the product behind it (backlog 136). Reproduced on
            // 2026-09-21: the engine was stopped under a resident fravashi.exe, the page's overlay said "restart
            // Fravashi from the launcher or the tray", and doing exactly that only focused this window,
            // so the container stayed exited until somebody thought to exit the tray process first. The
            // same ensure path the tray's Open uses: one short healthz probe when all is well, the whole
            // start sequence when it is not. The page polls healthz and reloads by itself once it answers.
            _ = EnsureBehindOpenWindowAsync();
            return;
        }

        // No window left (they were all closed and fravashi.exe stayed in the tray). Relaunching from the Start
        // menu lands here, and it used to reopen a window at a URL without checking anything was
        // listening on it, so a relaunch after a tray Stop showed the engine's error page too
        // (backlog 15). Go through the one open path instead: it starts the product first.
        OpenStudio(StudioTarget.Studio);
    }

    /// <summary>
    /// A relaunch with a window already open: bring the product back when it is not answering, and say so
    /// when that fails. The window is left where it is either way; its page reloads itself on recovery.
    /// </summary>
    private async Task EnsureBehindOpenWindowAsync()
    {
        var exit = await EnsureProductAsync(onboarding: false).ConfigureAwait(false);
        if (exit != ExitCodes.Success)
        {
            OnUi(() => ReportStartFailure(exit));
        }
    }

    private void FailAndExit(int exitCode, string? explained = null)
    {
        _splash.Hide();
        // Backlog item 7: the busy-ports refusal is the one failure the user can act on without reading a log,
        // so it names the ports here instead of leaving a bare exit code on screen. The text is the launcher's
        // own, built from the port list, so this box cannot drift into claiming ports the provider refuses.
        // Backlog 252: an engine failure the launcher recognised is the same kind of failure, said the same way.
        var reason = exitCode == ExitCodes.PortsUnavailable
            ? $"{ProductName.Short} could not start.\n\n{LauncherDefaults.PortsBusyMessage(LauncherDefaults.StudioPorts)}"
            : explained is not null
                ? $"{ProductName.Short} could not start.\n\n{explained}"
                : $"{ProductName.Short} could not start (exit code {exitCode}).";
        MessageBox.Show(
            $"{reason}\n\nSee the launcher log for details:\n{_launcherLog.Directory}",
            ProductName.Short, MessageBoxButtons.OK, MessageBoxIcon.Error);
        Environment.ExitCode = exitCode;
        ExitApp();
    }

    /// <summary>
    /// `fravashi exit`, the tray's Exit and a failed launch all land here. The sequence itself lives in
    /// <see cref="ResidentShutdown"/> (backlog 5): windows close on this thread, the bridge stops on
    /// the pool, the loop ends when both are over, and the watchdog behind it is a logged anomaly,
    /// not the way out. Nothing on the exit path blocks the UI thread on the bridge any more: that
    /// block, inside the loop's own teardown, was what the watchdog had been ending every time.
    /// </summary>
    private void ExitApp()
    {
        if (_shutdown is not null)
        {
            _launcherLog.Line("fravashi exit: already in progress.");
            return;
        }

        _launcherLog.Line("fravashi exit: closing windows and stopping the host bridge.");
        _tray?.Dispose();
        _tray = null;

        var bridgeApp = _bridgeApp;
        _bridgeApp = null;
        var appRunnerApp = _appRunnerApp;
        _appRunnerApp = null;
        _appRunnerSweep?.Dispose();
        _appRunnerSweep = null;
        var upgradeApp = _upgradeApp;
        _upgradeApp = null;
        _dnsRecoveryTimer?.Dispose();
        _dnsRecoveryTimer = null;
        _shutdown = new ResidentShutdown(
            _launcherLog.Line,
            ResidentShutdown.DefaultWatchdogDelay,
            ResidentShutdown.DefaultBridgeStopTimeout,
            LiveThreads.Describe,
            () => Environment.Exit(Environment.ExitCode));
        _ = _shutdown.RunAsync(
            CloseWindows(),
            bridgeApp is null && appRunnerApp is null && upgradeApp is null
                ? null
                : token => Task.WhenAll(
                    bridgeApp is null ? Task.CompletedTask : StopBridgeAsync(bridgeApp, token),
                    appRunnerApp is null ? Task.CompletedTask : StopBridgeAsync(appRunnerApp, token),
                    upgradeApp is null ? Task.CompletedTask : StopBridgeAsync(upgradeApp, token)),
            () => OnUi(ExitThread));
    }

    /// <summary>
    /// Closes every open window and completes when the last one is gone. Closing flushes hot-exit
    /// backups (MainWindow.OnFormClosing, capped at 800 ms per window); the container keeps running
    /// (ADR 0003 V3): `fravashi stop` is the deliberate stop action.
    /// </summary>
    private Task CloseWindows()
    {
        var open = _windows.All.Where(w => !w.IsDisposed).ToList();

        if (open.Count == 0)
        {
            return Task.CompletedTask;
        }

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remaining = open.Count;
        foreach (var window in open)
        {
            window.FormClosed += (_, _) =>
            {
                if (Interlocked.Decrement(ref remaining) == 0)
                {
                    _launcherLog.Line("fravashi exit: all windows closed.");
                    closed.TrySetResult();
                }
            };
            window.Close();
        }

        return closed.Task;
    }

    private static async Task StopBridgeAsync(WebApplication bridgeApp, CancellationToken cancellationToken)
    {
        await bridgeApp.StopAsync(cancellationToken).ConfigureAwait(false);
        await bridgeApp.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Runs on the UI thread inside the message loop's own teardown (WinForms disposes the context
    /// from there), so nothing here may block on the bridge or on any other thread.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _exitWait.Unregister(_exitEvent);
            _activateWait.Unregister(_activateEvent);
            _tray?.Dispose();
            _splash.Dispose();
            _startGate.Dispose();
            if (_bridgeApp is { } bridgeApp)
            {
                // The loop ended without the exit sequence (nothing does that today). Stop the bridge
                // off this thread and let process exit tear down whatever is left.
                _bridgeApp = null;
                _launcherLog.Line("fravashi exit: the message loop ended outside the exit sequence; stopping the host bridge in the background.");
                _ = Task.Run(() => StopBridgeAsync(bridgeApp, new CancellationTokenSource(ResidentShutdown.DefaultBridgeStopTimeout).Token));
            }

            _shutdown?.MessageLoopEnded();
        }

        base.Dispose(disposing);
    }
}
