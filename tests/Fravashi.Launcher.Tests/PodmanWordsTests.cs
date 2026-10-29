using AwesomeAssertions;
using Fravashi.Launcher;
using Xunit;

namespace Fravashi.Launcher.Tests;

/// <summary>
/// Backlog 252: the two Podman machine settings that stop Fravashi on a stock install, recognised from the engine's
/// own words (recorded on the test VM, 2026-10-08) and said as the measured fix.
/// </summary>
public sealed class PodmanWordsTests : IDisposable
{
    /// <summary>What crun printed for a plain <c>podman run</c> under WSL 3.0.1's cgroup isolation (#252).</summary>
    private const string RecordedPids =
        "Error: crun: controller `pids` is not available under /sys/fs/cgroup/non-systemd/machine.slice/"
        + "libpod-0d1f4c1e2b.scope/container/cgroup.controllers: OCI runtime error";

    /// <summary>What crun printed for the same run with <c>--memory</c> (#252).</summary>
    private const string RecordedMemoryMax =
        "Error: crun: open `memory.max` for writing: No such file or directory: OCI runtime attempted to invoke a "
        + "command that was not found";

    // Under the test's own output, not temp: the studio container mounts /tmp noexec, so a script there cannot run.
    private readonly string _directory =
        Path.Combine(AppContext.BaseDirectory, "podman-stand-in", Guid.NewGuid().ToString("N"));

    public PodmanWordsTests() => Directory.CreateDirectory(_directory);

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

    [Theory]
    [InlineData(RecordedPids)]
    [InlineData(RecordedMemoryMax)]
    public void A_recorded_cgroup_failure_under_podman_is_said_as_the_wslconfig_fix(string engineSaid)
    {
        var said = PodmanWords.CgroupIsolation("podman", engineSaid);

        said.Should().NotBeNull();
        said.Should().Contain("IsolateDistroCgroup=false").And.Contain(".wslconfig")
            .And.Contain("wsl --shutdown").And.Contain("podman machine start");
    }

    [Theory]
    [InlineData("Error: crun: open `memory.max.effective` is fine, the container ran out of memory (memory.max reached)")]
    [InlineData("Error: the container name \"fravashi\" is already in use by 0d1f4c1e2b")]
    [InlineData("Error: crun: executable file `/app/Fravashi.Web` not found in $PATH: No such file or directory")]
    public void An_engine_text_that_is_not_the_cgroup_failure_gets_no_advice(string engineSaid) =>
        PodmanWords.CgroupIsolation("podman", engineSaid).Should().BeNull();

    [Fact]
    public void The_same_text_under_another_engine_gets_no_podman_advice() =>
        PodmanWords.CgroupIsolation("docker", RecordedPids).Should().BeNull();

    [Theory]
    [InlineData("podman.exe")]
    [InlineData(@"C:\Program Files\RedHat\Podman\podman.exe")]
    [InlineData("/usr/bin/podman")]
    public void Podman_is_recognised_however_its_cli_is_named(string cli) =>
        PodmanWords.CgroupIsolation(cli, RecordedPids).Should().NotBeNull();

    [Fact]
    public void EngineFailures_explains_a_failed_podman_run_with_the_cgroup_advice() =>
        EngineFailures.Explain("podman", "fravashi", $"podman run ... failed (exit 127): {RecordedPids}")
            .Should().NotBeNull().And.Contain("IsolateDistroCgroup=false");

    [Fact]
    public void A_rootful_machine_is_said_with_the_switch_to_rootless_in_its_measured_order()
    {
        var said = PodmanWords.StudioUnanswered(9090, rootless: false);

        said.Should().NotBeNull();
        said.Should().Contain("ROOTFUL").And.Contain("http://127.0.0.1:9090");
        var steps = new[]
        {
            "podman machine stop", "podman machine set --rootful=false", "podman machine start", "podman load -i",
            "wsl --shutdown",
        };
        var positions = steps.Select(step => said!.IndexOf(step, StringComparison.Ordinal)).ToList();
        positions.Should().NotContain(-1).And.BeInAscendingOrder();
    }

