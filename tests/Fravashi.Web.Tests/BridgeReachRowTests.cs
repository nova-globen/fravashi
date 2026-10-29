using Bunit;
using Fravashi.Infrastructure.Bridge;
using Fravashi.Web.Components.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 252: Settings &gt; General &gt; Overview says, in words, when the studio cannot reach fravashi.exe's host bridge, and
/// asks again when the page opens. A bridge that answers, and a studio with no bridge on purpose, say nothing.
/// </summary>
public sealed class BridgeReachRowTests
{
    private static IConfiguration Bridged() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            [BridgeWorkspaceContext.UrlKey] = "http://172.25.208.1:9091",
            [BridgeWorkspaceContext.TokenKey] = "bridge-secret",
        })
        .Build();

    private static BunitContext With(BridgeReachMonitor monitor)
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton(monitor);
        return ctx;
    }

    [Fact]
    public async Task A_bridge_just_past_grace_names_the_host_not_running_first_with_no_command_yet()
    {
        var clock = new ManualClock();
        var probe = new ScriptedProbe(BridgeReachOutcome.TimedOut);
        var monitor = new BridgeReachMonitor(Bridged(), probe, clock: clock);
        // The start pass has been failing for longer than the grace, but not for long.
        await monitor.CheckAsync(CancellationToken.None);
        clock.Advance(BridgeReachMonitor.Grace);
        using var ctx = With(monitor);

        var cut = ctx.Render<BridgeReachRow>();

        cut.WaitForAssertion(() =>
        {
            var row = cut.Find("[data-testid=bridge-reach-row]").TextContent;
            Assert.Contains("fravashi.exe is not running right now (it was closed, restarted or updated)", row);
            Assert.Contains("Windows Firewall", row);
            Assert.DoesNotContain(BridgeReachWords.FirewallCommand([9091]), row);
        });
        // Opening the page asked the bridge again.
        Assert.Equal(2, probe.Asked);
    }

    [Fact]
    public async Task A_bridge_that_stays_unreachable_is_named_with_the_one_command()
    {
        var clock = new ManualClock();
        var probe = new ScriptedProbe(BridgeReachOutcome.TimedOut);
        var monitor = new BridgeReachMonitor(Bridged(), probe, clock: clock);
        await monitor.CheckAsync(CancellationToken.None);
        clock.Advance(BridgeReachMonitor.Grace);
        await monitor.CheckAsync(CancellationToken.None);
        // It has now stayed unreachable past the point where a restart would have cleared it.
        clock.Advance(BridgeReachWords.FirewallAdviceAfter);
        await monitor.CheckAsync(CancellationToken.None);
        using var ctx = With(monitor);

        var cut = ctx.Render<BridgeReachRow>();

        cut.WaitForAssertion(() =>
        {
            var row = cut.Find("[data-testid=bridge-reach-row]").TextContent;
            Assert.Contains("fravashi.exe is not running right now (it was closed, restarted or updated)", row);
            Assert.Contains("Windows Firewall", row);
            Assert.Contains(BridgeReachWords.FirewallCommand([9091]), row);
        });
    }

    [Fact]
    public void A_bridge_that_answers_shows_no_row()
    {
        using var ctx = With(new BridgeReachMonitor(Bridged(), new ScriptedProbe(BridgeReachOutcome.Answered)));

        var cut = ctx.Render<BridgeReachRow>();

        Assert.Empty(cut.FindAll("[data-testid=bridge-reach-row]"));
        Assert.Equal(string.Empty, cut.Markup.Trim());
    }

    [Fact]
    public void A_studio_with_no_bridge_on_purpose_shows_no_row_and_asks_nothing()
    {
        var probe = new ScriptedProbe(BridgeReachOutcome.TimedOut);
        using var ctx = With(new BridgeReachMonitor(new ConfigurationBuilder().Build(), probe));

        var cut = ctx.Render<BridgeReachRow>();

        Assert.Equal(string.Empty, cut.Markup.Trim());
        Assert.Equal(0, probe.Asked);
    }

    [Fact]
    public void A_bridge_that_has_only_just_stopped_answering_shows_no_row_yet()
    {
        using var ctx = With(new BridgeReachMonitor(Bridged(), new ScriptedProbe(BridgeReachOutcome.TimedOut), clock: new ManualClock()));

        var cut = ctx.Render<BridgeReachRow>();

        Assert.Equal(string.Empty, cut.Markup.Trim());
    }

    private sealed class ScriptedProbe(BridgeReachOutcome outcome) : IBridgeReachProbe
    {
        public int Asked { get; private set; }

        public Task<BridgeReachProbeResult> ProbeAsync(Uri bridge, CancellationToken cancellationToken)
        {
            Asked++;
            return Task.FromResult(new BridgeReachProbeResult(outcome));
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 8, 6, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
