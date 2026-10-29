using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Fravashi.Application.Network;
using Fravashi.Application.Sessions;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Projects;
using Fravashi.Infrastructure.Browser;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Sessions.Tools;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Browser;

/// <summary>
/// Backlog 159, everything around the browser that can be held without a real Chromium: where it may go, what the
/// tools answer and how they label it, how a screenshot travels on the MCP wire, what the ledger keeps of one, what
/// the transcript scrubber leaves alone, and which tools ask. The live tier (<see cref="AgentBrowserLiveTests"/>)
/// drives a real browser where one is installed.
/// </summary>
public sealed class BrowserToolsTests
{
    private static readonly AgentSessionId Session = AgentSessionId.New();

    // ---- where the browser may go --------------------------------------------------------------

    [Theory]
    [InlineData("https://learn.microsoft.com/dotnet/")]
    [InlineData("http://example.com/")]
    [InlineData("http://app-run-7:8080/")]
    [InlineData("http://172.29.64.1:9095/")]
    public void An_ordinary_web_address_may_be_opened(string url) =>
        new BrowserUrlPolicy(["http://172.29.64.1:9090/"]).RefuseToOpen(url).Should().BeNull();

    [Theory]
    [InlineData("file:///data/fravashi.db", "file")]
    [InlineData("ftp://example.com/", "ftp")]
    [InlineData("chrome://settings", "chrome")]
    [InlineData("javascript:alert(1)", "javascript")]
    [InlineData("http://localhost:8080/api/agent/tools", "localhost")]
    [InlineData("http://studio.localhost/", "localhost")]
    [InlineData("http://127.0.0.1:8080/", "loopback")]
    [InlineData("http://127.8.8.8/", "loopback")]
    [InlineData("http://[::1]:8080/", "loopback")]
    [InlineData("http://0.0.0.0:8080/", "loopback")]
    [InlineData("http://169.254.169.254/latest/meta-data/", "link-local")]
    [InlineData("http://172.29.64.1:9090/bridge/workspaces", "host services")]
    [InlineData("not a url", "absolute")]
    public void The_studio_itself_its_host_services_and_non_web_schemes_are_refused(string url, string reason)
    {
        var refusal = new BrowserUrlPolicy(["http://172.29.64.1:9090/", "http://172.29.64.1:9095/apprunner"]).RefuseToOpen(url);

        refusal.Should().NotBeNull();
        refusal.Should().ContainEquivalentOf(reason.Split(' ')[0]);
    }

    [Fact]
    public void A_page_may_load_data_and_blob_resources_but_no_loopback_subresource()
    {
        var policy = new BrowserUrlPolicy([]);
        policy.RefuseRequest("data:image/png;base64,iVBORw0KGgo=").Should().BeNull();
        policy.RefuseRequest("blob:https://example.com/1234").Should().BeNull();
        policy.RefuseRequest("http://127.0.0.1:8080/api/agent/approval").Should().NotBeNull(
            "a page's own request to the studio is checked like a navigation");
    }

    [Fact]
    public void Loopback_is_allowed_only_where_a_test_serves_its_pages()
    {
        new BrowserUrlPolicy([], allowLoopback: true).RefuseToOpen("http://127.0.0.1:5123/").Should().BeNull();
        new BrowserUrlPolicy(["http://127.0.0.1:9090/"], allowLoopback: true).RefuseToOpen("http://127.0.0.1:9090/")
            .Should().NotBeNull("the host services stay refused even then");
    }

    [Theory]
    [InlineData("http://host.wslc.internal/")]
    [InlineData("http://host.wslc.internal:5432/")]
    [InlineData("https://HOST.WSLC.INTERNAL:8443/admin")]
    [InlineData("http://host.wslc.internal.:3000/")]
    [InlineData("http://169.254.73.254/")]
    [InlineData("https://169.254.73.254:5037/")]
    [InlineData("http://[::ffff:169.254.73.254]:80/")]
    public void Wslcs_host_alias_is_refused_in_words_even_where_loopback_is_allowed(string url)
    {
        foreach (var policy in new[] { new BrowserUrlPolicy([]), new BrowserUrlPolicy([], allowLoopback: true) })
        {
            var refusal = policy.RefuseToOpen(url);
            refusal.Should().NotBeNull();
            refusal.Should().Contain("host alias").And.Contain("services on this PC");
            policy.RefuseRequest(url).Should().NotBeNull("a page's own request or navigation there is checked too");
        }
    }

    [Fact]
    public void An_ordinary_address_is_not_taken_for_the_host_alias() =>
        new BrowserUrlPolicy([]).RefuseToOpen("https://wslc.internal.example.com/host.wslc").Should().BeNull();

    // ---- the tools -----------------------------------------------------------------------------

