using System.Globalization;
using Fravashi.Application.Prototypes;
using Fravashi.Application.Verification;
using Fravashi.Infrastructure.Browser;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;

namespace Fravashi.Infrastructure.Verification;

/// <summary>
/// <see cref="IDesignCheck"/> on the Chromium and Playwright the image already ships (backlog 223, ADR 0030, plan @47).
/// <para>
/// Each check starts its OWN browser, with the agent browser's launch (<see cref="PlaywrightAgentBrowser.LaunchOptions"/>),
/// opens one context for the build and one for the prototype, and closes everything when it ends: a check is rare, so
/// there is no idle browser to keep, and nothing carries from one check or one side to the next. Every request either
/// context makes, subresources included, passes <see cref="BrowserUrlPolicy"/>, and the build's addresses are checked
/// before anything starts. The prototype is the pinned tree rendered by <see cref="PrototypeRenderer"/> and set as the
/// page's content, so it needs no server and makes no request.
/// </para>
/// </summary>
public sealed class PlaywrightDesignCheck : IDesignCheck
{
    private readonly BrowserUrlPolicy _policy;
    private readonly bool _jitless;
    private readonly ILogger _logger;

    public PlaywrightDesignCheck(
        BrowserUrlPolicy policy,
        bool jitless = true,
        ILogger<PlaywrightDesignCheck>? logger = null,
        string? browsersPath = null)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _jitless = jitless;
        _logger = logger ?? NullLogger<PlaywrightDesignCheck>.Instance;
        UnavailableReason = PlaywrightAgentBrowser.FindBrowsers(browsersPath);
    }

    /// <inheritdoc />
    public string? UnavailableReason { get; }

    /// <summary>
    /// Why <paramref name="request"/> cannot be checked at all, or null: no target, a screen the design does not have, or
    /// an address the policy refuses. Asked before a browser starts.
    /// </summary>
    public string? Refuse(DesignCheckRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Targets.Count == 0)
        {
            return "Say which address of the build shows which screen of the design.";
        }

        foreach (var target in request.Targets)
        {
            if (target.Screen < 0 || target.Screen >= request.Design.Screens.Count)
            {
                return string.Create(
                    CultureInfo.InvariantCulture, $"The design has no screen {target.Screen}; it has {request.Design.Screens.Count}.");
            }

            if (_policy.RefuseToOpen(target.Url) is { } why)
            {
                return $"The check will not open {target.Url}: {why}";
            }
        }

        return request.Budget > TimeSpan.Zero ? null : "A check needs some time to run.";
    }

    /// <inheritdoc />
    public async Task<DesignCheckResult> CheckAsync(DesignCheckRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Refuse(request) is { } refusal)
        {
            return DesignCheckResult.Refused(refusal);
        }

        if (UnavailableReason is { } unavailable)
        {
            return DesignCheckResult.Refused(unavailable);
        }

        var expectations = DesignExpectations.For(request.Design);
        using var budget = new CancellationTokenSource(request.Budget);
        var spent = string.Create(
            CultureInfo.InvariantCulture, $"the check ran out of its {request.Budget.TotalMinutes:0.#} minutes before reaching it");

        IPlaywright? playwright = null;
        IBrowser? browser = null;
        try
        {
            try
            {
                playwright = await Playwright.CreateAsync().ConfigureAwait(false);
                browser = await playwright.Chromium.LaunchAsync(LaunchOptions(_jitless, request.Marker)).ConfigureAwait(false);
            }
            catch (PlaywrightException ex)
            {
                _logger.LogWarning(ex, "The design check's browser could not start.");
                return DesignCheckResult.Refused("The browser could not start: " + ex.Message.Split('\n')[0]);
            }

            var screens = new List<DesignScreenCheck>();
            foreach (var target in request.Targets)
            {
                var screen = expectations.Screens[target.Screen];
                var html = PrototypeRenderer.Render(request.Design, request.Overlay, target.Screen).Html;
                // Backlog 226: only the build's side carries the run's window marker; the prototype's never does.
                var build = await SideAsync(
                    browser, screen, p => PlaywrightDesignPage.ForAddress(p, target.Url.Trim()), budget.Token, spent, cancellationToken,
                    request.Marker).ConfigureAwait(false);
                var prototype = await SideAsync(
                    browser, screen, p => PlaywrightDesignPage.ForDocument(p, html), budget.Token, spent, cancellationToken)
                    .ConfigureAwait(false);
                screens.Add(new DesignScreenCheck(target.Screen, target.Url, build, prototype));
            }

            return new DesignCheckResult(screens, null);
        }
        finally
        {
            if (browser is not null)
            {
                await QuietlyAsync(() => browser.CloseAsync()).ConfigureAwait(false);
            }

            playwright?.Dispose();
        }
    }

    /// <summary>
    /// The check's launch: the agent browser's, and with a <paramref name="marker"/> the run's origin alone treated as a
    /// secure context (backlog 292). Chromium's local network access checks refuse every request from an insecure
    /// document to a private address, granted or not, and the route's fulfilled navigation leaves the run's page with no
    /// address of its own, so its same-origin WebSocket was refused. Measured in the pinned Chromium (plan @107): the
    /// permission alone and this switch alone each still refuse; the two together connect. Switching the feature off
    /// also connects but drops the checks for every origin, so it is not used.
    /// </summary>
    public static BrowserTypeLaunchOptions LaunchOptions(bool jitless, WindowMarker? marker)
    {
        var launch = PlaywrightAgentBrowser.LaunchOptions(jitless);
        var origin = marker?.Origin.GetLeftPart(UriPartial.Authority);
        if (origin is not null && !origin.Contains(',', StringComparison.Ordinal))
        {
            launch.Args = [.. launch.Args ?? [], "--unsafely-treat-insecure-origin-as-secure=" + origin];
        }

        return launch;
    }

    /// <summary>The user agent a marked context starts from: a plain Chromium one, the marker is added to it.</summary>
    internal static string BaseUserAgent(string browserVersion) =>
        $"Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{browserVersion} Safari/537.36";

    /// <summary>
    /// One side of one screen, in a context of its own that is closed when the side is done.
    /// <para>
    /// Backlog 226: with a <paramref name="marker"/>, the context's user agent carries the run's window marker, because a
    /// WebSocket (Blazor's circuit) cannot be given a header by a route, and the ROUTE confines it: a request to any
    /// origin but the run's is refused, a request to the run's origin is fetched with redirects off and fulfilled, so a
    /// redirect comes back here as a new request and is decided again, and a WebSocket to any other origin is closed
    /// before it connects and recorded. The marker therefore reaches the run's address and nothing else. Backlog 292: the
    /// same context is granted local network access for the run's origin alone, so the run's own WebSocket connects.
    /// </para>
    /// </summary>
    private async Task<DesignCheckSide> SideAsync(
        IBrowser browser,
        DesignScreen screen,
        Func<IPage, PlaywrightDesignPage> open,
        CancellationToken budget,
        string spent,
        CancellationToken cancellationToken,
        WindowMarker? marker = null)
    {
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = DesignCheckDriver.BaseWidth, Height = DesignCheckDriver.ViewportHeight },
            AcceptDownloads = false,
            ServiceWorkers = ServiceWorkerPolicy.Block,
            UserAgent = marker?.UserAgent(BaseUserAgent(browser.Version)),
        }).ConfigureAwait(false);
        try
        {
            var page = open(await context.NewPageAsync().ConfigureAwait(false));
            if (marker is not null)
            {
                // Backlog 292: the route fulfils the run's navigation, so Chromium sees a document with no address of its
                // own and its local network access checks refused the page's same-origin WebSocket to the run's private
                // address. The permission goes to the run's origin only, which the launch treats as secure (LaunchOptions).
                await context.GrantPermissionsAsync(
                    [LocalNetworkAccess],
                    new BrowserContextGrantPermissionsOptions { Origin = marker.Origin.GetLeftPart(UriPartial.Authority) })
                    .ConfigureAwait(false);
                await context.RouteWebSocketAsync(url => !marker.Covers(url), async socket =>
                {
                    page.Refused("WEBSOCKET", socket.Url, marker.Refusal);
                    await socket.CloseAsync().ConfigureAwait(false);
                }).ConfigureAwait(false);
            }

            await context.RouteAsync("**/*", async route =>
            {
                if (_policy.RefuseRequest(route.Request.Url) is { } why)
                {
                    page.Refused(route.Request.Method, route.Request.Url, why);
                    await route.AbortAsync("blockedbyclient").ConfigureAwait(false);
                    return;
                }

                switch (marker?.Route(route.Request.Url))
                {
                    case WindowMarkerRoute.Refuse:
                        page.Refused(route.Request.Method, route.Request.Url, marker.Refusal);
                        await route.AbortAsync("blockedbyclient").ConfigureAwait(false);
                        return;

                    case WindowMarkerRoute.Carry:
                        await CarryAsync(route, page, marker).ConfigureAwait(false);
                        return;

                    default:
                        await route.ContinueAsync().ConfigureAwait(false);
                        return;
                }
            }).ConfigureAwait(false);

            var drive = await DesignCheckDriver.DriveAsync(page, screen, budget, spent, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<string> console = marker is null ? drive.Console : [.. drive.Console.Select(marker.Withhold)];
            return new DesignCheckSide(
                drive.Observation, DesignMatcher.Match(screen, drive.Observation), drive.Screenshots, console);
        }
        finally
        {
            await QuietlyAsync(() => context.CloseAsync()).ConfigureAwait(false);
        }
    }

    /// <summary>Playwright's name for Chromium's local and loopback network permissions (backlog 292).</summary>
    internal const string LocalNetworkAccess = "local-network-access";

    /// <summary>How many redirects a request to the run may take before it is refused.</summary>
    internal const int MaxRedirects = 10;

    /// <summary>
    /// A request to the run's own origin (backlog 226). The browser follows a redirect WITHOUT asking the route again
    /// (measured: a <c>fetch</c> to the run that redirected elsewhere reached the other origin with the marker), so the
    /// route follows the chain itself first, with redirects off, and every hop must be the run's origin and pass the
    /// policy; one that is not refuses the whole request. A subresource is then answered with the chain's final
    /// response, so the browser follows nothing. A navigation is answered with its first response, so the page's address
    /// is right, and the browser repeats the chain just checked against the run.
    /// </summary>
    private async Task CarryAsync(IRoute route, PlaywrightDesignPage page, WindowMarker marker)
    {
        try
        {
            var first = await route.FetchAsync(new RouteFetchOptions { MaxRedirects = 0 }).ConfigureAwait(false);
            var current = first;
            var url = route.Request.Url;
            var method = route.Request.Method;
            for (var hop = 0; RedirectTarget(current, url) is { } next; hop++)
            {
                var refusal = hop >= MaxRedirects
                    ? $"more than {MaxRedirects} redirects"
                    : _policy.RefuseRequest(next) ?? (marker.Route(next) == WindowMarkerRoute.Carry ? null : marker.Refusal);
                if (refusal is not null)
                {
                    page.Refused(method, next, refusal);
                    await route.AbortAsync("blockedbyclient").ConfigureAwait(false);
                    return;
                }

                // 307 and 308 keep the method and body; every other redirect is followed with a GET, as a browser does.
                method = current.Status is 307 or 308 ? method : "GET";
                url = next;
                current = await route.FetchAsync(new RouteFetchOptions
                {
                    Url = next,
                    Method = method,
                    MaxRedirects = 0,
                    PostData = method == route.Request.Method ? route.Request.PostDataBuffer : null,
                }).ConfigureAwait(false);
            }

            await route.FulfillAsync(new RouteFulfillOptions
            {
                Response = route.Request.IsNavigationRequest ? first : current,
            }).ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            // Deliberately NOT logged with the exception: Playwright's call log in its message lists the request's
            // headers, and the user agent there carries the run's token.
            _logger.LogDebug("A request to the verification run could not be fetched.");
            await QuietlyAsync(() => route.AbortAsync("failed")).ConfigureAwait(false);
        }
    }

    /// <summary>Where a redirect response sends the browser next, resolved against <paramref name="from"/>, or null when it is not one.</summary>
    private static string? RedirectTarget(IAPIResponse response, string from) =>
        response.Status is 301 or 302 or 303 or 307 or 308
        && response.Headers.TryGetValue("location", out var location)
        && Uri.TryCreate(new Uri(from), location, out var next)
            ? next.AbsoluteUri
            : null;

    private async Task QuietlyAsync(Func<Task> close)
    {
        try
        {
            await close().ConfigureAwait(false);
        }
        catch (PlaywrightException ex)
        {
            _logger.LogDebug(ex, "The design check's browser was already gone.");
        }
    }
}
