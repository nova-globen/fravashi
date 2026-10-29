using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Fravashi.Application.Verification;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Browser;
using Fravashi.Infrastructure.Verification;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Verification;

/// <summary>
/// Backlog 223: what the design check refuses before any browser starts. These run everywhere; a refusal never needs one.
/// </summary>
public sealed class PlaywrightDesignCheckTests
{
    private static readonly MockupDocument Design = Read();

    private static MockupDocument Read()
    {
        var read = MockupReader.Read(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mockups", "Fixtures", "team-directory.attempt-3.json")));
        return read.Document!;
    }

    private static PlaywrightDesignCheck Check(params string[] denied) =>
        new(new BrowserUrlPolicy(denied), browsersPath: Path.Combine(Path.GetTempPath(), "no-browsers-" + Guid.NewGuid().ToString("N")));

    private static Task<DesignCheckResult> Run(PlaywrightDesignCheck check, params DesignCheckTarget[] targets) =>
        check.CheckAsync(new DesignCheckRequest(Design, targets));

    [Theory]
    [InlineData("http://127.0.0.1:8722/", "a loopback address is the studio's own container")]
    [InlineData("http://localhost:5090/", "localhost is the studio's own container")]
    [InlineData("http://[::1]/", "a loopback address is the studio's own container")]
    [InlineData("file:///etc/passwd", "http and https pages only")]
    public async Task An_address_the_agent_browser_may_not_open_the_check_does_not_open_either(string url, string why)
    {
        var result = await Run(Check(), new DesignCheckTarget(0, url));

        result.Refusal.Should().Contain(why);
        result.Screens.Should().BeEmpty();
        result.Matches.Should().BeFalse();
    }

    [Fact]
    public async Task The_studios_own_endpoints_and_the_host_alias_are_refused()
    {
        var check = Check("http://172.17.0.1:47100/");

        (await Run(check, new DesignCheckTarget(0, "http://172.17.0.1:47100/ui"))).Refusal
            .Should().Contain("the studio's own host services");
        (await Run(check, new DesignCheckTarget(0, $"http://{AgentHostAlias.Name}:8080/"))).Refusal
            .Should().Contain("host alias");
    }

    [Fact]
    public async Task Every_target_is_checked_before_anything_runs()
    {
        var result = await Run(Check(), new DesignCheckTarget(0, "http://app.test/"), new DesignCheckTarget(0, "http://localhost/"));

        result.Refusal.Should().Contain("http://localhost/");
    }

    [Fact]
    public async Task A_screen_the_design_does_not_have_and_an_empty_request_are_refused()
    {
        (await Run(Check(), new DesignCheckTarget(3, "http://app.test/"))).Refusal.Should().Be("The design has no screen 3; it has 2.");
        (await Run(Check())).Refusal.Should().Be("Say which address of the build shows which screen of the design.");
        (await Check().CheckAsync(new DesignCheckRequest(Design, [new(0, "http://app.test/")]) { Budget = TimeSpan.Zero }))
            .Refusal.Should().Be("A check needs some time to run.");
    }

    [Fact]
    public async Task Without_a_browser_an_acceptable_request_is_refused_with_the_reason()
    {
        var check = Check();
        check.UnavailableReason.Should().Contain("no browser installed");

        (await Run(check, new DesignCheckTarget(0, "http://app.test/"))).Refusal.Should().Be(check.UnavailableReason);
    }

    [Fact]
    public void A_marked_check_treats_only_the_runs_origin_as_secure_and_an_unmarked_one_adds_nothing()
    {
        var marker = WindowMarker.For("http://fravashi-run-abc-relay:8080/not-found", "token")!;

        var marked = PlaywrightDesignCheck.LaunchOptions(jitless: true, marker).Args!;
        var unmarked = PlaywrightDesignCheck.LaunchOptions(jitless: true, null).Args!;

        marked.Should().Contain("--unsafely-treat-insecure-origin-as-secure=http://fravashi-run-abc-relay:8080");
        marked.Except(unmarked).Should().ContainSingle("nothing else is switched, the local network access checks included");
        unmarked.Should().BeEquivalentTo(PlaywrightAgentBrowser.LaunchOptions(true).Args);
    }
}
