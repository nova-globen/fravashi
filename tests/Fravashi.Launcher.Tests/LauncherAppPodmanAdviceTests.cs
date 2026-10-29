using AwesomeAssertions;
using Fravashi.Launcher;
using Xunit;

namespace Fravashi.Launcher.Tests;

/// <summary>
/// Backlog 252, the launcher's half: a studio that never answers from Windows on a ROOTFUL Podman machine is said as
/// the switch to rootless, and <c>fravashi status</c> relays what the studio found when it asked fravashi.exe's bridge.
/// </summary>
public sealed class LauncherAppPodmanAdviceTests : IDisposable
{
    private readonly string _directory;
    private readonly LauncherConfigStore _configStore;

    public LauncherAppPodmanAdviceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "fravashi-launcher-252", Guid.NewGuid().ToString("N"));
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

    private LauncherApp Build(FakeContainerRuntime runtime, List<string>? lines = null) => new(
        runtime,
        _configStore,
        launcherDirectory: _directory,
        log: line => lines?.Add(line),
        openBrowser: _ => { },
        isPortFree: port => port == LauncherDefaults.PreferredPort,
        defaultDataDir: () => Path.Combine(_directory, "data"));

    /// <summary>A started product: launcher.json written and the container running under <paramref name="engine"/>.</summary>
    private FakeContainerRuntime Started(string engine, bool? rootless)
    {
        var runtime = new FakeContainerRuntime { Name = engine, RootlessValue = rootless };
        Build(runtime).Run().Should().Be(ExitCodes.Success);
        runtime.ContainerIsRunning.Should().BeTrue();
        return runtime;
    }

    [Fact]
    public void A_running_container_on_a_rootful_podman_machine_is_told_to_switch_to_rootless()
    {
        var runtime = Started(RuntimeNames.Podman, rootless: false);

        var advice = Build(runtime).StudioUnansweredAdvice();

        advice.Should().Be(PodmanWords.StudioUnanswered(_configStore.Load()!.Port, rootless: false));
        advice.Should().Contain("podman machine set --rootful=false");
    }

    [Fact]
    public void A_podman_engine_that_cannot_say_gets_the_how_to_check_words() =>
        Build(Started(RuntimeNames.Podman, rootless: null)).StudioUnansweredAdvice()
            .Should().Contain("could not say");

    [Fact]
    public void A_rootless_machine_gets_nothing_added() =>
        Build(Started(RuntimeNames.Podman, rootless: true)).StudioUnansweredAdvice().Should().BeNull();

    [Theory]
    [InlineData(RuntimeNames.Docker)]
    [InlineData(RuntimeNames.Wslc)]
    public void Another_engine_gets_no_podman_advice(string engine) =>
        Build(Started(engine, rootless: false)).StudioUnansweredAdvice().Should().BeNull();

    [Fact]
    public void A_stopped_container_gets_no_rootful_advice()
    {
        var runtime = Started(RuntimeNames.Podman, rootless: false);
        runtime.ContainerIsRunning = false;

        Build(runtime).StudioUnansweredAdvice().Should().BeNull();
    }

    [Fact]
    public void Status_verb_relays_the_studios_bridge_words_while_the_container_runs()
    {
        var runtime = Started(RuntimeNames.Podman, rootless: true);
        var dataDir = _configStore.Load()!.DataDir;
        File.WriteAllText(Path.Combine(dataDir, BridgeReachFile.FileName), "Windows Firewall is probably blocking fravashi.exe.");
        var lines = new List<string>();

        Build(runtime, lines).Status();

        lines.Should().Contain("  Bridge    : Windows Firewall is probably blocking fravashi.exe.");
    }

    [Fact]
    public void Status_verb_says_nothing_about_the_bridge_when_the_studio_left_no_words()
    {
        var runtime = Started(RuntimeNames.Podman, rootless: true);
        var lines = new List<string>();

        Build(runtime, lines).Status();

        lines.Should().NotContain(line => line.Contains("Bridge"));
    }

    [Fact]
    public void Status_verb_says_nothing_from_a_stopped_studios_stale_words()
    {
        var runtime = Started(RuntimeNames.Podman, rootless: true);
        File.WriteAllText(Path.Combine(_configStore.Load()!.DataDir, BridgeReachFile.FileName), "stale words");
        runtime.ContainerIsRunning = false;
        var lines = new List<string>();

        Build(runtime, lines).Status();

        lines.Should().NotContain(line => line.Contains("stale words"));
    }
}