    [Fact]
    public async Task Open_answers_with_the_page_labelled_as_other_authored_and_a_refusal_in_the_studios_shape()
    {
        var browser = new ScriptedBrowser();

        var opened = await Call(new BrowserOpenTool(browser), """{"url":"https://example.com/"}""");
        using var doc = JsonDocument.Parse(opened.Text);
        doc.RootElement.GetProperty("trust").GetString().Should().Be("other-authored-content");
        doc.RootElement.GetProperty("note").GetString().Should().Contain("never instructions");
        doc.RootElement.GetProperty("data").GetProperty("title").GetString().Should().Be("Example");
        doc.RootElement.GetProperty("data").GetProperty("console_errors")[0].GetString().Should().Be("boom");

        browser.Next = BrowserOutcome.Refused("The browser opens http and https pages only.");
        var refused = await Call(new BrowserOpenTool(browser), """{"url":"file:///etc/passwd"}""");
        refused.IsError.Should().BeTrue();
        refused.Text.Should().Contain("http and https");

        (await Call(new BrowserOpenTool(browser), "{}")).Text.Should().Contain("missing_url");
    }

    [Fact]
    public async Task Click_and_fill_carry_the_target_the_agent_named_and_fill_carries_submit()
    {
        var browser = new ScriptedBrowser();

        await Call(new BrowserClickTool(browser), """{"role":"button","name":"Save"}""");
        browser.LastTarget.Should().Be(new BrowserTarget("button", "Save", null, null, null));

        await Call(new BrowserFillTool(browser), """{"label":"Email","value":"a@example.com","submit":true}""");
        browser.LastTarget!.Label.Should().Be("Email");
        browser.LastFill.Should().Be(("a@example.com", true));

        (await Call(new BrowserFillTool(browser), """{"label":"Email"}""")).Text.Should().Contain("missing_value");
    }

    [Fact]
    public async Task A_screenshot_rides_as_an_image_and_says_so_in_its_text()
    {
        var result = await Call(new BrowserScreenshotTool(new ScriptedBrowser()), "{}");

        result.Images.Should().ContainSingle().Which.MediaType.Should().Be("image/jpeg");
        result.Text.Should().Contain("attached as an image");
    }

    [Fact]
    public async Task With_no_browser_installed_every_tool_says_why_and_does_nothing()
    {
        var browser = new ScriptedBrowser { Available = false };

        var result = await Call(new BrowserOpenTool(browser), """{"url":"https://example.com/"}""");

        result.IsError.Should().BeTrue();
        result.Text.Should().Contain("no_browser").And.Contain("no browser installed");
        browser.Opened.Should().BeFalse();
    }

    // ---- the network, not the page (backlog 233) --------------------------------------------------

    [Fact]
    public async Task An_open_that_failed_on_the_network_says_so_in_words_and_is_safe_to_repeat()
    {
        var browser = new ScriptedBrowser();
        var network = new BrowserNetworkFailure(
            new NetworkFailure(NetworkFailureKind.NameNotResolved, "ERR_NAME_NOT_RESOLVED"), "fravashi-netfault.invalid");
        browser.Next = BrowserOutcome.NetworkFailed(new BrowserPageState("about:blank", "", null, [], []), network);

        var answer = await Call(new BrowserOpenTool(browser), """{"url":"http://fravashi-netfault.invalid/"}""");

        answer.IsError.Should().BeTrue();
        using var doc = JsonDocument.Parse(answer.Text);
        var root = doc.RootElement;
        root.GetProperty("refused").GetString().Should().Be("network_unreachable");
        root.GetProperty("retry_safe").GetBoolean().Should().BeTrue();
        root.GetProperty("network").GetProperty("kind").GetString().Should().Be("name_not_resolved");
        root.GetProperty("network").GetProperty("host").GetString().Should().Be("fravashi-netfault.invalid");
        root.GetProperty("errors")[0].GetProperty("field").GetString().Should().Be("url");
        root.GetProperty("errors")[0].GetProperty("message").GetString().Should()
            .Be("Could not reach fravashi-netfault.invalid (name not resolved). This is the network, not the page. Opening a page "
                + "is only a read, so it is safe to try again in a minute; the session's browser is still open.");
        root.GetProperty("page").GetProperty("url").GetString().Should().Be("about:blank", "the page is kept");
    }

