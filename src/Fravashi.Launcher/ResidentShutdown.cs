using System.Diagnostics;
using System.Globalization;

namespace Fravashi.Launcher;

/// <summary>
/// The resident fravashi.exe's exit sequence (backlog 5): close every window, stop the host bridge, then
/// end the message loop, with a watchdog behind it that is a BACKSTOP and never the normal path.
/// <para>
/// Why this exists: the process used to end the message loop with the windows closed and then stop
/// the bridge's Kestrel host SYNCHRONOUSLY from the application context's <c>Dispose</c>, which
/// WinForms runs on the UI thread inside the loop's own teardown. Measured with a stack dump of the
/// live process 1.2 s and 3.3 s after <c>fravashi exit</c>: the ONLY non-runtime thread alive was the
/// main (UI) thread, sitting in <c>Dispose</c> on <c>StopAsync(...).GetResult()</c>, still there
/// after its 2 s token had expired, so the five-second watchdog was what ended the process on every
/// recorded exit since 2026-08-05. Nothing outlived <c>Main</c>; <c>Main</c> never returned.
/// </para>
/// <para>
/// So the bridge stop now runs on the thread pool as a step of the sequence, bounded by
/// <see cref="ResidentShutdown"/>'s own timeout (the stop's cancellation token is honored by the
/// host, but a stop that ignores it is abandoned all the same), and the loop ends once both the
/// windows are closed and that step is over. The watchdog stays armed for the whole exit, the loop
/// teardown and the main thread's return included: when it fires it logs a WARNING naming the
/// anomaly, the steps still pending, and every live thread by name (see <see cref="LiveThreads"/>),
/// so the next such report answers "which thread" in one look. The product names the threads it
/// owns (the main thread is "fravashi main"); the runtime names its own.
/// </para>
/// </summary>
public sealed class ResidentShutdown
{
    public static readonly TimeSpan DefaultWatchdogDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// A stop with no long-lived streams completes in milliseconds; the SSE watch streams end on
    /// <c>ApplicationStopping</c>. Three seconds is generous and still well inside the watchdog.
    /// </summary>
    public static readonly TimeSpan DefaultBridgeStopTimeout = TimeSpan.FromSeconds(3);

    private const string WindowsStep = "windows closing";
    private const string BridgeStep = "host bridge stopping";
    private const string LoopStep = "message loop ending";

    private readonly Action<string> _log;
    private readonly TimeSpan _watchdogDelay;
    private readonly TimeSpan _bridgeStopTimeout;
    private readonly Func<IReadOnlyList<string>> _liveThreads;
    private readonly Action _forceExit;
    private readonly object _sync = new();
    private readonly List<string> _pending = [WindowsStep, BridgeStep, LoopStep];
    private bool _started;

    /// <param name="log">The launcher log line sink.</param>
    /// <param name="watchdogDelay">How long after <see cref="RunAsync"/> the watchdog fires.</param>
    /// <param name="bridgeStopTimeout">How long the bridge stop may take before it is abandoned.</param>
    /// <param name="liveThreads">Names the threads alive right now (the watchdog's report).</param>
    /// <param name="forceExit">What the watchdog does after logging: ends the process.</param>
    public ResidentShutdown(
        Action<string> log,
        TimeSpan watchdogDelay,
        TimeSpan bridgeStopTimeout,
        Func<IReadOnlyList<string>> liveThreads,
        Action forceExit)
    {
        _log = log;
        _watchdogDelay = watchdogDelay;
        _bridgeStopTimeout = bridgeStopTimeout;
        _liveThreads = liveThreads;
        _forceExit = forceExit;
    }

    /// <summary>Which steps have not completed yet, in order; empty once the loop has ended.</summary>
    public IReadOnlyList<string> Pending
    {
        get
        {
            lock (_sync)
            {
                return _pending.ToArray();
            }
        }
    }

