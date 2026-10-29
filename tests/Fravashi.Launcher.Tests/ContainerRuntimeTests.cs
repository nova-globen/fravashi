using Fravashi.Launcher;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

/// <summary>
/// Covers the runtime-agnostic layer (ADR 0009): the detector's selection rules and the per-runtime
/// <c>run</c> argument construction - in particular that Docker/Podman emit <c>--restart unless-stopped</c>
/// while wslc, which has no such flag, omits it but is otherwise identical.
/// </summary>
public sealed class ContainerRuntimeTests
{
    private static readonly ContainerRunSpec Spec =
        new("fravashi", "fravashi:latest", 9090, 8080, @"C:\data");

    // ---- detector ----

    [Fact]
    public void Detect_PicksFirstAvailable_InProbeOrder()
    {
        var docker = new NamedRuntime(RuntimeNames.Docker, available: false);
        var podman = new NamedRuntime(RuntimeNames.Podman, available: true);
        var wslc = new NamedRuntime(RuntimeNames.Wslc, available: true);

        ContainerRuntimeDetector.Detect(config: null, [docker, podman, wslc])
            .Name.Should().Be(RuntimeNames.Podman);
    }

    [Fact]
    public void Detect_WslcOnly_PicksWslc()
    {
        var docker = new NamedRuntime(RuntimeNames.Docker, available: false);
        var podman = new NamedRuntime(RuntimeNames.Podman, available: false);
        var wslc = new NamedRuntime(RuntimeNames.Wslc, available: true);

        ContainerRuntimeDetector.Detect(config: null, [docker, podman, wslc])
            .Name.Should().Be(RuntimeNames.Wslc);
    }

    [Fact]
    public void Detect_ExplicitRuntime_Wins_EvenOverAnAvailableEarlierCandidate()
    {
        var docker = new NamedRuntime(RuntimeNames.Docker, available: true);
        var wslc = new NamedRuntime(RuntimeNames.Wslc, available: true);
        var config = new LauncherConfig { Runtime = RuntimeNames.Wslc };

        ContainerRuntimeDetector.Detect(config, [docker, wslc])
            .Name.Should().Be(RuntimeNames.Wslc);
    }

    [Fact]
    public void Detect_ExplicitRuntime_HonouredWhenInstalledButNotAnswering()
    {
        // A pinned engine that is installed but not answering is still returned, so the ensuing "not available"
        // message names the engine the user asked for rather than silently switching.
        var docker = new NamedRuntime(RuntimeNames.Docker, available: true);
        var wslc = new NamedRuntime(RuntimeNames.Wslc, RuntimeAvailability.EngineNotResponding);
        var config = new LauncherConfig { Runtime = RuntimeNames.Wslc };

        ContainerRuntimeDetector.Detect(config, [docker, wslc])
            .Name.Should().Be(RuntimeNames.Wslc);
    }

    [Fact]
    public void Detect_APinToAnEngineThatIsNotInstalled_FallsBackToAutoDetect()
    {
        // 2026-10-04: podman was removed from the dev host, launcher.json still pinned it, and wslc was never tried.
        var docker = new NamedRuntime(RuntimeNames.Docker, RuntimeAvailability.CliMissing);
        var podman = new NamedRuntime(RuntimeNames.Podman, RuntimeAvailability.CliMissing);
        var wslc = new NamedRuntime(RuntimeNames.Wslc, available: true);
        var config = new LauncherConfig { Runtime = RuntimeNames.Podman };

        ContainerRuntimeDetector.Detect(config, [docker, podman, wslc])
            .Name.Should().Be(RuntimeNames.Wslc);
    }

    [Fact]
    public void Detect_UnknownExplicitRuntime_FallsBackToAutoDetect()
    {
        var docker = new NamedRuntime(RuntimeNames.Docker, available: true);
        var wslc = new NamedRuntime(RuntimeNames.Wslc, available: true);
        var config = new LauncherConfig { Runtime = "nonesuch" };

        ContainerRuntimeDetector.Detect(config, [docker, wslc])
            .Name.Should().Be(RuntimeNames.Docker);
    }

