using System.Collections.Concurrent;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;

namespace Fravashi.Infrastructure.Browser;

/// <summary>
/// <see cref="IAgentBrowser"/> over Playwright driving headless Chromium in the studio's own container (backlog 159,
/// decided 2026-09-27: the fast shape for this version; the sibling browser container is v2's, docs/v2.md
/// section 6). One browser for the process, one context and page per session, all in memory.
/// <para>
/// <b>Chromium's own sandbox cannot start here</b> (the container drops every capability, sets no-new-privileges and
/// keeps the engine's default seccomp profile), so the container is the boundary around a renderer and the defences
/// are layered around it instead: the driver runs on the image's Node through a wrapper that starts it with a CLEAN
/// environment (<c>PLAYWRIGHT_NODEJS_PATH</c>, <c>setup/playwright-node.sh</c>), and Chromium inherits that; the
/// launch passes an explicit environment of process basics as well; V8 runs without its JIT unless a person turns it
/// back on, which removes the largest class of renderer exploits; downloads and service workers are off; and every
/// request, subresources included, passes <see cref="BrowserUrlPolicy"/>. What remains is written down in the item:
/// a renderer compromise would run as the studio's uid.
/// </para>
/// </summary>
public sealed class PlaywrightAgentBrowser : IAgentBrowser, IAsyncDisposable
{
    /// <summary>The viewport every session's page has: what a screenshot and the pane show.</summary>
    public const int ViewportWidth = 1280;

    /// <summary>The viewport's height.</summary>
    public const int ViewportHeight = 800;

    /// <summary>The tallest full-page screenshot handed to the model; a longer page is cut there and says so.</summary>
    public const int MaxFullPageHeight = 4000;

    private const int MaxRecorded = 20;
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleSession = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan IdleBrowser = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RetryUnavailable = TimeSpan.FromMinutes(5);

    private readonly BrowserUrlPolicy _policy;
    private readonly bool _jitless;
    private readonly ILogger _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _launchGate = new(1, 1);
    private readonly ConcurrentDictionary<AgentSessionId, SessionPage> _pages = new();
    private readonly Timer _sweep;

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private DateTimeOffset _lastUse;
    private string? _launchFailure;
    private DateTimeOffset _launchFailedAt;

