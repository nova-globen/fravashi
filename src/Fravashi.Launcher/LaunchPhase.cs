using Fravashi.Domain;

namespace Fravashi.Launcher;

/// <summary>
/// Machine-readable launch phases for a GUI host's progress display (the fravashi.exe splash).
/// <see cref="LauncherApp"/> raises the first seven as it works; <see cref="WaitingForStudio"/> and
/// <see cref="Ready"/> are raised by the host itself around its readiness probe, so the whole
/// launch shares one vocabulary. The free-text log lines are unchanged; this is an additive channel.
/// </summary>
public enum LaunchPhase
{
    /// <summary>
    /// Raised by the host, before anything starts, only when it signs the person in on the studio's behalf
    /// (backlog 94): the browser is open and the launch waits for them. Usually skipped, because the studio keeps
    /// its own session and needs no help.
    /// </summary>
    SigningIn,

    CheckingRuntime,

    /// <summary>
    /// Raised only when the runtime is installed but its engine did not answer and the launcher is starting
    /// its machine (item 6). It is the slowest step of a post-reboot launch, so the splash must name it
    /// rather than sitting on "Checking the container runtime" for a minute.
    /// </summary>
    StartingRuntime,
    PreparingImage,
    StartingVpn,
    VerifyingIntegrity,
    PreparingContainer,
    CheckingUpdates,
    WaitingForStudio,
    Ready,
}

/// <summary>User-facing one-liners for each phase (shown on the splash).</summary>
public static class LaunchPhaseText
{
    public static string Describe(this LaunchPhase phase) => phase switch
    {
        LaunchPhase.SigningIn => "Signing you in: finish in your browser ...",
        LaunchPhase.CheckingRuntime => "Checking the container runtime ...",
        LaunchPhase.StartingRuntime => "Starting the container runtime ...",
        LaunchPhase.PreparingImage => $"Preparing the {ProductName.Short} image ...",
        LaunchPhase.StartingVpn => "Starting the VPN tunnel ...",
        LaunchPhase.VerifyingIntegrity => "Verifying image integrity ...",
        LaunchPhase.PreparingContainer => "Preparing the studio container ...",
        LaunchPhase.CheckingUpdates => "Checking for updates ...",
        LaunchPhase.WaitingForStudio => "Waiting for the studio to come up ...",
        LaunchPhase.Ready => "Ready.",
        _ => "Working ...",
    };
}
