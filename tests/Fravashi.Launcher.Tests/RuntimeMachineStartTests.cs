using Fravashi.Launcher;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

/// <summary>
/// Backlog item 6: nothing started a stopped container-runtime machine, so every reboot ended at "install a
/// container runtime" for a runtime that was plainly installed. Covers the three halves of the fix: the probe
/// tells a missing CLI from a silent engine, the detector prefers the engine that is actually installed, and
/// the launcher makes exactly one bounded start attempt and reports honestly when there is nothing to start.
/// </summary>
public sealed class RuntimeMachineStartTests : IDisposable
{
    private readonly string _directory;
    private readonly LauncherConfigStore _configStore;

    public RuntimeMachineStartTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "fravashi-machine-start-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _configStore = new LauncherConfigStore(Path.Combine(_directory, "launcher.json"));
    }

    private LauncherApp Build(FakeContainerRuntime runtime, List<string> log, List<LaunchPhase>? phases = null)
        => new(
            runtime,
            _configStore,
            launcherDirectory: _directory,
            log: log.Add,
            openBrowser: _ => { },
            isPortFree: port => port == LauncherDefaults.PreferredPort,
            defaultDataDir: () => Path.Combine(_directory, "data"),
            phase: phases is null ? _ => { } : phases.Add);

    // ---- the launcher's start attempt ----

    [Fact]
    public void Run_WhenTheEngineIsSilentButStartable_StartsTheMachineAndLaunches()
    {
        // The owner's post-reboot case: podman is installed, its machine is down. Before this, the launch
        // stopped at RuntimeUnavailable and told the user to install a runtime they already had.
        var runtime = new FakeContainerRuntime
        {
            Name = RuntimeNames.Podman,
            Availability = RuntimeAvailability.EngineNotResponding,
            MachineStart = new MachineStartResult(MachineStartStatus.Started, "Started the Podman machine 'x'."),
            AvailabilityAfterMachineStart = RuntimeAvailability.Available,
        };
        var log = new List<string>();
        var phases = new List<LaunchPhase>();

        Build(runtime, log, phases).Run().Should().Be(ExitCodes.Success);

        runtime.MachineStartAttempts.Should().Be(1);
        phases.Should().ContainInOrder(LaunchPhase.CheckingRuntime, LaunchPhase.StartingRuntime, LaunchPhase.PreparingImage);
        runtime.RunSpecs.Should().ContainSingle();
    }

    [Fact]
    public void Run_WhenTheCliIsMissing_DoesNotEvenTryToStartAMachine()
    {
        var runtime = new FakeContainerRuntime { Availability = RuntimeAvailability.CliMissing };
        var log = new List<string>();
        var phases = new List<LaunchPhase>();

        Build(runtime, log, phases).Run().Should().Be(ExitCodes.RuntimeUnavailable);

        runtime.MachineStartAttempts.Should().Be(0);
        phases.Should().NotContain(LaunchPhase.StartingRuntime);
        log.Should().Contain(l => l.Contains("No container runtime was found", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_WhenThereIsNoMachineToStart_RefusesWithTheRuntimesOwnReason()
    {
        // "A machine that cannot be started says why" (item 6 acceptance). The reason is the runtime's, not
        // a generic "install a runtime", and nothing is invented on the user's behalf.
        var runtime = new FakeContainerRuntime
        {
            Name = RuntimeNames.Docker,
            Availability = RuntimeAvailability.EngineNotResponding,
            MachineStart = MachineStartResult.NotSupported("Docker offers no CLI command to start it."),
        };
        var log = new List<string>();

        Build(runtime, log).Run().Should().Be(ExitCodes.RuntimeUnavailable);

        runtime.MachineStartAttempts.Should().Be(1);
        log.Should().Contain("Docker offers no CLI command to start it.");
        log.Should().NotContain(l => l.Contains("No container runtime was found", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_WhenTheMachineStartsButTheEngineStaysSilent_RefusesInsteadOfLoopingBehindTheSplash()
    {
        var runtime = new FakeContainerRuntime
        {
            Name = RuntimeNames.Podman,
            Availability = RuntimeAvailability.EngineNotResponding,
            MachineStart = new MachineStartResult(MachineStartStatus.Started, "Started the Podman machine 'x'."),
            AvailabilityAfterMachineStart = RuntimeAvailability.EngineNotResponding,
        };
        var log = new List<string>();

        Build(runtime, log).Run().Should().Be(ExitCodes.RuntimeUnavailable);

        runtime.MachineStartAttempts.Should().Be(1);
        log.Should().Contain(l => l.Contains("still does not answer", StringComparison.Ordinal));
    }

    [Fact]
    public void Stop_WhenTheEngineIsSilent_SaysItIsInstalled_RatherThanTellingTheUserToInstallIt()
    {
        // `fravashi stop` starts nothing, but it must not misdiagnose either.
        var runtime = new FakeContainerRuntime
        {
            Name = RuntimeNames.Podman,
            Availability = RuntimeAvailability.EngineNotResponding,
        };
        var log = new List<string>();

        Build(runtime, log).Stop().Should().Be(ExitCodes.RuntimeUnavailable);

        runtime.MachineStartAttempts.Should().Be(0);
        log.Should().Contain(l => l.Contains("is installed but its engine is not answering", StringComparison.Ordinal));
    }

    // ---- the detector ----

    [Fact]
    public void Detect_PrefersTheInstalledButSilentEngine_OverTheFirstCandidate()
    {
        // The fresh-install case: no launcher.json, docker not installed, podman installed with its machine
        // down. Falling through to candidate one told the user to install Docker.
        var docker = new ProbedRuntime(RuntimeNames.Docker, RuntimeAvailability.CliMissing);
        var podman = new ProbedRuntime(RuntimeNames.Podman, RuntimeAvailability.EngineNotResponding);
        var wslc = new ProbedRuntime(RuntimeNames.Wslc, RuntimeAvailability.CliMissing);

        ContainerRuntimeDetector.Detect(config: null, [docker, podman, wslc])
            .Name.Should().Be(RuntimeNames.Podman);
    }

    [Fact]
    public void Detect_StillPrefersAnAnsweringEngine_OverASilentOne()
    {
        var docker = new ProbedRuntime(RuntimeNames.Docker, RuntimeAvailability.EngineNotResponding);
        var podman = new ProbedRuntime(RuntimeNames.Podman, RuntimeAvailability.Available);

        ContainerRuntimeDetector.Detect(config: null, [docker, podman])
            .Name.Should().Be(RuntimeNames.Podman);
    }

    [Fact]
    public void Detect_ProbesEachCandidateExactlyOnce()
    {
        // Every probe starts a process against a possibly wedged engine; probing twice doubles the wait.
        var docker = new ProbedRuntime(RuntimeNames.Docker, RuntimeAvailability.CliMissing);
        var podman = new ProbedRuntime(RuntimeNames.Podman, RuntimeAvailability.CliMissing);

        ContainerRuntimeDetector.Detect(config: null, [docker, podman]);

        docker.Probes.Should().Be(1);
        podman.Probes.Should().Be(1);
    }

    // ---- podman machine list parsing (the decision made from real CLI output) ----

    [Fact]
    public void ParseMachines_ReadsTheRealPodmanShape()
    {
        // Verbatim from `podman machine list --format json` on Podman 5.8.5, trimmed to the read fields.
        const string Json = """
        [
            { "Name": "podman-machine-default", "Default": true, "Running": false, "Starting": false,
              "VMType": "wsl", "CPUs": 2 }
        ]
        """;

        var machines = PodmanMachineList.Parse(Json);

        machines.Should().ContainSingle();
        machines[0].Name.Should().Be("podman-machine-default");
        machines[0].IsDefault.Should().BeTrue();
        machines[0].Running.Should().BeFalse();
        PodmanMachineList.StartTarget(machines).Should().BeSameAs(machines[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("Error: cannot connect to the machine")]
    [InlineData("{\"Name\":\"not-an-array\"}")]
    public void ParseMachines_YieldsNothingForAnythingUnexpected(string output)
    {
        // No machines means "say so", never "guess and run machine init" (which downloads gigabytes).
        PodmanMachineList.Parse(output).Should().BeEmpty();
    }

    [Fact]
    public void StartTarget_RefusesToGuess_WhenSeveralMachinesAndNoneIsDefault()
    {
        var machines = new[]
        {
            new PodmanMachine("one", IsDefault: false, Running: false, Starting: false),
            new PodmanMachine("two", IsDefault: false, Running: false, Starting: false),
        };

        PodmanMachineList.StartTarget(machines).Should().BeNull();
    }

    [Fact]
    public void StartTarget_TakesTheOnlyMachine_EvenWhenItIsNotMarkedDefault()
    {
        var machines = new[] { new PodmanMachine("only", IsDefault: false, Running: false, Starting: false) };

        PodmanMachineList.StartTarget(machines)!.Name.Should().Be("only");
    }

    // ---- the real runtimes' honest answers ----

    [Fact]
    public void DockerAndWslc_ReportThatTheyHaveNoMachineToStart()
    {
        // Neither is startable: Docker exposes no such CLI, wslc starts its distro on demand. Saying so is
        // the requirement; guessing at a service name or an executable path is not.
        foreach (IContainerRuntime runtime in new IContainerRuntime[] { new DockerRuntime(), new WslcRuntime() })
        {
            var result = runtime.TryStartMachine();
            result.Status.Should().Be(MachineStartStatus.NotSupported);
            result.Started.Should().BeFalse();
            result.Message.Should().NotBeNullOrWhiteSpace();
        }
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

    /// <summary>A runtime whose probe result is fixed and counted, for exercising the detector.</summary>
    private sealed class ProbedRuntime(string name, RuntimeAvailability availability) : IContainerRuntime
    {
        public int Probes { get; private set; }

        public string Name => name;
        public RuntimeAvailability Probe()
        {
            Probes++;
            return availability;
        }

        public bool IsAvailable() => Probe() == RuntimeAvailability.Available;
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
