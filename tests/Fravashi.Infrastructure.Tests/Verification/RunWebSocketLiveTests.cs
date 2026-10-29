using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Fravashi.Application.Verification;
using Fravashi.Infrastructure.Browser;
using Fravashi.Infrastructure.Verification;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Verification;

/// <summary>
/// Backlog 292's live tier (plan @107): the REAL <see cref="PlaywrightDesignCheck"/> with a window marker, the run on the
/// container's own PRIVATE, non-loopback address. The marked context answers the run's navigation through its route, so
/// Chromium sees a document with no address of its own and its local network access checks refused the page's
/// same-origin WebSocket (Blazor's circuit) until the run's origin alone was treated as secure and granted
/// <c>local-network-access</c>. A WebSocket to any other origin is still closed by the route and recorded. Runs only where
/// <c>FRAVASHI_BROWSER_LIVE=1</c> is set and the machine has a non-loopback IPv4 address.
/// </summary>
public sealed class RunWebSocketLiveTests : IAsyncLifetime
{
    private const string Token = "0123456789" + "abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly ConcurrentQueue<string> _atRun = new();
    private readonly ConcurrentQueue<string> _elsewhere = new();
    private HttpListener? _run;
    private HttpListener? _other;
    private string _runUrl = string.Empty;
    private string _otherUrl = string.Empty;

    private static bool Enabled => Environment.GetEnvironmentVariable("FRAVASHI_BROWSER_LIVE") == "1";

    private static readonly IPAddress? InterfaceAddress = NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address)
        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)
            && !(a.GetAddressBytes() is [169, 254, ..]));

    private sealed class PrivateAddressLiveFactAttribute : FactAttribute
    {
        public PrivateAddressLiveFactAttribute() => Skip = !Enabled
            ? "Set FRAVASHI_BROWSER_LIVE=1 where a Chromium is installed (the studio container) to drive a real browser."
            : InterfaceAddress is null
                ? "This machine has no non-loopback IPv4 address to put the run on."
                : null;
    }

    public Task InitializeAsync()
    {
        if (!Enabled || InterfaceAddress is null)
        {
            return Task.CompletedTask;
        }

        // The run listens on every address and is reached by the interface's, which is private and not loopback.
        var runPort = FreePort();
        _runUrl = $"http://{InterfaceAddress}:{runPort}/";
        _run = Start($"http://*:{runPort}/", _atRun, RunPage);
        _otherUrl = $"http://127.0.0.1:{FreePort()}/";
        _other = Start(_otherUrl, _elsewhere, _ => (200, "text/plain", "elsewhere"));
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _run?.Close();
        _other?.Close();
        return Task.CompletedTask;
    }

    private (int Status, string Type, string Body) RunPage(string path) => path switch
    {
        "/pages/socket" => (200, "text/html; charset=utf-8", Faithful(Socket("'ws://' + location.host + '/socket'"))),
        "/pages/foreign-socket" => (200, "text/html; charset=utf-8", Faithful(Socket($"'{_otherUrl.Replace("http://", "ws://", StringComparison.Ordinal)}socket'"))),
        _ when path.StartsWith("/ws-settled/", StringComparison.Ordinal) => (200, "text/plain", "noted"),
        _ => (404, "text/plain", "not found"),
    };

    /// <summary>A page script that opens a WebSocket to <paramref name="address"/>, says hi, and tells the run how it went.</summary>
    private static string Socket(string address) =>
        "<script>(() => { let told = false; const tell = how => { if (!told) { told = true; fetch('/ws-settled/' + how); } };"
        + $" const ws = new WebSocket({address});"
        + " ws.onopen = () => ws.send('hi');"
        + " ws.onmessage = e => tell(e.data === 'echo:hi' ? 'echoed' : 'odd');"
        + " ws.onerror = () => tell('failed'); ws.onclose = () => tell('closed'); })();</script>";

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

    private async Task<string> SettledAsync()
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until)
        {
            if (_atRun.FirstOrDefault(p => p.StartsWith("GET /ws-settled/", StringComparison.Ordinal)) is { } settled)
            {
                return settled["GET /ws-settled/".Length..];
            }

            await Task.Delay(50);
        }

        return "never";
    }

    [PrivateAddressLiveFact]
    public async Task The_runs_own_websocket_on_a_private_address_connects_with_nothing_on_the_console()
    {
        var marker = WindowMarker.For(_runUrl, Token)!;

        var result = await Check().CheckAsync(
            new DesignCheckRequest(Design(), [new DesignCheckTarget(0, _runUrl + "pages/socket")]) { Marker = marker });

        result.Refusal.Should().BeNull();
        var screen = result.Screens.Should().ContainSingle().Subject;
        (await SettledAsync()).Should().Be("echoed", "the run saw: " + string.Join(" | ", _atRun) + " | console: " + string.Join("; ", screen.Build.Console));
        _atRun.Should().Contain("WS /socket");
        screen.Build.Console.Should().BeEmpty();
        screen.Build.Report.Matches.Should().BeTrue(string.Join("; ", screen.Build.Report.Findings.Select(f => f.Sentence)));
    }

    [PrivateAddressLiveFact]
    public async Task A_websocket_to_another_origin_is_still_closed_and_recorded()
    {
        var marker = WindowMarker.For(_runUrl, Token)!;

        var result = await Check().CheckAsync(
            new DesignCheckRequest(Design(), [new DesignCheckTarget(0, _runUrl + "pages/foreign-socket")]) { Marker = marker });

        result.Refusal.Should().BeNull();
        var screen = result.Screens.Should().ContainSingle().Subject;
        (await SettledAsync()).Should().BeOneOf("closed", "failed");
        await Task.Delay(TimeSpan.FromSeconds(1));
        _elsewhere.Should().BeEmpty("a marked context opens no WebSocket to another origin");
        var foreign = _otherUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "socket";
        screen.Build.Console.Should().Contain(c => c.Contains(foreign, StringComparison.Ordinal) && c.Contains(marker.Refusal, StringComparison.Ordinal));
        screen.Build.Console.Should().OnlyContain(c => !c.Contains(Token, StringComparison.Ordinal));
    }

    /// <summary>A listener that records each request as "METHOD /path" (a WebSocket as "WS /path") and echoes on a socket.</summary>
    private static HttpListener Start(string prefix, ConcurrentQueue<string> seen, Func<string, (int Status, string Type, string Body)> answer)
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
                if (context.Request.IsWebSocketRequest)
                {
                    seen.Enqueue("WS " + path);
                    _ = EchoAsync(context);
                    continue;
                }

                seen.Enqueue(context.Request.HttpMethod + " " + path);
                var (status, type, body) = answer(path);
                context.Response.StatusCode = status;
                context.Response.ContentType = type;
                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        });
        return listener;
    }

    private static async Task EchoAsync(HttpListenerContext context)
    {
        try
        {
            using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            var buffer = new byte[1024];
            while (socket.State == WebSocketState.Open)
            {
                var received = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                var reply = Encoding.UTF8.GetBytes("echo:" + Encoding.UTF8.GetString(buffer, 0, received.Count));
                await socket.SendAsync(reply, WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or HttpListenerException or ObjectDisposedException)
        {
            // The browser went away; nothing to record.
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
