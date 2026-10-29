using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Fravashi.Application.Verification;
using Fravashi.Infrastructure.Browser;
using Fravashi.Infrastructure.Verification;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Verification;

/// <summary>
/// Backlog 226's live tier (plan @69): the REAL <see cref="PlaywrightDesignCheck"/> in a real headless Chromium, with a
/// window marker. One server plays the run; a second, on another port and so another origin, plays everything else. The
/// run's page reaches the other origin twice, by an image and by a same-origin request the run REDIRECTS to it, and the
/// navigation itself starts with a same-origin redirect. Every request the run receives must carry the marker and its
/// token; the other origin must never receive the token at all. Runs only where <c>FRAVASHI_BROWSER_LIVE=1</c> is set,
/// like <see cref="DesignCheckLiveTests"/>.
/// </summary>
public sealed class WindowMarkerLiveTests : IAsyncLifetime
{
    private const string Token = "fedcba9876" + "543210fedcba9876543210fedcba9876543210fedcba9876543210";

    private readonly ConcurrentQueue<Arrived> _atRun = new();
    private readonly ConcurrentQueue<Arrived> _elsewhere = new();
    private HttpListener? _run;
    private HttpListener? _other;
    private HttpListener? _elsewhereOffLoopback;
    private string _runUrl = string.Empty;
    private string _otherUrl = string.Empty;
    private string _offLoopbackUrl = string.Empty;

    private static bool Enabled => Environment.GetEnvironmentVariable("FRAVASHI_BROWSER_LIVE") == "1";

    private sealed class BrowserLiveFactAttribute : FactAttribute
    {
        public BrowserLiveFactAttribute() => Skip = Enabled
            ? null
            : "Set FRAVASHI_BROWSER_LIVE=1 where a Chromium is installed (the studio container) to drive a real browser.";
    }

