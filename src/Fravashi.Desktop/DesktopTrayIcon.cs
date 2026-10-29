using Fravashi.Launcher;
using Fravashi.Domain;

namespace Fravashi.Desktop;

/// <summary>
/// The notification-area icon owned by the merged fravashi.exe (formerly a separate tray executable). All actions run
/// in-process: Open/Onboarding name a <see cref="StudioTarget"/> and the host's one open path starts
/// the product when it is not running before showing a window (backlog 15), Status/Stop/Start drive
/// <see cref="LauncherApp"/> on a worker thread with a line-collecting log (no more shelling out to a
/// sibling exe), Website opens the browser, Exit ends the process (the container keeps running;
/// Stop product is the deliberate stop).
///
/// The menu tells the truth about the product's state: Start and Stop are both always present with
/// exactly the applicable one enabled (<see cref="TrayProductMenu"/>), above a state line that names
/// what is running. The state is read off the UI thread whenever the menu opens and after any action
/// that changes it, because asking the container engine costs a CLI round trip and a menu that
/// stalls on right-click would be its own defect.
/// </summary>
internal sealed class DesktopTrayIcon : IDisposable
{
    /// <summary>Win32 caps the notification-area tooltip; a longer string throws rather than truncating.</summary>
    private const int TooltipLimit = 63;

    private readonly LauncherConfigStore _configStore;
    private readonly LauncherLog _log;
    private readonly Action<StudioTarget> _openStudio;
    private readonly Action _exitApp;
    private readonly SynchronizationContext _ui;
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly ToolStripMenuItem _stateItem;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;
    private bool _disposed;

    /// <summary>
    /// What clicking a balloon runs, or null for one with nothing to do (backlog 304, ADR 0031 decision 12).
    /// <see cref="NotifyIcon"/> has exactly one <c>BalloonTipClicked</c> event for every balloon
    /// <see cref="ShowBalloon"/> ever raises (the stop-issued balloon, the DNS-recovery explanation, the
    /// residency tip, and a waiting toast); every call to <see cref="ShowBalloon"/> REPLACES this with its own
    /// action (null for the three ordinary balloons), cleared once a click runs it. The design ASSUMES a
    /// click can only land on the balloon shown last, which this change does not measure: fravashi.exe has no
    /// test project, and on Windows an older balloon can sit in the notification centre after a newer one is
    /// shown, so a click there might still raise <c>BalloonTipClicked</c> and run the NEWEST action instead of
    /// nothing. Proving this right (or wrong) is for the host's installed-window check on #304: show a
    /// waiting toast, then an ordinary balloon, then click each from the notification centre.
    /// </summary>
    private Action? _balloonClick;

    public DesktopTrayIcon(LauncherConfigStore configStore, LauncherLog log, Action<StudioTarget> openStudio, Action exitApp)
    {
        _configStore = configStore;
        _log = log;
        _openStudio = openStudio;
        _exitApp = exitApp;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _icon = DesktopIcon.Load();
        _stateItem = new ToolStripMenuItem($"Checking {ProductName.Short} ...") { Enabled = false };
        _startItem = new ToolStripMenuItem("Start product", image: null, (_, _) => StartProduct());
        _stopItem = new ToolStripMenuItem("Stop product", image: null, (_, _) => StopProduct());
        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = $"{ProductName.Short}: right-click for commands ({ProductName.Command} help)",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _notifyIcon.DoubleClick += (_, _) => _openStudio(StudioTarget.Studio);
        _notifyIcon.BalloonTipClicked += (_, _) =>
        {
            var click = _balloonClick;
            _balloonClick = null;
            click?.Invoke();
        };
        RefreshState();
    }

