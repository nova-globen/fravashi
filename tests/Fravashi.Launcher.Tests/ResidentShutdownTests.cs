using AwesomeAssertions;
using Xunit;

namespace Fravashi.Launcher.Tests;

public class ResidentShutdownTests
{
    private readonly List<string> _log = [];
    private int _forceExits;
    private int _loopEnds;

    private ResidentShutdown Build(TimeSpan? watchdog = null, TimeSpan? bridgeTimeout = null) => new(
        line => { lock (_log) { _log.Add(line); } },
        watchdog ?? TimeSpan.FromSeconds(30),
        bridgeTimeout ?? TimeSpan.FromSeconds(5),
        () => ["0x1 \"fravashi main\"", "0x2 \".NET TP Worker\""],
        () => Interlocked.Increment(ref _forceExits));

    private string Log()
    {
        lock (_log)
        {
            return string.Join('\n', _log);
        }
    }

    [Fact]
    public async Task Ends_the_message_loop_once_windows_are_closed_and_the_bridge_is_stopped()
    {
        var shutdown = Build();
        var bridgeToken = CancellationToken.None;

        await shutdown.RunAsync(
            Task.CompletedTask,
            async token => { bridgeToken = token; await Task.Delay(20, CancellationToken.None); },
            () => Interlocked.Increment(ref _loopEnds));

        _loopEnds.Should().Be(1);
        _forceExits.Should().Be(0);
        bridgeToken.CanBeCanceled.Should().BeTrue("the bridge stop is handed the bounded token");
        Log().Should().Contain("host bridge stopped in").And.Contain("ending the message loop")
            .And.NotContain("watchdog");
        shutdown.Pending.Should().Equal("message loop ending");
    }

    [Fact]
    public async Task Waits_for_the_windows_before_ending_the_loop()
    {
        var shutdown = Build();
        var windows = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = shutdown.RunAsync(windows.Task, null, () => Interlocked.Increment(ref _loopEnds));
        await Task.Delay(100);
        _loopEnds.Should().Be(0);
        shutdown.Pending.Should().Equal("windows closing", "message loop ending");

        windows.SetResult();
        await run;

        _loopEnds.Should().Be(1);
    }

    [Fact]
    public async Task A_bridge_stop_that_ignores_its_token_is_abandoned_after_the_timeout_and_the_exit_goes_on()
    {
        var shutdown = Build(bridgeTimeout: TimeSpan.FromMilliseconds(100));

        await shutdown.RunAsync(
            Task.CompletedTask,
            _ => new TaskCompletionSource().Task,
            () => Interlocked.Increment(ref _loopEnds));

        _loopEnds.Should().Be(1);
        Log().Should().Contain("WARNING fravashi exit: the host bridge did not stop within");
    }

    [Fact]
    public async Task A_failing_bridge_stop_is_logged_and_the_exit_goes_on()
    {
        var shutdown = Build();

        await shutdown.RunAsync(
            Task.CompletedTask,
            _ => throw new InvalidOperationException("listener already gone"),
            () => Interlocked.Increment(ref _loopEnds));

        _loopEnds.Should().Be(1);
        Log().Should().Contain("WARNING fravashi exit: the host bridge stop failed: listener already gone");
    }

    [Fact]
    public async Task The_watchdog_logs_an_anomaly_with_the_pending_steps_and_the_live_threads_then_forces_exit()
    {
        var shutdown = Build(watchdog: TimeSpan.FromMilliseconds(200));
        var neverClosing = new TaskCompletionSource().Task;

        _ = shutdown.RunAsync(neverClosing, null, () => Interlocked.Increment(ref _loopEnds));
        await WaitForForceExitAsync();

        _loopEnds.Should().Be(0);
        var line = _log.Should().ContainSingle(l => l.Contains("watchdog fired")).Subject;
        line.Should().StartWith("WARNING fravashi exit: watchdog fired after 0.2s; this is an anomaly")
            .And.Contain("Still pending: windows closing, message loop ending.")
            .And.Contain("Live threads: 0x1 \"fravashi main\"; 0x2 \".NET TP Worker\".")
            .And.EndWith("Forcing process exit.");
    }

    [Fact]
    public async Task A_watchdog_firing_after_the_sequence_completed_blames_a_thread_rather_than_a_step()
    {
        var shutdown = Build(watchdog: TimeSpan.FromMilliseconds(200));

        await shutdown.RunAsync(Task.CompletedTask, null, () => Interlocked.Increment(ref _loopEnds));
        shutdown.MessageLoopEnded();
        await WaitForForceExitAsync();

        shutdown.Pending.Should().BeEmpty();
        Log().Should().Contain("message loop ended; the process ends when the main thread returns")
            .And.Contain("Still pending: nothing (the sequence completed, so a foreground thread is holding the process)");
    }

    [Fact]
    public async Task A_second_run_is_ignored_and_logged()
    {
        var shutdown = Build();

        await shutdown.RunAsync(Task.CompletedTask, null, () => Interlocked.Increment(ref _loopEnds));
        await shutdown.RunAsync(Task.CompletedTask, null, () => Interlocked.Increment(ref _loopEnds));

        _loopEnds.Should().Be(1);
        Log().Should().Contain("already in progress");
    }

    private async Task WaitForForceExitAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref _forceExits) == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        _forceExits.Should().Be(1);
    }
}
