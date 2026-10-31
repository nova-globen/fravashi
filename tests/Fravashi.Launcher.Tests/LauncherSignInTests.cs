using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Launcher;
using Xunit;

namespace Fravashi.Launcher.Tests;

/// <summary>
/// Backlog 94: the launcher signs the person in BEFORE the container starts, host-side, and hands the result to the
/// studio. The identity provider here is a stub, and the "browser" is a test that follows the authorize URL's
/// redirect by making the real HTTP request to the launcher's real loopback listener, so the listener, the state
/// check and the code exchange are all exercised for real.
/// </summary>
public sealed class LauncherSignInTests
{
    private const string Issuer = "https://idp.example.test";

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static string IdToken(string sub, string? nonce)
    {
        static string B64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("{\"alg\":\"none\"}")}.{B64(JsonSerializer.Serialize(new { sub, nonce }))}.sig";
    }

    private sealed class StubIdp(Func<string, string, (HttpStatusCode, string)> token) : HttpMessageHandler
    {
        public string? TokenForm { get; private set; }

        public string? NonceSent { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    authorization_endpoint = $"{Issuer}/connect/authorize",
                    token_endpoint = $"{Issuer}/connect/token",
                }));
            }

            if (path.EndsWith("/connect/token", StringComparison.Ordinal))
            {
                TokenForm = await request.Content!.ReadAsStringAsync(cancellationToken);
                var (status, body) = token(TokenForm, NonceSent ?? string.Empty);
                return Json(status, body);
            }

            return Json(HttpStatusCode.NotFound, "{}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    /// <summary>A browser that signs in instantly: it follows the authorize URL to the redirect with the given answer.</summary>
    private static Action<string> Browser(StubIdp idp, Func<IReadOnlyDictionary<string, string>, string> redirectQuery, List<string>? opened = null) =>
        url =>
        {
            opened?.Add(url);
            var query = new Uri(url).Query.TrimStart('?').Split('&')
                .Select(p => p.Split('=', 2))
                .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
            idp.NonceSent = query["nonce"];
            var redirect = query["redirect_uri"] + "?" + redirectQuery(query);
            _ = Task.Run(async () =>
            {
                using var client = new HttpClient();
                await client.GetAsync(redirect.Replace("localhost", "127.0.0.1", StringComparison.Ordinal));
            });
        };

    private static StubIdp Idp(bool nonceMatches = true, HttpStatusCode status = HttpStatusCode.OK) =>
        new((form, nonce) => status == HttpStatusCode.OK
            ? (status, JsonSerializer.Serialize(new
            {
                access_token = "access-1",
                refresh_token = "refresh-1",
                id_token = IdToken("user-1", nonceMatches ? nonce : "someone-elses"),
                expires_in = 3600,
            }))
            : (status, "{\"error\":\"invalid_grant\"}"));

    private static Task<LauncherSignInResult> SignIn(StubIdp idp, Action<string> browser, int port, TimeSpan? wait = null) =>
        new LauncherSignIn(new HttpClient(idp), browser).SignInAsync(
            new LauncherSignInTarget(Issuer, "fravashi-studio", "openid profile offline_access", port),
            wait ?? TimeSpan.FromSeconds(10),
            CancellationToken.None);

    /// <summary>
    /// A sign-in on a port that was free a moment ago. Another test running at the same time can take that port between
    /// <see cref="FreePort"/> and the listener (seen once in CI, 2026-10-30: "port 36089 is in use"), and then the
    /// sign-in stops before it fetches or opens anything, so it is simply tried again on another port.
    /// </summary>
    private static async Task<(LauncherSignInResult Result, int Port)> SignInOnFreePort(
        StubIdp idp, Action<string> browser, TimeSpan? wait = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            var port = FreePort();
            var result = await SignIn(idp, browser, port, wait);
            if (attempt == 5 || result.Session is not null || !result.Outcome.Contains($"port {port} is in use", StringComparison.Ordinal))
            {
                return (result, port);
            }
        }
    }

    [Fact]
    public async Task A_completed_browser_sign_in_ends_in_a_session_for_the_studio()
    {
        var idp = Idp();
        var opened = new List<string>();
        var (result, port) = await SignInOnFreePort(idp, Browser(idp, q => $"code=the-code&state={q["state"]}", opened));

        result.Session.Should().NotBeNull(result.Outcome);
        result.Session!.AccessToken.Should().Be("access-1");
        result.Session.RefreshToken.Should().Be("refresh-1");
        result.Session.Issuer.Should().Be(Issuer);
        result.Session.ExpiresUtc.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(50));
        opened.Should().ContainSingle().Which.Should().StartWith($"{Issuer}/connect/authorize?")
            .And.Contain("code_challenge_method=S256")
            .And.Contain(Uri.EscapeDataString($"http://localhost:{port}/auth/callback/idp"));
        idp.TokenForm.Should().Contain("code=the-code").And.Contain("code_verifier=")
            .And.NotContain("client_secret", "the launcher never holds one");
    }

    [Fact]
    public async Task The_code_verifier_sent_to_the_token_endpoint_is_the_one_the_challenge_was_made_from()
    {
        var idp = Idp();
        string? challenge = null;
        await SignInOnFreePort(idp, Browser(idp, q => { challenge = q["code_challenge"]; return $"code=c&state={q["state"]}"; }));

        var verifier = Uri.UnescapeDataString(idp.TokenForm!.Split('&').Single(p => p.StartsWith("code_verifier=", StringComparison.Ordinal))["code_verifier=".Length..]);
        LauncherSignIn.Challenge(verifier).Should().Be(challenge);
    }

    [Fact]
    public async Task A_redirect_carrying_another_state_is_refused_and_nothing_is_exchanged()
    {
        var idp = Idp();

        var (result, _) = await SignInOnFreePort(idp, Browser(idp, _ => "code=c&state=forged"));

        result.Session.Should().BeNull();
        result.Outcome.Should().Contain("state");
        idp.TokenForm.Should().BeNull();
    }

    [Fact]
    public async Task A_provider_refusal_ends_without_a_session()
    {
        var idp = Idp();

        var (result, _) = await SignInOnFreePort(idp, Browser(idp, q => $"error=access_denied&state={q["state"]}"));

        result.Session.Should().BeNull();
        result.Outcome.Should().Contain("access_denied");
    }

    [Fact]
    public async Task A_refused_exchange_ends_without_a_session()
    {
        var idp = Idp(status: HttpStatusCode.BadRequest);

        var (result, _) = await SignInOnFreePort(idp, Browser(idp, q => $"code=c&state={q["state"]}"));

        result.Session.Should().BeNull();
        result.Outcome.Should().Contain("400");
    }

    [Fact]
    public async Task An_id_token_from_another_sign_in_is_refused()
    {
        var idp = Idp(nonceMatches: false);

        var (result, _) = await SignInOnFreePort(idp, Browser(idp, q => $"code=c&state={q["state"]}"));

        result.Session.Should().BeNull();
        result.Outcome.Should().Contain("nonce");
    }

    [Fact]
    public async Task Nobody_finishing_in_the_browser_ends_the_wait_rather_than_the_start()
    {
        var idp = Idp();

        var (result, _) = await SignInOnFreePort(idp, _ => { }, TimeSpan.FromMilliseconds(300));

        result.Session.Should().BeNull();
        result.Outcome.Should().Contain("within");
    }

    [Fact]
    public async Task A_port_the_studio_is_already_serving_is_left_alone()
    {
        var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Start();
        try
        {
            var opened = new List<string>();
            var idp = Idp();

            var result = await SignIn(idp, Browser(idp, _ => string.Empty, opened), ((IPEndPoint)busy.LocalEndpoint).Port);

            result.Session.Should().BeNull();
            result.Outcome.Should().Contain("in use");
            opened.Should().BeEmpty("no browser is opened for a sign-in that cannot be answered");
        }
        finally
        {
            busy.Stop();
        }
    }

    [Fact]
    public async Task Other_requests_to_the_listener_are_turned_away_and_the_wait_goes_on()
    {
        var port = FreePort();
        using var callback = LoopbackCallback.TryListen(port, LauncherSignIn.CallbackPath)!;
        var waiting = callback.WaitAsync(CancellationToken.None);
        using var client = new HttpClient();

        var stray = await client.GetAsync($"http://127.0.0.1:{port}/favicon.ico");
        var real = await client.GetAsync($"http://127.0.0.1:{port}/auth/callback/idp?code=abc&state=s%20t");
        var answer = await waiting;

        stray.StatusCode.Should().Be(HttpStatusCode.NotFound);
        real.StatusCode.Should().Be(HttpStatusCode.OK);
        (await real.Content.ReadAsStringAsync()).Should().Contain("signed in");
        answer["code"].Should().Be("abc");
        answer["state"].Should().Be("s t");
    }

    [Fact]
    public async Task An_idle_preconnect_opened_first_does_not_hold_up_or_end_the_real_redirect()
    {
        // Measured in the installed v0.9.31 on 2026-09-23: Edge opened a speculative connection that sent nothing,
        // the listener waited on it, the real redirect queued behind it, and when Edge dropped the idle socket a
        // minute later the whole sign-in failed. Reproduced here with an idle socket and one that drops at once.
        var port = FreePort();
        using var callback = LoopbackCallback.TryListen(port, LauncherSignIn.CallbackPath)!;
        var waiting = callback.WaitAsync(CancellationToken.None);

        using var idle = new TcpClient();
        await idle.ConnectAsync(IPAddress.Loopback, port);
        using (var dropped = new TcpClient())
        {
            await dropped.ConnectAsync(IPAddress.Loopback, port);
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var real = await client.GetAsync($"http://127.0.0.1:{port}/auth/callback/idp?code=abc&state=s");
        var finished = await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(5)));

        real.StatusCode.Should().Be(HttpStatusCode.OK);
        finished.Should().BeSameAs(waiting, "the redirect is answered while the idle connection is still open");
        (await waiting)["code"].Should().Be("abc");
    }

    [Theory]
    [InlineData(true, true, false, "running")]
    [InlineData(false, false, false, "not said")]
    [InlineData(false, true, true, "usable")]
    public void The_launcher_signs_in_only_when_the_studio_would_otherwise_ask(
        bool studioAnswers, bool coordinatesKnown, bool sessionUsable, string because)
    {
        LauncherSignIn.SkipReason(coordinatesKnown, sessionUsable, studioAnswers).Should().Contain(because);
    }

    [Fact]
    public void A_stopped_studio_whose_session_is_gone_is_signed_in_for()
    {
        LauncherSignIn.SkipReason(coordinatesKnown: true, sessionUsable: false, studioAnswers: false).Should().BeNull();
    }
}