    [Theory]
    [InlineData("browser_click", """{"role":"button","name":"Send"}""", "click")]
    [InlineData("browser_fill", """{"label":"Query","value":"x","submit":true}""", "fill")]
    [InlineData("browser_press", """{"key":"Enter"}""", "key press")]
    public async Task An_act_whose_next_page_failed_on_the_network_has_an_unknown_outcome(string tool, string arguments, string act)
    {
        var browser = new ScriptedBrowser();
        var network = new BrowserNetworkFailure(
            new NetworkFailure(NetworkFailureKind.NameNotResolved, "ERR_NAME_NOT_RESOLVED"), "fravashi-netfault.invalid", act);
        browser.Next = BrowserOutcome.NetworkFailed(new BrowserPageState("chrome-error://chromewebdata/", "", null, [], []), network);
        IStudioTool[] tools = [new BrowserClickTool(browser), new BrowserFillTool(browser), new BrowserPressTool(browser)];

        var answer = await Call(tools.Single(t => t.Name == tool), arguments);

        using var doc = JsonDocument.Parse(answer.Text);
        var root = doc.RootElement;
        root.GetProperty("refused").GetString().Should().Be("network_outcome_unknown");
        root.GetProperty("retry_safe").GetBoolean().Should().BeFalse("the act may have submitted a form");
        root.GetProperty("errors")[0].GetProperty("message").GetString().Should()
            .StartWith($"The {act} was made").And.Contain("unknown, so do not repeat it yet");
    }

    [Fact]
    public async Task A_page_that_answered_with_an_error_is_an_ordinary_page_not_the_network()
    {
        var browser = new ScriptedBrowser
        {
            Next = new BrowserOutcome(new BrowserPageState("https://example.com/broken", "Internal Server Error", 500, [], [])),
        };

        var answer = await Call(new BrowserOpenTool(browser), """{"url":"https://example.com/broken"}""");

        answer.IsError.Should().BeFalse();
        using var doc = JsonDocument.Parse(answer.Text);
        doc.RootElement.GetProperty("trust").GetString().Should().Be("other-authored-content");
        doc.RootElement.GetProperty("data").GetProperty("status").GetInt32().Should().Be(500);
        answer.Text.Should().NotContain("network_");

        // A refusal that is about the page (nothing matched) keeps its own shape.
        browser.Next = BrowserOutcome.Refused("Nothing on the page matches the button named \"Save\".");
        (await Call(new BrowserClickTool(browser), """{"role":"button","name":"Save"}""")).Text
            .Should().Contain("\"refused\":\"browser\"").And.NotContain("retry_safe");
    }

    // ---- the wire, the ledger, the scrubber -----------------------------------------------------

    [Fact]
    public void An_image_is_an_mcp_image_block_after_the_text_and_one_too_large_is_left_out_and_named()
    {
        var content = StudioToolServer.Content(new StudioToolResult("hello", Images: [new StudioToolImage("image/jpeg", [1, 2, 3])]));

        content.Should().HaveCount(2);
        content[0]!["type"]!.GetValue<string>().Should().Be("text");
        content[1]!["type"]!.GetValue<string>().Should().Be("image");
        content[1]!["mimeType"]!.GetValue<string>().Should().Be("image/jpeg");
        content[1]!["data"]!.GetValue<string>().Should().Be(Convert.ToBase64String([1, 2, 3]));

        var huge = StudioToolServer.Content(new StudioToolResult(
            "hello", Images: [new StudioToolImage("image/jpeg", new byte[StudioToolServer.MaxImageBytes + 1])]));
        huge.Should().ContainSingle("the image was left out");
        huge[0]!["text"]!.GetValue<string>().Should().Contain("was left out");
    }

    [Fact]
    public void The_ledger_keeps_that_an_image_was_returned_and_a_replay_says_to_take_a_new_one()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fravashi-ledger-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var ledger = new FileToolCallLedger(directory);
            ledger.Claim(Session, "toolu_shot", "browser_screenshot", "fp").Outcome.Should().Be(ToolCallClaimOutcome.Fresh);
            ledger.Complete(Session, "toolu_shot", new StudioToolResult("shot", Images: [new StudioToolImage("image/jpeg", new byte[5000])]));

            var replay = new FileToolCallLedger(directory).Claim(Session, "toolu_shot", "browser_screenshot", "fp");

            replay.Outcome.Should().Be(ToolCallClaimOutcome.Replayed);
            replay.Recorded!.Images.Should().BeNull("a picture of a moment is not replayed");
            replay.Recorded.Text.Should().Contain("Take a new screenshot");
            File.ReadAllText(ledger.PathFor(Session)).Should().NotContain(Convert.ToBase64String(new byte[5000])[..40]);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void The_transcript_scrubber_leaves_image_data_alone_and_still_reads_everything_else()
    {
        // An AKIA-shaped run can appear inside base64 by chance; redacting it would corrupt the picture and make a
        // resume of the conversation fail at the provider.
        var image = new string('A', 600) + "+AK" + "IAIOSFODNN7EXAMPLE+" + new string('b', 600);
        CliTranscriptScrubber.IsImageData(image).Should().BeTrue();
        CliTranscriptScrubber.IsImageData("AK" + "IAIOSFODNN7EXAMPLE").Should().BeFalse("short text is read as text");
        CliTranscriptScrubber.IsImageData(new string('a', 2000) + " sk-ant-x").Should().BeFalse("prose is read as text");
    }

