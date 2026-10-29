using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

/// <summary>
/// Backlog 270: rootless Podman starts a container in pasta network mode, and Podman refuses to join a pasta
/// container to another network, so the app runner could never attach the studio to a run's edge network and no
/// scenario step reached the application. On rootless Podman (and only there) the studio runs on a user-defined
/// network of its own, which can be joined. Measured on the VM: such a container joins a second network, resolves a
/// container there by name, and still reaches fravashi.exe's bridge and the internet.
/// </summary>
public sealed class StudioNetworkTests : IDisposable
{
    private readonly string _directory;
    private readonly LauncherConfigStore _configStore;

    public StudioNetworkTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "fravashi-launcher-tests", Guid.NewGuid().ToString("N"));
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

    private static readonly ContainerRunSpec Spec = new("fravashi", "fravashi:latest", 9090, 8080, "/host/data");

    // ---- who gets the network ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(RuntimeNames.Podman, true, false, ContainerHardening.StudioNetwork)]
    [InlineData(RuntimeNames.Podman, false, false, null)]
    [InlineData(RuntimeNames.Podman, null, false, null)]
    [InlineData(RuntimeNames.Podman, true, true, null)]
    [InlineData(RuntimeNames.Docker, true, false, null)]
    [InlineData(RuntimeNames.Docker, null, false, null)]
    [InlineData(RuntimeNames.Wslc, null, false, null)]
    public void Only_rootless_podman_without_a_vpn_sidecar_puts_the_studio_on_its_own_network(
        string runtime, bool? rootless, bool vpnOwnsTheNetwork, string? expected)
    {
        ContainerHardening.StudioNetworkFor(runtime, rootless, vpnOwnsTheNetwork).Should().Be(expected);
    }

    [Fact]
    public void A_spec_with_a_network_runs_on_it_and_still_publishes_on_loopback_only()
    {
        var args = new PodmanRuntime().BuildRunArgs(Spec with { Network = ContainerHardening.StudioNetwork });

        args.Should().ContainInConsecutiveOrder("--network", ContainerHardening.StudioNetwork);
        args.Should().ContainInConsecutiveOrder("--publish", "127.0.0.1:9090:8080");
    }

    [Fact]
    public void Without_a_network_the_run_arguments_are_unchanged_on_every_runtime()
    {
        foreach (var args in new[]
                 {
                     new DockerRuntime().BuildRunArgs(Spec),
                     new PodmanRuntime().BuildRunArgs(Spec),
                     new WslcRuntime().BuildRunArgs(Spec),
                 })
        {
            args.Should().NotContain("--network");
        }
    }

    [Fact]
    public void The_vpn_netns_wins_over_a_network_so_the_kill_switch_is_never_bypassed()
    {
        var args = new PodmanRuntime().BuildRunArgs(Spec with { NetworkContainer = "fravashi-vpn", Network = ContainerHardening.StudioNetwork });

        args.Should().ContainInConsecutiveOrder("--network", "container:fravashi-vpn");
        args.Should().NotContain(ContainerHardening.StudioNetwork);
    }

    // ---- what the launcher creates -------------------------------------------------------------------------------

    private LauncherApp Build(FakeContainerRuntime runtime) => new(
        runtime,
        _configStore,
        launcherDirectory: _directory,
        log: _ => { },
        openBrowser: _ => { },
        isPortFree: _ => true,
        defaultDataDir: () => Path.Combine(_directory, "data"));

    private void SaveConfig(VpnConfig? vpn = null) => _configStore.Save(new LauncherConfig
    {
        Port = LauncherDefaults.PreferredPort,
        DataDir = Path.Combine(_directory, "data"),
        Vpn = vpn,
    });

    [Fact]
    public void On_rootless_podman_a_new_studio_is_created_on_its_network_which_is_made_first_and_recorded()
    {
        var podman = new FakeContainerRuntime { Name = RuntimeNames.Podman, RootlessValue = true };
        SaveConfig();

        Build(podman).Run().Should().Be(ExitCodes.Success);

        podman.EnsuredNetworks.Should().Equal(ContainerHardening.StudioNetwork);
        podman.RunSpecs.Should().ContainSingle().Which.Network.Should().Be(ContainerHardening.StudioNetwork);
        _configStore.Load()!.StudioNetworkBaked.Should().Be(ContainerHardening.StudioNetwork);
    }

    [Theory]
    [InlineData(RuntimeNames.Docker, null)]
    [InlineData(RuntimeNames.Podman, false)]
    [InlineData(RuntimeNames.Wslc, null)]
    public void Elsewhere_no_network_is_made_and_the_studio_keeps_the_engine_default(string runtime, bool? rootless)
    {
        var engine = new FakeContainerRuntime { Name = runtime, RootlessValue = rootless };
        SaveConfig();

        Build(engine).Run().Should().Be(ExitCodes.Success);

        engine.EnsuredNetworks.Should().BeEmpty();
        engine.RunSpecs.Should().ContainSingle().Which.Network.Should().BeNull();
        (_configStore.Load()!.StudioNetworkBaked ?? "").Should().BeEmpty();
    }

    [Fact]
    public void An_existing_pasta_container_on_rootless_podman_is_recreated_on_the_network()
    {
        // What every rootless install has today: a container created before this, which recorded no network.
        var podman = new FakeContainerRuntime
        {
            Name = RuntimeNames.Podman, RootlessValue = true, ContainerPresent = true, ContainerIsRunning = true,
        };
        SaveConfig();

        Build(podman).Run().Should().Be(ExitCodes.Success);

        podman.Removed.Should().ContainSingle("a container's network is fixed when it is created");
        podman.RunSpecs.Should().ContainSingle().Which.Network.Should().Be(ContainerHardening.StudioNetwork);
    }

    [Fact]
    public void Once_recreated_the_studio_is_left_alone_on_the_next_start()
    {
        var first = new FakeContainerRuntime { Name = RuntimeNames.Podman, RootlessValue = true };
        SaveConfig();
        Build(first).Run().Should().Be(ExitCodes.Success);

        var second = new FakeContainerRuntime
        {
            Name = RuntimeNames.Podman, RootlessValue = true, ContainerPresent = true, ContainerIsRunning = true,
        };
        Build(second).Run().Should().Be(ExitCodes.Success);

        second.Removed.Should().BeEmpty();
    }

    [Fact]
    public void A_podman_that_cannot_say_once_does_not_move_the_studio_back_off_its_network()
    {
        var first = new FakeContainerRuntime { Name = RuntimeNames.Podman, RootlessValue = true };
        SaveConfig();
        Build(first).Run().Should().Be(ExitCodes.Success);

        var unanswered = new FakeContainerRuntime
        {
            Name = RuntimeNames.Podman, RootlessValue = null, ContainerPresent = true, ContainerIsRunning = true,
        };
        Build(unanswered).Run().Should().Be(ExitCodes.Success);

        unanswered.Removed.Should().BeEmpty();
        _configStore.Load()!.StudioNetworkBaked.Should().Be(ContainerHardening.StudioNetwork);
    }

    [Fact]
    public void An_existing_docker_container_that_recorded_no_network_is_not_recreated()
    {
        var docker = new FakeContainerRuntime { Name = RuntimeNames.Docker, ContainerPresent = true, ContainerIsRunning = true };
        SaveConfig();

        Build(docker).Run().Should().Be(ExitCodes.Success);

        docker.Removed.Should().BeEmpty();
    }

    [Fact]
    public void The_baked_settings_carry_the_network_so_the_comparison_sees_it()
    {
        var store = _configStore;
        var config = new LauncherConfig { Port = LauncherDefaults.PreferredPort, DataDir = Path.Combine(_directory, "data") };

        new StudioContainer(new FakeContainerRuntime { Name = RuntimeNames.Podman, RootlessValue = true }, store)
            .Baked(config).StudioNetwork.Should().Be(ContainerHardening.StudioNetwork);
        new StudioContainer(new FakeContainerRuntime { Name = RuntimeNames.Podman, RootlessValue = true }, store)
            .Baked(config with { Vpn = new VpnConfig { Enabled = true, ContainerName = "fravashi-vpn" } })
            .StudioNetwork.Should().BeEmpty("the sidecar owns the studio's network");
        new StudioContainer(new FakeContainerRuntime { Name = RuntimeNames.Docker }, store)
            .Baked(config).StudioNetwork.Should().BeEmpty();
    }
}