    /// <summary>
    /// Shows a balloon; a null or empty <paramref name="text"/> would make
    /// <see cref="NotifyIcon.ShowBalloonTip(int, string, string, ToolTipIcon)"/> throw
    /// <see cref="ArgumentException"/>, so it is dropped silently instead, on this call's own UI-thread post;
    /// whitespace-only text is dropped the same way, to avoid showing a balloon with nothing readable in it,
    /// though the WinForms call itself would not have thrown for it.
    /// </summary>
    /// <param name="onClick">
    /// Backlog 304: what clicking THIS balloon runs, or null for the ordinary balloons (stop issued, DNS
    /// recovery, the residency tip), which do nothing when clicked.
    /// </param>
    public void ShowBalloon(string text, Action? onClick = null) => _ui.Post(_ =>
    {
        if (_disposed || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _balloonClick = onClick;
        _notifyIcon.ShowBalloonTip(4000, ProductName.Short, text, ToolTipIcon.Info);
    }, null);

    /// <summary>Re-reads the product state off the UI thread and applies it to the menu.</summary>
    public void RefreshState() => Task.Run(() =>
    {
        try
        {
            ApplyState(CreateLauncher().ProductState());
        }
        catch (Exception ex)
        {
            _log.Line($"Could not read the product state for the tray menu: {ex.Message}");
            ApplyState(ProductRunState.RuntimeUnavailable);
        }
    });

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(_stateItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add($"Open {ProductName.Short}", image: null, (_, _) => _openStudio(StudioTarget.Studio));
        menu.Items.Add("Onboarding / settings", image: null, (_, _) => _openStudio(StudioTarget.Onboarding));
        menu.Items.Add("Website", image: null, (_, _) => BrowserLauncher.Open(CurrentWebsite(), _log.Line));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Status", image: null, (_, _) => ShowStatus());
        menu.Items.Add(_startItem);
        menu.Items.Add(_stopItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", image: null, (_, _) => _exitApp());
        // The state can change behind our back (`fravashi stop` from a console, a crashed engine), so the
        // menu re-reads it on every open. The read is asynchronous: the items carry the last known
        // state and correct themselves while the menu is still on screen.
        menu.Opening += (_, _) => RefreshState();
        return menu;
    }

    private void ApplyState(ProductRunState state)
    {
        _ui.Post(_ =>
        {
            if (_disposed)
            {
                return;
            }

            var model = TrayProductMenu.For(state);
            _stateItem.Text = model.StateLabel;
            _startItem.Enabled = model.CanStart;
            _stopItem.Enabled = model.CanStop;
            _notifyIcon.Text = Tooltip($"{model.StateLabel} (right-click for commands)");
        }, null);
    }

    private static string Tooltip(string text) =>
        text.Length <= TooltipLimit ? text : text[..TooltipLimit];

    private string CurrentWebsite() =>
        LauncherDefaults.ResolveWebsiteUrl(_configStore.Load()?.WebsiteUrl);

    private LauncherApp CreateLauncher(Action<string>? log = null)
    {
        var runtime = ContainerRuntimeDetector.Detect(_configStore.Load());
        return new LauncherApp(
            runtime,
            _configStore,
            launcherDirectory: AppContext.BaseDirectory,
            log: log ?? _log.Line,
            openBrowser: url => BrowserLauncher.Open(url, _log.Line),
            tray: new NoopTrayControl());
    }

    /// <summary>Runs a launcher verb off the UI thread, collecting its lines for display.</summary>
    private string RunVerb(Func<LauncherApp, int> verb)
    {
        var lines = new List<string>();
        verb(CreateLauncher(line =>
        {
            lines.Add(line);
            _log.Line(line);
        }));
        return string.Join(Environment.NewLine, lines);
    }

    private void ShowStatus() => Task.Run(() =>
    {
        var text = RunVerb(app => app.Status());
        MessageBox.Show(
            text.Length > 0 ? text : $"Could not read {ProductName.Short} status.",
            $"{ProductName.Short} status", MessageBoxButtons.OK, MessageBoxIcon.Information);
    });

    /// <summary>
    /// The Start item the tray never had (backlog 15). It goes through the host's one open path, so it
    /// runs the real launcher start sequence behind the splash and only then shows the studio.
    /// </summary>
    private void StartProduct() => _openStudio(StudioTarget.Studio);

    private void StopProduct() => Task.Run(() =>
    {
        var text = RunVerb(app => app.Stop());
        ShowBalloon(text.Length > 0 ? text : $"{ProductName.Short} stop issued.");
        RefreshState();
    });

    public void Dispose()
    {
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }
}
