using System.Net;
using System.Net.Sockets;
using AwesomeAssertions;
using Fravashi.Infrastructure.Bridge;
using Fravashi.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Bridge;

/// <summary>
/// Backlog 252: the studio asks fravashi.exe's host bridge for an answer from inside the container and, when none comes,
/// says why in words. Backlog 278: a timed-out connection names fravashi.exe not running first, and gives the firewall's
/// one-line fix only once the bridge has stayed unreachable long enough that the firewall becomes the more likely
/// cause. A bridge that answers, one that has not failed for long enough, and a studio with no bridge on purpose all
/// say nothing.
/// </summary>
public sealed class BridgeReachTests : IDisposable
{
    /// <summary>What the container was given on the test VM, 2026-10-08 (Podman, the WSL gateway address).</summary>
    private static readonly Uri VmBridge = new("http://172.25.208.1:9091");

    private static readonly int[] VmPorts = [9091, 9095, 9097];

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "fravashi-bridge-reach", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- the words -------------------------------------------------------------------------------------------------

    [Fact]
    public void A_timeout_just_past_grace_names_the_host_not_running_first_and_withholds_the_firewall_command()
    {
        var words = BridgeReachWords.For(BridgeReachOutcome.TimedOut, VmBridge, VmPorts, unreachableFor: BridgeReachMonitor.Grace);

        words.Should().NotBeNull();
        words.Should().Contain("fravashi.exe is not running right now (it was closed, restarted or updated)")
            .And.Contain("Windows Firewall")
            .And.Contain("http://172.25.208.1:9091");
        words.Should().NotContain(BridgeReachWords.FirewallCommand(VmPorts));
    }

    [Fact]
    public void A_timeout_that_stays_unreachable_past_the_firewall_threshold_names_both_causes_with_the_one_command()
    {
        var words = BridgeReachWords.For(
            BridgeReachOutcome.TimedOut,
            VmBridge,
            VmPorts,
            unreachableFor: BridgeReachWords.FirewallAdviceAfter);

        words.Should().NotBeNull();
        words.Should().Contain("fravashi.exe is not running right now (it was closed, restarted or updated)")
            .And.Contain("Windows Firewall")
            .And.Contain("Block rules named fravashi.exe")
            .And.Contain("inbound allow rule for fravashi.exe on TCP port(s) 9091, 9095, 9097")
            .And.Contain(BridgeReachWords.FirewallCommand(VmPorts))
            .And.Contain("http://172.25.208.1:9091");
        // fravashi.exe not running is still named before the firewall is.
        words!.IndexOf("fravashi.exe is not running right now", StringComparison.Ordinal)
            .Should().BeLessThan(words.IndexOf("Windows Firewall", StringComparison.Ordinal));
    }

    [Fact]
    public void The_one_command_removes_the_block_rules_first_then_allows_the_host_on_exactly_its_ports()
    {
        var command = BridgeReachWords.FirewallCommand(VmPorts);

        command.IndexOf("Remove-NetFirewallRule", StringComparison.Ordinal)
            .Should().BeLessThan(command.IndexOf("New-NetFirewallRule", StringComparison.Ordinal));
        command.Should().Contain("Where-Object Action -eq 'Block'")
            .And.Contain("-Direction Inbound -Action Allow -Protocol TCP -LocalPort 9091,9095,9097")
            .And.Contain($"-Program \"{BridgeReachWords.InstalledDesktopPath}\"");
    }

    [Fact]
    public void A_refused_connection_says_the_host_is_not_running_and_never_blames_the_firewall()
    {
        var words = BridgeReachWords.For(BridgeReachOutcome.Refused, VmBridge, VmPorts);

        words.Should().Contain("nothing is listening").And.Contain("Start Fravashi again");
        words.Should().NotContain("Firewall");
    }

    [Fact]
    public void A_timeout_to_wslcs_forwarded_host_name_is_not_blamed_on_the_firewall()
    {
        var words = BridgeReachWords.For(BridgeReachOutcome.TimedOut, new Uri("http://host.wslc.internal:9091"), [9091]);

        words.Should().Contain("did not answer in time");
        words.Should().NotContain("Firewall");
    }

    [Fact]
    public void Any_other_failure_is_said_with_its_own_words_and_no_firewall_claim()
    {
        var words = BridgeReachWords.For(BridgeReachOutcome.Failed, VmBridge, VmPorts, "Name or service not known");

        words.Should().Contain("Name or service not known.");
        words.Should().NotContain("Firewall");
    }

    [Fact]
    public void A_bridge_that_answers_is_said_as_nothing() =>
        BridgeReachWords.For(BridgeReachOutcome.Answered, VmBridge, VmPorts).Should().BeNull();

    [Theory]
    [InlineData(BridgeReachOutcome.TimedOut)]
    [InlineData(BridgeReachOutcome.Refused)]
    [InlineData(BridgeReachOutcome.Failed)]
    public void The_words_carry_no_long_dash(BridgeReachOutcome outcome)
    {
        var words = BridgeReachWords.For(outcome, VmBridge, VmPorts, "reason")!;

        words.Should().NotContain(((char)0x2014).ToString()).And.NotContain(((char)0x2013).ToString());
    }

