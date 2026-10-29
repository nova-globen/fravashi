using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Browser;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Browser;

/// <summary>
/// Backlog 159's live tier: a REAL headless Chromium through the real <see cref="PlaywrightAgentBrowser"/>, against a
/// page this test serves on loopback. It runs where a browser is installed and <c>FRAVASHI_BROWSER_LIVE=1</c> is set
/// (the studio container, where the image bakes one), and says why it did not run everywhere else. Unit tests assert
/// the studio's own model back to itself; this is what shows the driver, the wrapper and the browser agree.
/// </summary>
public sealed class AgentBrowserLiveTests : IAsyncLifetime
{
    private const string Html = """
        <!doctype html><html><head><title>Proof page</title></head><body>
        <h1>Agent browser proof</h1>
        <label for="q">Search</label><input id="q" />
        <button onclick="document.getElementById('out').textContent = 'clicked:' + document.getElementById('q').value">Go</button>
        <button>Twin</button><button>Twin</button>
        <p id="out">idle</p>
        <a href="/next">Onwards</a>
        <script>console.error('a console error for the proof');</script>
        <img src="http://127.0.0.1:1/never.png" />
        </body></html>
        """;

    private const string NextHtml = """
        <!doctype html><html><head><title>Next page</title></head><body><h1>Arrived</h1></body></html>
        """;

    // One link covering the whole viewport, so a click by COORDINATES (what the pane sends) lands on it.
    private const string WholeLinkHtml = """
        <!doctype html><html><head><title>Whole link</title></head><body>
        <a href="/next" style="display:block;position:fixed;inset:0">Anywhere</a>
        </body></html>
        """;

    // A form whose submit goes to a name that does not resolve (backlog 233).
    private const string FormHtml = """
        <!doctype html><html><head><title>Form page</title></head><body>
        <form action="http://fravashi-netfault.invalid/submit" method="post"><input name="q" value="x" /><button type="submit">Send</button></form>
        </body></html>
        """;

    private readonly AgentSessionId _session = AgentSessionId.New();
    private HttpListener? _server;
    private string _url = string.Empty;
    private PlaywrightAgentBrowser? _browser;

    private static bool Enabled => Environment.GetEnvironmentVariable("FRAVASHI_BROWSER_LIVE") == "1";

    /// <summary>Skipped with its reason at discovery time, because Assert.Skip needs xunit v3.</summary>
    private sealed class BrowserLiveFactAttribute : FactAttribute
    {
        public BrowserLiveFactAttribute() => Skip = Enabled
            ? null
            : "Set FRAVASHI_BROWSER_LIVE=1 where a Chromium is installed (the studio container) to drive a real browser.";
    }