    /// <summary>
    /// Runs the sequence once. <paramref name="windowsClosed"/> completes when the host has closed
    /// every window (already complete when none was open); <paramref name="stopBridge"/> stops the
    /// bridge host and is null when none was started; <paramref name="endMessageLoop"/> is invoked
    /// exactly once, from a thread-pool thread, when both are over (the host marshals it to the UI
    /// thread itself). A second call is ignored and logged: the sequence is already under way.
    /// </summary>
    public Task RunAsync(Task windowsClosed, Func<CancellationToken, Task>? stopBridge, Action endMessageLoop)
    {
        lock (_sync)
        {
            if (_started)
            {
                _log("fravashi exit: already in progress; ignoring the repeated request.");
                return Task.CompletedTask;
            }

            _started = true;
        }

        ArmWatchdog();
        return RunCoreAsync(windowsClosed, stopBridge, endMessageLoop);
    }

    /// <summary>
    /// The host calls this when the message loop has ended (the application context is disposed). A
    /// watchdog that fires after it names no pending step and blames a thread, which is the only
    /// thing that can hold the process once the main thread is on its way out of <c>Main</c>.
    /// </summary>
    public void MessageLoopEnded()
    {
        Complete(LoopStep);
        _log("fravashi exit: message loop ended; the process ends when the main thread returns.");
    }

    private async Task RunCoreAsync(Task windowsClosed, Func<CancellationToken, Task>? stopBridge, Action endMessageLoop)
    {
        var bridge = StopBridgeAsync(stopBridge);
        await windowsClosed.ConfigureAwait(false);
        Complete(WindowsStep);
        await bridge.ConfigureAwait(false);
        _log("fravashi exit: windows closed and host bridge stopped; ending the message loop.");
        endMessageLoop();
    }

    private async Task StopBridgeAsync(Func<CancellationToken, Task>? stopBridge)
    {
        if (stopBridge is null)
        {
            Complete(BridgeStep);
            return;
        }

        var clock = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(_bridgeStopTimeout);
        try
        {
            // On the pool, never on the caller's thread: the UI thread's synchronization context must
            // not be what the host's continuations are queued to while that thread is busy elsewhere.
            await Task.Run(() => stopBridge(timeout.Token)).WaitAsync(timeout.Token).ConfigureAwait(false);
            _log($"fravashi exit: host bridge stopped in {clock.ElapsedMilliseconds} ms.");
        }
        catch (OperationCanceledException)
        {
            _log(
                $"WARNING fravashi exit: the host bridge did not stop within {Seconds(_bridgeStopTimeout)}s; "
                + "continuing without it (process exit tears the listener down).");
        }
        catch (Exception ex)
        {
            _log($"WARNING fravashi exit: the host bridge stop failed: {ex.Message}. Continuing.");
        }
        finally
        {
            Complete(BridgeStep);
        }
    }

    /// <summary>Log lines read the same on every machine: the decimal point is never the user's culture's.</summary>
    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);

    private void Complete(string step)
    {
        lock (_sync)
        {
            _pending.Remove(step);
        }
    }

    private void ArmWatchdog() =>
        _ = Task.Delay(_watchdogDelay).ContinueWith(_ => FireWatchdog(), TaskScheduler.Default);

    private void FireWatchdog()
    {
        string pending;
        lock (_sync)
        {
            pending = _pending.Count == 0
                ? "nothing (the sequence completed, so a foreground thread is holding the process)"
                : string.Join(", ", _pending);
        }

        string threads;
        try
        {
            threads = string.Join("; ", _liveThreads());
        }
        catch (Exception ex)
        {
            threads = $"unavailable ({ex.GetType().Name}: {ex.Message})";
        }

        _log(
            $"WARNING fravashi exit: watchdog fired after {Seconds(_watchdogDelay)}s; this is an anomaly, "
            + "the exit sequence is expected to end the process by itself. "
            + $"Still pending: {pending}. Live threads: {threads}. Forcing process exit.");
        _forceExit();
    }
}