    public PlaywrightAgentBrowser(
        BrowserUrlPolicy policy,
        bool jitless = true,
        ILogger<PlaywrightAgentBrowser>? logger = null,
        Func<DateTimeOffset>? clock = null,
        string? browsersPath = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _jitless = jitless;
        _logger = logger ?? NullLogger<PlaywrightAgentBrowser>.Instance;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        UnavailableReason = FindBrowsers(browsersPath);
        _sweep = new Timer(_ => _ = SweepAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <inheritdoc />
    public event Action<AgentSessionId>? Changed;

    /// <inheritdoc />
    public bool IsAvailable => UnavailableReason is null && !RecentlyFailed;

    /// <inheritdoc />
    public string? UnavailableReason { get; }

    private bool RecentlyFailed => _launchFailure is not null && _clock() - _launchFailedAt < RetryUnavailable;

    /// <inheritdoc />
    public BrowserPageState? Current(AgentSessionId session) =>
        _pages.TryGetValue(session, out var page) ? page.State() : null;

    /// <inheritdoc />
    public async Task<BrowserOutcome> OpenAsync(AgentSessionId session, string url, CancellationToken cancellationToken = default)
    {
        if (_policy.RefuseToOpen(url) is { } refusal)
        {
            return BrowserOutcome.Refused(refusal);
        }

        return await WithPageAsync(session, create: true, async page =>
        {
            page.ResetRecords();
            var response = await page.Page.GotoAsync(url.Trim(), new PageGotoOptions
            {
                WaitUntil = WaitUntilState.Load,
                Timeout = (float)OpenTimeout.TotalMilliseconds,
            }).ConfigureAwait(false);
            page.Status = response?.Status;
            return new BrowserOutcome(await page.StateAsync().ConfigureAwait(false));
        }, cancellationToken, opening: url).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<(BrowserOutcome Outcome, string? Content, bool Truncated)> ReadAsync(
        AgentSessionId session, BrowserReadMode mode, int max, CancellationToken cancellationToken = default)
    {
        string? content = null;
        var outcome = await WithPageAsync(session, create: false, async page =>
        {
            var body = page.Page.Locator("body");
            content = mode == BrowserReadMode.Outline
                ? await body.AriaSnapshotAsync(new LocatorAriaSnapshotOptions { Timeout = 10_000 }).ConfigureAwait(false)
                : await body.InnerTextAsync(new LocatorInnerTextOptions { Timeout = 10_000 }).ConfigureAwait(false);
            return new BrowserOutcome(await page.StateAsync().ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);

        if (content is null || content.Length <= max)
        {
            return (outcome, content, false);
        }

        return (outcome, content[..max], true);
    }

    /// <inheritdoc />
    public Task<BrowserOutcome> ClickAsync(AgentSessionId session, BrowserTarget target, CancellationToken cancellationToken = default) =>
        ActOnAsync(session, target, "click", locator => locator.ClickAsync(new LocatorClickOptions { Timeout = 10_000 }), cancellationToken);

    /// <inheritdoc />
    public Task<BrowserOutcome> FillAsync(
        AgentSessionId session, BrowserTarget target, string text, bool submit, CancellationToken cancellationToken = default) =>
        ActOnAsync(session, target, "fill", async locator =>
        {
            await locator.FillAsync(text ?? string.Empty, new LocatorFillOptions { Timeout = 10_000 }).ConfigureAwait(false);
            if (submit)
            {
                await locator.PressAsync("Enter", new LocatorPressOptions { Timeout = 10_000 }).ConfigureAwait(false);
            }
        }, cancellationToken);

    /// <inheritdoc />
    public Task<BrowserOutcome> PressAsync(AgentSessionId session, string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 64)
        {
            return Task.FromResult(BrowserOutcome.Refused("Say which key, such as Enter, Tab, Escape or Control+A."));
        }

        return WithPageAsync(session, create: false, async page =>
        {
            await page.Page.Keyboard.PressAsync(key.Trim()).ConfigureAwait(false);
            await SettleAsync(page.Page).ConfigureAwait(false);
            return new BrowserOutcome(await page.StateAsync().ConfigureAwait(false));
        }, cancellationToken, actName: "key press");
    }

    /// <inheritdoc />
    public async Task<(BrowserOutcome Outcome, byte[]? Jpeg)> ScreenshotAsync(
        AgentSessionId session, bool fullPage, CancellationToken cancellationToken = default)
    {
        byte[]? jpeg = null;
        var outcome = await WithPageAsync(session, create: false, async page =>
        {
            var options = new PageScreenshotOptions { Type = ScreenshotType.Jpeg, Quality = 70, FullPage = fullPage };
            if (fullPage)
            {
                var height = await page.Page.EvaluateAsync<int>("() => document.documentElement.scrollHeight").ConfigureAwait(false);
                if (height > MaxFullPageHeight)
                {
                    options.Clip = new Clip { X = 0, Y = 0, Width = ViewportWidth, Height = MaxFullPageHeight };
                }
            }

            jpeg = await page.Page.ScreenshotAsync(options).ConfigureAwait(false);
            return new BrowserOutcome(await page.StateAsync().ConfigureAwait(false));
        }, cancellationToken).ConfigureAwait(false);
        return (outcome, jpeg);
    }

    /// <inheritdoc />
    public async Task CloseAsync(AgentSessionId session)
    {
        if (!_pages.TryRemove(session, out var page))
        {
            return;
        }

        try
        {
            await page.Context.CloseAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException ex)
        {
            _logger.LogDebug(ex, "The browser context of session {SessionId} was already gone.", session);
        }

        Changed?.Invoke(session);
    }

    /// <inheritdoc />
    public async Task<byte[]?> FrameAsync(AgentSessionId session, CancellationToken cancellationToken = default)
    {
        if (!_pages.TryGetValue(session, out var page))
        {
            return null;
        }

        try
        {
            return await page.Page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Jpeg, Quality = 50, Timeout = 5_000 })
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            // Mid-navigation; the next frame will do.
            return null;
        }
    }

    /// <inheritdoc />
    public Task<BrowserOutcome> ClickAtAsync(AgentSessionId session, double x, double y, CancellationToken cancellationToken = default) =>
        WithPageAsync(session, create: false, async page =>
        {
            await page.Page.Mouse.ClickAsync(
                (float)Math.Clamp(x, 0, ViewportWidth - 1), (float)Math.Clamp(y, 0, ViewportHeight - 1)).ConfigureAwait(false);
            await SettleAsync(page.Page).ConfigureAwait(false);
            return new BrowserOutcome(await page.StateAsync().ConfigureAwait(false));
        }, cancellationToken, actName: "click");

    /// <inheritdoc />
    public Task<BrowserOutcome> TypeTextAsync(AgentSessionId session, string text, CancellationToken cancellationToken = default) =>
        WithPageAsync(session, create: false, async page =>
        {
            await page.Page.Keyboard.TypeAsync(text ?? string.Empty).ConfigureAwait(false);
            return new BrowserOutcome(await page.StateAsync().ConfigureAwait(false));
        }, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _sweep.DisposeAsync().ConfigureAwait(false);
        foreach (var session in _pages.Keys.ToList())
        {
            await CloseAsync(session).ConfigureAwait(false);
        }

        await CloseBrowserAsync().ConfigureAwait(false);
        _launchGate.Dispose();
    }

    // ---- the acts --------------------------------------------------------------------------------

    private Task<BrowserOutcome> ActOnAsync(
        AgentSessionId session, BrowserTarget target, string actName, Func<ILocator, Task> act, CancellationToken cancellationToken)
    {
        if (target is null || target.IsEmpty)
        {
            return Task.FromResult(BrowserOutcome.Refused(
                "Say which element: a role with its name, its visible text, a field's label, or a CSS selector."));
        }

        return WithPageAsync(session, create: false, async page =>
        {
            if (Locate(page.Page, target) is not { } locator)
            {
                return BrowserOutcome.Refused(
                    $"\"{target.Role}\" is not an ARIA role. Use one such as button, link, textbox, checkbox, heading or menuitem.");
            }

            var count = await locator.CountAsync().ConfigureAwait(false);
            if (count == 0)
            {
                return BrowserOutcome.Refused($"Nothing on the page matches {target.Describe()}. Read the outline to see what is there.");
            }

            if (count > 1)
            {
                return BrowserOutcome.Refused(
                    $"{count} elements match {target.Describe()}, so the studio did not guess. Name it more exactly (a role with its full name, or a selector).");
            }

            await act(locator).ConfigureAwait(false);
            await SettleAsync(page.Page).ConfigureAwait(false);
            return new BrowserOutcome(await page.StateAsync().ConfigureAwait(false));
        }, cancellationToken, actName: actName);
    }

    /// <summary>The locator a target names, or null when its role is not one Playwright knows.</summary>
    public static ILocator? Locate(IPage page, BrowserTarget target)
    {
        if (!string.IsNullOrWhiteSpace(target.Role))
        {
            if (!Enum.TryParse<AriaRole>(target.Role.Trim(), ignoreCase: true, out var role))
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(target.Name)
                ? page.GetByRole(role)
                : page.GetByRole(role, new PageGetByRoleOptions { Name = target.Name.Trim() });
        }

        if (!string.IsNullOrWhiteSpace(target.Text))
        {
            return page.GetByText(target.Text.Trim());
        }

        if (!string.IsNullOrWhiteSpace(target.Label))
        {
            return page.GetByLabel(target.Label.Trim());
        }

        return page.Locator(target.Selector!.Trim());
    }

    /// <summary>
    /// Waits for what an act started. A click or a key that navigates begins the navigation a moment AFTER it returns,
    /// so a load-state wait made at once found the old document already loaded and answered with the page as it was;
    /// seen in the window on 2026-09-27, where a click showed the old address with the new title loading. So the wait
    /// gives a navigation that moment first, then waits for the document to load, bounded either way.
    /// </summary>
    private static async Task SettleAsync(IPage page)
    {
        try
        {
            await Task.Delay(300).ConfigureAwait(false);
            await page.WaitForLoadStateAsync(LoadState.Load, new PageWaitForLoadStateOptions { Timeout = 10_000 })
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A page still loading is still a page; the state says where it is.
        }
        catch (PlaywrightException)
        {
            // The page went away under the wait (it closed itself); the state says so.
        }
    }

    /// <summary>
    /// Waits, briefly, for the error page Chromium shows after a navigation failed on the network. It commits a moment
    /// AFTER Playwright has thrown, and an open made before then is itself "interrupted by another navigation to
    /// chrome-error://chromewebdata/" (measured in the live tier, 2026-10-07), so without this wait the "simply open it
    /// again" the answer promises would fail once on its own. The signal is THIS act's commit, not the address: after an
    /// earlier failure the address already reads chrome-error while the next error page is still on its way. Bounded,
    /// because not every failure shows one.
    /// </summary>
    private static async Task ErrorPageCommittedAsync(SessionPage page)
    {
        if (page.ErrorPageShown is not { } shown)
        {
            return;
        }

        await Task.WhenAny(shown.Task, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
    }

    /// <summary>Re-reads a page's title after it loaded and says the page changed, for the pane.</summary>
    private async Task RefreshAsync(AgentSessionId session, SessionPage page)
    {
        await page.SafeStateAsync().ConfigureAwait(false);
        Changed?.Invoke(session);
    }

    /// <summary>
    /// Runs one act on the session's page, one at a time. <paramref name="opening"/> is the address a
    /// <c>browser_open</c> goes to, and <paramref name="actName"/> names a click, fill or key press; either way a navigation
    /// that failed on the network is answered as that (backlog 233) rather than in Chromium's words, and the page is
    /// kept, so the agent can simply open it again.
    /// </summary>
    private async Task<BrowserOutcome> WithPageAsync(
        AgentSessionId session,
        bool create,
        Func<SessionPage, Task<BrowserOutcome>> act,
        CancellationToken cancellationToken,
        string? opening = null,
        string? actName = null)
    {
        SessionPage? page;
        if (create)
        {
            var (made, refusal) = await PageForAsync(session, cancellationToken).ConfigureAwait(false);
            if (made is null)
            {
                return BrowserOutcome.Refused(refusal!);
            }

            page = made;
        }
        else if (!_pages.TryGetValue(session, out page))
        {
            return BrowserOutcome.Refused("This session has no page open. Open one with browser_open first.");
        }

        await page.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            page.LastUse = _clock();
            _lastUse = page.LastUse;
            page.FailedNavigation = null;
            page.ErrorPageShown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var outcome = await act(page).ConfigureAwait(false);
            if (actName is not null && outcome.Ok && page.FailedNavigation is { } failed
                && BrowserNetworkErrors.OfAct(actName, failed.Url, failed.Failure, page.Page.Url) is { } afterAct)
            {
                return BrowserOutcome.NetworkFailed(outcome.Page, afterAct);
            }

            return outcome;
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            var network = opening is not null
                ? BrowserNetworkErrors.OfOpen(ex, opening, OpenTimeout)
                : actName is not null && BrowserNetworkErrors.Classify(ex.Message) is { IsNetwork: true } failure
                    ? new BrowserNetworkFailure(failure, BrowserNetworkErrors.HostOf(page.Page.Url), actName)
                    : null;
            if (network is not null)
            {
                await ErrorPageCommittedAsync(page).ConfigureAwait(false);
            }

            var state = await page.SafeStateAsync().ConfigureAwait(false);
            return network is null ? new BrowserOutcome(state, Clean(ex.Message)) : BrowserOutcome.NetworkFailed(state, network);
        }
        finally
        {
            page.Gate.Release();
            Changed?.Invoke(session);
        }
    }

    private async Task<(SessionPage? Page, string? Refusal)> PageForAsync(AgentSessionId session, CancellationToken cancellationToken)
    {
        if (_pages.TryGetValue(session, out var existing))
        {
            return (existing, null);
        }

        if (UnavailableReason is { } reason)
        {
            return (null, reason);
        }

        if (RecentlyFailed)
        {
            return (null, "The browser could not start a moment ago: " + _launchFailure);
        }

        await _launchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_pages.TryGetValue(session, out existing))
            {
                return (existing, null);
            }

            var browser = await BrowserAsync().ConfigureAwait(false);
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = ViewportWidth, Height = ViewportHeight },
                AcceptDownloads = false,
                ServiceWorkers = ServiceWorkerPolicy.Block,
            }).ConfigureAwait(false);

            var created = new SessionPage(context, await context.NewPageAsync().ConfigureAwait(false));
            await context.RouteAsync("**/*", async route =>
            {
                if (_policy.RefuseRequest(route.Request.Url) is { } why)
                {
                    created.RecordFailure($"{route.Request.Method} {route.Request.Url}: refused by the studio ({why})");
                    await route.AbortAsync("blockedbyclient").ConfigureAwait(false);
                    return;
                }

                await route.ContinueAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);

            // A navigation the page starts itself (a link a person clicked in the pane, a form, a redirect) is announced
            // when it lands, with the new title, so the pane and the next answer describe the page that is there now.
            created.Page.Load += (_, _) => _ = RefreshAsync(session, created);
            created.Page.FrameNavigated += (_, frame) =>
            {
                if (frame == created.Page.MainFrame)
                {
                    if (frame.Url.StartsWith("chrome-error:", StringComparison.Ordinal))
                    {
                        created.ErrorPageShown?.TrySetResult();
                    }

                    Changed?.Invoke(session);
                }
            };

            created.Page.Console += (_, message) =>
            {
                if (message.Type == "error")
                {
                    created.RecordConsole(message.Text);
                }
            };
            created.Page.PageError += (_, error) => created.RecordConsole(error);
            created.Page.RequestFailed += (_, request) =>
            {
                created.RecordFailure($"{request.Method} {request.Url}: {request.Failure}");
                if (request.IsNavigationRequest && request.Frame == created.Page.MainFrame)
                {
                    created.FailedNavigation = new FailedNavigation(request.Url, request.Failure);
                }
            };
            created.Page.Response += (_, response) =>
            {
                // The status is the main frame's last navigation, whoever started it, not only an open's.
                if (response.Request.IsNavigationRequest && response.Frame == created.Page.MainFrame)
                {
                    created.Status = response.Status;
                }

                if (response.Status >= 400)
                {
                    created.RecordFailure($"{response.Request.Method} {response.Url}: HTTP {response.Status}");
                }
            };

            created.LastUse = _clock();
            _pages[session] = created;
            return (created, null);
        }
        catch (PlaywrightException ex)
        {
            _launchFailure = Clean(ex.Message);
            _launchFailedAt = _clock();
            _logger.LogWarning(ex, "The agent browser could not start.");
            return (null, "The browser could not start: " + _launchFailure);
        }
        finally
        {
            _launchGate.Release();
        }
    }

    private async Task<IBrowser> BrowserAsync()
    {
        if (_browser is { IsConnected: true } running)
        {
            return running;
        }

        _playwright ??= await Playwright.CreateAsync().ConfigureAwait(false);
        _browser = await _playwright.Chromium.LaunchAsync(LaunchOptions(_jitless)).ConfigureAwait(false);
        return _browser;
    }

    /// <summary>
    /// How every browser the studio starts is launched: headless, with these hardening arguments (V8 without its JIT
    /// unless <paramref name="jitless"/> is false) and <see cref="CleanEnvironment"/>. The design check (backlog 223)
    /// launches with these too, so the two cannot drift apart.
    /// </summary>
    public static BrowserTypeLaunchOptions LaunchOptions(bool jitless)
    {
        var args = new List<string>
        {
            "--disable-dev-shm-usage",
            "--disable-gpu",
            "--disable-extensions",
            "--disable-background-networking",
            "--disable-component-update",
            "--disable-sync",
            "--no-first-run",
            "--mute-audio",
            "--disable-3d-apis",
        };
        if (jitless)
        {
            args.Add("--js-flags=--jitless");
        }

        return new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = args,
            Env = CleanEnvironment(),
        };
    }

    /// <summary>
    /// The browser's environment, set explicitly: the process basics a renderer needs and nothing else, so no token the
    /// studio holds is in a renderer's environment even if the driver's wrapper were bypassed.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string>> CleanEnvironment()
    {
        string[] names = ["PATH", "HOME", "LANG", "LANGUAGE", "TZ", "TMPDIR", "FONTCONFIG_PATH"];
        foreach (var name in names)
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                yield return new(name, value);
            }
        }
    }

    private async Task SweepAsync()
    {
        try
        {
            var now = _clock();
            foreach (var (session, page) in _pages.ToArray())
            {
                if (now - page.LastUse > IdleSession)
                {
                    await CloseAsync(session).ConfigureAwait(false);
                }
            }

            if (_pages.IsEmpty && _browser is not null && now - _lastUse > IdleBrowser)
            {
                await CloseBrowserAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is PlaywrightException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "The agent browser sweep found nothing to close cleanly.");
        }
    }

    private async Task CloseBrowserAsync()
    {
        await _launchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_browser is not null)
            {
                await _browser.CloseAsync().ConfigureAwait(false);
                _browser = null;
            }

            _playwright?.Dispose();
            _playwright = null;
        }
        catch (PlaywrightException ex)
        {
            _logger.LogDebug(ex, "The agent browser was already gone.");
        }
        finally
        {
            _launchGate.Release();
        }
    }

    /// <summary>
    /// Why no browser can run here, or null when the browsers are where Playwright will look for them. Checked once, at
    /// start: the image bakes them under PLAYWRIGHT_BROWSERS_PATH, and a dev host normally has none. The design check
    /// looks in the same places and nowhere else.
    /// </summary>
    internal static string? FindBrowsers(string? browsersPath)
    {
        var path = browsersPath ?? Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            path = OperatingSystem.IsWindows()
                ? Path.Combine(local, "ms-playwright")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "ms-playwright");
        }

        return Directory.Exists(path) && Directory.EnumerateDirectories(path, "chromium*").Any()
            ? null
            : "This studio has no browser installed. The studio image bakes one; a studio run outside it has none.";
    }

    private static string Clean(string message)
    {
        var first = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
            ?? "the browser did not say why";
        return first.Length <= 400 ? first : first[..400];
    }

    /// <summary>A main-frame navigation that failed, as Chromium reported it (set from the page's event thread).</summary>
    private sealed record FailedNavigation(string Url, string? Failure);

    /// <summary>One session's context and page, and what the page has said so far.</summary>
    private sealed class SessionPage(IBrowserContext context, IPage page)
    {
        private readonly Lock _records = new();
        private readonly List<string> _console = [];
        private readonly List<string> _failures = [];
        private string _title = string.Empty;
        private FailedNavigation? _failedNavigation;
        private TaskCompletionSource? _errorPageShown;

        public IBrowserContext Context { get; } = context;

        public IPage Page { get; } = page;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public DateTimeOffset LastUse { get; set; }

        public int? Status { get; set; }

        /// <summary>The main frame's last navigation that failed since the current act began, with Chromium's text.</summary>
        /// <summary>Completed when the main frame commits Chromium's error page during the current act.</summary>
        public TaskCompletionSource? ErrorPageShown
        {
            get => Volatile.Read(ref _errorPageShown);
            set => Volatile.Write(ref _errorPageShown, value);
        }

        public FailedNavigation? FailedNavigation
        {
            get => Volatile.Read(ref _failedNavigation);
            set => Volatile.Write(ref _failedNavigation, value);
        }

        public void ResetRecords()
        {
            lock (_records)
            {
                _console.Clear();
                _failures.Clear();
            }
        }

        public void RecordConsole(string text) => Record(_console, text);

        public void RecordFailure(string text) => Record(_failures, text);

        public async Task<BrowserPageState> StateAsync()
        {
            _title = await Page.TitleAsync().ConfigureAwait(false);
            return State();
        }

        public async Task<BrowserPageState?> SafeStateAsync()
        {
            try
            {
                return await StateAsync().ConfigureAwait(false);
            }
            catch (PlaywrightException)
            {
                return State();
            }
        }

        public BrowserPageState State()
        {
            lock (_records)
            {
                return new BrowserPageState(Page.Url, _title, Status, [.. _console], [.. _failures]);
            }
        }

        private void Record(List<string> list, string text)
        {
            lock (_records)
            {
                list.Add(text.Length <= 300 ? text : text[..300]);
                if (list.Count > MaxRecorded)
                {
                    list.RemoveAt(0);
                }
            }
        }
    }
}
