namespace Fravashi.WebSetup;

/// <summary>
/// The process exit codes of FravashiSetup.exe. They are a contract with whoever drives a silent install
/// (a host on the test VM, a script), so a value is never reused for a different meaning.
/// </summary>
public enum SetupExitCode
{
    /// <summary>The engine said the release is installed.</summary>
    Installed = 0,

    /// <summary>The engine ran and the install did not complete; the log and the verdict say why.</summary>
    Failed = 1,

    /// <summary>The command line was not understood, or only the usage was asked for (<c>/?</c>); nothing was started.</summary>
    BadArguments = 2,

    // 3 meant "the forge refused without a token" until the forge retired. It is never reused.

    /// <summary>Windows PowerShell could not be started, or ended without the engine reaching a verdict.</summary>
    EngineDidNotRun = 4,

    /// <summary>The person cancelled a running install.</summary>
    Cancelled = 5,

    /// <summary>
    /// The update channel has no release in the folder asked for (its manifest answered 404): a version that was never
    /// published, or, before the first release, <c>latest</c> itself.
    /// </summary>
    NotPublished = 6,
}
