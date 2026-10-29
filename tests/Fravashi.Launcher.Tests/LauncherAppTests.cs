using Fravashi.Launcher;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

public sealed class LauncherAppTests : IDisposable
{
    private readonly string _directory;
    private readonly LauncherConfigStore _configStore;

    public LauncherAppTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "fravashi-launcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _configStore = new LauncherConfigStore(Path.Combine(_directory, "launcher.json"));
    }

    private LauncherApp Build(FakeContainerRuntime docker, List<string>? opened = null, Action<string>? log = null)
        => new(
            docker,
            _configStore,
            launcherDirectory: _directory,
            log: log ?? (_ => { }),
            openBrowser: url => (opened ??= new List<string>()).Add(url),
            isPortFree: port => port == LauncherDefaults.PreferredPort,
            // Keep first-run created configs inside the test's temp tree - never the real per-user data dir.
            defaultDataDir: () => Path.Combine(_directory, "data"));

    [Fact]
    public void Run_ReportsThePhaseSequence_ForAPlainLaunch()
    {
        var docker = new FakeContainerRuntime { Available = true, HasImage = true };
        var phases = new List<LaunchPhase>();
        var app = new LauncherApp(
            docker,
            _configStore,
            launcherDirectory: _directory,
            log: _ => { },
            openBrowser: _ => { },
            isPortFree: port => port == LauncherDefaults.PreferredPort,
            defaultDataDir: () => Path.Combine(_directory, "data"),
            phase: phases.Add);

        app.Run().Should().Be(ExitCodes.Success);

        phases.Should().ContainInOrder(
            LaunchPhase.CheckingRuntime,
            LaunchPhase.PreparingImage,
            LaunchPhase.VerifyingIntegrity,
            LaunchPhase.PreparingContainer,
            LaunchPhase.CheckingUpdates);
        // VPN is off in this config, so its phase must not appear.
        phases.Should().NotContain(LaunchPhase.StartingVpn);
    }

    [Fact]
    public void Run_WithABridge_InjectsTheEndpoint_AndRecordsIt()
    {
        var docker = new FakeContainerRuntime { Available = true, HasImage = true };
        var app = new LauncherApp(
            docker,
            _configStore,
            launcherDirectory: _directory,
            log: _ => { },
            openBrowser: _ => { },
            isPortFree: port => port == LauncherDefaults.PreferredPort,
            defaultDataDir: () => Path.Combine(_directory, "data"),
            bridge: new BridgeLaunch("http://172.23.160.1:9091", "bridge-secret"));

        app.Run().Should().Be(ExitCodes.Success);

        var spec = docker.RunSpecs.Should().ContainSingle().Subject;
        spec.BridgeUrl.Should().Be("http://172.23.160.1:9091");
        spec.BridgeToken.Should().Be("bridge-secret");
        _configStore.Load()!.BridgeUrl.Should().Be("http://172.23.160.1:9091");
    }

    [Fact]
    public void Run_WhenTheBridgeUrlDrifted_RecreatesTheContainer()
    {
        // A reboot reassigns the WSL subnet; the env baked into the container goes stale, so the
        // launcher must recreate (env cannot change on an existing container).
        var docker = new FakeContainerRuntime
        {
            Available = true, HasImage = true, ContainerPresent = true, ContainerIsRunning = true,
        };
        _configStore.Save(new LauncherConfig
        {
            Port = LauncherDefaults.PreferredPort,
            DataDir = Path.Combine(_directory, "data"),
            BridgeUrl = "http://172.23.160.1:9091",
        });

        var app = new LauncherApp(
            docker,
            _configStore,
            launcherDirectory: _directory,
            log: _ => { },
            openBrowser: _ => { },
            isPortFree: _ => true,
            defaultDataDir: () => Path.Combine(_directory, "data"),
            bridge: new BridgeLaunch("http://172.30.5.1:9091", "bridge-secret"));

        app.Run().Should().Be(ExitCodes.Success);

        docker.Removed.Should().ContainSingle();
        docker.RunSpecs.Should().ContainSingle().Which.BridgeUrl.Should().Be("http://172.30.5.1:9091");
        _configStore.Load()!.BridgeUrl.Should().Be("http://172.30.5.1:9091");
    }

    // ---- backlog 87: the app runner beside the bridge ------------------------------------------------------

    private LauncherApp AppWithAppRunner(FakeContainerRuntime docker, string url, string token) => new(
        docker,
        _configStore,
        launcherDirectory: _directory,
        log: _ => { },
        openBrowser: _ => { },
        isPortFree: _ => true,
        defaultDataDir: () => Path.Combine(_directory, "data"),
        bridge: new BridgeLaunch("http://172.23.160.1:9091", "bridge-secret", url, token));

    [Fact]
    public void Run_WithAnAppRunner_InjectsItsOwnEndpoint_AndRecordsOnlyAFingerprintOfItsToken()
    {
        var docker = RunningContainer();
        docker.ContainerPresent = false;
        _configStore.Save(new LauncherConfig { Port = LauncherDefaults.PreferredPort, DataDir = Path.Combine(_directory, "data") });

        AppWithAppRunner(docker, "http://172.23.160.1:9095", "runner-secret").Run().Should().Be(ExitCodes.Success);

        var spec = docker.RunSpecs.Should().ContainSingle().Subject;
        spec.AppRunnerUrl.Should().Be("http://172.23.160.1:9095");
        spec.AppRunnerToken.Should().Be("runner-secret");
        spec.BridgeToken.Should().Be("bridge-secret", "the two tokens are different secrets");
        var baked = _configStore.Load()!.AppRunnerBaked;
        baked.Should().StartWith("http://172.23.160.1:9095|").And.NotContain("runner-secret");
    }

    [Theory]
    [InlineData("http://172.23.160.1:9096", "runner-secret")]
    [InlineData("http://172.23.160.1:9095", "rotated-secret")]
    public void Run_WhenTheAppRunnerEndpointOrTokenChanged_RecreatesTheContainer(string url, string token)
    {
        var first = RunningContainer();
        first.ContainerPresent = false;
        _configStore.Save(new LauncherConfig { Port = LauncherDefaults.PreferredPort, DataDir = Path.Combine(_directory, "data") });
        AppWithAppRunner(first, "http://172.23.160.1:9095", "runner-secret").Run().Should().Be(ExitCodes.Success);

        var second = RunningContainer();
        AppWithAppRunner(second, url, token).Run().Should().Be(ExitCodes.Success);

        second.Removed.Should().ContainSingle("a restart cannot change a container's environment");
        second.RunSpecs.Should().ContainSingle().Which.AppRunnerToken.Should().Be(token);
    }

    [Fact]
    public void Run_WhenTheAppRunnerIsUnchanged_KeepsTheContainer()
    {
        var first = RunningContainer();
        first.ContainerPresent = false;
        _configStore.Save(new LauncherConfig { Port = LauncherDefaults.PreferredPort, DataDir = Path.Combine(_directory, "data") });
        AppWithAppRunner(first, "http://172.23.160.1:9095", "runner-secret").Run().Should().Be(ExitCodes.Success);

        var second = RunningContainer();
        AppWithAppRunner(second, "http://172.23.160.1:9095", "runner-secret").Run().Should().Be(ExitCodes.Success);

        second.Removed.Should().BeEmpty();
    }

    [Fact]
    public void The_container_arguments_carry_the_app_runner_only_when_both_halves_are_there()
    {
        var withBoth = new List<string>();
        ContainerRunArgs.AppendBridgeEnv(withBoth, new ContainerRunSpec("c", "i", 1, 2, "d",
            BridgeUrl: "http://b", BridgeToken: "t", AppRunnerUrl: "http://r", AppRunnerToken: "rt"));
        var withoutToken = new List<string>();
        ContainerRunArgs.AppendBridgeEnv(withoutToken, new ContainerRunSpec("c", "i", 1, 2, "d",
            BridgeUrl: "http://b", BridgeToken: "t", AppRunnerUrl: "http://r"));

        withBoth.Should().Contain(["FRAVASHI_APPRUNNER_URL=http://r", "FRAVASHI_APPRUNNER_TOKEN=rt"]);
        withoutToken.Should().NotContain(a => a.StartsWith("FRAVASHI_APPRUNNER", StringComparison.Ordinal));
    }

    // ---- backlog 157: the upgrade operation beside the bridge ---------------------------------------------------

    private LauncherApp AppWithUpgrade(FakeContainerRuntime docker, string url, string token) => new(
        docker,
        _configStore,
        launcherDirectory: _directory,
        log: _ => { },
        openBrowser: _ => { },
        isPortFree: _ => true,
        defaultDataDir: () => Path.Combine(_directory, "data"),
        bridge: new BridgeLaunch("http://172.23.160.1:9091", "bridge-secret", "http://172.23.160.1:9095", "runner-secret", url, token));

    [Fact]
    public void Run_WithAnUpgradeOperation_InjectsItsOwnEndpoint_AndRecordsOnlyAFingerprintOfItsToken()
    {
        var docker = RunningContainer();
        docker.ContainerPresent = false;
        _configStore.Save(new LauncherConfig { Port = LauncherDefaults.PreferredPort, DataDir = Path.Combine(_directory, "data") });

        AppWithUpgrade(docker, "http://172.23.160.1:9097", "upgrade-secret").Run().Should().Be(ExitCodes.Success);

        var spec = docker.RunSpecs.Should().ContainSingle().Subject;
        spec.UpgradeUrl.Should().Be("http://172.23.160.1:9097");
        spec.UpgradeToken.Should().Be("upgrade-secret");
        spec.AppRunnerToken.Should().Be("runner-secret", "three endpoints, three different secrets");
        _configStore.Load()!.UpgradeBaked.Should().StartWith("http://172.23.160.1:9097|").And.NotContain("upgrade-secret");
    }

    [Fact]
    public void Run_WhenTheUpgradeTokenWasRotated_RecreatesTheContainer_AndKeepsItWhenNothingMoved()
    {
        var first = RunningContainer();
        first.ContainerPresent = false;
        _configStore.Save(new LauncherConfig { Port = LauncherDefaults.PreferredPort, DataDir = Path.Combine(_directory, "data") });
        AppWithUpgrade(first, "http://172.23.160.1:9097", "upgrade-secret").Run().Should().Be(ExitCodes.Success);

        var same = RunningContainer();
        AppWithUpgrade(same, "http://172.23.160.1:9097", "upgrade-secret").Run().Should().Be(ExitCodes.Success);
        same.Removed.Should().BeEmpty();

        var rotated = RunningContainer();
        AppWithUpgrade(rotated, "http://172.23.160.1:9097", "rotated-secret").Run().Should().Be(ExitCodes.Success);
        rotated.Removed.Should().ContainSingle("a restart cannot change a container's environment");
        rotated.RunSpecs.Should().ContainSingle().Which.UpgradeToken.Should().Be("rotated-secret");
    }

    [Fact]
    public void The_container_arguments_carry_the_upgrade_operation_only_when_both_halves_are_there()
    {
        var withBoth = new List<string>();
        ContainerRunArgs.AppendBridgeEnv(withBoth, new ContainerRunSpec("c", "i", 1, 2, "d",
            BridgeUrl: "http://b", BridgeToken: "t", UpgradeUrl: "http://u", UpgradeToken: "ut"));
        var withoutToken = new List<string>();
        ContainerRunArgs.AppendBridgeEnv(withoutToken, new ContainerRunSpec("c", "i", 1, 2, "d",
            BridgeUrl: "http://b", BridgeToken: "t", UpgradeUrl: "http://u"));

        withBoth.Should().Contain(["FRAVASHI_UPGRADE_URL=http://u", "FRAVASHI_UPGRADE_TOKEN=ut"]);
        withoutToken.Should().NotContain(a => a.StartsWith("FRAVASHI_UPGRADE", StringComparison.Ordinal));
    }

    // ---- backlog 135: a rotated bridge token ---------------------------------------------------
    // Met on the owner's machine, 2026-09-21. The token was rotated (on a session's own advice, after it had
    // been written to a log), fravashi.exe restarted, and no repository would open: the container still carried the
    // OLD token as an environment value from its creation, the URL had not moved, so nothing looked stale.

    private LauncherApp AppWithBridge(FakeContainerRuntime docker, string token) => new(
        docker,
        _configStore,
        launcherDirectory: _directory,
        log: _ => { },
        openBrowser: _ => { },
        isPortFree: _ => true,
        defaultDataDir: () => Path.Combine(_directory, "data"),
        bridge: new BridgeLaunch("http://172.23.160.1:9091", token));

    private static FakeContainerRuntime RunningContainer() => new()
    {
        Available = true, HasImage = true, ContainerPresent = true, ContainerIsRunning = true,
    };

    [Fact]
    public void Run_WhenTheBridgeTokenWasRotated_RecreatesTheContainer_AndNeverStoresTheToken()
    {
        var first = RunningContainer();
        first.ContainerPresent = false;
        _configStore.Save(new LauncherConfig { Port = LauncherDefaults.PreferredPort, DataDir = Path.Combine(_directory, "data") });
        AppWithBridge(first, "token-before-rotation").Run().Should().Be(ExitCodes.Success);

        var recorded = _configStore.Load()!.BridgeTokenFingerprint;
        recorded.Should().NotBeNullOrWhiteSpace();
        recorded.Should().NotContain("token-before-rotation", "a fingerprint is recorded, never the token");

        var second = RunningContainer();
        AppWithBridge(second, "token-after-rotation").Run().Should().Be(ExitCodes.Success);

        second.Removed.Should().ContainSingle();
        second.RunSpecs.Should().ContainSingle().Which.BridgeToken.Should().Be("token-after-rotation");
        _configStore.Load()!.BridgeTokenFingerprint.Should().NotBe(recorded);
    }

    [Fact]
    public void Run_WhenTheBridgeTokenIsUnchanged_KeepsTheContainer()
    {
        var first = RunningContainer();
        first.ContainerPresent = false;
        _configStore.Save(new LauncherConfig { Port = LauncherDefaults.PreferredPort, DataDir = Path.Combine(_directory, "data") });
        AppWithBridge(first, "same-token").Run().Should().Be(ExitCodes.Success);

        var second = RunningContainer();
        AppWithBridge(second, "same-token").Run().Should().Be(ExitCodes.Success);

        second.Removed.Should().BeEmpty();
        second.RunSpecs.Should().BeEmpty();
    }

    [Fact]
    public void Run_WithALauncherJsonFromBeforeFingerprints_KeepsTheContainer_AndStartsRecording()
    {
        // An install upgraded to this build has no fingerprint yet. Not knowing is not evidence of a rotation,
        // and recreating every existing container on upgrade would be a cost with nothing behind it.
        _configStore.Save(new LauncherConfig
        {
            Port = LauncherDefaults.PreferredPort,
            DataDir = Path.Combine(_directory, "data"),
            BridgeUrl = "http://172.23.160.1:9091",
        });
        var docker = RunningContainer();

        AppWithBridge(docker, "some-token").Run().Should().Be(ExitCodes.Success);

        docker.Removed.Should().BeEmpty();
        _configStore.Load()!.BridgeTokenFingerprint.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Run_AfterTheBrokerEndpointIsSet_RecreatesTheContainer_BecauseARestartCannotChangeItsEnvironment()
    {
        // Backlog 138, met on 2026-09-21: `fravashi broker <url>` said "fravashi stop then fravashi", that restarted the same
        // container, and the endpoint never reached the studio until the container was removed by hand.
        var first = RunningContainer();
        first.ContainerPresent = false;
        _configStore.Save(new LauncherConfig { Port = LauncherDefaults.PreferredPort, DataDir = Path.Combine(_directory, "data") });
        AppWithBridge(first, "same-token").Run().Should().Be(ExitCodes.Success);

        AppWithBridge(RunningContainer(), "same-token").SetBroker("https://broker.example.test").Should().Be(ExitCodes.Success);

        var second = RunningContainer();
        AppWithBridge(second, "same-token").Run().Should().Be(ExitCodes.Success);
        second.Removed.Should().ContainSingle();
        second.RunSpecs.Should().ContainSingle().Which.BrokerBaseUrl.Should().Be("https://broker.example.test");

        // Settled: the next start keeps what it made.
        var third = RunningContainer();
        AppWithBridge(third, "same-token").Run().Should().Be(ExitCodes.Success);
        third.Removed.Should().BeEmpty();

        // And turning it off is a change too.
        AppWithBridge(RunningContainer(), "same-token").SetBroker("off").Should().Be(ExitCodes.Success);
        var fourth = RunningContainer();
        AppWithBridge(fourth, "same-token").Run().Should().Be(ExitCodes.Success);
        fourth.Removed.Should().ContainSingle();
        fourth.RunSpecs.Should().ContainSingle().Which.BrokerBaseUrl.Should().BeNullOrEmpty();
    }

    [Fact]
    public void Run_WithNoBrokerThenOrNow_KeepsTheContainer_OnAnInstallThatNeverRecordedOne()
    {
        // The upgrade path for nearly every install: no broker, and a launcher.json from before this field.
        _configStore.Save(new LauncherConfig
        {
            Port = LauncherDefaults.PreferredPort,
            DataDir = Path.Combine(_directory, "data"),
            BridgeUrl = "http://172.23.160.1:9091",
        });
        var docker = RunningContainer();

        AppWithBridge(docker, "some-token").Run().Should().Be(ExitCodes.Success);

        docker.Removed.Should().BeEmpty();
    }

    [Fact]
    public void Run_WhenTheBridgeUrlIsUnchanged_KeepsTheContainer()
    {
        var docker = new FakeContainerRuntime
        {
            Available = true, HasImage = true, ContainerPresent = true, ContainerIsRunning = true,
        };
        _configStore.Save(new LauncherConfig
        {
            Port = LauncherDefaults.PreferredPort,
            DataDir = Path.Combine(_directory, "data"),
            BridgeUrl = "http://172.23.160.1:9091",
        });

        var app = new LauncherApp(
            docker,
            _configStore,
            launcherDirectory: _directory,
            log: _ => { },
            openBrowser: _ => { },
            isPortFree: _ => true,
            defaultDataDir: () => Path.Combine(_directory, "data"),
            bridge: new BridgeLaunch("http://172.23.160.1:9091", "bridge-secret"));

        app.Run().Should().Be(ExitCodes.Success);

        docker.Removed.Should().BeEmpty();
        docker.RunSpecs.Should().BeEmpty();
    }

    [Fact]
    public void Run_WithoutABridge_KeepsThePreviousArgShape()
    {
        var docker = new FakeContainerRuntime { Available = true, HasImage = true };

        Build(docker).Run().Should().Be(ExitCodes.Success);

        var spec = docker.RunSpecs.Should().ContainSingle().Subject;
        spec.BridgeUrl.Should().BeNull();
        spec.BridgeToken.Should().BeNull();
        new DockerRuntime().BuildRunArgs(spec).Should().NotContain(a => a.Contains("FRAVASHI_BRIDGE"));
    }

    [Fact]
    public void Run_WhenRuntimeUnavailable_ReturnsRuntimeUnavailable()
    {
        var docker = new FakeContainerRuntime { Available = false };

        Build(docker).Run().Should().Be(ExitCodes.RuntimeUnavailable);
    }

    [Fact]
    public void Run_FirstRun_ReservesPreferredPort_CreatesContainer_OpensBrowser()
    {
        var docker = new FakeContainerRuntime { Available = true, HasImage = true };
        var opened = new List<string>();

        var exit = Build(docker, opened).Run();

        exit.Should().Be(ExitCodes.Success);
        _configStore.Load()!.Port.Should().Be(LauncherDefaults.PreferredPort);
        docker.RunSpecs.Should().ContainSingle();
        docker.RunSpecs[0].HostPort.Should().Be(LauncherDefaults.PreferredPort);
        docker.RunSpecs[0].ContainerPort.Should().Be(LauncherDefaults.ContainerPort);
        opened.Should().ContainSingle().Which.Should().Be($"http://localhost:{LauncherDefaults.PreferredPort}");
    }

    [Fact]
    public void Run_FirstRun_EnsuresTheWorkspacesVolume_AndMountsItInTheSpec()
    {
        // Plan 2d (decision 2): the contained agents' synced copies live on a named volume.
        var docker = new FakeContainerRuntime { Available = true, HasImage = true };

        Build(docker).Run().Should().Be(ExitCodes.Success);

        docker.EnsuredVolumes.Should().ContainSingle().Which.Should().Be(LauncherDefaults.WorkspacesVolume);
        var spec = docker.RunSpecs.Should().ContainSingle().Subject;
        spec.WorkspacesVolume.Should().Be(LauncherDefaults.WorkspacesVolume);
        spec.WorkspacesMountPath.Should().Be(LauncherDefaults.WorkspacesMountPath);
    }

    [Fact]
    public void Run_RemovesNoVolume()
    {
        // The pre-2c working-copy volume's retirement served installs older than 0.9.101 only and was removed with the
        // bridge release; the former agent-workspaces volume is kept until the cleanup release.
        var docker = new FakeContainerRuntime { Available = true, HasImage = true, RemoveVolumeResult = true };

        Build(docker).Run().Should().Be(ExitCodes.Success);

        docker.RemovedVolumes.Should().BeEmpty();
    }
    [Fact]
    public void Run_WhenImageMissing_LoadsFromTarball()
    {
        File.WriteAllText(Path.Combine(_directory, LauncherDefaults.ImageTarFileName), "fake tar");
        var docker = new FakeContainerRuntime { Available = true, HasImage = false };

        Build(docker).Run().Should().Be(ExitCodes.Success);

        docker.LoadedTars.Should().ContainSingle();
    }

    [Fact]
    public void Run_WhenImageMissing_AndNoTarball_ReturnsImageMissing()
    {
        var docker = new FakeContainerRuntime { Available = true, HasImage = false };

        Build(docker).Run().Should().Be(ExitCodes.ImageMissing);
    }

    [Fact]
    public void Run_WhenContainerExistsStopped_StartsIt_DoesNotCreateNew()
    {
        var docker = new FakeContainerRuntime { Available = true, HasImage = true, ContainerPresent = true, ContainerIsRunning = false };

        Build(docker).Run().Should().Be(ExitCodes.Success);

        docker.Started.Should().ContainSingle();
        docker.RunSpecs.Should().BeEmpty();
    }

    [Fact]
    public void Run_WhenContainerPredatesTheLoadedImage_RecreatesIt()
    {
        // A local-bundle reinstall swaps the image under the same tag; starting the old container
        // would run the old build (the owner hit this on 2026-07-26). The launcher must recreate.
        var docker = new FakeContainerRuntime
        {
            Available = true,
            HasImage = true,
            ContainerPresent = true,
            ContainerIsRunning = true,
            ImageDigestValue = "sha256:newimage",
            ContainerImageIdValue = "oldimage",
        };

        Build(docker).Run().Should().Be(ExitCodes.Success);

        docker.Removed.Should().ContainSingle();
        docker.RunSpecs.Should().ContainSingle();
        docker.Started.Should().BeEmpty();
        // Housekeeping: the image the removed container ran is retired (non-forced) once the new container
        // is up, so a reinstall does not leave a dangling <none> image behind.
        docker.RemovedImages.Should().ContainSingle().Which.Should().Be("oldimage");
    }

    [Fact]
    public void Run_WhenContainerMatchesTheLoadedImage_KeepsIt()
    {
        // Engines report ids with and without the sha256: prefix; a match must not recreate.
        var docker = new FakeContainerRuntime
        {
            Available = true,
            HasImage = true,
            ContainerPresent = true,
            ContainerIsRunning = false,
            ImageDigestValue = "sha256:sameimage",
            ContainerImageIdValue = "sameimage",
        };

        Build(docker).Run().Should().Be(ExitCodes.Success);

        docker.Removed.Should().BeEmpty();
        docker.Started.Should().ContainSingle();
        docker.RunSpecs.Should().BeEmpty();
        docker.RemovedImages.Should().BeEmpty();
    }

    [Fact]
    public void Run_WhenRuntimeCannotReportTheContainerImage_KeepsThePreviousBehaviour()
    {
        var docker = new FakeContainerRuntime
        {
            Available = true,
            HasImage = true,
            ContainerPresent = true,
            ContainerIsRunning = false,
            ContainerImageIdValue = null,
        };

        Build(docker).Run().Should().Be(ExitCodes.Success);

        docker.Removed.Should().BeEmpty();
        docker.Started.Should().ContainSingle();
    }

    [Fact]
    public void Run_SecondLaunch_ReusesPersistedPort()
    {
        // A persisted port is reused as long as it is one the identity provider has registered (backlog item 7);
        // a stored port OUTSIDE that list is moved back onto it, which its own test below covers.
        var stored = LauncherDefaults.StudioPorts[^1];
        _configStore.Save(new LauncherConfig { Port = stored, DataDir = Path.Combine(_directory, "data") });
        var docker = new FakeContainerRuntime { Available = true, HasImage = true };

        Build(docker).Run().Should().Be(ExitCodes.Success);

        docker.RunSpecs[0].HostPort.Should().Be(stored);
    }

    // ---- F5 VPN sidecar orchestration (T9) ----

    private LauncherApp BuildWithVpn(FakeContainerRuntime runtime, List<string>? log = null, Func<VpnConfig, bool>? probe = null)
        => new(
            runtime,
            _configStore,
            launcherDirectory: _directory,
            log: line => (log ??= new List<string>()).Add(line),
            openBrowser: _ => { },
            isPortFree: port => port == LauncherDefaults.PreferredPort,
            defaultDataDir: () => Path.Combine(_directory, "data"),
            vpnConnectedProbe: probe ?? (_ => true),
            vpnPollInterval: TimeSpan.Zero,
            vpnReadyAttempts: 3);

    private void SaveVpnConfig(bool enabled = true) =>
        _configStore.Save(new LauncherConfig
        {
            Port = LauncherDefaults.PreferredPort,
            DataDir = Path.Combine(_directory, "data"),
            Vpn = new VpnConfig { Enabled = enabled },
        });

    [Fact]
    public void Run_VpnEnabled_OnDocker_StartsTheSidecarFirst_ThenTheStudioJoinedToItsNetns()
    {
        SaveVpnConfig();
        var runtime = new FakeContainerRuntime { Available = true, HasImage = true, SupportsVpn = true };

        BuildWithVpn(runtime).Run().Should().Be(ExitCodes.Success);

        // Sidecar started (VPN1) and the studio joined its netns (VPN2/VPN6), not publishing its own port.
        runtime.VpnSpecs.Should().ContainSingle().Which.ContainerName.Should().Be(LauncherDefaults.VpnContainerName);
        var studio = runtime.RunSpecs.Should().ContainSingle().Subject;
        studio.NetworkContainer.Should().Be(LauncherDefaults.VpnContainerName);
        studio.VpnControlServerUrl.Should().Be($"http://127.0.0.1:{LauncherDefaults.VpnControlServerPort}");
    }

    [Fact]
    public void Run_VpnEnabled_RecordsTheSidecarAsASecondIntegrityComponent()
    {
        SaveVpnConfig();
        var runtime = new FakeContainerRuntime { Available = true, HasImage = true, SupportsVpn = true };

        BuildWithVpn(runtime).Run().Should().Be(ExitCodes.Success);

        // VPN4: the launcher-written integrity record carries BOTH the studio and the VPN sidecar.
        var recordPath = Path.Combine(_directory, "data", "integrity-record.json");
        File.Exists(recordPath).Should().BeTrue();
        var json = File.ReadAllText(recordPath);
        json.Should().Contain("vpn-sidecar").And.Contain("sidecar");
    }

    [Fact]
    public void Run_VpnEnabled_OnWslc_WhichCannotRunASidecar_RefusesWithGuidance_AndCreatesNoStudio()
    {
        SaveVpnConfig();
        // A runtime that does NOT support the sidecar (wslc-like): SupportsVpn stays false.
        var runtime = new FakeContainerRuntime { Available = true, HasImage = true, Name = "wslc", SupportsVpn = false };
        var log = new List<string>();

        BuildWithVpn(runtime, log).Run().Should().Be(ExitCodes.VpnUnsupportedRuntime);

        runtime.VpnSpecs.Should().BeEmpty();
        runtime.RunSpecs.Should().BeEmpty(); // the studio is never started without the tunnel it must run through.
        string.Join("\n", log).Should().Contain("Docker or Podman");
    }

    [Fact]
    public void Run_VpnEnabled_WithASwappedSidecarImage_FailsClosed()
    {
        // Pin a digest that will NOT match the resolved sidecar digest → refuse (VPN4 fail-closed).
        _configStore.Save(new LauncherConfig
        {
            Port = LauncherDefaults.PreferredPort,
            DataDir = Path.Combine(_directory, "data"),
            Vpn = new VpnConfig { Enabled = true, PinnedImageDigest = "sha256:EXPECTED_DIFFERENT" },
        });
        var runtime = new FakeContainerRuntime { Available = true, HasImage = true, SupportsVpn = true, VpnDigestValue = "sha256:ACTUAL" };

        BuildWithVpn(runtime).Run().Should().Be(ExitCodes.IntegrityRefused);

        runtime.RunSpecs.Should().BeEmpty(); // the studio never joins a swapped sidecar's netns.
    }

    [Fact]
    public void Run_VpnEnabled_TunnelNotConfirmed_StillStartsTheStudio_WhichHoldsAtTheGate()
    {
        SaveVpnConfig();
        var runtime = new FakeContainerRuntime { Available = true, HasImage = true, SupportsVpn = true };
        var log = new List<string>();

        // The readiness probe never confirms - the launcher must NOT block; the studio's own gate holds it.
        BuildWithVpn(runtime, log, probe: _ => false).Run().Should().Be(ExitCodes.Success);

        runtime.VpnSpecs.Should().ContainSingle();
        runtime.RunSpecs.Should().ContainSingle();
        string.Join("\n", log).Should().Contain("waiting-for-connection");
    }

    [Fact]
    public void Run_VpnDisabled_StartsNoSidecar_AndPublishesTheStudioPortNormally()
    {
        _configStore.Save(new LauncherConfig
        {
            Port = LauncherDefaults.PreferredPort,
            DataDir = Path.Combine(_directory, "data"),
            Vpn = new VpnConfig { Enabled = false },
        });
        var runtime = new FakeContainerRuntime { Available = true, HasImage = true, SupportsVpn = true };

        BuildWithVpn(runtime).Run().Should().Be(ExitCodes.Success);

        runtime.VpnSpecs.Should().BeEmpty();
        runtime.RunSpecs.Should().ContainSingle().Which.NetworkContainer.Should().BeNull();
    }

    // Backlog item 7: a moved studio port breaks sign-in, because the identity provider matches the redirect
    // URI exactly (port included) and only has the ports in LauncherDefaults.StudioPorts registered. The
    // launcher therefore never leaves that list, and fails honestly instead of moving off it.

    [Fact]
    public void Run_FirstRun_ReservesTheFirstFreeRegisteredPort()
    {
        var expected = LauncherDefaults.StudioPorts[0];
        var runtime = new FakeContainerRuntime { Available = true, HasImage = true };
        var log = new List<string>();
        var app = new LauncherApp(
            runtime,
            _configStore,
            launcherDirectory: _directory,
            log: log.Add,
            openBrowser: _ => { },
            // Only the registered ports count as candidates; a free 41733 is not one of them.
            isPortFree: port => port == expected || port == 41733,
            defaultDataDir: () => Path.Combine(_directory, "data"));

        app.Run().Should().Be(ExitCodes.Success);

        _configStore.Load()!.Port.Should().Be(expected);
        runtime.RunSpecs.Should().ContainSingle().Which.HostPort.Should().Be(expected);
        string.Join("\n", log).Should().Contain(
            $"reserved host port {expected.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    }

    [Fact]
    public void Run_FirstRun_WhenEveryRegisteredPortIsBusy_FailsNamingThem_AndStartsNothing()
    {
        var runtime = new FakeContainerRuntime { Available = true, HasImage = true };
        var log = new List<string>();
        var app = new LauncherApp(
            runtime,
            _configStore,
            launcherDirectory: _directory,
            log: log.Add,
            openBrowser: _ => { },
            isPortFree: _ => false,
            defaultDataDir: () => Path.Combine(_directory, "data"));

        app.Run().Should().Be(ExitCodes.PortsUnavailable);

        var text = string.Join("\n", log);
        foreach (var candidate in LauncherDefaults.StudioPorts)
        {
            text.Should().Contain(candidate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // Nothing was started on a port sign-in would then be refused on, and no config was written.
        runtime.RunSpecs.Should().BeEmpty();
        runtime.Started.Should().BeEmpty();
        _configStore.Load().Should().BeNull();
    }

    [Fact]
    public void Run_KeepsAStoredRegisteredPort_EvenWhileOurOwnContainerHoldsIt()
    {
        // On a normal restart the thing holding 9090 IS the running studio container. A busy probe must
        // never be read as a reason to move the port.
        _configStore.Save(new LauncherConfig
        {
            Port = LauncherDefaults.PreferredPort,
            DataDir = Path.Combine(_directory, "data"),
        });
        var runtime = new FakeContainerRuntime
        {
            Available = true, HasImage = true, ContainerPresent = true, ContainerIsRunning = true,
        };
        var app = new LauncherApp(
            runtime,
            _configStore,
            launcherDirectory: _directory,
            log: _ => { },
            openBrowser: _ => { },
            isPortFree: _ => false,
            defaultDataDir: () => Path.Combine(_directory, "data"));

        app.Run().Should().Be(ExitCodes.Success);

        _configStore.Load()!.Port.Should().Be(LauncherDefaults.PreferredPort);
        runtime.Removed.Should().BeEmpty();
    }

    [Fact]
    public void Run_WhenTheStoredPortIsOutsideTheRegisteredList_MovesBackOntoIt_PersistsAndSaysWhy()
    {
        // An install that reserved a random free port before the list existed (or a hand-edited
        // launcher.json) would serve where the provider has no redirect URI registered.
        _configStore.Save(new LauncherConfig
        {
            Port = 41733,
            DataDir = Path.Combine(_directory, "data"),
        });
        var runtime = new FakeContainerRuntime
        {
            Available = true, HasImage = true, ContainerPresent = true, ContainerIsRunning = true,
        };
        var log = new List<string>();
        var expected = LauncherDefaults.StudioPorts[0];
        var app = new LauncherApp(
            runtime,
            _configStore,
            launcherDirectory: _directory,
            log: log.Add,
            openBrowser: _ => { },
            isPortFree: port => port == expected,
            defaultDataDir: () => Path.Combine(_directory, "data"));

        app.Run().Should().Be(ExitCodes.Success);

        _configStore.Load()!.Port.Should().Be(expected);
        var text = string.Join("\n", log);
        text.Should().Contain("41733");
        text.Should().Contain("identity provider");
        // The move lands on a port sign-in actually works on, and says so.
        text.Should().Contain(LauncherDefaults.SignInPortsSentence);
        // A container's published port is baked at creation, so the stale one must be recreated.
        runtime.Removed.Should().Contain(LauncherDefaults.ContainerName);
        runtime.RunSpecs.Should().ContainSingle().Which.HostPort.Should().Be(expected);
    }

    [Fact]
    public void Run_WhenThePortMovesAndTheImageAlsoMoved_RetiresTheSupersededImage()
    {
        // The field combination behind this: a new fravashi.exe brings a new image under the same tag and lands on
        // a launcher.json whose port predates the registered list. The port move used to short-circuit the
        // image comparison, so the container was recreated from the current image (right) while the superseded
        // one was never retired and the log never mentioned the swap (wrong: one dangling image per reinstall).
        _configStore.Save(new LauncherConfig
        {
            Port = 41733,
            DataDir = Path.Combine(_directory, "data"),
        });
        var runtime = new FakeContainerRuntime
        {
            Available = true,
            HasImage = true,
            ContainerPresent = true,
            ContainerIsRunning = true,
            ImageDigestValue = "sha256:new222",
            ContainerImageIdValue = "sha256:old111",
        };
        var log = new List<string>();
        var expected = LauncherDefaults.StudioPorts[0];
        var app = new LauncherApp(
            runtime,
            _configStore,
            launcherDirectory: _directory,
            log: log.Add,
            openBrowser: _ => { },
            isPortFree: port => port == expected,
            defaultDataDir: () => Path.Combine(_directory, "data"));

        app.Run().Should().Be(ExitCodes.Success);

        runtime.Removed.Should().ContainSingle().Which.Should().Be(LauncherDefaults.ContainerName);
        runtime.RunSpecs.Should().ContainSingle().Which.HostPort.Should().Be(expected);
        runtime.RemovedImages.Should().ContainSingle().Which.Should().Be("sha256:old111");
        var text = string.Join("\n", log);
        text.Should().Contain("older image");
        text.Should().Contain("publishes the previous host port");
    }

    [Fact]
    public void Run_WhenTheImageMoved_KeepsTheSupersededImageIfSomebodyStillNamesIt()
    {
        // Backlog 165, on the start path a reinstall takes: the old image's only name was a backup tag, and the
        // remove by id took it. Only an image nothing names is housekeeping.
        _configStore.Save(new LauncherConfig
        {
            Port = LauncherDefaults.StudioPorts[0],
            DataDir = Path.Combine(_directory, "data"),
        });
        var runtime = new FakeContainerRuntime
        {
            Available = true,
            HasImage = true,
            ContainerPresent = true,
            ContainerIsRunning = true,
            ImageDigestValue = "sha256:new222",
            ContainerImageIdValue = "sha256:old111",
        };
        runtime.ImageNamesValue["sha256:old111"] = ["localhost/fravashi:backup-0.9.39", "localhost/fravashi:backup-0.9.39.hold"];
        var log = new List<string>();
        var app = new LauncherApp(
            runtime,
            _configStore,
            launcherDirectory: _directory,
            log: log.Add,
            openBrowser: _ => { },
            isPortFree: _ => true,
            defaultDataDir: () => Path.Combine(_directory, "data"));

        app.Run().Should().Be(ExitCodes.Success);

        runtime.RunSpecs.Should().ContainSingle("the container is still recreated from the newer image");
        runtime.RemovedImages.Should().BeEmpty();
        log.Should().Contain(l => l.Contains("Kept the superseded image") && l.Contains("backup-0.9.39"));
    }

    [Fact]
    public void Run_WhenTheStoredPortIsOutsideTheList_AndEveryRegisteredPortIsBusy_FailsHonestly()
    {
        _configStore.Save(new LauncherConfig
        {
            Port = 41733,
            DataDir = Path.Combine(_directory, "data"),
        });
        var runtime = new FakeContainerRuntime { Available = true, HasImage = true };
        var log = new List<string>();
        var app = new LauncherApp(
            runtime,
            _configStore,
            launcherDirectory: _directory,
            log: log.Add,
            openBrowser: _ => { },
            isPortFree: _ => false,
            defaultDataDir: () => Path.Combine(_directory, "data"));

        app.Run().Should().Be(ExitCodes.PortsUnavailable);

        runtime.RunSpecs.Should().BeEmpty();
        // The unusable port is left recorded rather than overwritten with a guess.
        _configStore.Load()!.Port.Should().Be(41733);
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

internal sealed class FakeContainerRuntime : IContainerRuntime
{
    public string Name { get; set; } = "fake";
    public bool Available { get; set; } = true;
    public bool HasImage { get; set; } = true;
    public bool ContainerPresent { get; set; }
    public bool ContainerIsRunning { get; set; }
    public string? ImageVersionValue { get; set; } = "sha256:abc123 (created 2026-07-16)";
    public string? ImageDigestValue { get; set; } = "sha256:abc123";
    public string LogsValue { get; set; } = "";

    public List<string> LoadedTars { get; } = new();
    public List<string> Started { get; } = new();
    public List<string> Stopped { get; } = new();
    public List<string> Removed { get; } = new();
    public List<string> EnsuredVolumes { get; } = new();
    public List<string> RemovedVolumes { get; } = new();
    public bool RemoveVolumeResult { get; set; }
    public List<ContainerRunSpec> RunSpecs { get; } = new();

    // F5 VPN sidecar (T9).
    public bool SupportsVpn { get; set; }
    public string? VpnDigestValue { get; set; } = "sha256:vpn0000";
    public List<VpnSidecarRunSpec> VpnSpecs { get; } = new();
    public bool SupportsVpnSidecar => SupportsVpn;
    public void RunVpnSidecar(VpnSidecarRunSpec spec) => VpnSpecs.Add(spec);

    /// <summary>The digest the tag resolves to AFTER a load (null = unchanged); models a tag-moving load.</summary>
    public string? ImageDigestAfterLoadValue { get; set; }

    /// <summary>Images retired via the non-forced remove (housekeeping paths).</summary>
    public List<string> RemovedImages { get; } = new();
    public void RemoveImage(string imageRef) => RemovedImages.Add(imageRef);

    /// <summary>What still names each image (backlog 165); an image not listed carries no name.</summary>
    public Dictionary<string, IReadOnlyList<string>> ImageNamesValue { get; } = new(StringComparer.Ordinal);
    public IReadOnlyList<string> ImageNames(string imageRef) =>
        ImageNamesValue.TryGetValue(imageRef, out var names) ? names : [];

    /// <summary>
    /// What the probe reports (item 6). Null keeps the older boolean behaviour, so every test written before
    /// the distinction existed still reads "unavailable" as "not installed" and nothing gets started.
    /// </summary>
    public RuntimeAvailability? Availability { get; set; }

    /// <summary>What <see cref="TryStartMachine"/> answers; null means the interface default (not supported).</summary>
    public MachineStartResult? MachineStart { get; set; }

    /// <summary>What the probe reports AFTER a successful machine start (null = unchanged).</summary>
    public RuntimeAvailability? AvailabilityAfterMachineStart { get; set; }

    /// <summary>How many times a machine start was attempted; the launcher must attempt at most one.</summary>
    public int MachineStartAttempts { get; private set; }

    public RuntimeAvailability Probe() =>
        Availability ?? (Available ? RuntimeAvailability.Available : RuntimeAvailability.CliMissing);

    public MachineStartResult TryStartMachine()
    {
        MachineStartAttempts++;
        var result = MachineStart ?? MachineStartResult.NotSupported($"'{Name}' has no machine to start.");
        if (result.Started && AvailabilityAfterMachineStart is { } after)
        {
            Availability = after;
            Available = after == RuntimeAvailability.Available;
        }

        return result;
    }

    public bool IsAvailable() => Probe() == RuntimeAvailability.Available;
    public bool ImageExists(string image) => ModelsNames ? Names.ContainsKey(Key(image)) : HasImage;

    // ---- backlog 190: an engine's image NAMES --------------------------------------------------------------------
    // Off (the older model, where every name resolves to ImageDigestValue) until a test gives an image a name or says
    // what a load prints. On, a name resolves only to what it was given, a load moves only the names it prints (Moby:
    // localhost/fravashi:latest, never fravashi:latest), and a tag copies one name onto another.

    /// <summary>Image name to id, as the engine resolves it.</summary>
    public Dictionary<string, string> Names { get; } = new(StringComparer.Ordinal);

    /// <summary>The names a load prints and points at <see cref="LoadedImageId"/>.</summary>
    public List<string> LoadNames { get; } = new();

    /// <summary>The id of the image a load brings in.</summary>
    public string LoadedImageId { get; set; } = "sha256:loaded";

    /// <summary>Podman's naming: <c>localhost/X</c> and <c>X</c> are one reference.</summary>
    public bool PodmanNaming { get; set; }

    /// <summary>The release version label of each image id.</summary>
    public Dictionary<string, string> VersionLabels { get; } = new(StringComparer.Ordinal);

    /// <summary>The release variant label of each image id (backlog 251).</summary>
    public Dictionary<string, string> VariantLabels { get; } = new(StringComparer.Ordinal);

    /// <summary>Every tag the launcher asked for, as (source, target).</summary>
    public List<(string Source, string Target)> Tags { get; } = new();

    /// <summary>The id each created container's image name resolved to when it was created.</summary>
    public List<string?> CreatedFromImageIds { get; } = new();

    private bool ModelsNames => Names.Count > 0 || LoadNames.Count > 0;

    private string Key(string name) => PodmanNaming ? LoadedImageName.Unqualified(name) : name;

    /// <summary>True when the engine refuses every tag.</summary>
    public bool RefusesTags { get; set; }

    public bool TagImage(string source, string target)
    {
        if (!ModelsNames || RefusesTags)
        {
            return false;
        }

        // An image id is a tag source too (podman and Docker accept one), which is how a refused update puts the
        // install's name back on the previous image (backlog 256).
        if (!Names.TryGetValue(Key(source), out var id))
        {
            if (!source.StartsWith("sha256:", StringComparison.Ordinal))
            {
                return false;
            }

            id = source;
        }

        Tags.Add((source, target));
        Names[Key(target)] = id;
        return true;
    }

    /// <summary>The bytes of each file handed to <see cref="LoadImage"/>, read when it was handed over.</summary>
    public List<byte[]> LoadedBytes { get; } = new();

    public IReadOnlyList<string> LoadImage(string tarPath)
    {
        LoadedTars.Add(tarPath);
        LoadedBytes.Add(File.Exists(tarPath) ? File.ReadAllBytes(tarPath) : []);
        HasImage = true;
        if (ModelsNames)
        {
            foreach (var name in LoadNames)
            {
                Names[Key(name)] = LoadedImageId;
            }
        }
        else if (ImageDigestAfterLoadValue is not null)
        {
            ImageDigestValue = ImageDigestAfterLoadValue;
        }

        return [.. LoadNames];
    }
    /// <summary>Container names the caller asked about; proves a name override reaches the query.</summary>
    public List<string> RunningQueries { get; } = new();

    public bool ContainerExists(string containerName) => ContainerPresent;

    /// <summary>What the engine says about rootless mode (backlog 252); null models "cannot say".</summary>
    public bool? RootlessValue { get; set; }
    public bool? RunsRootless() => RootlessValue;

    public bool ContainerRunning(string containerName)
    {
        RunningQueries.Add(containerName);
        return ContainerIsRunning;
    }
    public void StartContainer(string containerName) => Started.Add(containerName);
    /// <summary>What the engine says when a stop exits non-zero (null = a clean stop), thrown the way the real
    /// runtime throws it.</summary>
    public string? StopComplaint { get; set; }

    /// <summary>True when the complaining stop really did leave the container up.</summary>
    public bool StopLeavesItRunning { get; set; }

    /// <summary>True when the engine stops answering during the stop: from then on the probe fails and the
    /// running check reads false, which is what the real runtimes do when their CLI cannot reach the engine.</summary>
    public bool StopTakesTheEngineDown { get; set; }

    public void StopContainer(string containerName)
    {
        Stopped.Add(containerName);
        ContainerIsRunning = StopLeavesItRunning;
        if (StopTakesTheEngineDown)
        {
            Available = false;
            ContainerIsRunning = false;
        }

        if (StopComplaint is not null)
        {
            throw new InvalidOperationException(StopComplaint);
        }
    }
    public void RemoveContainer(string containerName) { Removed.Add(containerName); ContainerPresent = false; ContainerIsRunning = false; }

    /// <summary>What <see cref="CanResolve"/> answers (backlog 218); true (the interface default) unless a test
    /// means to model a container whose resolver has actually broken.</summary>
    public bool CanResolveValue { get; set; } = true;

    /// <summary>Names asked about, so a test can assert the fixed hostname reached here unchanged.</summary>
    public List<string> ResolveQueries { get; } = new();

    public bool CanResolve(string containerName, string hostname)
    {
        ResolveQueries.Add(hostname);
        return CanResolveValue;
    }
    public void EnsureVolume(string volumeName) => EnsuredVolumes.Add(volumeName);

    /// <summary>Networks the launcher asked to exist (backlog 270).</summary>
    public List<string> EnsuredNetworks { get; } = new();
    public void EnsureNetwork(string networkName) => EnsuredNetworks.Add(networkName);
    public bool RemoveVolume(string volumeName) { RemovedVolumes.Add(volumeName); return RemoveVolumeResult; }
    public void RunContainer(ContainerRunSpec spec)
    {
        // The engine refuses a run on a network that does not exist (backlog 270).
        if (spec.Network is { Length: > 0 } network && !EnsuredNetworks.Contains(network))
        {
            throw new InvalidOperationException($"Error: unable to find network with name or ID {network}: network not found");
        }

        RunSpecs.Add(spec);
        CreatedFromImageIds.Add(ResolveImageDigest(spec.Image));
        ContainerPresent = true;
        ContainerIsRunning = true;
    }
    public string? ImageVersion(string image) => HasImage ? ImageVersionValue : null;

    /// <summary>The image-label release version <c>fravashi status</c> falls back to when launcher.json has none.</summary>
    public string? ImageReleaseVersionValue { get; set; }
    public string? ImageReleaseVersion(string image) =>
        !ModelsNames ? (HasImage ? ImageReleaseVersionValue : null)
        : ResolveImageDigest(image) is { } id && VersionLabels.TryGetValue(id, out var version) ? version : null;

    /// <summary>The image-label release variant (backlog 251); null models an image without the label.</summary>
    public string? ImageVariantValue { get; set; }
    public string? ImageVariant(string image) =>
        !ModelsNames ? (HasImage ? ImageVariantValue : null)
        : ResolveImageDigest(image) is { } id && VariantLabels.TryGetValue(id, out var variant) ? variant : null;

    public string? ResolveImageDigest(string image) =>
        image.Contains("gluetun", StringComparison.OrdinalIgnoreCase) ? VpnDigestValue
        : ModelsNames ? (Names.TryGetValue(Key(image), out var id) ? id : null)
        : HasImage ? ImageDigestValue : null;
    public string GetLogs(string containerName, int tail) => LogsValue;

    /// <summary>The image the existing container was created from (null = runtime cannot report it).</summary>
    public string? ContainerImageIdValue { get; set; }
    public string? ContainerImageId(string containerName) => ContainerPresent ? ContainerImageIdValue : null;
}

/// <summary>Records tray-control calls so the verb dispatch can be asserted without a real tray process.</summary>
internal sealed class FakeTrayControl : ITrayControl
{
    public bool Running { get; set; }
    public int SignalExitCalls { get; private set; }

    public bool IsRunning() => Running;
    public bool SignalExit() { SignalExitCalls++; return Running; }
}