    [Fact]
    public void Detect_NoneAvailable_ReturnsFirstCandidate_ForTheUnavailableMessagePath()
    {
        var docker = new NamedRuntime(RuntimeNames.Docker, available: false);
        var wslc = new NamedRuntime(RuntimeNames.Wslc, available: false);

        ContainerRuntimeDetector.Detect(config: null, [docker, wslc])
            .Name.Should().Be(RuntimeNames.Docker);
    }

    // ---- per-runtime run args ----

    [Fact]
    public void DockerRunArgs_IncludeABoundedRestartPolicy_AndTheStandardMounts()
    {
        var args = new DockerRuntime().BuildRunArgs(Spec);

        // Bounded on purpose (backlog 122): unless-stopped has no ceiling and a studio that crashed on
        // startup was restarted 1501 times in 35 minutes on the owner's laptop.
        args.Should().ContainInConsecutiveOrder("--restart", "on-failure:5");
        args.Should().NotContain("unless-stopped");
        args.Should().ContainInConsecutiveOrder("--name", "fravashi");
        args.Should().ContainInConsecutiveOrder("--publish", "127.0.0.1:9090:8080");
        args.Should().ContainInConsecutiveOrder("--volume", @"C:\data:/data");
        args.Should().ContainInConsecutiveOrder("--env", "FRAVASHI_DATA_DIR=/data");
        args.Should().EndWith("fravashi:latest");
    }

    [Fact]
    public void PodmanRunArgs_CarryTheRootlessKeepIdUserns_ForTheDataBindMount()
    {
        // The rootless-Podman /data ownership fix: keep-id maps the container's uid/gid 1000 to the host user
        // that owns the bind mount, so the hardened non-root app can write the SQLite DB (otherwise it opens
        // read-only and the studio crash-loops with "attempt to write a readonly database").
        var args = new PodmanRuntime().BuildRunArgs(Spec);

        args.Should().ContainInConsecutiveOrder("--userns", "keep-id:uid=1000,gid=1000");
        // The keep-id remap sits with the other create flags, before the /data bind mount it protects.
        args.ToList().IndexOf("--userns").Should().BeLessThan(args.ToList().IndexOf(@"C:\data:/data"));
    }

    [Fact]
    public void PodmanRunArgs_AreOtherwiseIdenticalToDocker_AsideFromTheKeepIdUserns()
    {
        var podman = new PodmanRuntime().BuildRunArgs(Spec).ToList();
        podman.Remove("--userns");
        podman.Remove("keep-id:uid=1000,gid=1000");

        podman.Should().Equal(new DockerRuntime().BuildRunArgs(Spec));
    }

    [Fact]
    public void DockerAndWslcRunArgs_DoNotCarryTheKeepIdUserns_ItIsPodmanOnly()
    {
        new DockerRuntime().BuildRunArgs(Spec).Should().NotContain("--userns");
        new WslcRuntime().BuildRunArgs(Spec).Should().NotContain("--userns");
    }

    [Fact]
    public void WslcRunArgs_OmitRestart_ButKeepEveryOtherArg()
    {
        var args = new WslcRuntime().BuildRunArgs(Spec);

        args.Should().NotContain("--restart");
        args.Should().ContainInConsecutiveOrder("--name", "fravashi");
        args.Should().ContainInConsecutiveOrder("--publish", "127.0.0.1:9090:8080");
        args.Should().ContainInConsecutiveOrder("--volume", @"C:\data:/data");
        args.Should().ContainInConsecutiveOrder("--env", "FRAVASHI_DATA_DIR=/data");
        args.Should().EndWith("fravashi:latest");
    }

