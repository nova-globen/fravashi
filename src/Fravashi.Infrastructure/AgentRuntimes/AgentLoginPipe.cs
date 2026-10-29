using System.Threading.Channels;

namespace Fravashi.Infrastructure.AgentRuntimes;

/// <summary>One line read from a login process, tagged with the stream it came from.</summary>
public readonly record struct LoginLine(string Text, bool IsError);

/// <summary>
/// The transport under an <see cref="Application.AgentRuntimes.IAgentLoginSession"/>: a live process whose lines
/// are streamed and whose stdin accepts forwarded input. Abstracted so the session's parsing/streaming logic is
/// tested against a scripted pipe, while the real one wraps an OS process (optionally under a PTY).
/// </summary>
public interface IAgentLoginPipe : IAsyncDisposable
{
    ChannelReader<LoginLine> Output { get; }

    ValueTask SendLineAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Resolves with the process exit code; a negative sentinel when it never started.</summary>
    Task<int> Completion { get; }

    bool HasExited { get; }
}

/// <summary>Creates the transport for a login command.</summary>
public interface IAgentLoginPipeFactory
{
    IAgentLoginPipe Create(string fileName, IReadOnlyList<string> arguments);
}