    /// <summary>
    /// The container's own non-loopback IPv4 address, or null. Backlog 254 puts the other origin there, because a fetch
    /// between two loopback origins can be stopped by Chromium's own address-space rule, which would hide the route.
    /// </summary>
    private static readonly IPAddress? InterfaceAddress = NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address)
        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)
            && !(a.GetAddressBytes() is [169, 254, ..]));

    private sealed class OffLoopbackLiveFactAttribute : FactAttribute
    {
        public OffLoopbackLiveFactAttribute() => Skip = !Enabled
            ? "Set FRAVASHI_BROWSER_LIVE=1 where a Chromium is installed (the studio container) to drive a real browser."
            : InterfaceAddress is null
                ? "This machine has no non-loopback IPv4 address to put the other origin on."
                : null;
    }

    public Task InitializeAsync()
    {
        if (!Enabled)
        {
            return Task.CompletedTask;
        }

        _runUrl = $"http://127.0.0.1:{FreePort()}/";
        _otherUrl = $"http://127.0.0.1:{FreePort()}/";
        _run = Start(_runUrl, _atRun, RunPage, cors: false);
        _other = Start(_otherUrl, _elsewhere, _ => (200, "text/plain", "elsewhere", null), cors: true);
        if (InterfaceAddress is not null)
        {
            // Listening on every address; the page reaches it by the interface's address, which is not loopback.
            var port = FreePort();
            _offLoopbackUrl = $"http://{InterfaceAddress}:{port}/";
            _elsewhereOffLoopback = Start($"http://*:{port}/", _elsewhere, _ => (200, "text/plain", "elsewhere", null), cors: true);
        }

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _run?.Close();
        _other?.Close();
        _elsewhereOffLoopback?.Close();
        return Task.CompletedTask;
    }

    /// <summary>The run: <c>/start</c> redirects to the page, the page reaches the other origin, <c>/away</c> redirects there.</summary>
    private (int Status, string Type, string Body, string? Location) RunPage(string path) => path switch
    {
        "/start" => (302, "text/plain", string.Empty, "/pages/faithful"),
        "/away" => (302, "text/plain", string.Empty, _otherUrl + "landed"),
        "/bounce" => (302, "text/plain", string.Empty, "/bounce-again"),
        "/bounce-again" => (307, "text/plain", string.Empty, _otherUrl + "chained"),
        "/pages/faithful" => (200, "text/html; charset=utf-8", Faithful(
                $"<img src=\"{_otherUrl}pixel.png\" alt=\"\"><script>fetch('/away').catch(() => {{}});</script>"), null),
        // Backlog 254: a cross-origin fetch with a header that is not CORS-safelisted, which makes a real browser send a
        // preflight OPTIONS first. Once the fetch has settled either way, the page tells the run so on its own origin.
        "/pages/preflight" => (200, "text/html; charset=utf-8", Faithful(
                $"<script>fetch('{_offLoopbackUrl}probe', {{ headers: {{ 'X-Probe': '1' }} }})"
                + ".then(() => fetch('/fetch-settled/done'), () => fetch('/fetch-settled/failed'));</script>"), null),
        "/fetch-settled/done" or "/fetch-settled/failed" => (200, "text/plain", "noted", null),
        _ => (404, "text/plain", "not found", null),
    };

    private static string Faithful(string beforeBodyEnds) => File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Verification", "Pages", "faithful.html"))
        .Replace("</body>", beforeBodyEnds + "</body>", StringComparison.Ordinal);

    private static MockupDocument Design()
    {
        var read = MockupReader.Read(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mockups", "Fixtures", "team-directory.attempt-3.json")));
        read.IsValid.Should().BeTrue(string.Join("; ", read.Errors));
        return read.Document!;
    }

    private static PlaywrightDesignCheck Check() => new(new BrowserUrlPolicy(["http://127.0.0.1:1/"], allowLoopback: true));

    [BrowserLiveFact]
    public async Task The_marker_reaches_the_run_through_its_redirects_and_never_the_other_origin()
    {
        var marker = WindowMarker.For(_runUrl, Token)!;

        var result = await Check().CheckAsync(
            new DesignCheckRequest(Design(), [new DesignCheckTarget(0, _runUrl + "start")]) { Marker = marker });

        result.Refusal.Should().BeNull();
        var screen = result.Screens.Should().ContainSingle().Subject;
        screen.Build.Report.Matches.Should().BeTrue(
            "the page arrived through the run's own redirect: "
            + string.Join("; ", screen.Build.Report.Findings.Select(f => f.Sentence)) + " | " + string.Join("; ", screen.Build.Console));

        _atRun.Select(r => r.Path).Should().Contain(["/start", "/pages/faithful", "/away"]);
        _atRun.Should().OnlyContain(r => r.Agent.Contains($"FravashiWindow/1.0 tk/{Token}", StringComparison.Ordinal),
            "every request the run receives carries the marker");
        _elsewhere.Should().OnlyContain(
            r => !r.Agent.Contains(Token, StringComparison.Ordinal) && !r.Agent.Contains("FravashiWindow", StringComparison.Ordinal),
            "the other origin received: " + string.Join(", ", _elsewhere.Select(r => r.Path)));
        _elsewhere.Should().BeEmpty("a marked context never contacts another origin, by a link or by a redirect");

        screen.Build.Console.Should().Contain(c => c.Contains(_otherUrl + "pixel.png", StringComparison.Ordinal) && c.Contains(marker.Refusal, StringComparison.Ordinal));
        screen.Build.Console.Should().Contain(c => c.Contains(_otherUrl + "landed", StringComparison.Ordinal), "the redirect target was refused, not followed");
        screen.Build.Console.Concat(screen.Prototype.Console).Should().OnlyContain(c => !c.Contains(Token, StringComparison.Ordinal));
    }

    [BrowserLiveFact]
    public async Task A_navigation_whose_redirects_leave_the_run_after_a_hop_inside_it_is_refused_before_anything_leaves()
    {
        var marker = WindowMarker.For(_runUrl, Token)!;

        var result = await Check().CheckAsync(
            new DesignCheckRequest(Design(), [new DesignCheckTarget(0, _runUrl + "bounce")]) { Marker = marker });

        var screen = result.Screens.Should().ContainSingle().Subject;
        screen.Build.Report.Matches.Should().BeFalse("the page was never loaded");
        _elsewhere.Should().BeEmpty("the chain was followed by the route, and its last hop refused");
        screen.Build.Console.Should().Contain(c => c.Contains(_otherUrl + "chained", StringComparison.Ordinal) && c.Contains(marker.Refusal, StringComparison.Ordinal));
        _atRun.Should().OnlyContain(r => r.Agent.Contains($"tk/{Token}", StringComparison.Ordinal));
    }

    /// <summary>
    /// Backlog 254: a CORS preflight is the one request the route might never be asked about, and it would carry the user
    /// agent. The other origin records every request its socket accepts, so this asserts what ARRIVED there, and it
    /// answers a preflight permissively so that a preflight which got through would be followed by the GET as well.
    /// <para>
    /// The other origin is on the container's own interface address, not loopback. Measured 2026-10-08 in Chromium
    /// 153.0.8010.12 with it at 172.17.0.2: the route is asked about the GET itself and refuses it, and nothing of any
    /// method reaches the other origin. With the route continuing every request instead (so the page loads from loopback),
    /// the other origin received the GET carrying the marker, once per page load, and no OPTIONS at all. With only the
    /// refusal removed, the run's page (fulfilled by the route) was stopped by Chromium's own rule ("Permission was denied
    /// for this request to access the `local` address space") before anything left, so the console assertion, which
    /// needs the route's own refusal, is the one that catches that case.
    /// </para>
    /// </summary>
    [OffLoopbackLiveFact]
    public async Task A_cross_origin_fetch_that_needs_a_preflight_sends_nothing_to_the_other_origin()
    {
        var marker = WindowMarker.For(_runUrl, Token)!;

        var result = await Check().CheckAsync(
            new DesignCheckRequest(Design(), [new DesignCheckTarget(0, _runUrl + "pages/preflight")]) { Marker = marker });

        result.Refusal.Should().BeNull();
        var screen = result.Screens.Should().ContainSingle().Subject;

        // The page says on its own origin when the fetch has settled, so the measurement waits on the browser rather than
        // guessing; one more second covers a request to the other origin that was already on its way.
        var settled = DateTime.UtcNow.AddSeconds(10);
        while (!_atRun.Any(r => r.Path.StartsWith("/fetch-settled/", StringComparison.Ordinal)) && DateTime.UtcNow < settled)
        {
            await Task.Delay(50);
        }

        _atRun.Should().Contain(r => r.Path.StartsWith("/fetch-settled/", StringComparison.Ordinal),
            "the page's fetch must have settled, or nothing was measured; the run saw: " + string.Join(" | ", _atRun));
        await Task.Delay(TimeSpan.FromSeconds(1));

        _elsewhere.Should().BeEmpty(
            "a marked context sends nothing to another origin, not even a preflight; it received: " + string.Join(" | ", _elsewhere));
        _atRun.Select(r => r.Path).Should().Contain(["/pages/preflight", "/fetch-settled/failed"]);
        _atRun.Should().OnlyContain(r => r.Agent.Contains($"FravashiWindow/1.0 tk/{Token}", StringComparison.Ordinal),
            "the run's own origin still receives the marker on every request");
        screen.Build.Console.Should().Contain(c => c.Contains(_offLoopbackUrl + "probe", StringComparison.Ordinal) && c.Contains(marker.Refusal, StringComparison.Ordinal));
        screen.Build.Console.Should().OnlyContain(c => !c.Contains(Token, StringComparison.Ordinal));
    }

    [BrowserLiveFact]
    public async Task Without_a_marker_the_check_presents_nothing_and_the_other_origin_is_reached_as_before()
    {
        var result = await Check().CheckAsync(new DesignCheckRequest(Design(), [new DesignCheckTarget(0, _runUrl + "pages/faithful")]));

        result.Refusal.Should().BeNull();
        _atRun.Should().NotBeEmpty().And.OnlyContain(r => !r.Agent.Contains("FravashiWindow", StringComparison.Ordinal));
        _elsewhere.Select(r => r.Path).Should().Contain("/pixel.png");
    }

    /// <summary>One request as it arrived at a listener's socket, recorded before anything decides how to answer it.</summary>
    private sealed record Arrived(string Method, string Path, string Agent, string Origin, string PreflightMethod, string PreflightHeaders)
    {
        public override string ToString() =>
            $"{Method} {Path} user-agent=[{Agent}] origin=[{Origin}] access-control-request-method=[{PreflightMethod}] "
            + $"access-control-request-headers=[{PreflightHeaders}]";
    }

    private static HttpListener Start(
        string prefix,
        ConcurrentQueue<Arrived> seen,
        Func<string, (int Status, string Type, string Body, string? Location)> answer,
        bool cors)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                var path = context.Request.Url?.AbsolutePath ?? "/";
                var request = context.Request;
                seen.Enqueue(new Arrived(
                    request.HttpMethod,
                    path,
                    request.UserAgent ?? string.Empty,
                    request.Headers["Origin"] ?? string.Empty,
                    request.Headers["Access-Control-Request-Method"] ?? string.Empty,
                    request.Headers["Access-Control-Request-Headers"] ?? string.Empty));
                var (status, type, body, location) = answer(path);
                context.Response.StatusCode = status;
                if (cors)
                {
                    // Permissive on purpose (backlog 254): a preflight that got through is then followed by the real request.
                    context.Response.AddHeader("Access-Control-Allow-Origin", "*");
                    context.Response.AddHeader("Access-Control-Allow-Methods", "GET");
                    context.Response.AddHeader("Access-Control-Allow-Headers", "X-Probe");
                }

                context.Response.ContentType = type;
                if (location is not null)
                {
                    context.Response.RedirectLocation = location;
                }

                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        });
        return listener;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
