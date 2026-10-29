using System.Threading.Channels;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.AgentRuntimes;

/// <summary>
/// Bridges an <see cref="IAgentLoginPipe"/> to the neutral <see cref="IAgentLoginSession"/> the UI consumes:
/// every process line is parsed into an <see cref="AgentLoginChunk"/> (URL / device code highlighted) and
/// streamed; user input is forwarded to the process stdin; a start and an exit notice bracket the stream.
/// </summary>
public sealed class AgentLoginSession : IAgentLoginSession
{
    private readonly IAgentLoginPipe _pipe;
    private readonly Func<string, string?>? _keepToken;
    private readonly Channel<AgentLoginChunk> _channel = Channel.CreateUnbounded<AgentLoginChunk>();

    /// <param name="keepToken">
    /// Backlog 183: for a runtime whose sign-in PRINTS a long-lived token, stores it and answers null, or answers why
    /// it was not stored. The token is captured from the output by <see cref="SubscriptionTokenScanner"/>, which hides
    /// every sign of a token from the panel whether or not one is kept (backlog 268).
    /// </param>
    public AgentLoginSession(AgentTool tool, IAgentLoginPipe pipe, Func<string, string?>? keepToken = null)
    {
        Tool = tool;
        _pipe = pipe;
        _keepToken = keepToken;
        _channel.Writer.TryWrite(new AgentLoginChunk(
            DateTimeOffset.UtcNow,
            $"Starting sign-in for {AgentRuntimeCatalog.For(tool).DisplayName}…",
            AgentLoginChunkKind.Notice));
        _ = PumpAsync();
    }

    public AgentTool Tool { get; }

    public ChannelReader<AgentLoginChunk> Output => _channel.Reader;

    public Task<int> Completion => _pipe.Completion;

    public bool HasExited => _pipe.HasExited;

    public string? TokenRefusal { get; private set; }

    public Task SendInputAsync(string text, CancellationToken cancellationToken = default) =>
        _pipe.SendLineAsync(text ?? string.Empty, cancellationToken).AsTask();

    private async Task PumpAsync()
    {
        // Every line goes through the scanner, for every runtime: a sign of a token is hidden whether or not anything
        // keeps it (backlog 268).
        var scanner = new SubscriptionTokenScanner();
        try
        {
            await foreach (var line in _pipe.Output.ReadAllAsync())
            {
                var text = scanner.Show(line.Text ?? string.Empty);
                var chunk = AgentLoginTextParser.ToChunk(DateTimeOffset.UtcNow, text, line.IsError);
                if ((chunk.Text.Length == 0 && chunk.Url is null && chunk.DeviceCode is null)
                    || AgentLoginTextParser.IsSpinnerFrame(chunk.Text))
                {
                    continue;
                }

                await _channel.Writer.WriteAsync(chunk);
            }
        }
        catch
        {
            // Streaming ended abnormally; the exit notice below still fires.
        }

        var code = await _pipe.Completion;
        _channel.Writer.TryWrite(new AgentLoginChunk(
            DateTimeOffset.UtcNow,
            code == 0 ? "Sign-in process finished." : $"Sign-in process exited with code {code}.",
            AgentLoginChunkKind.Notice));

        scanner.Finish();
        var token = scanner.Token;
        string? outcome = null;
        if (_keepToken is not null && code == 0)
        {
            // Recorded before the stream completes, so a reader that saw the stream end can rely on it (backlog 267).
            TokenRefusal = token is null ? null : _keepToken(token);
            outcome = token is not null
                ? TokenRefusal is { } refusal
                    ? $"The token was not stored: {refusal}"
                    : "Signed in: the token is stored encrypted and every conversation is handed it."
                : scanner.SawSeveral
                    ? "The sign-in printed more than one different token, so nothing was stored; none of them is shown."
                    : scanner.SawUnreadable
                        ? "The sign-in printed something shaped like a token that could not be read whole (the terminal moved "
                            + "the cursor inside it, part of it was missing, or the line after it may be the rest of it), so it "
                            + "is hidden and nothing was stored."
                        : "The sign-in finished, but no token appeared in what it printed, so nothing was stored.";
        }
        else if (_keepToken is not null)
        {
            outcome = "The sign-in did not finish, so nothing was stored.";
        }
        else if (token is not null || scanner.SawSeveral || scanner.SawUnreadable)
        {
            outcome = "Something shaped like a token was printed; it is hidden and nothing was stored.";
        }

        if (outcome is not null)
        {
            _channel.Writer.TryWrite(new AgentLoginChunk(DateTimeOffset.UtcNow, outcome, AgentLoginChunkKind.Notice));
        }

        _channel.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync() => await _pipe.DisposeAsync();
}
