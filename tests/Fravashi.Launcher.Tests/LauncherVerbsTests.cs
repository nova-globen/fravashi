using Fravashi.Launcher;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

/// <summary>Covers the <c>fravashi</c> verb methods on <see cref="LauncherApp"/> with fakes.</summary>
public sealed class LauncherVerbsTests : IDisposable
{
    private readonly string _directory;
    private readonly LauncherConfigStore _configStore;
    private readonly List<string> _log = new();
    private readonly List<string> _opened = new();

    public LauncherVerbsTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "fravashi-launcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _configStore = new LauncherConfigStore(Path.Combine(_directory, "launcher.json"));
    }

    private LauncherApp Build(FakeContainerRuntime docker, FakeTrayControl? tray = null)
        => new(
            docker,
            _configStore,
            launcherDirectory: _directory,
            log: _log.Add,
            openBrowser: _opened.Add,
            isPortFree: port => port == LauncherDefaults.PreferredPort,
            tray: tray ?? new FakeTrayControl(),
            defaultDataDir: () => Path.Combine(_directory, "data"));

    private void SeedConfig(int port = 9090, string name = "fravashi")
        => _configStore.Save(new LauncherConfig { Port = port, DataDir = Path.Combine(_directory, "data"), ContainerName = name });

    [Fact]
    public void Stop_WhenRunning_StopsContainer_KeepsPort()
    {
        SeedConfig(port: 9090);
        var docker = new FakeContainerRuntime { Available = true, ContainerPresent = true, ContainerIsRunning = true };

        Build(docker).Stop().Should().Be(ExitCodes.Success);

        docker.Stopped.Should().ContainSingle().Which.Should().Be("fravashi");
        _configStore.Load()!.Port.Should().Be(9090); // port stays reserved
    }

    /// <summary>
    /// Met on the owner's machine on 2026-09-21: podman STOPPED the container, then failed to unmount its overlay
    /// ("replacing mount point ...: directory not empty") and exited 125. Every non-zero exit was fatal, so
    /// `fravashi stop` died with an unhandled exception and a stack trace, reporting a failure for a stop that had
    /// happened. What a caller needs from a stop is its postcondition, and that can be ASKED.
    /// </summary>
    [Fact]
    public void Stop_WhenTheEngineComplainsButTheContainerIsDown_Succeeds_AndSaysWhatTheEngineSaid()
    {
        SeedConfig();
        var docker = new FakeContainerRuntime
        {
            Available = true, ContainerPresent = true, ContainerIsRunning = true,
            StopComplaint = "podman stop fravashi failed (exit 125): unmounting container: directory not empty",
        };

        Build(docker).Stop().Should().Be(ExitCodes.Success);

        docker.Stopped.Should().ContainSingle();
        _log.Should().Contain(line => line.Contains("directory not empty"), "the engine's complaint is surfaced, never swallowed");
        _log.Should().Contain(line => line.Contains("Fravashi is stopped"));
    }

    /// <summary>The other half, which keeps the change honest: a stop that did NOT stop anything still fails.</summary>
    [Fact]
    public void Stop_WhenTheEngineComplainsAndTheContainerIsStillUp_StillFails()
    {
        SeedConfig();
        var docker = new FakeContainerRuntime
        {
            Available = true, ContainerPresent = true, ContainerIsRunning = true,
            StopComplaint = "podman stop fravashi failed (exit 125): cannot reach the engine",
            StopLeavesItRunning = true,
        };

        var act = () => Build(docker).Stop();

        act.Should().Throw<InvalidOperationException>().WithMessage("*cannot reach the engine*");
        _log.Should().NotContain(line => line.Contains("Fravashi is stopped"));
    }

    /// <summary>
    /// Silence is not an answer. The runtimes' running check reads FALSE when the engine cannot be reached, so
    /// "not running" alone would turn an engine that died mid-stop into a confirmed stop, and a restore would
    /// then replace the database underneath a container that may still be up.
    /// </summary>
    [Fact]
    public void Stop_WhenTheEngineGoesSilent_IsNotTakenForAStoppedContainer()
    {
        SeedConfig();
        var docker = new FakeContainerRuntime
        {
            Available = true, ContainerPresent = true, ContainerIsRunning = true,
            StopComplaint = "podman stop fravashi failed (exit 125): cannot connect to the engine",
            StopTakesTheEngineDown = true,
        };

        var act = () => Build(docker).Stop();

        act.Should().Throw<InvalidOperationException>().WithMessage("*cannot connect to the engine*");
        _log.Should().NotContain(line => line.Contains("Fravashi is stopped"));
    }

    [Fact]
    public void Stop_WhenAlreadyStopped_IsNoOp()
    {
        SeedConfig();
        var docker = new FakeContainerRuntime { Available = true, ContainerPresent = true, ContainerIsRunning = false };

        Build(docker).Stop().Should().Be(ExitCodes.Success);

        docker.Stopped.Should().BeEmpty();
    }

    [Fact]
    public void Stop_WhenRuntimeUnavailable_ReturnsRuntimeUnavailable()
    {
        SeedConfig();
        Build(new FakeContainerRuntime { Available = false }).Stop().Should().Be(ExitCodes.RuntimeUnavailable);
    }

    [Fact]
    public void Onboarding_EnsuresContainer_OpensSettingsUrl()
    {
        var docker = new FakeContainerRuntime { Available = true, HasImage = true };

        Build(docker).Onboarding().Should().Be(ExitCodes.Success);

        _opened.Should().ContainSingle().Which.Should().Be($"http://localhost:{LauncherDefaults.PreferredPort}/onboarding?mode=settings");
    }

    [Fact]
    public void Website_OpensConfiguredUrl()
    {
        _configStore.Save(new LauncherConfig { Port = 9090, DataDir = _directory, WebsiteUrl = "https://example.test" });

        Build(new FakeContainerRuntime()).Website().Should().Be(ExitCodes.Success);

        _opened.Should().ContainSingle().Which.Should().Be("https://example.test");
    }

    [Fact]
    public void Website_WhenNoConfig_OpensDefault()
    {
        Build(new FakeContainerRuntime()).Website().Should().Be(ExitCodes.Success);

        _opened.Should().ContainSingle().Which.Should().Be(LauncherDefaults.WebsiteUrl);
    }

    [Fact]
    public void Website_WhenTheConfigSetsNone_OpensTheBuildsOwn()
    {
        // Backlog 112, through the verb the owner actually types: an unset website is the build's, at each use.
        _configStore.Save(new LauncherConfig { Port = 9090, DataDir = _directory });

        Build(new FakeContainerRuntime()).Website().Should().Be(ExitCodes.Success);

        _opened.Should().ContainSingle().Which.Should().Be(LauncherDefaults.WebsiteUrl);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    public void Status_NeverSaysProtectionsAreActiveWhileNoContainerIsRunning(bool available, bool present, bool running)
    {
        // Seen on the owner's machine after a reboot, with the engine down: "Container : unknown (podman is
        // not available)" and, five lines later, "8/8 protections active on podman". That count is a PLAN
        // worked out from the runtime's name. Nothing is enforced on a container that is not running, and a
        // status line is the last place to say otherwise.
        SeedConfig(port: 9090);
        var engine = new FakeContainerRuntime { Available = available, ContainerPresent = present, ContainerIsRunning = running };

        Build(engine).Status().Should().Be(ExitCodes.Success);

        var sandbox = _log.Single(line => line.Contains("Sandbox"));
        sandbox.Should().NotContain("protections active");
        sandbox.Should().Contain("will apply").And.Contain("not running");
    }

    [Fact]
    public void Status_SaysProtectionsAreActiveWhenTheContainerIsRunning()
    {
        SeedConfig(port: 9090);
        var engine = new FakeContainerRuntime { Available = true, ContainerPresent = true, ContainerIsRunning = true };

        Build(engine).Status().Should().Be(ExitCodes.Success);

        _log.Single(line => line.Contains("Sandbox")).Should().Contain("protections active");
    }

    [Fact]
    public void Status_ReportsRunningStateAndPort_ButNeverAUrl()
    {
        SeedConfig(port: 9191);
        var docker = new FakeContainerRuntime { Available = true, ContainerPresent = true, ContainerIsRunning = true };

        Build(docker).Status().Should().Be(ExitCodes.Success);

        var text = string.Join('\n', _log);
        text.Should().Contain("running");
        text.Should().Contain("9191");
        // The studio is window-only (ADR 0017): a URL in user-facing output only invites a browser
        // paste that lands on the refusal page (directive 2026-07-26).
        text.Should().NotContain("http://localhost");
    }

    [Fact]
    public void Logs_PrintsContainerLogs()
    {
        SeedConfig();
        var docker = new FakeContainerRuntime { Available = true, ContainerPresent = true, LogsValue = "line-a\nline-b" };

        Build(docker).Logs(50).Should().Be(ExitCodes.Success);

        string.Join('\n', _log).Should().Contain("line-a");
    }

    [Fact]
    public void Exit_WhenTrayRunning_SignalsExit_DoesNotStopContainer()
    {
        SeedConfig();
        var tray = new FakeTrayControl { Running = true };
        var docker = new FakeContainerRuntime { Available = true, ContainerPresent = true, ContainerIsRunning = true };

        Build(docker, tray).Exit().Should().Be(ExitCodes.Success);

        tray.SignalExitCalls.Should().Be(1);
        docker.Stopped.Should().BeEmpty(); // exit never touches the container
    }

    [Fact]
    public void Exit_WhenNoTray_ReportsNothingToClose()
    {
        var tray = new FakeTrayControl { Running = false };

        Build(new FakeContainerRuntime(), tray).Exit().Should().Be(ExitCodes.Success);

        string.Join('\n', _log).Should().Contain("No Fravashi tray");
    }


    [Fact]
    public void SetBroker_WithUrl_PersistsBrokerBaseUrl()
    {
        SeedConfig();

        Build(new FakeContainerRuntime()).SetBroker("http://broker.example:8080").Should().Be(ExitCodes.Success);

        _configStore.Load()!.Broker!.BaseUrl.Should().Be("http://broker.example:8080");
    }

    [Fact]
    public void SetBroker_Off_ClearsBrokerSection()
    {
        _configStore.Save(new LauncherConfig
        {
            Port = 9090,
            DataDir = _directory,
            Broker = new BrokerLaunchConfig { BaseUrl = "http://broker.example:8080" },
        });

        Build(new FakeContainerRuntime()).SetBroker("off").Should().Be(ExitCodes.Success);

        _configStore.Load()!.Broker.Should().BeNull();
    }

    [Fact]
    public void SetBroker_NonAbsoluteUrl_ReturnsUsageError_AndLeavesConfigUnchanged()
    {
        SeedConfig();

        Build(new FakeContainerRuntime()).SetBroker("not-a-url").Should().Be(ExitCodes.UsageError);

        _configStore.Load()!.Broker.Should().BeNull();
    }

    [Fact]
    public void SetBroker_PreservesExistingUserTokenSecretRef()
    {
        _configStore.Save(new LauncherConfig
        {
            Port = 9090,
            DataDir = _directory,
            Broker = new BrokerLaunchConfig { BaseUrl = "http://old:8080", UserTokenSecretRef = "broker:custom" },
        });

        Build(new FakeContainerRuntime()).SetBroker("http://new:8080").Should().Be(ExitCodes.Success);

        var broker = _configStore.Load()!.Broker!;
        broker.BaseUrl.Should().Be("http://new:8080");
        broker.UserTokenSecretRef.Should().Be("broker:custom");
    }

    [Fact]
    public void Status_WithBroker_ReportsTheEndpoint()
    {
        _configStore.Save(new LauncherConfig
        {
            Port = 9090,
            DataDir = Path.Combine(_directory, "data"),
            Broker = new BrokerLaunchConfig { BaseUrl = "http://broker.example:8080" },
        });
        var docker = new FakeContainerRuntime { Available = true, ContainerPresent = true, ContainerIsRunning = true };

        Build(docker).Status().Should().Be(ExitCodes.Success);

        string.Join('\n', _log).Should().Contain("http://broker.example:8080");
    }

    [Fact]
    public void ContainerNameOverride_TargetsAlternateContainer()
    {
        SeedConfig(name: "fravashi");
        var docker = new FakeContainerRuntime { Available = true, ContainerPresent = true, ContainerIsRunning = true };
        var app = new LauncherApp(
            docker, _configStore, _directory,
            log: _log.Add, openBrowser: _opened.Add,
            isPortFree: _ => true, tray: new FakeTrayControl(),
            containerNameOverride: "studio-selftest",
            defaultDataDir: () => Path.Combine(_directory, "data"));

        app.Stop().Should().Be(ExitCodes.Success);

        docker.Stopped.Should().ContainSingle().Which.Should().Be("studio-selftest");
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
}
