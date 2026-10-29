using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Fravashi.Launcher;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Product = Fravashi.Domain.ProductName;

namespace Fravashi.Desktop;

/// <summary>
/// The chromeless studio window. The OS caption bar is removed (WM_NCCALCSIZE eats it while the native
/// resize frame and snap/minimize animations survive); the web app draws its own title bar and drives
/// minimize/maximize/close over the WebView2 message bridge (<c>{"cmd":"minimize|maximize|close"}</c>);
/// the host answers window-state changes with <c>{"maximized":bool}</c> so the restore icon tracks
/// reality. Title-bar dragging comes from WebView2's non-client region support (<c>app-region: drag</c>
/// CSS in the page). Browser accelerator actions are disabled so every key press belongs to the studio.
///
/// DPI is per-monitor v2 (see the csproj), so every measurement here is taken for THIS window's DPI
/// rather than the process's start-time one: the design sizes below are logical (96 dpi) and scaled at
/// handle creation, and the frame metrics behind the caption removal come from
/// <c>GetSystemMetricsForDpi</c>. WebView2 keeps its own rasterization scale in step: the WinForms
/// wrapper carries no DPI code at all, and the underlying controller's
/// <c>ShouldDetectMonitorScaleChanges</c> defaults to true, so it tracks the monitor itself. That only
/// agrees with the host while the host is per-monitor aware too, which is exactly what the old
/// SystemAware default broke.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MainWindow : Form
{
    private const int WM_NCCALCSIZE = 0x0083;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int SM_CYSIZEFRAME = 33;
    private const int SM_CXPADDEDBORDER = 92;
    private const uint SWP_FRAMECHANGED_REFRESH = 0x0037; // NOSIZE | NOMOVE | NOZORDER | NOACTIVATE | FRAMECHANGED
    private const uint SWP_RESIZE_ONLY = 0x0014;          // NOZORDER | NOACTIVATE
    private const int DefaultDpi = 96;

    /// <summary>The window's default and smallest sizes in LOGICAL pixels; scaled to the window's DPI.</summary>
    private static readonly Size DesignSize = new(1440, 900);
    private static readonly Size DesignMinimumSize = new(760, 480);

    /// <summary>Logical width of the top-left/top-right corner grips inside the top resize band.</summary>
    private const int DesignCornerGrip = 16;

    /// <summary>Logical wrap width of the recovery page's detail line.</summary>
    private const int DesignErrorTextWidth = 520;

    /// <summary>The studio's own dark: the window, the WebView's pre-paint, and the nav overlay share it.</summary>
    private static readonly Color StudioDark = Color.FromArgb(11, 12, 15);

    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };

    // A navigation that leaves the studio (the sign-in hop to the identity provider, plan Phase 3)
    // tears our document down, so no in-page spinner can survive it: WebView2 paints its own blank
    // frame until the remote document commits. This host-side overlay covers that gap. It appears
    // only after a short delay, so the fast same-origin loads never flash it.
    private readonly Panel _navOverlay = new() { Dock = DockStyle.Fill, BackColor = StudioDark, Visible = false };
    private readonly Label _navMessage = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleCenter,
        ForeColor = Color.FromArgb(214, 219, 230),
        Font = new Font("Segoe UI", 11F),
        Text = "Loading ...",
    };
    private readonly System.Windows.Forms.Timer _navOverlayDelay = new() { Interval = 400 };
    private readonly System.Windows.Forms.Timer _navOverlaySafety = new() { Interval = 30_000 };

    // Backlog 15: a borderless window has no address bar and no reload button, so the browser engine's
    // own ERR_CONNECTION_REFUSED page is a dead end. This covers it with our message and one button.
    private readonly Panel _errorOverlay = new() { Dock = DockStyle.Fill, BackColor = StudioDark, Visible = false };
    private readonly Label _errorHeadline = new()
    {
        AutoSize = true,
        ForeColor = Color.FromArgb(233, 237, 245),
        Font = new Font("Segoe UI", 14F, FontStyle.Bold),
        Anchor = AnchorStyles.None,
        Margin = new Padding(0, 0, 0, 10),
    };
    private readonly Label _errorDetail = new()
    {
        AutoSize = true,
        ForeColor = Color.FromArgb(170, 178, 192),
        Font = new Font("Segoe UI", 10F),
        TextAlign = ContentAlignment.MiddleCenter,
        Anchor = AnchorStyles.None,
        Margin = new Padding(0, 0, 0, 20),
    };
    private readonly Button _errorAction = new()
    {
        AutoSize = true,
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(35, 38, 46),
        ForeColor = Color.FromArgb(226, 231, 240),
        Font = new Font("Segoe UI", 10F),
        Padding = new Padding(16, 6, 16, 6),
        Cursor = Cursors.Hand,
        Anchor = AnchorStyles.None,
    };

    private readonly string _url;
    private readonly string? _windowToken;

    /// <summary>Navigations this window cancelled on purpose; their "failure" is ours and means nothing.</summary>
    private readonly HashSet<ulong> _cancelledNavigations = new();

    private FormWindowState _lastState = FormWindowState.Normal;
    private bool _closeFlushed;
    private bool _scaleReported;
    private string _lastNavigationTarget;

    public MainWindow(string url, string? windowToken = null, string? workspaceId = null, string? workspaceName = null)
    {
        _url = url;
        _lastNavigationTarget = url;
        _windowToken = windowToken;
        WorkspaceId = workspaceId;
        // The native caption (taskbar and Alt+Tab) is chrome and named for the command the owner types,
        // which since the rename is the product name too (backlog 32). The studio pushes its own
        // chrome-worded caption once the page loads (OnWebMessageReceived's "title" command).
        Text = WindowCaption.For(workspaceName);
        Icon = DesktopIcon.Load();
        // Placeholder values: OnHandleCreated re-applies both for the DPI the window actually lands on.
        MinimumSize = DesignMinimumSize;
        Size = DesignSize;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        // Match the web title bar's dark rail: any frame sliver that peeks through during state
        // transitions blends in instead of flashing white.
        BackColor = StudioDark;
        // The caption removal leaves no native top frame, so a thin strip of the form stays exposed
        // above the WebView as the top resize grip (WM_NCHITTEST maps it to HTTOP below). It paints
        // in the title bar's own color, so it reads as part of the title bar.
        Padding = new Padding(0, TopResizeBand, 0, 0);
        _navOverlay.Controls.Add(_navMessage);
        BuildErrorOverlay();
        // Added before the WebView so the two overlays sit above it in the z-order.
        Controls.Add(_errorOverlay);
        Controls.Add(_navOverlay);
        Controls.Add(_webView);
        _navOverlayDelay.Tick += (_, _) => ShowNavOverlay();
        // Never strand the window behind the overlay: a navigation that never completes still
        // gives it back after this deadline.
        _navOverlaySafety.Tick += (_, _) => HideNavOverlay();
        // The overlay covers the web-drawn title bar, so it carries the window drag itself.
        _navOverlay.MouseDown += (_, e) => DragWindowFrom(e);
        _navMessage.MouseDown += (_, e) => DragWindowFrom(e);
        Load += async (_, _) => await InitializeAsync();
        Resize += (_, _) => OnWindowStateMaybeChanged();
    }

    /// <summary>
    /// How this window recovers from a failed navigation (backlog 15): the host answers what to say
    /// and starts the product. Null only in a shell with no host, where the button just re-navigates.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal IStudioRecovery? Recovery { get; set; }

    /// <summary>
    /// The resident host's window map (backlog 29): what a binding change re-keys, and who acts on the
    /// close/switch workspace commands. Null only in a shell with no host, where the window follows its
    /// navigations alone exactly as before.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal IWorkspaceWindowHost? WorkspaceHost { get; set; }

    private void BuildErrorOverlay()
    {
        var stack = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = StudioDark,
            Anchor = AnchorStyles.None,
        };
        stack.Controls.Add(_errorHeadline);
        stack.Controls.Add(_errorDetail);
        stack.Controls.Add(_errorAction);

        // A single-cell TableLayoutPanel is what actually centers a child in WinForms; a plain Panel
        // with Anchor.None only keeps the child's original offset.
        var centre = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 1,
            BackColor = StudioDark,
        };
        centre.Controls.Add(stack);
        _errorOverlay.Controls.Add(centre);
        _errorAction.FlatAppearance.BorderColor = Color.FromArgb(58, 63, 76);
        _errorAction.Click += (_, _) => _ = RetryAsync();
        // The overlay hides the web-drawn title bar, so it carries the window drag like the nav one.
        _errorOverlay.MouseDown += (_, e) => DragWindowFrom(e);
        centre.MouseDown += (_, e) => DragWindowFrom(e);
    }

    /// <summary>
    /// The workspace this window carries (plan 2b-ii). Every same-origin top-level navigation is
    /// rewritten to carry <c>?ws={id}</c>, so any full page load (reload, forceLoad navigation,
    /// reconnect) lands with the circuit's workspace; a navigation that explicitly carries a
    /// DIFFERENT ws is adopted as the window's new workspace. Changed only through
    /// <see cref="ShowWorkspace"/>, so the native caption follows it (backlog 29).
    /// </summary>
    public string? WorkspaceId { get; private set; }

    /// <summary>
    /// Records the workspace this window now shows (null: none) and re-titles the native caption to
    /// match. The host calls it after re-keying its window map; the studio page still pushes its own
    /// caption once it loads (the "title" command), which names the same workspace.
    /// </summary>
    internal void ShowWorkspace(string? workspaceId, string? workspaceName)
    {
        WorkspaceId = string.IsNullOrWhiteSpace(workspaceId) ? null : workspaceId;
        Text = WindowCaption.For(WorkspaceId is null ? null : workspaceName ?? WorkspaceId);
    }

    /// <summary>
    /// This window's current DPI, asked of the OS rather than remembered: under per-monitor v2 it
    /// changes when the display scale changes or the window is dragged to another monitor.
    /// </summary>
    private int WindowDpi => IsHandleCreated ? DpiOf(Handle) : DeviceDpi;

    private static int DpiOf(IntPtr window)
    {
        var dpi = window == IntPtr.Zero ? 0u : GetDpiForWindow(window);
        return dpi == 0 ? DefaultDpi : (int)dpi;
    }

    private static int ScaleForDpi(int logical, int dpi) => (int)Math.Round(logical * (dpi / (double)DefaultDpi));

    private static Size ScaleForDpi(Size logical, int dpi) =>
        new(ScaleForDpi(logical.Width, dpi), ScaleForDpi(logical.Height, dpi));

    /// <summary>The exposed strip above the WebView that acts as the top resize grip, at this DPI.</summary>
    private static int TopResizeBandFor(int dpi) =>
        Math.Max(ScaleForDpi(4, dpi), GetSystemMetricsForDpi(SM_CYSIZEFRAME, (uint)dpi));

    private int TopResizeBand => TopResizeBandFor(WindowDpi);

    /// <summary>Sizes the window for the DPI it was created on, then keeps the DPI-derived metrics honest.</summary>
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var dpi = WindowDpi;
        ApplyDpiMetrics(dpi);
        if (WindowState == FormWindowState.Normal)
        {
            // The design size is logical, so a 150% display gets a proportionally larger window. Cap it
            // at most of the work area: without that, a screen whose logical size is barely larger than
            // the design size opens a window that reads as maximized (and a smaller one opens a window
            // bigger than the screen).
            var work = Screen.FromHandle(Handle).WorkingArea;
            var wanted = ScaleForDpi(DesignSize, dpi);
            Size = new Size(
                Math.Min(wanted.Width, work.Width * 9 / 10),
                Math.Min(wanted.Height, work.Height * 9 / 10));
            CenterToScreen();
        }

        WindowLog.Line("host", $"Window created at {dpi} dpi, {Size.Width}x{Size.Height}.");
    }

    /// <summary>
    /// The display scale changed under a live window (or the window moved to a monitor with another
    /// scale). Windows has already offered the new bounds and WinForms took them; what it cannot know is
    /// the non-client arithmetic this window does itself (ADR 0014), so redo that for the new DPI.
    /// </summary>
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyDpiMetrics(e.DeviceDpiNew);
        WindowLog.Line("host", $"Window DPI changed {e.DeviceDpiOld} -> {e.DeviceDpiNew}; {Size.Width}x{Size.Height}.");
        // WebView2 tracks the monitor's scale itself; report what it settled on, so a field complaint
        // about a mis-scaled page can be checked against the two numbers rather than guessed at.
        _ = ReportPageScaleAsync();
    }

    private void ApplyDpiMetrics(int dpi)
    {
        MinimumSize = ScaleForDpi(DesignMinimumSize, dpi);
        // AutoSize labels are measured in real pixels, so the recovery page's wrap width is a logical
        // number scaled here like everything else, not a constant that grows into the window edges.
        _errorDetail.MaximumSize = new Size(ScaleForDpi(DesignErrorTextWidth, dpi), 0);
        Padding = WindowState == FormWindowState.Maximized
            ? Padding.Empty
            : new Padding(0, TopResizeBandFor(dpi), 0, 0);
        if (IsHandleCreated)
        {
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_FRAMECHANGED_REFRESH);
        }
    }

    /// <summary>Logs the window DPI beside the page's own device pixel ratio and viewport.</summary>
    private async Task ReportPageScaleAsync()
    {
        if (_webView.CoreWebView2 is not { } core)
        {
            return;
        }

        try
        {
            var json = await core.ExecuteScriptAsync(
                    "JSON.stringify({dpr:devicePixelRatio,w:innerWidth,h:innerHeight})")
                .WaitAsync(TimeSpan.FromSeconds(2));
            WindowLog.Line("host", $"Window {WindowDpi} dpi, client {ClientSize.Width}x{ClientSize.Height}, page {json}.");
        }
        catch (Exception ex)
        {
            WindowLog.Line("host", $"Page scale unavailable: {ex.Message}");
        }
    }

    private void OnWindowStateMaybeChanged()
    {
        if (WindowState != _lastState)
        {
            var previous = _lastState;
            _lastState = WindowState;
            // Maximized windows cannot be resized, and the band would show as a dark strip above a
            // title bar that should touch the screen edge (Fitts: the caption buttons own the corner).
            Padding = WindowState == FormWindowState.Maximized ? Padding.Empty : new Padding(0, TopResizeBand, 0, 0);
            // Recalculate the non-client area after maximize/restore.
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_FRAMECHANGED_REFRESH);
            if (previous == FormWindowState.Maximized && WindowState == FormWindowState.Normal)
            {
                // DWM leaves a stale sliver of the maximized frame in the top border band after a
                // restore, and only an actual size change makes it repaint (users see the line vanish
                // on the first manual resize). Nudge one pixel out and back, deferred so it reads the
                // settled bounds rather than a mid-transition rectangle; visually imperceptible.
                BeginInvoke(() =>
                {
                    if (WindowState != FormWindowState.Normal)
                    {
                        return;
                    }

                    var bounds = Bounds;
                    SetWindowPos(Handle, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width + 1, bounds.Height, SWP_RESIZE_ONLY);
                    SetWindowPos(Handle, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_RESIZE_ONLY);
                });
            }
        }

        PostWindowState();
    }

    private async Task InitializeAsync()
    {
        try
        {
            // Keep browser state out of Program Files: the WebView2 profile lives with the user.
            // It moves with the rest of the folder (ProductHome; the rename to Fravashi moved the former folder to %LOCALAPPDATA%\Fravashi).
            var dataFolder = Path.Combine(ProductHome.Current, "webview2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: dataFolder);
            // WebView2 paints its default background (WHITE) for every frame before a document
            // commits; on a slow cross-origin hop that reads as a broken blank window. Paint the
            // studio's dark instead, so even an uncovered frame belongs to the product.
            _webView.DefaultBackgroundColor = StudioDark;
            await _webView.EnsureCoreWebView2Async(environment);

            var settings = _webView.CoreWebView2.Settings;
            // The studio serves ONLY this shell (ADR 0017): the user agent carries the window marker
            // plus the launcher-minted access token, which the server's window-only gate requires.
            settings.UserAgent = settings.UserAgent + " FravashiWindow/1.0"
                + (string.IsNullOrWhiteSpace(_windowToken) ? string.Empty : $" tk/{_windowToken}");
            // The studio owns the keyboard: no browser accelerator ACTIONS (key events still reach the
            // page, which is the whole point of the window shell).
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.IsPasswordAutosaveEnabled = false;
            // Zoom belongs to the document (the editor scales its own font on Ctrl+wheel/pinch);
            // the window chrome must never scale.
            settings.IsZoomControlEnabled = false;
            settings.IsPinchZoomEnabled = false;
            TryEnableNonClientRegions(settings);

            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _webView.CoreWebView2.NavigationStarting += OnNavigationStarting;
            _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            _webView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
            await WireDevConsoleLoggingAsync(_webView.CoreWebView2);
            _webView.Source = new Uri(_url);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            // The studio is window-only (ADR 0017): no browser fallback exists by design, so a
            // missing WebView2 runtime means installing it, not opening a browser.
            WindowLog.Line("host", "WebView2 runtime not found; asked the user to install it.");
            MessageBox.Show(
                $"{Product.Short} needs the Microsoft WebView2 runtime, which is not installed on this machine. "
                + $"Install it (https://developer.microsoft.com/microsoft-edge/webview2/) and start {Product.Short} again.",
                Product.Short, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
        }
    }

    /// <summary>
    /// Forwards the page's developer-console stream (console.*, uncaught exceptions) into the
    /// window log via the DevTools protocol. The studio window has no visible devtools, so without
    /// this those messages would vanish (directive 2026-07-26: all logs reach the data path).
    /// </summary>
    private static async Task WireDevConsoleLoggingAsync(CoreWebView2 core)
    {
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Runtime.enable", "{}");
            core.GetDevToolsProtocolEventReceiver("Runtime.consoleAPICalled").DevToolsProtocolEventReceived +=
                (_, e) => WindowLog.Line("console", WindowLog.Summarize(e.ParameterObjectAsJson));
            core.GetDevToolsProtocolEventReceiver("Runtime.exceptionThrown").DevToolsProtocolEventReceived +=
                (_, e) => WindowLog.Line("page-error", WindowLog.Summarize(e.ParameterObjectAsJson));
        }
        catch (Exception ex)
        {
            WindowLog.Line("host", $"Dev-console forwarding unavailable: {ex.Message}");
        }
    }

    /// <summary>
    /// Hot exit's last line of defense: closing the window flushes every dirty buffer's backup
    /// before the page dies (the in-page debounce covers crashes; this covers the deliberate close).
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_closeFlushed && _webView.CoreWebView2 is { } core)
        {
            _closeFlushed = true;
            e.Cancel = true;
            FlushBackupsThenClose(core);
        }

        base.OnFormClosing(e);
    }

    private async void FlushBackupsThenClose(CoreWebView2 core)
    {
        await FlushBackupsAsync(core);
        Close();
    }

    /// <summary>Asks the Code page to write every dirty buffer's backup now, bounded at 800 ms.</summary>
    private static async Task FlushBackupsAsync(CoreWebView2 core)
    {
        try
        {
            await core.ExecuteScriptAsync(
                    "window.fravashiCode && fravashiCode.flushBackups ? (fravashiCode.flushBackups(), true) : false")
                .WaitAsync(TimeSpan.FromMilliseconds(800));
        }
        catch (Exception)
        {
            // Never hold the window (or its navigation) hostage over a flush; the debounced backups are
            // already on disk.
        }
    }

    private static void TryEnableNonClientRegions(CoreWebView2Settings settings)
    {
        try
        {
            // app-region: drag support for the web-drawn title bar; older runtimes throw, in which
            // case the window still works, it just cannot be dragged until the runtime updates.
            settings.IsNonClientRegionSupportEnabled = true;
        }
        catch (NotImplementedException)
        {
        }
        catch (COMException)
        {
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? command = null;
        string? title = null;
        string? workspace = null;
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            if (message.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (message.RootElement.TryGetProperty("cmd", out var value))
                {
                    command = value.GetString();
                }

                if (message.RootElement.TryGetProperty("title", out var titleValue)
                    && titleValue.ValueKind == JsonValueKind.String)
                {
                    title = titleValue.GetString();
                }

                if (message.RootElement.TryGetProperty("ws", out var workspaceValue)
                    && workspaceValue.ValueKind == JsonValueKind.String)
                {
                    workspace = workspaceValue.GetString();
                }
            }
        }
        catch (JsonException)
        {
            return;
        }

        switch (command)
        {
            case "minimize":
                WindowState = FormWindowState.Minimized;
                break;
            case "maximize":
                WindowState = WindowState == FormWindowState.Maximized
                    ? FormWindowState.Normal
                    : FormWindowState.Maximized;
                break;
            case "close":
                Close();
                break;
            case "title":
                // The studio names the native caption after the circuit's active workspace; the
                // launch-time value (workspaceName at construction) was only a first guess made before
                // the page loaded. The studio sends the chrome-worded caption here (its in-page
                // title bar keeps the product name Fravashi); blank falls back to the bare chrome name.
                Text = string.IsNullOrWhiteSpace(title) ? WindowCaption.ChromeName : title;
                break;
            case "closeWorkspace" when IsStudioSource(e.Source):
                // Backlog 29: the title bar's workspace menu. The host decides (re-key, maybe close this
                // window in favour of the existing home window) and then navigates. Deferred so the
                // bridge callback returns before any navigation or close starts.
                BeginInvoke(() => WorkspaceHost?.CloseWorkspace(this));
                break;
            case "switchWorkspace" when IsStudioSource(e.Source) && !string.IsNullOrWhiteSpace(workspace):
                BeginInvoke(() => WorkspaceHost?.SwitchWorkspace(this, workspace!));
                break;
        }
    }

    /// <summary>
    /// Backlog 15: a navigation that fails gets OUR page, not the engine's. WebView2 reports
    /// <c>IsSuccess = false</c> for deliberate cancellations too, and this window cancels one on
    /// purpose every time it re-imposes its workspace query, so both filters apply: the navigation ids
    /// we cancelled ourselves, and the statuses WebView2 documents as non-catastrophic
    /// (<see cref="StudioNavigationFailure"/>).
    /// </summary>
    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_cancelledNavigations.Remove(e.NavigationId))
        {
            // Our own cancel: the replacement navigation is already under way and owns the overlay.
            return;
        }

        HideNavOverlay();
        if (!e.IsSuccess && StudioNavigationFailure.DeservesOurPage(e.WebErrorStatus.ToString()))
        {
            WindowLog.Line("host", $"Navigation to {_lastNavigationTarget} failed: {e.WebErrorStatus}.");
            _ = ShowFailurePageAsync();
            return;
        }

        if (!e.IsSuccess)
        {
            return; // cancelled or an auth challenge: nothing failed that the user must act on
        }

        HideErrorOverlay();
        PostWindowState();
        if (!_scaleReported)
        {
            // One baseline line per window: window DPI beside the page's device pixel ratio.
            // The two disagreeing IS the mis-scaled window defect (backlog 93).
            _scaleReported = true;
            _ = ReportPageScaleAsync();
        }
    }

    /// <summary>
    /// Shows the recovery page, first with a neutral holding message and then with the one the product
    /// state earns. Reading that state costs a container-engine round trip, so it is asked off the UI
    /// thread rather than freezing the window on the frame the user is already unhappy with.
    /// </summary>
    private async Task ShowFailurePageAsync()
    {
        ShowErrorOverlay(new StudioUnavailableMessage(
            $"{Product.Short} could not be reached",
            "Checking whether the product is running ...",
            "Try again"));

        if (Recovery is not { } recovery)
        {
            return;
        }

        try
        {
            var message = await Task.Run(recovery.Describe);
            if (!IsDisposed && !Disposing && _errorOverlay.Visible)
            {
                ShowErrorOverlay(message);
            }
        }
        catch (Exception ex)
        {
            WindowLog.Line("host", $"Could not describe the studio failure: {ex.Message}");
        }
    }

    /// <summary>
    /// The recovery page's one button: start the product when it is not running, then re-navigate. The
    /// overlay stays up across both steps, so the engine's error page is never revealed in between.
    /// </summary>
    private async Task RetryAsync()
    {
        _errorAction.Enabled = false;
        _errorHeadline.Text = $"Starting {Product.Short} ...";
        _errorDetail.Text = "This takes a few seconds while the studio container comes up.";
        var started = true;
        try
        {
            if (Recovery is { } recovery)
            {
                started = await recovery.EnsureRunningAsync();
            }
        }
        catch (Exception ex)
        {
            WindowLog.Line("host", $"Retry could not start the product: {ex.Message}");
            started = false;
        }

        if (IsDisposed || Disposing)
        {
            return;
        }

        _errorAction.Enabled = true;
        if (!started)
        {
            ShowErrorOverlay(new StudioUnavailableMessage(
                $"{Product.Short} could not be started",
                $"The launcher could not bring the studio up. The window log has the details:\n{WindowLog.Directory}",
                "Try again"));
            return;
        }

        Navigate(_lastNavigationTarget);
    }

    /// <summary>
    /// A link that asks for a new window and leaves the studio opens in the person's own browser, where their sign-ins
    /// are (<see cref="ExternalLinks"/>); anything else keeps WebView2's default.
    /// </summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (!ExternalLinks.OpensInBrowser(e.Uri, _url))
        {
            return;
        }

        e.Handled = true;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri) { UseShellExecute = true });
            WindowLog.Line("host", $"Opened a link in the default browser: {new Uri(e.Uri).Host}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            WindowLog.Line("host", $"Could not open a link in the default browser: {ex.Message}");
        }
    }

    /// <summary>Enforces/adopts the window's workspace on every same-origin top-level navigation.</summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var target)
            || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
        {
            return; // about:blank, devtools and friends: nothing to cover, nothing to enforce
        }

        // What a retry re-navigates to: the URL the window was actually last asked for, which after a
        // workspace re-imposition or a redirect is not the one it was constructed with.
        _lastNavigationTarget = e.Uri;

        if (!Uri.TryCreate(_url, UriKind.Absolute, out var studio)
            || !string.Equals(target.Host, studio.Host, StringComparison.OrdinalIgnoreCase)
            || target.Port != studio.Port)
        {
            // Leaving the studio (the sign-in hop): name where we are going, because the wait is
            // someone else's server and the window would otherwise sit blank.
            BeginNavOverlay($"Taking you to {target.Host} ...");
            return;
        }

        if (WorkspaceNavigation.PassesUntouched(target))
        {
            // #372: a sign-in callback's state is single-use; re-imposing ?ws= here sent it to the studio twice.
            BeginNavOverlay("Signing in ...");
            return;
        }

        var carried = System.Web.HttpUtility.ParseQueryString(target.Query)["ws"];
        var host = WorkspaceHost;
        var decision = WorkspaceNavigation.Decide(
            WorkspaceId, carried, id => host?.IsShownByAnotherWindow(this, id) ?? false);

        switch (decision.Action)
        {
            case WorkspaceNavigationAction.ProceedUnbound:
            case WorkspaceNavigationAction.ProceedBound:
                BeginNavOverlay("Loading ...");
                return;

            case WorkspaceNavigationAction.Adopt:
                // An explicit ws wins: the window follows the navigation. Backlog 29: the host re-keys
                // its map and re-titles, so the folder is found under the id the window now shows.
                BeginNavOverlay("Loading ...");
                if (host is null)
                {
                    ShowWorkspace(decision.WorkspaceId, null);
                }
                else
                {
                    host.Adopt(this, decision.WorkspaceId!);
                }

                return;

            case WorkspaceNavigationAction.FocusOwner:
                // One window per folder: the workspace is already open elsewhere, so stay on this page
                // and bring that window forward. No replacement navigation follows, so no overlay.
                RememberCancelled(e.NavigationId);
                e.Cancel = true;
                HideNavOverlay();
                WindowLog.Line("host", $"Navigation to workspace {decision.WorkspaceId} refused: another window shows it.");
                BeginInvoke(() => host?.FocusWorkspace(decision.WorkspaceId!));
                return;

            case WorkspaceNavigationAction.ReImpose:
                BeginNavOverlay("Loading ...");
                RememberCancelled(e.NavigationId);
                e.Cancel = true;
                _webView.CoreWebView2.Navigate(WorkspaceNavigation.WithWorkspace(target, decision.WorkspaceId!));
                return;
        }
    }

    /// <summary>
    /// Cancelling raises NavigationCompleted with IsSuccess false; remember the id so the failure page is
    /// not shown for a "failure" this window caused on purpose (backlog 15). An id whose completion never
    /// arrives would otherwise accumulate for the life of the window, so the set is capped: only the
    /// navigation being cancelled right now is ever needed, and it is added after.
    /// </summary>
    private void RememberCancelled(ulong navigationId)
    {
        if (_cancelledNavigations.Count > 64)
        {
            _cancelledNavigations.Clear();
        }

        _cancelledNavigations.Add(navigationId);
    }

    /// <summary>True when a web message came from the studio's own origin, not from a page it navigated to.</summary>
    private bool IsStudioSource(string? source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var from)
        && Uri.TryCreate(_url, UriKind.Absolute, out var studio)
        && string.Equals(from.Host, studio.Host, StringComparison.OrdinalIgnoreCase)
        && from.Port == studio.Port;

    /// <summary>
    /// Navigates after giving the Code page's hot exit its flush (the same bounded call a window close
    /// makes), because a close or switch workspace replaces the page that holds the unsaved buffers.
    /// </summary>
    internal async Task NavigateAfterFlushAsync(string url)
    {
        if (_webView.CoreWebView2 is { } core)
        {
            await FlushBackupsAsync(core);
        }

        if (!IsDisposed && !Disposing)
        {
            Navigate(url);
        }
    }

    /// <summary>Navigates the hosted studio to another studio URL (tray: Open / Onboarding).</summary>
    public void Navigate(string url)
    {
        if (_webView.CoreWebView2 is { } core)
        {
            core.Navigate(url);
        }
        else
        {
            _webView.Source = new Uri(url);
        }
    }

    private void PostWindowState()
    {
        var core = _webView.CoreWebView2;
        if (core is not null)
        {
            var maximized = WindowState == FormWindowState.Maximized ? "true" : "false";
            core.PostWebMessageAsJson($"{{\"maximized\":{maximized}}}");
        }
    }

    /// <summary>
    /// Remove the caption bar but keep the resizable frame. For WM_NCCALCSIZE the default processing
    /// sizes the client area (insetting every side by the frame plus the caption on top); we then push
    /// the top edge back to the window top so no caption remains. While maximized the window overhangs
    /// every screen edge by the frame, so all four sides are clamped back inside the monitor,
    /// otherwise the web title bar's corners (search box, caption buttons) hang past the screen.
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
        {
            var left = Marshal.ReadInt32(m.LParam, 0);
            var top = Marshal.ReadInt32(m.LParam, 4);
            var right = Marshal.ReadInt32(m.LParam, 8);
            var bottom = Marshal.ReadInt32(m.LParam, 12);
            base.WndProc(ref m);
            if (IsZoomed(Handle))
            {
                // Per-monitor v2: the frame metrics belong to the monitor this window is on, and asking
                // for the process's start-time ones is what leaves a maximized window overhanging.
                var dpi = (uint)DpiOf(m.HWnd);
                var inset = GetSystemMetricsForDpi(SM_CYSIZEFRAME, dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
                Marshal.WriteInt32(m.LParam, 0, left + inset);
                Marshal.WriteInt32(m.LParam, 4, top + inset);
                Marshal.WriteInt32(m.LParam, 8, right - inset);
                Marshal.WriteInt32(m.LParam, 12, bottom - inset);
            }
            else
            {
                // Keep the base's side/bottom frame (the invisible resize borders); only the top goes
                // full bleed so the web title bar starts at the window edge.
                Marshal.WriteInt32(m.LParam, 4, top);
            }

            m.Result = IntPtr.Zero;
            return;
        }

        if (m.Msg == WM_NCHITTEST)
        {
            base.WndProc(ref m);
            // The exposed strip above the WebView (see Padding in the constructor) acts as the top
            // resize grip; without this the window can be resized from every edge except the top.
            if (m.Result == HTCLIENT && !IsZoomed(Handle))
            {
                var dpi = DpiOf(m.HWnd);
                var lParam = unchecked((int)(long)m.LParam);
                var x = (short)(lParam & 0xFFFF);
                var y = (short)((lParam >> 16) & 0xFFFF);
                if (y >= Top && y < Top + TopResizeBandFor(dpi))
                {
                    var grip = ScaleForDpi(DesignCornerGrip, dpi);
                    m.Result = x < Left + grip ? HTTOPLEFT
                        : x >= Right - grip ? HTTOPRIGHT
                        : HTTOP;
                }
            }

            return;
        }

        base.WndProc(ref m);
    }

    /// <summary>Arms the delayed loading overlay for a navigation just starting.</summary>
    private void BeginNavOverlay(string message)
    {
        _navMessage.Text = message;
        _navOverlayDelay.Stop();
        _navOverlayDelay.Start();
        _navOverlaySafety.Stop();
        _navOverlaySafety.Start();
    }

    private void ShowNavOverlay()
    {
        _navOverlayDelay.Stop();
        _navOverlay.Visible = true;
        _navOverlay.BringToFront();
        // Logged so a field report ("it went blank") can be checked against what the host actually
        // painted, rather than taken on trust.
        WindowLog.Line("host", $"Navigation overlay shown: {_navMessage.Text}");
    }

    private void HideNavOverlay()
    {
        _navOverlayDelay.Stop();
        _navOverlaySafety.Stop();
        if (_navOverlay.Visible)
        {
            _navOverlay.Visible = false;
            if (!_errorOverlay.Visible)
            {
                _webView.BringToFront();
            }
        }
    }

    /// <summary>Puts our recovery page over the engine's error page (backlog 15).</summary>
    private void ShowErrorOverlay(StudioUnavailableMessage message)
    {
        _errorHeadline.Text = message.Headline;
        _errorDetail.Text = message.Detail;
        _errorAction.Text = message.ActionLabel;
        _errorAction.Enabled = true;
        if (!_errorOverlay.Visible)
        {
            _errorOverlay.Visible = true;
            WindowLog.Line("host", $"Studio recovery page shown: {message.Headline}");
        }

        _errorOverlay.BringToFront();
    }

    private void HideErrorOverlay()
    {
        if (_errorOverlay.Visible)
        {
            _errorOverlay.Visible = false;
            _webView.BringToFront();
        }
    }

    /// <summary>Lets a drag on the overlay move the window (it hides the web-drawn title bar).</summary>
    private void DragWindowFrom(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, IntPtr.Zero);
    }

    /// <summary>Windows 10 1607 and later; fravashi.exe has no older floor (ADR 0014's shell needs 1809+).</summary>
    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