    [Fact]
    public void A_rootless_machine_gets_no_rootful_advice() =>
        PodmanWords.StudioUnanswered(9090, rootless: true).Should().BeNull();

    [Fact]
    public void An_engine_that_cannot_say_is_said_as_that_with_how_to_check()
    {
        var said = PodmanWords.StudioUnanswered(9092, rootless: null);

        said.Should().NotBeNull();
        said.Should().Contain("could not say").And.Contain("podman machine inspect")
            .And.Contain("podman machine set --rootful=false").And.Contain("http://127.0.0.1:9092");
    }

    [Theory]
    [InlineData("true\n", true)]
    [InlineData("false", false)]
    [InlineData("", null)]
    [InlineData("Error: cannot connect to Podman", null)]
    public void Podman_infos_rootless_answer_is_read_as_true_false_or_cannot_say(string stdout, bool? expected) =>
        PodmanRuntime.ReadRootless(stdout).Should().Be(expected);

    [Fact]
    public void The_rootless_question_carries_no_quotes_into_the_machine() =>
        string.Concat(PodmanRuntime.BuildRootlessArgs()).Should().NotContain("\"");

    [Fact]
    public void ExplanationIn_finds_an_explained_failure_anywhere_in_the_chain()
    {
        var explained = new ExplainedEngineFailure("the advice", new InvalidOperationException("engine words"));

        EngineFailures.ExplanationIn(explained).Should().Be("the advice");
        EngineFailures.ExplanationIn(new InvalidOperationException("outer", explained)).Should().Be("the advice");
        EngineFailures.ExplanationIn(new AggregateException(explained)).Should().Be("the advice");
        EngineFailures.ExplanationIn(new InvalidOperationException("podman run failed")).Should().BeNull();
    }

    [PosixFact]
    public void A_podman_run_that_fails_with_the_recorded_text_throws_the_advice_with_the_engine_words_inside()
    {
        var podman = new PodmanStandIn(FakePodman(RecordedPids));

        var thrown = FluentActions.Invoking(() => podman.RunContainer(new ContainerRunSpec("fravashi", "fravashi:latest", 9090, 8080, _directory)))
            .Should().Throw<ExplainedEngineFailure>().Which;

        thrown.Message.Should().Contain("IsolateDistroCgroup=false");
        thrown.InnerException!.Message.Should().Contain("controller `pids` is not available");
    }

    [PosixFact]
    public void Starting_an_existing_container_that_fails_with_the_recorded_text_throws_the_advice_too()
    {
        var podman = new PodmanStandIn(FakePodman(RecordedMemoryMax));

        FluentActions.Invoking(() => podman.StartContainer("fravashi"))
            .Should().Throw<ExplainedEngineFailure>().Which.Message.Should().Contain("IsolateDistroCgroup=false");
    }

    [PosixFact]
    public void A_podman_start_failing_for_another_reason_keeps_the_engine_words_unexplained()
    {
        var podman = new PodmanStandIn(FakePodman("Error: no container with name or ID \"fravashi\" found"));

        var thrown = FluentActions.Invoking(() => podman.StartContainer("fravashi"))
            .Should().Throw<InvalidOperationException>().Which;

        thrown.Should().NotBeOfType<ExplainedEngineFailure>();
        thrown.Message.Should().Contain("no container with name");
    }

    /// <summary>A script NAMED podman that fails the way the engine did, so the real process plumbing runs.</summary>
    private string FakePodman(string stderr)
    {
        var path = Path.Combine(_directory, "podman");
        File.WriteAllText(path, $"#!/bin/sh\ncat >&2 <<'EOF'\n{stderr}\nEOF\nexit 127\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    private sealed class PodmanStandIn(string cli) : DockerCompatibleRuntime(RuntimeNames.Podman, cli);

    private sealed class PosixFactAttribute : FactAttribute
    {
        public PosixFactAttribute()
        {
            if (OperatingSystem.IsWindows())
            {
                Skip = "Drives a shell script standing in for podman; the Linux runs (CI, the container) cover it.";
            }
        }
    }
}
