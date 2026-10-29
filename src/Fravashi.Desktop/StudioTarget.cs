using Fravashi.Launcher;

namespace Fravashi.Desktop;

/// <summary>
/// Where a tray action wants the studio window to land. The tray used to hand a URL over, which is
/// exactly how it ended up pointing a window at a dead container (backlog 15): a URL looks valid
/// whether or not anything is listening on it. Naming the DESTINATION instead lets the one open path
/// start the product first and build the URL from the port it actually came up on.
/// </summary>
internal enum StudioTarget
{
    /// <summary>The studio itself: focus a window already showing it, else open one on the welcome page.</summary>
    Studio,

    /// <summary>The onboarding / settings re-entry URL; always navigated to, since it names a page.</summary>
    Onboarding,
}

/// <summary>
/// How a studio window recovers from a failed navigation (backlog 15). Implemented by
/// <see cref="DesktopApplicationContext"/>, which owns the launcher: the window never drives the
/// container itself, it only asks what to say and asks for the product to be running.
/// </summary>
internal interface IStudioRecovery
{
    /// <summary>
    /// The message for the current product state. Shells out to the container runtime, so the caller
    /// must ask off the UI thread.
    /// </summary>
    StudioUnavailableMessage Describe();

    /// <summary>
    /// Makes the studio answer: a no-op when it already does, otherwise the full launcher start
    /// sequence with the splash phases and the healthz wait. False means it could not be started.
    /// </summary>
    Task<bool> EnsureRunningAsync();
}
