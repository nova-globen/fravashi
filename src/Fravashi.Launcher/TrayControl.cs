using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Fravashi.Launcher;

/// <summary>
/// The launcher's cross-process handle on the resident fravashi.exe. Named OS primitives, not IPC: a mutex tells us
/// whether a resident is already running (single-instance), and a manual-reset event lets <c>fravashi exit</c> ask a
/// running one to close. Non-Windows builds get a no-op: the resident is Windows-only today.
/// </summary>
public interface ITrayControl
{
    /// <summary>True if a resident process currently holds the single-instance mutex (under either name, see
    /// <see cref="TrayControlNames.FormerSingleInstanceMutex"/>).</summary>
    bool IsRunning();

    /// <summary>
    /// Signals a running resident to exit. Returns true if one was present and signalled, false if none was running
    /// (nothing to close).
    /// </summary>
    bool SignalExit();
}

/// <summary>
/// Names of the OS primitives coupling `fravashi` invocations to the resident fravashi.exe GUI instance. Per-session
/// (<c>Local\</c>).
/// </summary>
public static class TrayControlNames
{
    public const string SingleInstanceMutex = @"Local\Fravashi.Tray.SingleInstance";
    public const string ExitEvent = @"Local\Fravashi.Tray.Exit";

    /// <summary>
    /// The single-instance mutex of an install from before the internal rename (the bridge release). The new resident
    /// holds it as well, so an old copy started beside it finds "already running" and leaves, and an old copy already
    /// running is asked to close (<see cref="FormerExitEvent"/>) before the new one starts. Removed in the cleanup release.
    /// </summary>
    public const string FormerSingleInstanceMutex = FormerInstall.SingleInstanceMutex;

    /// <summary>The exit event of an install from before the internal rename (the bridge release).</summary>
    public const string FormerExitEvent = FormerInstall.ExitEvent;

    /// <summary>Second GUI launch signals this; the resident fravashi.exe focuses/reopens its window.</summary>
    public const string ActivateEvent = @"Local\Fravashi.Desktop.Activate";

    /// <summary>
    /// File beside launcher.json a second `fravashi --open PATH` writes before signalling
    /// <see cref="ActivateEvent"/>; the resident instance reads-and-deletes it and opens that
    /// folder's window (plan 2b-ii).
    /// </summary>
    public const string OpenRequestFile = "open-request.txt";
}

/// <summary>A tray control that does nothing - used on non-Windows and injected in tests.</summary>
public sealed class NoopTrayControl : ITrayControl
{
    public bool IsRunning() => false;
    public bool SignalExit() => false;
}

/// <summary>
/// The Windows control. Uses <c>Local\</c>-scoped named primitives, so a running resident is discoverable across the two
/// processes without a socket or pipe. All operations are best-effort: a missing primitive or an access error means "no
/// resident", never a crash. Both the current names and the former ones (an old copy, the bridge release) are asked.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTrayControl : ITrayControl
{
    public bool IsRunning() =>
        MutexExists(TrayControlNames.SingleInstanceMutex) || MutexExists(TrayControlNames.FormerSingleInstanceMutex);

    public bool SignalExit()
    {
        var signalled = Signal(TrayControlNames.ExitEvent);
        return Signal(TrayControlNames.FormerExitEvent) || signalled;
    }

    /// <summary>Whether a named mutex exists (someone holds or created it).</summary>
    public static bool MutexExists(string name)
    {
        try
        {
            if (Mutex.TryOpenExisting(name, out var mutex))
            {
                mutex.Dispose();
                return true;
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return false;
    }

    /// <summary>Sets a named event if it exists; true when it did.</summary>
    public static bool Signal(string name)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(name, out var handle))
            {
                handle.Set();
                handle.Dispose();
                return true;
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return false;
    }
}

/// <summary>Factory that returns the platform-appropriate <see cref="ITrayControl"/>.</summary>
public static class TrayControlFactory
{
    public static ITrayControl Create() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new WindowsTrayControl()
            : new NoopTrayControl();
}