    public Task InitializeAsync()
    {
        if (!Enabled)
        {
            return Task.CompletedTask;
        }

        var port = FreePort();
        _url = $"http://127.0.0.1:{port}/";
        _server = new HttpListener();
        _server.Prefixes.Add(_url);
        _server.Start();
        _ = Task.Run(async () =>
        {
            while (_server.IsListening)
            {
                try
                {
                    var context = await _server.GetContextAsync();
                    var path = context.Request.Url?.AbsolutePath;
                    var bytes = Encoding.UTF8.GetBytes(path == "/next" ? NextHtml : path == "/whole" ? WholeLinkHtml : path == "/form" ? FormHtml : Html);

                    // The second page answers with its own status, so an answer carrying the first page's 200 is caught.
                    context.Response.StatusCode = path == "/next" ? 203 : 200;
                    context.Response.ContentType = "text/html";
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
                catch (HttpListenerException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        });

        // Loopback is allowed so the test can serve its page; one loopback port stays refused to prove the check runs.
        _browser = new PlaywrightAgentBrowser(new BrowserUrlPolicy(["http://127.0.0.1:1/"], allowLoopback: true));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }

        _server?.Close();
    }

    [BrowserLiveFact]
    public async Task A_real_browser_opens_reads_acts_and_screenshots_a_page_and_refuses_what_it_must()
    {
        var browser = _browser!;
        browser.IsAvailable.Should().BeTrue(browser.UnavailableReason);

        var opened = await browser.OpenAsync(_session, _url);
        opened.Ok.Should().BeTrue(opened.Refusal);
        opened.Page!.Title.Should().Be("Proof page");
        opened.Page.Status.Should().Be(200);
        opened.Page.ConsoleErrors.Should().Contain(e => e.Contains("a console error for the proof"));
        opened.Page.FailedRequests.Should().Contain(f => f.Contains("refused by the studio"), "the image on a refused port was stopped");

        var (read, text, _) = await browser.ReadAsync(_session, BrowserReadMode.Text, 10_000);
        read.Ok.Should().BeTrue();
        text.Should().Contain("Agent browser proof");

        var (_, outline, _) = await browser.ReadAsync(_session, BrowserReadMode.Outline, 10_000);
        outline.Should().Contain("heading").And.Contain("button \"Go\"");

        (await browser.FillAsync(_session, new BrowserTarget(Label: "Search"), "hello", submit: false)).Ok.Should().BeTrue();
        (await browser.ClickAsync(_session, new BrowserTarget("button", "Go"))).Ok.Should().BeTrue();
        (await browser.ReadAsync(_session, BrowserReadMode.Text, 10_000)).Content.Should().Contain("clicked:hello");

        // A click that navigates answers with the page it navigated TO: its address, its title and its status (a
        // defect seen in the window on 2026-09-27 answered with the old address and the first page's status).
        var onwards = await browser.ClickAsync(_session, new BrowserTarget("link", "Onwards"));
        onwards.Ok.Should().BeTrue(onwards.Refusal);
        onwards.Page!.Url.Should().EndWith("/next");
        onwards.Page.Title.Should().Be("Next page");
        onwards.Page.Status.Should().Be(203, "the status is the page navigated to, not the first page's");

        // The pane's click is by coordinates, which does not wait for a navigation the way a locator click does.
        (await browser.OpenAsync(_session, _url + "whole")).Ok.Should().BeTrue();
        var changed = 0;
        browser.Changed += session => { if (session == _session) { Interlocked.Increment(ref changed); } };
        var byMouse = await browser.ClickAtAsync(_session, 640, 400);
        byMouse.Ok.Should().BeTrue(byMouse.Refusal);
        byMouse.Page!.Url.Should().EndWith("/next", "a click that navigates answers with where it went");
        byMouse.Page.Title.Should().Be("Next page");
        byMouse.Page.Status.Should().Be(203);
        browser.Current(_session)!.Title.Should().Be("Next page", "the pane reads the page that is there now");
        changed.Should().BeGreaterThan(0);
        (await browser.OpenAsync(_session, _url)).Ok.Should().BeTrue();

        var twin = await browser.ClickAsync(_session, new BrowserTarget(Text: "Twin"));
        twin.Ok.Should().BeFalse();
        twin.Refusal.Should().Contain("2 elements match");

        var (shot, jpeg) = await browser.ScreenshotAsync(_session, fullPage: false);
        shot.Ok.Should().BeTrue();
        jpeg.Should().NotBeNull();
        jpeg![0].Should().Be(0xFF, "a JPEG starts FF D8");
        jpeg[1].Should().Be(0xD8);

        (await browser.OpenAsync(_session, "file:///etc/passwd")).Refusal.Should().Contain("http and https");

        await browser.CloseAsync(_session);
        browser.Current(_session).Should().BeNull();
    }

    [BrowserLiveFact]
    public async Task A_network_failure_is_said_in_words_the_page_survives_and_a_submit_that_failed_is_unknown()
    {
        var browser = _browser!;

        // A name under .invalid never resolves (RFC 6761), so this breaks nothing but the one navigation.
        var failed = await browser.OpenAsync(_session, "http://fravashi-netfault.invalid/");
        failed.Ok.Should().BeFalse();
        failed.Network.Should().NotBeNull(failed.Refusal);
        failed.Network!.Failure.Kind.Should().Be(Fravashi.Application.Network.NetworkFailureKind.NameNotResolved);
        failed.Network.RetrySafe.Should().BeTrue();
        failed.Refusal.Should().StartWith("Could not reach fravashi-netfault.invalid (name not resolved).");
        browser.Current(_session).Should().NotBeNull("the session's page is kept, so the agent can simply open again");

        var closedPort = FreePort();
        var refused = await browser.OpenAsync(_session, $"http://127.0.0.1:{closedPort}/");
        refused.Network!.Failure.Kind.Should().Be(Fravashi.Application.Network.NetworkFailureKind.Refused);

        var next = await browser.OpenAsync(_session, _url + "form");
        next.Ok.Should().BeTrue(next.Refusal);
        next.Page!.Title.Should().Be("Form page");

        var submitted = await browser.ClickAsync(_session, new BrowserTarget("button", "Send"));
        submitted.Ok.Should().BeFalse();
        submitted.Network.Should().NotBeNull(submitted.Refusal);
        submitted.Network!.OutcomeUnknown.Should().BeTrue();
        submitted.Network.Host.Should().Be("fravashi-netfault.invalid");
        submitted.Refusal.Should().StartWith("The click was made");

        // A click that leads nowhere new is not blamed on the stale failure.
        (await browser.OpenAsync(_session, _url)).Ok.Should().BeTrue();
        var go = await browser.ClickAsync(_session, new BrowserTarget("button", "Go"));
        go.Ok.Should().BeTrue(go.Refusal);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
