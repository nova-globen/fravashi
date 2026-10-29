namespace Fravashi.Launcher;

/// <summary>
/// What a runtime's availability probe actually found. The old boolean could not tell "the engine is not
/// installed" from "the engine is installed and its machine is down", which is why a fresh launch on a host
/// with Podman installed but its machine stopped told the user to install Docker.
/// </summary>
public enum RuntimeAvailability
{
    /// <summary>The CLI executable is not on PATH: this runtime is not installed at all.</summary>
    CliMissing,

    /// <summary>
    /// The CLI is installed but its engine did not answer: a non-zero probe (Podman's machine is stopped,
    /// Docker Desktop is not running) or a probe that never returned inside its deadline (a wedged engine).
    /// </summary>
    EngineNotResponding,

    /// <summary>The CLI is installed and its engine answered.</summary>
    Available,
}

/// <summary>How a <see cref="IContainerRuntime.TryStartMachine"/> attempt ended.</summary>
public enum MachineStartStatus
{
    /// <summary>This runtime has no machine anyone can start from a CLI, so nothing was attempted.</summary>
    NotSupported,

    /// <summary>The runtime supports machines but none exists. Fravashi never creates one (see the message).</summary>
    NoMachine,

    /// <summary>The machine already reports as running or starting, so the engine is wedged rather than stopped.</summary>
    AlreadyRunning,

    /// <summary>The machine was started and the CLI reported success.</summary>
    Started,

    /// <summary>The start was attempted and failed (the runtime's own error is in the message).</summary>
    Failed,

    /// <summary>The start did not finish inside its deadline and its process tree was killed.</summary>
    TimedOut,
}

/// <summary>
/// The outcome of trying to start a container runtime's backing machine, with a message that is always
/// safe to show the user: it either says what happened or says why nothing could be done.
/// </summary>
/// <param name="Status">What happened.</param>
/// <param name="Message">One user-facing sentence (or two), never empty.</param>
public sealed record MachineStartResult(MachineStartStatus Status, string Message)
{
    /// <summary>True only when the engine should now answer, i.e. a re-probe is worth doing.</summary>
    public bool Started => Status == MachineStartStatus.Started;

    /// <summary>The honest "there is nothing to start here" answer for Docker and wslc.</summary>
    public static MachineStartResult NotSupported(string message) =>
        new(MachineStartStatus.NotSupported, message);
}
