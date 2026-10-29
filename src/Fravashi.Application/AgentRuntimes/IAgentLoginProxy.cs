using System.Threading.Channels;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.AgentRuntimes;

/// <summary>How to render one streamed line of a proxied interactive login.</summary>
public enum AgentLoginChunkKind
{
    /// <summary>Ordinary stdout.</summary>
    Output,

    /// <summary>Stderr, or a line the CLI marked as an error.</summary>
    Error,

    /// <summary>A proxy notice (the process started / exited / was cancelled).</summary>
    Notice,
}

/// <summary>
/// One streamed line from a proxied interactive login. <see cref="Url"/> and <see cref="DeviceCode"/> are the
/// parsed highlights the UI turns into a clickable link and a copyable code; both null when the line carries
/// neither.
/// </summary>
/// <param name="At">When the line was produced.</param>
/// <param name="Text">The raw line (control characters already stripped).</param>
/// <param name="Kind">How to render it.</param>
/// <param name="Url">A verification URL detected in the line, if any.</param>
/// <param name="DeviceCode">A device/user code detected in the line, if any.</param>
public sealed record AgentLoginChunk(
    DateTimeOffset At,
    string Text,
    AgentLoginChunkKind Kind,
    string? Url = null,
    string? DeviceCode = null);

/// <summary>
/// A live interactive-login session: the CLI's own sign-in command running in this environment, its output
/// streamed to the browser and the user's keystrokes forwarded to its stdin. This is the web↔CLI proxy for the
/// login step (S13/S14) - Fravashi offers login exactly as the CLI offers it (URL, device code, token prompt),
/// it does not reimplement the flow.
/// </summary>
public interface IAgentLoginSession : IAsyncDisposable
{
    /// <summary>Which runtime is signing in.</summary>
    AgentTool Tool { get; }

    /// <summary>
    /// The live output stream; completes when the process exits. Its end does not promise that <see cref="Completion"/>
    /// has resolved yet (#316), so a reader that needs the exit code awaits it.
    /// </summary>
    ChannelReader<AgentLoginChunk> Output { get; }

    /// <summary>Resolves with the process exit code once it ends (or a sentinel if it never started).</summary>
    Task<int> Completion { get; }

    /// <summary>True once the process has exited.</summary>
    bool HasExited { get; }

    /// <summary>
    /// Backlog 267: why the token the sign-in printed was NOT kept (the credential store's own refusal), set before
    /// <see cref="Output"/> completes. Null when it was kept, or when there was nothing to keep. A sign-in whose token
    /// was refused has not signed this runtime in, whatever its exit code says.
    /// </summary>
    string? TokenRefusal { get; }

    /// <summary>Forwards a line of user input to the process's stdin (a newline is appended).</summary>
    Task SendInputAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>Starts a proxied interactive-login session for a runtime (S13/S14).</summary>
public interface IAgentLoginProxy
{
    /// <summary>
    /// Starts the CLI's interactive sign-in command and begins streaming. In a Linux container the command is
    /// run under a pseudo-TTY (<c>script</c>) because these CLIs demand one; the returned session ends
    /// gracefully with an error notice rather than throwing when the CLI cannot start.
    /// </summary>
    IAgentLoginSession Start(AgentTool tool);
}
