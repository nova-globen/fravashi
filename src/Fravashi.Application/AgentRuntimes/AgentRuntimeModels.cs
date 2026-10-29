using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.AgentRuntimes;

/// <summary>
/// Whether an agent runtime (a coding-agent CLI) is authenticated, and how sure we are. The value is set
/// only from a <b>real</b> check - a stored API key we inject on every run, or the CLI's own auth-status
/// command - never assumed. When neither can prove sign-in, the state is <see cref="Unknown"/>, not
/// authenticated (the assertion-gap rule: no "authenticated" badge without a mechanism behind it).
/// </summary>
public enum AgentRuntimeAuthState
{
    /// <summary>No mechanism could determine the state (e.g. the CLI has no status command and no key is stored).</summary>
    Unknown,

    /// <summary>A real check ran and the runtime is not signed in.</summary>
    SignedOut,

    /// <summary>A real check ran (stored key, or the CLI's status command) and the runtime is authenticated.</summary>
    Authenticated,

    /// <summary>The runtime is not installed, so authentication cannot be evaluated.</summary>
    NotInstalled,
}

/// <summary>The outcome of an update/install attempt for one runtime.</summary>
public enum AgentRuntimeUpdateOutcome
{
    /// <summary>Not yet attempted this run.</summary>
    Pending,

    /// <summary>The update/install is currently running.</summary>
    Running,

    /// <summary>The package manager ran and reported success (may or may not have changed the version).</summary>
    Succeeded,

    /// <summary>The update/install failed (offline, package error, timeout). Never blocks the app.</summary>
    Failed,

    /// <summary>Deliberately not attempted (e.g. not running in the container, or the runtime was not selected).</summary>
    Skipped,
}

/// <summary>
/// A point-in-time view of one agent runtime: whether the CLI is installed, its version, and its auth state.
/// Every field is observed, never assumed - <see cref="Installed"/> comes from the CLI answering
/// <c>--version</c>, <see cref="Version"/> from that output, and <see cref="AuthState"/> from a real check.
/// </summary>
/// <param name="Tool">Which runtime this describes.</param>
/// <param name="Installed">True when the CLI is present and answered its version command.</param>
/// <param name="Version">The reported version, when installed; otherwise null.</param>
/// <param name="AuthState">The authentication state from a real check.</param>
/// <param name="AuthDetail">A human-readable note on how the auth state was determined (the mechanism).</param>
public sealed record AgentRuntimeStatus(
    AgentTool Tool,
    bool Installed,
    string? Version,
    AgentRuntimeAuthState AuthState,
    string? AuthDetail);

/// <summary>The recorded result of an update/install attempt for one runtime, for the runtime-status surface.</summary>
/// <param name="Tool">Which runtime.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="FromVersion">The version before the attempt, when known.</param>
/// <param name="ToVersion">The version after the attempt, when known.</param>
/// <param name="Message">A short human-readable note (e.g. the failure reason).</param>
/// <param name="RanUtc">When the attempt finished.</param>
public sealed record AgentRuntimeUpdateResult(
    AgentTool Tool,
    AgentRuntimeUpdateOutcome Outcome,
    string? FromVersion,
    string? ToVersion,
    string? Message,
    DateTimeOffset RanUtc);

/// <summary>
/// A single command to run against a coding-agent CLI (or the package manager that installs it), behind the
/// <see cref="IAgentCommandRunner"/> seam so the process shell-out is faked in tests. <paramref name="StdIn"/>
/// is written to the child's standard input and the stream closed, for CLIs that read a key/token from stdin
/// (e.g. <c>codex login --with-api-key</c>). <paramref name="WorkingDirectory"/> is the child's
/// cwd (plan 2d: an agent runs INSIDE its workspace copy); null inherits the app's.
/// </summary>
public sealed record AgentCommand(
    string FileName,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout,
    IReadOnlyDictionary<string, string>? Environment = null,
    string? StdIn = null,
    string? WorkingDirectory = null);

/// <summary>The result of running an <see cref="AgentCommand"/>.</summary>
/// <param name="Started">False when the process could not be started (e.g. the CLI is not installed).</param>
/// <param name="ExitCode">The exit code, when the process ran to completion; null on a start failure or timeout.</param>
/// <param name="StandardOutput">Captured stdout.</param>
/// <param name="StandardError">Captured stderr.</param>
/// <param name="TimedOut">True when the process was killed for exceeding its timeout.</param>
public sealed record AgentCommandResult(
    bool Started,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut)
{
    /// <summary>True when the process started, did not time out, and exited zero.</summary>
    public bool Success => Started && !TimedOut && ExitCode == 0;

    /// <summary>A start failure, for CLIs that are not installed.</summary>
    public static AgentCommandResult NotStarted(string error) => new(false, null, string.Empty, error, false);
}
