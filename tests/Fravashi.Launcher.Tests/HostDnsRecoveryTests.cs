using Fravashi.Launcher;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

public sealed class HostDnsServersTests
{
    [Fact]
    public void Resolve_CollectsDistinctServers_FromUpNonLoopbackInterfaces_SortedForComparison()
    {
        var servers = HostDnsServers.Resolve(() =>
        [
            (true, false, new List<string> { "8.8.8.8", "1.1.1.1" }),
            (true, false, new List<string> { "1.1.1.1" }), // duplicate across interfaces, counted once
            (false, false, new List<string> { "9.9.9.9" }), // down: ignored
            (true, true, new List<string> { "127.0.0.53" }), // loopback: ignored
        ]);

        servers.Should().Equal("1.1.1.1", "8.8.8.8");
    }

    [Fact]
    public void Resolve_WithNoInterfaces_IsEmpty()
    {
        HostDnsServers.Resolve(() => []).Should().BeEmpty();
    }

    [Fact]
    public void SignatureAndParse_RoundTrip_RegardlessOfInputOrder()
    {
        var signature = HostDnsServers.Signature(["8.8.8.8", "1.1.1.1"]);

        HostDnsServers.Parse(signature).Should().Equal("1.1.1.1", "8.8.8.8");
    }

    [Fact]
    public void Parse_OfNullOrBlank_IsEmpty()
    {
        HostDnsServers.Parse(null).Should().BeEmpty();
        HostDnsServers.Parse("").Should().BeEmpty();
        HostDnsServers.Parse("   ").Should().BeEmpty();
    }
}

public sealed class HostDnsWatchTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 23, 0, 0, TimeSpan.Zero);
    private static readonly string[] Baseline = ["10.0.0.1"];
    private static readonly string[] Changed = ["1.1.1.1", "8.8.8.8"];

    [Fact]
    public void Evaluate_WhenCurrentMatchesBaseline_IsUnchanged()
    {
        var watch = new HostDnsWatch(TimeSpan.FromSeconds(30));

        watch.Evaluate(Baseline, Baseline, Start).Should().Be(DnsDriftDecision.Unchanged);
    }

    [Fact]
    public void Evaluate_ComparesAsASet_OrderAndDuplicatesIgnored()
    {
        var watch = new HostDnsWatch(TimeSpan.FromSeconds(30));

        watch.Evaluate(["1.1.1.1", "8.8.8.8"], ["8.8.8.8", "1.1.1.1", "8.8.8.8"], Start)
            .Should().Be(DnsDriftDecision.Unchanged);
    }

    [Fact]
    public void Evaluate_ANewValue_DebouncesBeforeTheWindowElapses()
    {
        var watch = new HostDnsWatch(TimeSpan.FromSeconds(30));

        watch.Evaluate(Baseline, Changed, Start).Should().Be(DnsDriftDecision.Debouncing);
        watch.Evaluate(Baseline, Changed, Start + TimeSpan.FromSeconds(10)).Should().Be(DnsDriftDecision.Debouncing);
        watch.Evaluate(Baseline, Changed, Start + TimeSpan.FromSeconds(29)).Should().Be(DnsDriftDecision.Debouncing);
    }

    [Fact]
    public void Evaluate_TheSameNewValueHeldForTheWholeWindow_Acts()
    {
        var watch = new HostDnsWatch(TimeSpan.FromSeconds(30));

        watch.Evaluate(Baseline, Changed, Start).Should().Be(DnsDriftDecision.Debouncing);
        watch.Evaluate(Baseline, Changed, Start + TimeSpan.FromSeconds(30)).Should().Be(DnsDriftDecision.Act);
    }

    [Fact]
    public void Evaluate_AFlapBackToTheBaselineMidWindow_NeverActs()
    {
        var watch = new HostDnsWatch(TimeSpan.FromSeconds(30));

        watch.Evaluate(Baseline, Changed, Start).Should().Be(DnsDriftDecision.Debouncing);
        // The VPN reconnects and DNS flaps back to the baseline before the window elapses.
        watch.Evaluate(Baseline, Baseline, Start + TimeSpan.FromSeconds(10)).Should().Be(DnsDriftDecision.Unchanged);
        watch.Evaluate(Baseline, Changed, Start + TimeSpan.FromSeconds(40)).Should().Be(DnsDriftDecision.Debouncing);
    }

    [Fact]
    public void Evaluate_ASecondDifferentValueMidWindow_RestartsTheWindowRatherThanActing()
    {
        var watch = new HostDnsWatch(TimeSpan.FromSeconds(30));
        string[] thirdValue = ["9.9.9.9"];

        watch.Evaluate(Baseline, Changed, Start).Should().Be(DnsDriftDecision.Debouncing);
        watch.Evaluate(Baseline, thirdValue, Start + TimeSpan.FromSeconds(20)).Should().Be(DnsDriftDecision.Debouncing);
        // Only 10s since the window restarted on the third value - not yet 30s.
        watch.Evaluate(Baseline, thirdValue, Start + TimeSpan.FromSeconds(30)).Should().Be(DnsDriftDecision.Debouncing);
        watch.Evaluate(Baseline, thirdValue, Start + TimeSpan.FromSeconds(50)).Should().Be(DnsDriftDecision.Act);
    }

    [Fact]
    public void Reset_ClearsThePendingCandidate_SoTheNextDifferenceStartsAFreshWindow()
    {
        var watch = new HostDnsWatch(TimeSpan.FromSeconds(30));
        watch.Evaluate(Baseline, Changed, Start).Should().Be(DnsDriftDecision.Debouncing);

        watch.Reset();

        watch.Evaluate(Baseline, Changed, Start + TimeSpan.FromSeconds(30)).Should().Be(DnsDriftDecision.Debouncing);
    }
}

