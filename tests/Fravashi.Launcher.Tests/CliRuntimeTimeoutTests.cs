using System.Diagnostics;
using Fravashi.Launcher;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

/// <summary>
/// The load-bearing half of backlog item 6: the runtime probe was itself unbounded (ReadToEnd, ReadToEnd,
/// WaitForExit with no deadline), so a wedged engine hung the splash forever before any start attempt
/// existed. These drive REAL child processes through the shared CLI runner, because the failure being
/// prevented is a process that never returns and no fake can stand in for that.
/// </summary>
public sealed class CliRuntimeTimeoutTests
{
    /// <summary>A command that runs far longer than any deadline used here, without needing a shell.</summary>
    private static (string Cli, string[] Args) LongRunningCommand() =>
        OperatingSystem.IsWindows() ? ("ping", ["-n", "30", "127.0.0.1"]) : ("sleep", ["30"]);

    /// <summary>A command that exits zero immediately.</summary>
    private static (string Cli, string[] Args) SucceedingCommand() =>
        OperatingSystem.IsWindows() ? ("ping", ["-n", "1", "127.0.0.1"]) : ("sleep", ["0"]);

    /// <summary>A command that exits non-zero immediately (the engine answered, badly).</summary>
    private static (string Cli, string[] Args) FailingCommand() =>
        OperatingSystem.IsWindows() ? ("ping", ["--fravashi-not-a-flag"]) : ("sleep", ["--fravashi-not-a-flag"]);

    [Fact]
    public void ACallThatNeverReturns_IsCancelledOnItsDeadline_RatherThanHangingTheCaller()
    {
        var (cli, args) = LongRunningCommand();
        var harness = new CliHarness(cli);
        var stopwatch = Stopwatch.StartNew();

        var (exitCode, stderr, timedOut) = harness.RunFor(TimeSpan.FromMilliseconds(400), args);
        stopwatch.Stop();

        timedOut.Should().BeTrue();
        exitCode.Should().NotBe(0);
        stderr.Should().Contain("did not answer");
        // The whole point: the caller is back long before the child would have finished (30 seconds).
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void ACallWithinItsDeadline_ReportsTheRealExitCode_AndIsNotMarkedTimedOut()
    {
        var (cli, args) = SucceedingCommand();

        var (exitCode, _, timedOut) = new CliHarness(cli).RunFor(TimeSpan.FromSeconds(30), args);

        timedOut.Should().BeFalse();
        exitCode.Should().Be(0);
    }

    [Fact]
    public void AnUnboundedCall_StillWaitsForACommandThatFinishes()
    {
        // Loading an image tarball is legitimately slow, so it runs with no deadline; that path must still work.
        var (cli, args) = SucceedingCommand();

        var (exitCode, _, timedOut) = new CliHarness(cli).RunFor(timeout: null, args);

        timedOut.Should().BeFalse();
        exitCode.Should().Be(0);
    }

    [Fact]
    public void Probe_ReportsCliMissing_WhenTheExecutableIsNotOnPath()
    {
        new CliHarness("fravashi-no-such-runtime-cli")
            .ProbeWith("version")
            .Should().Be(RuntimeAvailability.CliMissing);
    }

    [Fact]
    public void Probe_ReportsEngineNotResponding_WhenTheCliIsThereButTheCallFails()
    {
        // The stopped-machine case: podman is installed and answers instantly with a non-zero exit. That is
        // NOT "no runtime installed", and telling those apart is what lets the launcher offer to start it.
        var (cli, args) = FailingCommand();

        new CliHarness(cli).ProbeWith(args).Should().Be(RuntimeAvailability.EngineNotResponding);
    }

    [Fact]
    public void Probe_ReportsAvailable_WhenTheCallSucceeds()
    {
        var (cli, args) = SucceedingCommand();

        new CliHarness(cli).ProbeWith(args).Should().Be(RuntimeAvailability.Available);
    }

    /// <summary>
    /// Exposes the shared CLI runner over an arbitrary executable. It rides on the Docker-compatible runtime
    /// only to inherit the process plumbing; none of the container verbs are used here.
    /// </summary>
    private sealed class CliHarness(string cli) : DockerCompatibleRuntime("harness", cli)
    {
        public (int ExitCode, string StdErr, bool TimedOut) RunFor(TimeSpan? timeout, params string[] args)
        {
            var outcome = RunBounded(timeout, args);
            return (outcome.ExitCode, outcome.StdErr, outcome.TimedOut);
        }

        public RuntimeAvailability ProbeWith(params string[] args) => ProbeAvailability(args);
    }
}