    [Fact]
    public void WslcRunArgs_OmitTheDockerOnlyHardeningFlags_ButKeepTheSharedOnes()
    {
        var args = new WslcRuntime().BuildRunArgs(Spec);

        // Rejected by wslc (ADR 0009 matrix) - never emitted.
        args.Should().NotContain("--cap-drop");
        args.Should().NotContain("--security-opt");
        args.Should().NotContain("--read-only");
        args.Should().NotContain("--pids-limit");
        args.Should().NotContain("--restart");

        // Shared, accepted by wslc.
        args.Should().Contain("--user");
        args.Should().Contain("--memory");
        args.Should().Contain("--tmpfs");
        args.Should().ContainInConsecutiveOrder("--ulimit", "nproc=4096:4096");
        args.Should().ContainInConsecutiveOrder("--publish", "127.0.0.1:9090:8080");
    }

    // ---- the retired repo volume (plan 2c): no runtime may mount it or set its env ----

    [Fact]
    public void RunArgs_NeverMountTheRetiredRepoVolume()
    {
        foreach (var args in new[]
        {
            new DockerRuntime().BuildRunArgs(Spec),
            new PodmanRuntime().BuildRunArgs(Spec),
            new WslcRuntime().BuildRunArgs(Spec),
        })
        {
            args.Should().NotContain(a => a.Contains("/repos"));
            args.Should().NotContain(a => a.StartsWith("FRAVASHI_REPO_ROOT", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void WslcRunArgs_OmitTheDockerOnlyHardening()
    {
        var args = new WslcRuntime().BuildRunArgs(Spec);

        args.Should().NotContain("--read-only");
        args.Should().NotContain("--restart");
        args.Should().EndWith("fravashi:latest");
    }

    [Fact]
    public void VolumeRemoveArgs_AreTargetedNeverAPrune()
    {
        var runtime = new DockerRuntime();

        runtime.BuildVolumeRemoveArgs("fravashi-repo").Should().Equal("volume", "rm", "fravashi-repo");
    }

    // ---- the agent-workspaces volume (plan 2d, decision 2) ----

    private static readonly ContainerRunSpec SpecWithWorkspaces = Spec with
    {
        WorkspacesVolume = "fravashi-workspaces",
        WorkspacesMountPath = "/workspaces",
    };

    [Fact]
    public void DockerRunArgs_WithWorkspacesVolume_MountIt_WithoutTheChownSuffix()
    {
        var args = new DockerRuntime().BuildRunArgs(SpecWithWorkspaces);

        args.Should().ContainInConsecutiveOrder("--volume", "fravashi-workspaces:/workspaces");
        args.Should().ContainInConsecutiveOrder("--env", "FRAVASHI_WORKSPACES_ROOT=/workspaces");
        args.Should().NotContain("fravashi-workspaces:/workspaces:U");
    }

    [Fact]
    public void PodmanRunArgs_WithWorkspacesVolume_ChownItToTheContainerUser()
    {
        // Rootless Podman creates the named volume root-owned; without :U the hardened uid-1000
        // app cannot write the synced copies (the exact failure the retired /repos volume had).
        var args = new PodmanRuntime().BuildRunArgs(SpecWithWorkspaces);

        args.Should().ContainInConsecutiveOrder("--volume", "fravashi-workspaces:/workspaces:U");
        args.Should().NotContain("fravashi-workspaces:/workspaces");
        args.Should().ContainInConsecutiveOrder("--env", "FRAVASHI_WORKSPACES_ROOT=/workspaces");
    }

    [Fact]
    public void WslcRunArgs_WithWorkspacesVolume_MountIt_Plain()
    {
        var args = new WslcRuntime().BuildRunArgs(SpecWithWorkspaces);

        args.Should().ContainInConsecutiveOrder("--volume", "fravashi-workspaces:/workspaces");
        args.Should().ContainInConsecutiveOrder("--env", "FRAVASHI_WORKSPACES_ROOT=/workspaces");
        args.Should().NotContain("--restart");
    }

    // ---- F3 integrity env (T8) ----

    [Fact]
    public void RunArgs_WithIntegrityFields_EmitTheIntegrityEnv_OnBothRuntimes()
    {
        var spec = Spec with { IntegrityKey = "KEYB64==", ImageDigest = "sha256:abc123" };

        foreach (var args in new[] { new DockerRuntime().BuildRunArgs(spec), new WslcRuntime().BuildRunArgs(spec) })
        {
            args.Should().ContainInConsecutiveOrder("--env", "FRAVASHI_INTEGRITY_KEY=KEYB64==");
            args.Should().ContainInConsecutiveOrder("--env", "FRAVASHI_IMAGE_DIGEST=sha256:abc123");
            args.Should().EndWith("fravashi:latest");
        }
    }

    [Fact]
    public void RunArgs_WithoutIntegrityFields_OmitTheIntegrityEnv()
    {
        var args = new DockerRuntime().BuildRunArgs(Spec);

        args.Should().NotContain(a => a.StartsWith("FRAVASHI_INTEGRITY_KEY", StringComparison.Ordinal));
        args.Should().NotContain(a => a.StartsWith("FRAVASHI_IMAGE_DIGEST", StringComparison.Ordinal));
    }

    // ---- broker endpoint env (ADR 0010, corp-broker tier - D2 gap fix) ----

    [Fact]
    public void RunArgs_WithBrokerBaseUrl_EmitBrokerEnv_OnAllRuntimes()
    {
        var spec = Spec with { BrokerBaseUrl = "http://broker.example:8080" };

        foreach (var args in new[]
                 {
                     new DockerRuntime().BuildRunArgs(spec),
                     new PodmanRuntime().BuildRunArgs(spec),
                     new WslcRuntime().BuildRunArgs(spec),
                 })
        {
            args.Should().ContainInConsecutiveOrder("--env", "Broker__BaseUrl=http://broker.example:8080");
            args.Should().ContainInConsecutiveOrder("--env", "Broker__Enabled=true");
            args.Should().EndWith("fravashi:latest");
        }
    }

    [Fact]
    public void RunArgs_WithBrokerUserTokenSecretRef_EmitTheRefEnv()
    {
        var spec = Spec with
        {
            BrokerBaseUrl = "http://broker.example:8080",
            BrokerUserTokenSecretRef = "broker:user-token",
        };

        new DockerRuntime().BuildRunArgs(spec)
            .Should().ContainInConsecutiveOrder("--env", "Broker__UserTokenSecretRef=broker:user-token");
    }

    [Fact]
    public void RunArgs_WithoutBroker_OmitBrokerEnv_AndAreByteForByteTheNonBrokeredShape()
    {
        // The single-user default (no broker configured) must be unchanged: no Broker__ env at all, and the whole
        // arg list identical to the pre-broker shape.
        var args = new DockerRuntime().BuildRunArgs(Spec);

        args.Should().NotContain(a => a.StartsWith("Broker__", StringComparison.Ordinal));

        // The absence of a broker URL yields exactly the same args as a spec that could never carry one.
        args.Should().Equal(new DockerRuntime().BuildRunArgs(Spec with { BrokerBaseUrl = null }));
    }

    [Fact]
    public void RunArgs_WithBrokerRefButNoBaseUrl_EmitNothing()
    {
        // A ref without a base URL is inert - nothing to point the CLI at, so no Broker__ env is emitted.
        var spec = Spec with { BrokerUserTokenSecretRef = "broker:user-token" };

        new DockerRuntime().BuildRunArgs(spec)
            .Should().NotContain(a => a.StartsWith("Broker__", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("docker")]
    [InlineData("wslc")]
    public void VolumeArgBuilders_AreTheSharedInspectAndCreateShape(string runtimeName)
    {
        CliContainerRuntime runtime = runtimeName == "wslc" ? new WslcRuntime() : new DockerRuntime();

        runtime.BuildVolumeInspectArgs("fravashi-repo").Should().Equal("volume", "inspect", "fravashi-repo");
        runtime.BuildVolumeCreateArgs("fravashi-repo").Should().Equal("volume", "create", "fravashi-repo");
    }

    [Fact]
    public void BuildResolveArgs_IsAnExecOfGetentHosts_ForTheFixedHostname()
    {
        // Backlog 218: shared across docker/podman/wslc (CliContainerRuntime.CanResolve), never built from
        // configuration text, so this is the ONE pure shape every runtime's CanResolve runs.
        CliContainerRuntime.BuildResolveArgs("fravashi-studio", "api.anthropic.com")
            .Should().Equal("exec", "fravashi-studio", "getent", "hosts", "api.anthropic.com");
    }

    // ---- backlog 251: the image's variant label, read as its version label is ----

    [Fact]
    public void The_docker_family_reads_labels_from_one_json_inspect_of_the_image()
    {
        DockerCompatibleRuntime.BuildImageLabelsArgs("fravashi:latest")
            .Should().Equal("image", "inspect", "fravashi:latest", "--format", "{{json .Config.Labels}}");
    }

    [Fact]
    public void The_docker_family_parses_the_variant_label_and_reads_an_absent_one_as_not_known()
    {
        const string corp = """{"org.fravashi.version":"0.9.64","org.fravashi.variant":"corp-broker"}""";
        DockerCompatibleRuntime.ReadLabel(corp + "\n", ImageLabels.Variant).Should().Be("corp-broker");
        DockerCompatibleRuntime.ReadLabel("""{"org.fravashi.version":"0.9.40"}""", ImageLabels.Variant)
            .Should().BeNull("an older image carries no variant label");
        DockerCompatibleRuntime.ReadLabel("""{"org.fravashi.variant":""}""", ImageLabels.Variant).Should().BeNull();
        DockerCompatibleRuntime.ReadLabel("null", ImageLabels.Variant).Should().BeNull("an image with no labels at all");
        DockerCompatibleRuntime.ReadLabel("Error: no such image", ImageLabels.Variant).Should().BeNull();
    }

    [Fact]
    public void Image_variant_asks_for_the_variant_label()
    {
        var runtime = new LabelRecordingRuntime();

        runtime.ImageVariant("fravashi:latest").Should().Be("corp-broker");
        runtime.ImageReleaseVersion("fravashi:latest").Should().Be("0.9.64");
        runtime.Asked.Should().Equal(("fravashi:latest", ImageLabels.Variant), ("fravashi:latest", ImageLabels.Version));
    }

    /// <summary>Answers labels from a fixed set and records which were asked, with no process started.</summary>
    private sealed class LabelRecordingRuntime() : DockerCompatibleRuntime(RuntimeNames.Podman, "podman")
    {
        public List<(string Image, string Label)> Asked { get; } = new();

        public override string? ImageLabel(string image, string label)
        {
            Asked.Add((image, label));
            return label switch
            {
                ImageLabels.Variant => "corp-broker",
                ImageLabels.Version => "0.9.64",
                _ => null,
            };
        }
    }

    [Fact]
    public void RuntimeNames_AreProductNeutral()
    {
        new IContainerRuntime[] { new DockerRuntime(), new PodmanRuntime(), new WslcRuntime() }
            .Select(r => r.Name)
            .Should().BeEquivalentTo([RuntimeNames.Docker, RuntimeNames.Podman, RuntimeNames.Wslc]);
    }

    /// <summary>A runtime whose name and availability are fixed, for exercising the detector.</summary>
    private sealed class NamedRuntime(string name, RuntimeAvailability state) : IContainerRuntime
    {
        public NamedRuntime(string name, bool available)
            : this(name, available ? RuntimeAvailability.Available : RuntimeAvailability.CliMissing)
        {
        }

        public string Name => name;
        public bool IsAvailable() => state == RuntimeAvailability.Available;
        public RuntimeAvailability Probe() => state;
        public bool ImageExists(string image) => false;
        public IReadOnlyList<string> LoadImage(string tarPath) => [];
        public bool ContainerExists(string containerName) => false;
        public bool ContainerRunning(string containerName) => false;
        public void StartContainer(string containerName) { }
        public void StopContainer(string containerName) { }
        public void RemoveContainer(string containerName) { }
        public void EnsureVolume(string volumeName) { }
        public bool RemoveVolume(string volumeName) => false;
        public void RunContainer(ContainerRunSpec spec) { }
        public string? ImageVersion(string image) => null;
        public string? ResolveImageDigest(string image) => null;
        public string GetLogs(string containerName, int tail) => "";
        public string? ImageReleaseVersion(string image) => null;
    }
}