public sealed class StudioDnsRecoveryAppliesToTests
{
    [Theory]
    [InlineData(RuntimeNames.Wslc, true)]
    [InlineData(RuntimeNames.Docker, false)]
    [InlineData(RuntimeNames.Podman, false)]
    public void AppliesTo_OnlyWslc(string runtimeName, bool expected)
    {
        StudioDnsRecovery.AppliesTo(runtimeName).Should().Be(expected);
    }
}

public sealed class StudioDnsRecoveryTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 23, 0, 0, TimeSpan.Zero);

    private readonly string _directory;
    private readonly LauncherConfigStore _configStore;

    public StudioDnsRecoveryTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "fravashi-dns-recovery-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _configStore = new LauncherConfigStore(Path.Combine(_directory, "launcher.json"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private LauncherConfig BaseConfig(string? dnsServersBaked = null) => new()
    {
        Port = LauncherDefaults.PreferredPort,
        ContainerName = "fravashi-test",
        DataDir = _directory,
        DnsServersBaked = dnsServersBaked,
    };

    private string RestartReasonPath => Path.Combine(_directory, RestartReasonFile.FileName);

    [Fact]
    public void Poll_WithNoRecordedBaseline_AdoptsTheCurrentValue_WithoutRestarting()
    {
        _configStore.Save(BaseConfig(dnsServersBaked: null));
        var runtime = new FakeContainerRuntime { ContainerIsRunning = true };
        var log = new List<string>();
        var recovery = new StudioDnsRecovery(runtime, _configStore, log.Add);

        recovery.Poll(["1.1.1.1"], Start);

        runtime.Stopped.Should().BeEmpty();
        runtime.Started.Should().BeEmpty();
        _configStore.Load()!.DnsServersBaked.Should().Be("1.1.1.1");
        log.Should().BeEmpty();
    }

    [Fact]
    public void Poll_WhenTheContainerIsNotRunning_DoesNothing()
    {
        _configStore.Save(BaseConfig(dnsServersBaked: "10.0.0.1"));
        var runtime = new FakeContainerRuntime { ContainerIsRunning = false };
        var recovery = new StudioDnsRecovery(runtime, _configStore, _ => { });

        recovery.Poll(["1.1.1.1", "8.8.8.8"], Start);
        recovery.Poll(["1.1.1.1", "8.8.8.8"], Start + TimeSpan.FromMinutes(5));

        runtime.Stopped.Should().BeEmpty();
        runtime.Started.Should().BeEmpty();
    }

    [Fact]
    public void Poll_ADriftHeldForTheWholeWindow_ButTheContainerStillResolves_AdoptsTheBaseline_WithoutRestarting()
    {
        // Measured the night this item was filed: the owner reconnecting the VPN changed the host's DNS
        // servers a SECOND time, to a set the container could still resolve through. Restarting on every
        // change rather than on an actual failure would have cut off a running turn for nothing.
        _configStore.Save(BaseConfig(dnsServersBaked: "10.0.0.1"));
        var runtime = new FakeContainerRuntime { ContainerIsRunning = true, CanResolveValue = true };
        var log = new List<string>();
        var recovery = new StudioDnsRecovery(runtime, _configStore, log.Add, debounce: TimeSpan.FromSeconds(30));

        recovery.Poll(["1.1.1.1", "8.8.8.8"], Start);
        recovery.Poll(["1.1.1.1", "8.8.8.8"], Start + TimeSpan.FromSeconds(30));

        runtime.Stopped.Should().BeEmpty();
        runtime.Started.Should().BeEmpty();
        runtime.ResolveQueries.Should().Equal(StudioDnsRecovery.AgentApiHost);
        _configStore.Load()!.DnsServersBaked.Should().Be(HostDnsServers.Signature(["1.1.1.1", "8.8.8.8"]));
        File.Exists(RestartReasonPath).Should().BeFalse("nothing was restarted, so nothing needs explaining");
        log.Should().ContainSingle(line => line.Contains("fravashi-test", StringComparison.Ordinal)
            && line.Contains("still resolves", StringComparison.Ordinal));
    }

    [Fact]
    public void Poll_ADriftHeldForTheWholeWindow_AndTheContainerCannotResolve_RestartsIt_AndRecordsTheNewBaseline()
    {
        _configStore.Save(BaseConfig(dnsServersBaked: "10.0.0.1"));
        var runtime = new FakeContainerRuntime { ContainerIsRunning = true, CanResolveValue = false };
        var log = new List<string>();
        string? balloon = null;
        var recovery = new StudioDnsRecovery(runtime, _configStore, log.Add, debounce: TimeSpan.FromSeconds(30), onRestarted: r => balloon = r);

        recovery.Poll(["1.1.1.1", "8.8.8.8"], Start);
        runtime.Stopped.Should().BeEmpty("the drift has not held for the whole window yet");

        recovery.Poll(["1.1.1.1", "8.8.8.8"], Start + TimeSpan.FromSeconds(30));

        runtime.Stopped.Should().Equal("fravashi-test");
        runtime.Started.Should().Equal("fravashi-test");
        runtime.ResolveQueries.Should().Equal(StudioDnsRecovery.AgentApiHost);
        _configStore.Load()!.DnsServersBaked.Should().Be(HostDnsServers.Signature(["1.1.1.1", "8.8.8.8"]));
        log.Should().ContainSingle(line => line.Contains("fravashi-test", StringComparison.Ordinal)
            && line.Contains("can no longer resolve", StringComparison.Ordinal));
        balloon.Should().Be(StudioDnsRecovery.RestartExplanation);
        File.ReadAllText(RestartReasonPath).Should().Be(StudioDnsRecovery.RestartExplanation);
    }

    [Fact]
    public void Poll_AFlappingDns_NeverRestarts()
    {
        _configStore.Save(BaseConfig(dnsServersBaked: "10.0.0.1"));
        var runtime = new FakeContainerRuntime { ContainerIsRunning = true };
        var recovery = new StudioDnsRecovery(runtime, _configStore, _ => { }, debounce: TimeSpan.FromSeconds(30));

        // A VPN flapping between the baseline and a candidate every poll, well past when a stable change
        // would have triggered a restart.
        for (var i = 0; i < 10; i++)
        {
            var now = Start + TimeSpan.FromSeconds(i * 15);
            recovery.Poll(i % 2 == 0 ? ["1.1.1.1"] : ["10.0.0.1"], now);
        }

        runtime.Stopped.Should().BeEmpty();
        runtime.Started.Should().BeEmpty();
    }

    [Fact]
    public void Poll_AfterActing_ANewStableValueCanTriggerAnotherRestartLater()
    {
        _configStore.Save(BaseConfig(dnsServersBaked: "10.0.0.1"));
        var runtime = new FakeContainerRuntime { ContainerIsRunning = true, CanResolveValue = false };
        var recovery = new StudioDnsRecovery(runtime, _configStore, _ => { }, debounce: TimeSpan.FromSeconds(30));

        recovery.Poll(["1.1.1.1"], Start);
        recovery.Poll(["1.1.1.1"], Start + TimeSpan.FromSeconds(30));
        runtime.Stopped.Should().Equal("fravashi-test");
        // The fake models StopContainer as leaving the container down unless told otherwise; the real
        // runtime's StartContainer right after brings it back up.
        runtime.ContainerIsRunning = true;

        // A second, later drift away from the NEW baseline.
        recovery.Poll(["8.8.8.8"], Start + TimeSpan.FromMinutes(5));
        recovery.Poll(["8.8.8.8"], Start + TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(30));

        runtime.Stopped.Should().Equal("fravashi-test", "fravashi-test");
        runtime.Started.Should().Equal("fravashi-test", "fravashi-test");
    }

    [Fact]
    public void Poll_WhenTheRestartFails_LogsIt_InsteadOfThrowing_AndWaitsAFullWindowBeforeTryingAgain()
    {
        // The resident process polls from a timer callback, where an exception would end fravashi.exe with every window
        // and the bridge; the real runtime's stop throws when the engine refuses or does not answer in time.
        _configStore.Save(BaseConfig(dnsServersBaked: "10.0.0.1"));
        var runtime = new FakeContainerRuntime
        {
            ContainerIsRunning = true, CanResolveValue = false, StopComplaint = "the engine did not answer", StopLeavesItRunning = true,
        };
        var log = new List<string>();
        string? balloon = null;
        var recovery = new StudioDnsRecovery(runtime, _configStore, log.Add, debounce: TimeSpan.FromSeconds(30), onRestarted: r => balloon = r);

        recovery.Poll(["1.1.1.1"], Start);
        var act = () => recovery.Poll(["1.1.1.1"], Start + TimeSpan.FromSeconds(30));

        act.Should().NotThrow();
        runtime.Stopped.Should().Equal("fravashi-test");
        runtime.Started.Should().BeEmpty();
        balloon.Should().BeNull("nothing was restarted");
        _configStore.Load()!.DnsServersBaked.Should().Be("10.0.0.1", "the container was never started on the new servers");
        log.Should().Contain(line => line.Contains("Could not recover", StringComparison.Ordinal)
            && line.Contains("the engine did not answer", StringComparison.Ordinal));

        // One poll later is not a full window since the failure, so no second attempt yet; a full window later is.
        recovery.Poll(["1.1.1.1"], Start + TimeSpan.FromSeconds(45));
        runtime.Stopped.Should().HaveCount(1);
        recovery.Poll(["1.1.1.1"], Start + TimeSpan.FromSeconds(75));
        runtime.Stopped.Should().HaveCount(2);
    }

    [Fact]
    public void Poll_CalledAgainWhileOneIsStillRunning_ReturnsAtOnce_WithoutAskingTheRuntimeAnything()
    {
        // The timer fires on schedule whether or not the previous callback has returned, and a restart can outlast one
        // interval. The nested call is made from inside the restart (its onRestarted), where the first is still running.
        _configStore.Save(BaseConfig(dnsServersBaked: "10.0.0.1"));
        var runtime = new FakeContainerRuntime { ContainerIsRunning = true, CanResolveValue = false };
        StudioDnsRecovery? recovery = null;
        var askedByTheNestedPoll = -1;
        recovery = new StudioDnsRecovery(runtime, _configStore, _ => { }, debounce: TimeSpan.FromSeconds(30), onRestarted: _ =>
        {
            var before = runtime.RunningQueries.Count;
            recovery!.Poll(["9.9.9.9"], Start + TimeSpan.FromMinutes(10));
            askedByTheNestedPoll = runtime.RunningQueries.Count - before;
        });

        recovery.Poll(["1.1.1.1"], Start);
        recovery.Poll(["1.1.1.1"], Start + TimeSpan.FromSeconds(30));

        askedByTheNestedPoll.Should().Be(0);
        runtime.Stopped.Should().Equal("fravashi-test");
        _configStore.Load()!.DnsServersBaked.Should().Be("1.1.1.1");

        // And the guard is released afterwards: the next poll asks again.
        var asked = runtime.RunningQueries.Count;
        recovery.Poll(["1.1.1.1"], Start + TimeSpan.FromMinutes(11));
        runtime.RunningQueries.Should().HaveCount(asked + 1);
    }
}