    [Fact]
    public void The_full_firewall_advice_carries_no_long_dash()
    {
        var words = BridgeReachWords.For(BridgeReachOutcome.TimedOut, VmBridge, VmPorts, unreachableFor: BridgeReachWords.FirewallAdviceAfter)!;

        words.Should().NotContain(((char)0x2014).ToString()).And.NotContain(((char)0x2013).ToString());
    }

    // ---- the monitor -----------------------------------------------------------------------------------------------

    private static IConfiguration Bridged(string url = "http://172.25.208.1:9091") => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            [BridgeWorkspaceContext.UrlKey] = url,
            [BridgeWorkspaceContext.TokenKey] = "bridge-secret",
            ["FRAVASHI_APPRUNNER_URL"] = "http://172.25.208.1:9095",
            ["FRAVASHI_UPGRADE_URL"] = "http://172.25.208.1:9097",
        })
        .Build();

    private BridgeReachMonitor Monitor(IConfiguration configuration, ScriptedProbe probe, ManualClock clock) =>
        new(configuration, probe, new DataDirectory(_dataDir), clock: clock);

    private string ReachFile => Path.Combine(_dataDir, BridgeReachMonitor.FileName);

    [Fact]
    public async Task A_bridge_that_never_answers_is_said_once_failures_have_lasted_the_grace_and_left_for_the_status_verb()
    {
        var clock = new ManualClock();
        var probe = new ScriptedProbe(BridgeReachOutcome.TimedOut);
        var monitor = Monitor(Bridged(), probe, clock);

        (await monitor.CheckAsync(CancellationToken.None)).Should().Be(BridgeReachState.Checking);
        monitor.Words.Should().BeNull();
        File.Exists(ReachFile).Should().BeFalse();

        clock.Advance(BridgeReachMonitor.Grace);
        (await monitor.CheckAsync(CancellationToken.None)).Should().Be(BridgeReachState.Unreachable);

        var expected = BridgeReachWords.For(
            BridgeReachOutcome.TimedOut,
            new Uri("http://172.25.208.1:9091"),
            VmPorts,
            unreachableFor: BridgeReachMonitor.Grace);
        monitor.Words.Should().Be(expected);
        monitor.Words.Should().Contain("fravashi.exe is not running right now").And.Contain("Windows Firewall")
            .And.NotContain(BridgeReachWords.FirewallCommand(VmPorts));
        monitor.Words!.IndexOf("fravashi.exe is not running right now", StringComparison.Ordinal)
            .Should().BeLessThan(monitor.Words.IndexOf("Windows Firewall", StringComparison.Ordinal));
        File.ReadAllText(ReachFile).Should().Be(monitor.Words);
    }

    [Fact]
    public async Task A_bridge_that_stays_unreachable_past_the_firewall_threshold_gets_the_full_firewall_advice()
    {
        var clock = new ManualClock();
        var probe = new ScriptedProbe(BridgeReachOutcome.TimedOut);
        var monitor = Monitor(Bridged(), probe, clock);

        await monitor.CheckAsync(CancellationToken.None);
        clock.Advance(BridgeReachMonitor.Grace);
        (await monitor.CheckAsync(CancellationToken.None)).Should().Be(BridgeReachState.Unreachable);
        monitor.Words.Should().NotContain(BridgeReachWords.FirewallCommand(VmPorts));

        clock.Advance(BridgeReachWords.FirewallAdviceAfter);
        (await monitor.CheckAsync(CancellationToken.None)).Should().Be(BridgeReachState.Unreachable);

        monitor.Words.Should().Contain("fravashi.exe is not running right now").And.Contain("Windows Firewall")
            .And.Contain(BridgeReachWords.FirewallCommand(VmPorts));
        monitor.Words!.IndexOf("fravashi.exe is not running right now", StringComparison.Ordinal)
            .Should().BeLessThan(monitor.Words.IndexOf("Windows Firewall", StringComparison.Ordinal));
        File.ReadAllText(ReachFile).Should().Be(monitor.Words);
    }

    [Fact]
    public async Task A_bridge_that_is_slow_to_start_is_never_called_blocked()
    {
        var clock = new ManualClock();
        var probe = new ScriptedProbe(BridgeReachOutcome.TimedOut);
        var monitor = Monitor(Bridged(), probe, clock);

        await monitor.CheckAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(20));
        await monitor.CheckAsync(CancellationToken.None);
        probe.Next = BridgeReachOutcome.Answered;
        clock.Advance(TimeSpan.FromSeconds(20));

        (await monitor.CheckAsync(CancellationToken.None)).Should().Be(BridgeReachState.Reachable);
        monitor.Words.Should().BeNull();

        // One answer resets the clock: a later failure starts a fresh grace.
        probe.Next = BridgeReachOutcome.TimedOut;
        clock.Advance(TimeSpan.FromMinutes(5));
        (await monitor.CheckAsync(CancellationToken.None)).Should().Be(BridgeReachState.Checking);
        monitor.Words.Should().BeNull();
    }

    [Fact]
    public async Task A_bridge_that_answers_again_clears_the_words_and_the_file()
    {
        var clock = new ManualClock();
        var probe = new ScriptedProbe(BridgeReachOutcome.TimedOut);
        var monitor = Monitor(Bridged(), probe, clock);
        await monitor.CheckAsync(CancellationToken.None);
        clock.Advance(BridgeReachMonitor.Grace);
        await monitor.CheckAsync(CancellationToken.None);
        File.Exists(ReachFile).Should().BeTrue();

        probe.Next = BridgeReachOutcome.Answered;
        (await monitor.CheckAsync(CancellationToken.None)).Should().Be(BridgeReachState.Reachable);

        monitor.Words.Should().BeNull();
        File.Exists(ReachFile).Should().BeFalse();
    }

    [Fact]
    public async Task A_bridge_that_answers_says_nothing()
    {
        var monitor = Monitor(Bridged(), new ScriptedProbe(BridgeReachOutcome.Answered), new ManualClock());

        (await monitor.CheckAsync(CancellationToken.None)).Should().Be(BridgeReachState.Reachable);

        monitor.Words.Should().BeNull();
        File.Exists(ReachFile).Should().BeFalse();
    }

    [Fact]
    public async Task The_dev_fallback_with_no_bridge_is_never_asked_and_says_nothing()
    {
        var probe = new ScriptedProbe(BridgeReachOutcome.TimedOut);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Repository:Root"] = "/some/clone" })
            .Build();
        var clock = new ManualClock();
        var monitor = Monitor(configuration, probe, clock);

        monitor.Configured.Should().BeFalse();
        await monitor.CheckAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(10));
        (await monitor.CheckAsync(CancellationToken.None)).Should().Be(BridgeReachState.NotConfigured);

        probe.Asked.Should().Be(0);
        monitor.Words.Should().BeNull();
    }

    [Fact]
    public async Task A_bridge_url_without_its_token_is_not_a_bridge_to_ask()
    {
        var probe = new ScriptedProbe(BridgeReachOutcome.TimedOut);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [BridgeWorkspaceContext.UrlKey] = "http://172.25.208.1:9091" })
            .Build();

        var monitor = Monitor(configuration, probe, new ManualClock());
        await monitor.CheckAsync(CancellationToken.None);

        probe.Asked.Should().Be(0);
        monitor.State.Should().Be(BridgeReachState.NotConfigured);
    }

    // ---- the probe -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_probe_reads_a_refused_port_as_refused()
    {
        // A port that was just free: nothing listens, so the kernel refuses the connection.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var result = await new HttpBridgeReachProbe().ProbeAsync(new Uri($"http://127.0.0.1:{port}"), CancellationToken.None);

        result.Outcome.Should().Be(BridgeReachOutcome.Refused);
    }

    [Fact]
    public async Task The_probe_reads_any_http_answer_as_answered_and_asks_the_tokenless_healthz()
    {
        var handler = new AnsweringHandler(HttpStatusCode.Unauthorized);

        var result = await new HttpBridgeReachProbe(handler).ProbeAsync(VmBridge, CancellationToken.None);

        result.Outcome.Should().Be(BridgeReachOutcome.Answered);
        handler.Asked!.AbsoluteUri.Should().Be("http://172.25.208.1:9091/bridge/healthz");
    }

    [Fact]
    public async Task The_probe_reads_no_answer_within_its_budget_as_timed_out()
    {
        var result = await new HttpBridgeReachProbe(new SilentHandler(), TimeSpan.FromMilliseconds(200))
            .ProbeAsync(VmBridge, CancellationToken.None);

        result.Outcome.Should().Be(BridgeReachOutcome.TimedOut);
    }

    [Theory]
    [InlineData(SocketError.ConnectionRefused, BridgeReachOutcome.Refused)]
    [InlineData(SocketError.TimedOut, BridgeReachOutcome.TimedOut)]
    [InlineData(SocketError.HostNotFound, BridgeReachOutcome.Failed)]
    public void The_probe_names_a_failed_request_by_its_socket_error(SocketError error, BridgeReachOutcome expected) =>
        HttpBridgeReachProbe.Classify(new HttpRequestException("connect failed", new SocketException((int)error)))
            .Outcome.Should().Be(expected);

    // ---- doubles ---------------------------------------------------------------------------------------------------

    private sealed class ScriptedProbe(BridgeReachOutcome first) : IBridgeReachProbe
    {
        public BridgeReachOutcome Next { get; set; } = first;

        public int Asked { get; private set; }

        public Task<BridgeReachProbeResult> ProbeAsync(Uri bridge, CancellationToken cancellationToken)
        {
            Asked++;
            return Task.FromResult(new BridgeReachProbeResult(Next));
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 8, 6, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class AnsweringHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public Uri? Asked { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    /// <summary>Packets dropped on the way: nothing ever comes back, which is what a Block rule looks like.</summary>
    private sealed class SilentHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
