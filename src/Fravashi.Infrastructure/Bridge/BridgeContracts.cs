namespace Fravashi.Infrastructure.Bridge;

// Studio-side mirrors of the host bridge's JSON contracts (src/Fravashi.Bridge). Deliberately
// duplicated: the production studio must not reference the bridge hosting library; the wire format
// (camelCase via JsonSerializerDefaults.Web) is the contract, and the bridge integration tests in
// Fravashi.Infrastructure.Tests pin the two sides together.

/// <summary>A file-system entry inside the active bridge workspace.</summary>
public sealed record BridgeEntryDto(string Name, string Path, bool IsDirectory, long Size, DateTimeOffset ModifiedUtc);

/// <summary>The outcome of one git invocation on the host.</summary>
public sealed record BridgeGitResultDto(int ExitCode, string Stdout, string Stderr)
{
    /// <summary>The bridge's exit code for "git could not be started on the host" (backlog 266); see the bridge's own.</summary>
    public const int GitMissingExitCode = 127;

    /// <summary>How the error text of that answer starts.</summary>
    public const string GitMissingMessage = "git is not installed on this machine";

    /// <summary>
    /// True when the HOST has no git (or could not start it), as opposed to git refusing. Both the code and the text,
    /// because 127 is a shell's code for a missing command, and git does run a shell for a "!" alias, so git exiting
    /// 127 is conceivable (not measured).
    /// </summary>
    public bool IsGitMissing =>
        ExitCode == GitMissingExitCode && Stderr.StartsWith(GitMissingMessage, StringComparison.Ordinal);
}

/// <summary>One search hit from the host-side search.</summary>
public sealed record BridgeSearchMatchDto(string Path, int Line, int Column, int Length, string Preview);

/// <summary>A file change streamed from the host watcher (SSE).</summary>
public sealed record BridgeFileEventDto(string Type, string Path, string? OldPath);

/// <summary>A conversation file the host CLI wrote for the active workspace (backlog 43).</summary>
public sealed record BridgeCliConversationDto(string Name, long Size, DateTimeOffset ModifiedUtc);

/// <summary>A registered host workspace as listed by the bridge.</summary>
public sealed record BridgeWorkspaceDto(string Id, string Name, string RootPath);

/// <summary>A worktree the bridge made (or found already made) for one work item (#306, #308).</summary>
public sealed record BridgeWorktreeDto(BridgeWorkspaceDto Workspace, string Branch, string BaseBranch, bool BranchReused);

/// <summary>What the HOST established about a folder the studio asked it to grant.</summary>
public enum BridgeRegistrationOutcome
{
    /// <summary>Granted; the workspace is on the result.</summary>
    Registered,

    /// <summary>
    /// The host looked and the folder is positively not there (its parent is attached and readable
    /// and does not contain it). Wire: 404.
    /// </summary>
    Missing,

    /// <summary>
    /// The host could not look right now: the volume is not attached (an unmapped drive letter, an
    /// unplugged disk, an offline share) or an ancestor would not answer. Wire: 503. Routinely
    /// transient, so the studio must offer a retry and must never say the folder is gone.
    /// </summary>
    Unreachable,
}

/// <summary>The host's answer to a registration attempt; see <see cref="BridgeRegistrationOutcome"/>.</summary>
public sealed record BridgeRegistration(BridgeRegistrationOutcome Outcome, BridgeWorkspaceDto? Workspace = null)
{
    public static readonly BridgeRegistration Missing = new(BridgeRegistrationOutcome.Missing);

    public static readonly BridgeRegistration Unreachable = new(BridgeRegistrationOutcome.Unreachable);

    public static BridgeRegistration Registered(BridgeWorkspaceDto workspace)
        => new(BridgeRegistrationOutcome.Registered, workspace);
}

/// <summary>A file read: the bytes plus the host's last-write stamp (from the response header).</summary>
public sealed record BridgeFileContent(byte[] Bytes, DateTimeOffset? ModifiedUtc);

/// <summary>
/// A guarded write was refused by the host: the target changed since it was read, or it vanished
/// (<see cref="Missing"/>). Mapped from HTTP 412/410; the writer turns it into the studio's
/// save-conflict flow (ADR 0016).
/// </summary>
public sealed class BridgeConflictException(bool missing) : Exception
{
    public bool Missing { get; } = missing;
}
