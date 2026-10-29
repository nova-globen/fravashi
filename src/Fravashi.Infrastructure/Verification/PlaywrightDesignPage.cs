using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Fravashi.Application.Verification;
using Microsoft.Playwright;

namespace Fravashi.Infrastructure.Verification;

/// <summary>
/// <see cref="IDesignPage"/> over one Playwright page (backlog 223, plan @47). It loads either an address (the build) or
/// a rendered HTML document (the prototype, set as the page's content, so no server and no request), and bounds every
/// call: a load 30 seconds, anything else 10. A failure or a timeout becomes a <see cref="DesignPageException"/>.
/// </summary>
public sealed class PlaywrightDesignPage : IDesignPage
{
    /// <summary>The longest a load may take.</summary>
    public static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The longest a snapshot, a probe, an act or a picture may take.</summary>
    public static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a dialog phase waits for a dialog to show before reading whatever is there.</summary>
    public static readonly TimeSpan DialogWait = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Measures one element: its box, the WCAG 2 contrast of its visible text (computed colour against the first
    /// non-transparent background up its ancestors, white when there is none; a colour Chromium does not report as
    /// <c>rgb()</c> is not measured), and the text its <c>aria-labelledby</c> points at.
    /// </summary>
    internal const string ProbeScript = """
        (el, ask) => {
          const out = { box: null, contrast: null, labelledBy: null };
          if (ask.box) {
            const r = el.getBoundingClientRect();
            if (r.width > 0 || r.height > 0) { out.box = { x: r.x, y: r.y, width: r.width, height: r.height }; }
          }
          if (ask.contrast && (el.innerText || "").trim().length > 0) {
            const parse = c => {
              const m = /^rgba?\(([^)]*)\)$/.exec(c || "");
              if (!m) { return null; }
              const p = m[1].split(/[\s,\/]+/).filter(s => s.length > 0).map(Number);
              return p.length >= 3 ? { r: p[0], g: p[1], b: p[2], a: p.length > 3 ? p[3] : 1 } : null;
            };
            let bg = null;
            for (let n = el; n && n.nodeType === 1; n = n.parentElement) {
              const b = parse(getComputedStyle(n).backgroundColor);
              if (b && b.a > 0) { bg = b; break; }
            }
            const white = { r: 255, g: 255, b: 255, a: 1 };
            const over = (c, under) => ({ r: c.r * c.a + under.r * (1 - c.a), g: c.g * c.a + under.g * (1 - c.a), b: c.b * c.a + under.b * (1 - c.a), a: 1 });
            bg = bg ? over(bg, white) : white;
            const fg = parse(getComputedStyle(el).color);
            if (fg) {
              const lin = v => { v = v / 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4); };
              const lum = c => 0.2126 * lin(c.r) + 0.7152 * lin(c.g) + 0.0722 * lin(c.b);
              const a = lum(over(fg, bg)), b = lum(bg);
              out.contrast = (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
            }
          }
          if (ask.name) {
            const ids = (el.getAttribute("aria-labelledby") || "").split(/\s+/).filter(s => s.length > 0);
            const parts = ids.map(id => document.getElementById(id)).filter(n => n)
              .map(n => (n.textContent || "").replace(/\s+/g, " ").trim()).filter(s => s.length > 0);
            if (parts.length > 0) { out.labelledBy = parts.join(" "); }
          }
          return out;
        }
        """;

    private const string Frames = "() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(() => r(true))))";

    private readonly IPage _page;
    private readonly string? _url;
    private readonly string? _html;
    private readonly ConcurrentQueue<string> _said = new();

    private PlaywrightDesignPage(IPage page, string? url, string? html)
    {
        _page = page;
        _url = url;
        _html = html;
        page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                _said.Enqueue("console error: " + message.Text);
            }
        };
        page.PageError += (_, error) => _said.Enqueue("page error: " + error);
        page.RequestFailed += (_, request) =>
        {
            // A request the policy aborted is recorded by the route, with the policy's reason.
            if (request.Failure?.Contains("ERR_BLOCKED_BY_CLIENT", StringComparison.Ordinal) != true)
            {
                _said.Enqueue($"request failed: {request.Method} {request.Url}: {request.Failure}");
            }
        };
        page.Response += (_, response) =>
        {
            if (response.Status >= 400)
            {
                _said.Enqueue($"HTTP {response.Status}: {response.Request.Method} {response.Url}");
            }
        };
    }

    /// <summary>A page that loads <paramref name="url"/> (the build).</summary>
    public static PlaywrightDesignPage ForAddress(IPage page, string url) => new(page, url, null);

    /// <summary>A page that shows <paramref name="html"/> (the prototype).</summary>
    public static PlaywrightDesignPage ForDocument(IPage page, string html) => new(page, null, html);

    /// <summary>Records what the route refused, so it is returned with the page's other complaints.</summary>
    public void Refused(string method, string url, string why) => _said.Enqueue($"refused by the studio: {method} {url} ({why})");

    /// <inheritdoc />
    public Task SetViewportAsync(int width, int height, CancellationToken cancellationToken) =>
        BoundAsync(() => _page.SetViewportSizeAsync(width, height), StepTimeout, "setting the viewport", cancellationToken);

    /// <inheritdoc />
    public Task LoadAsync(CancellationToken cancellationToken) => BoundAsync(async () =>
    {
        if (_html is not null)
        {
            await _page.SetContentAsync(_html, new PageSetContentOptions
            {
                WaitUntil = WaitUntilState.Load,
                Timeout = (float)LoadTimeout.TotalMilliseconds,
            }).ConfigureAwait(false);
            return;
        }

        await _page.GotoAsync(_url!, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.Load,
            Timeout = (float)LoadTimeout.TotalMilliseconds,
        }).ConfigureAwait(false);
    }, LoadTimeout + StepTimeout, "loading the page", cancellationToken);

    /// <inheritdoc />
    public async Task SettleAsync(bool dialog, CancellationToken cancellationToken)
    {
        // A click that navigates starts the navigation a moment after it returns (the agent browser learned this).
        await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        await QuietlyAsync(() => _page.WaitForLoadStateAsync(LoadState.Load, new PageWaitForLoadStateOptions
        {
            Timeout = (float)StepTimeout.TotalMilliseconds,
        }), cancellationToken).ConfigureAwait(false);
        if (dialog)
        {
            await QuietlyAsync(() => _page.Locator("dialog[open], [role=dialog], [role=alertdialog]").First.WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = (float)DialogWait.TotalMilliseconds }),
                cancellationToken).ConfigureAwait(false);
        }

        await QuietlyAsync(() => _page.EvaluateAsync<bool>(Frames), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<string> SnapshotAsync(CancellationToken cancellationToken) => BoundAsync(
        () => _page.Locator("body").AriaSnapshotAsync(new LocatorAriaSnapshotOptions
        {
            Mode = AriaSnapshotMode.Ai,
            Timeout = (float)StepTimeout.TotalMilliseconds,
        }),
        StepTimeout + StepTimeout, "reading the page's outline", cancellationToken);

    /// <inheritdoc />
    public Task<DesignProbe> ProbeAsync(string reference, string role, DesignProbeAsk ask, CancellationToken cancellationToken) =>
        BoundAsync(async () =>
        {
            var element = ByReference(reference);
            var measured = await element.EvaluateAsync<Measured>(
                ProbeScript,
                new { box = ask.Box, contrast = ask.Contrast, name = ask.Name },
                new LocatorEvaluateOptions { Timeout = (float)StepTimeout.TotalMilliseconds }).ConfigureAwait(false);

            // The candidate is only a name if the browser's own role engine computes the same one for this element.
            string? name = null;
            if (measured.LabelledBy is { } candidate && Enum.TryParse<AriaRole>(role, ignoreCase: true, out var aria))
            {
                var same = await element.And(_page.GetByRole(aria, new PageGetByRoleOptions { Name = candidate, Exact = true }))
                    .CountAsync().ConfigureAwait(false);
                name = same == 1 ? candidate : null;
            }

            var box = measured.Box is { } b ? new ObservedBox(b.X, b.Y, b.Width, b.Height) : null;
            return new DesignProbe(box, measured.Contrast, name);
        }, StepTimeout + StepTimeout, $"measuring {reference}", cancellationToken);

    /// <inheritdoc />
    public Task ActAsync(string reference, DesignPageAct act, CancellationToken cancellationToken) => BoundAsync(async () =>
    {
        var element = ByReference(reference);
        var timeout = (float)StepTimeout.TotalMilliseconds;
        switch (act)
        {
            case DesignPageAct.ChooseSecondOption:
                await element.SelectOptionAsync(new SelectOptionValue { Index = 1 }, new LocatorSelectOptionOptions { Timeout = timeout })
                    .ConfigureAwait(false);
                break;
            case DesignPageAct.Type:
                await element.FillAsync("a", new LocatorFillOptions { Timeout = timeout }).ConfigureAwait(false);
                await element.PressAsync("Tab", new LocatorPressOptions { Timeout = timeout }).ConfigureAwait(false);
                break;
            default:
                await element.ClickAsync(new LocatorClickOptions { Timeout = timeout }).ConfigureAwait(false);
                break;
        }
    }, StepTimeout + StepTimeout, $"acting on {reference}", cancellationToken);

    /// <inheritdoc />
    public Task<byte[]> ScreenshotAsync(CancellationToken cancellationToken) => BoundAsync(
        () => _page.ScreenshotAsync(new PageScreenshotOptions
        {
            Type = ScreenshotType.Png,
            Timeout = (float)StepTimeout.TotalMilliseconds,
        }),
        StepTimeout + StepTimeout, "taking the picture", cancellationToken);

    /// <inheritdoc />
    public IReadOnlyList<string> TakeConsole()
    {
        var taken = new List<string>();
        while (_said.TryDequeue(out var entry))
        {
            taken.Add(entry);
        }

        return taken;
    }

    private ILocator ByReference(string reference) => _page.Locator("aria-ref=" + reference);

    private static async Task BoundAsync(Func<Task> act, TimeSpan limit, string doing, CancellationToken cancellationToken) =>
        await BoundAsync(async () =>
        {
            await act().ConfigureAwait(false);
            return true;
        }, limit, doing, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Runs a page call under its own limit and the check's token. Playwright's own timeouts sit inside the limit; the
    /// outer one is the backstop for a call that has none.
    /// </summary>
    private static async Task<T> BoundAsync<T>(Func<Task<T>> act, TimeSpan limit, string doing, CancellationToken cancellationToken)
    {
        try
        {
            return await act().WaitAsync(limit, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new DesignPageException($"{doing} took longer than {limit.TotalSeconds:0} seconds", ex);
        }
        catch (PlaywrightException ex)
        {
            throw new DesignPageException($"{doing} failed: {FirstLine(ex.Message)}", ex);
        }
    }

    private static async Task QuietlyAsync(Func<Task> wait, CancellationToken cancellationToken)
    {
        try
        {
            await wait().WaitAsync(StepTimeout + StepTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
        {
            // A page still busy is still a page; what is read next says where it is.
        }
    }

    private static string FirstLine(string message)
    {
        var first = message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
            ?? "the browser did not say why";
        return first.Length <= 300 ? first : first[..300];
    }

    private sealed class Measured
    {
        [JsonPropertyName("box")]
        public MeasuredBox? Box { get; set; }

        [JsonPropertyName("contrast")]
        public double? Contrast { get; set; }

        [JsonPropertyName("labelledBy")]
        public string? LabelledBy { get; set; }
    }

    private sealed class MeasuredBox
    {
        [JsonPropertyName("x")]
        public double X { get; set; }

        [JsonPropertyName("y")]
        public double Y { get; set; }

        [JsonPropertyName("width")]
        public double Width { get; set; }

        [JsonPropertyName("height")]
        public double Height { get; set; }
    }
}