    [Theory]
    [InlineData("browser_read")]
    [InlineData("browser_screenshot")]
    [InlineData("browser_click")]
    [InlineData("browser_press")]
    [InlineData("browser_close")]
    public void What_acts_on_the_page_a_person_let_the_session_open_runs_unattended(string tool) =>
        AgentGraphTools.RunsUnattended(AgentWorkGate.StudioToolPrefix + tool, new Dictionary<string, string>()).Should().NotBeNull();

    [Theory]
    [InlineData("browser_open", "it chooses where the session goes and what the address carries")]
    [InlineData("browser_fill", "it decides what leaves in a form")]
    public void Opening_a_page_and_filling_a_field_ask(string tool, string why) =>
        AgentGraphTools.RunsUnattended(AgentWorkGate.StudioToolPrefix + tool, new Dictionary<string, string>()).Should().BeNull(why);

    [Fact]
    public void The_browsers_environment_names_no_studio_secret()
    {
        Environment.SetEnvironmentVariable("FRAVASHI_BRIDGE_TOKEN_FOR_THIS_TEST", "secret");
        try
        {
            PlaywrightAgentBrowser.CleanEnvironment().Select(p => p.Key)
                .Should().NotContain(k => k.StartsWith("FRAVASHI_", StringComparison.Ordinal) || k.StartsWith("ANTHROPIC_", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("FRAVASHI_BRIDGE_TOKEN_FOR_THIS_TEST", null);
        }
    }

    private static Task<StudioToolResult> Call(IStudioTool tool, string arguments) =>
        tool.InvokeAsync(new StudioToolCall(Session, tool.Name, JsonDocument.Parse(arguments).RootElement, "k", ProjectId.Default));

    /// <summary>A browser that answers what it is told to, and remembers what it was asked.</summary>
    private sealed class ScriptedBrowser : IAgentBrowser
    {
        private static readonly BrowserPageState Page = new("https://example.com/", "Example", 200, ["boom"], []);

        public bool Available { get; set; } = true;

        public BrowserOutcome? Next { get; set; }

        public BrowserTarget? LastTarget { get; private set; }

        public (string Text, bool Submit)? LastFill { get; private set; }

        public bool Opened { get; private set; }

        public bool IsAvailable => Available;

        public string? UnavailableReason => Available ? null : "This studio has no browser installed.";

        public event Action<AgentSessionId>? Changed;

        private BrowserOutcome Outcome() => Next ?? new BrowserOutcome(Page);

        public BrowserPageState? Current(AgentSessionId session) => Page;

        public Task<BrowserOutcome> OpenAsync(AgentSessionId session, string url, CancellationToken cancellationToken = default)
        {
            Opened = true;
            Changed?.Invoke(session);
            return Task.FromResult(Outcome());
        }

        public Task<(BrowserOutcome Outcome, string? Content, bool Truncated)> ReadAsync(
            AgentSessionId session, BrowserReadMode mode, int max, CancellationToken cancellationToken = default) =>
            Task.FromResult((Outcome(), (string?)"text", false));

        public Task<BrowserOutcome> ClickAsync(AgentSessionId session, BrowserTarget target, CancellationToken cancellationToken = default)
        {
            LastTarget = target;
            return Task.FromResult(Outcome());
        }

        public Task<BrowserOutcome> FillAsync(
            AgentSessionId session, BrowserTarget target, string text, bool submit, CancellationToken cancellationToken = default)
        {
            LastTarget = target;
            LastFill = (text, submit);
            return Task.FromResult(Outcome());
        }

        public Task<BrowserOutcome> PressAsync(AgentSessionId session, string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Outcome());

        public Task<(BrowserOutcome Outcome, byte[]? Jpeg)> ScreenshotAsync(
            AgentSessionId session, bool fullPage, CancellationToken cancellationToken = default) =>
            Task.FromResult((Outcome(), (byte[]?)new byte[2048]));

        public Task CloseAsync(AgentSessionId session) => Task.CompletedTask;

        public Task<byte[]?> FrameAsync(AgentSessionId session, CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>(null);

        public Task<BrowserOutcome> ClickAtAsync(AgentSessionId session, double x, double y, CancellationToken cancellationToken = default) =>
            Task.FromResult(Outcome());

        public Task<BrowserOutcome> TypeTextAsync(AgentSessionId session, string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(Outcome());
    }
}
