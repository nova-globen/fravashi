using System.Diagnostics;
using System.Threading.Channels;

namespace Fravashi.Infrastructure.AgentRuntimes;

/// <summary>
/// A real login transport over an OS process. stdout and stderr are streamed line-by-line into one channel and
/// the child's stdin is kept open for forwarded input. A start failure is surfaced as a single error line and an
/// immediate completion with a negative exit code - the caller never sees an exception from a missing CLI.
/// <para>
/// The child's environment is the allowlist in <see cref="AgentProcessEnvironment"/>, not the studio's own
/// (backlog 72).
/// </para>
/// </summary>
public sealed class ProcessLoginPipe : IAgentLoginPipe
{
    private readonly Process? _process;
    private readonly string _enter;
    private readonly Channel<LoginLine> _channel = Channel.CreateUnbounded<LoginLine>();
    private readonly TaskCompletionSource<int> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ProcessLoginPipe(string fileName, IReadOnlyList<string> arguments)
    {
        _enter = EnterFor(fileName);
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Backlog 72: the interactive vendor sign-in is the same class of child as a session, so it gets
        // the same allowlist environment rather than the studio's own. What it needs survives: it is
        // found on PATH, it runs under SHELL or /bin/sh, and it writes its credentials under HOME.
        AgentProcessEnvironment.Restrict(startInfo);

        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        try
        {
            _process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            _channel.Writer.TryWrite(new LoginLine($"Could not start the sign-in command: {ex.Message}", IsError: true));
            _channel.Writer.TryComplete();
            _completion.TrySetResult(-1);
            return;
        }

        if (_process is null)
        {
            _channel.Writer.TryWrite(new LoginLine("The sign-in command did not start.", IsError: true));
            _channel.Writer.TryComplete();
            _completion.TrySetResult(-1);
            return;
        }

        _ = PumpAsync(_process);
    }

    /// <summary>How long after the text its Enter follows, so the two are not read as one paste.</summary>
    public static readonly TimeSpan EnterPause = TimeSpan.FromMilliseconds(400);

    /// <summary>Enter on a raw terminal.</summary>
    private static readonly string CarriageReturn = ((char)13).ToString();

    /// <summary>
    /// What pressing Enter is for a CLI started by <paramref name="fileName"/>. Under <c>script</c> the CLI reads a
    /// pseudo-terminal in raw mode, where Enter is a carriage return and a line feed is just another character: the code
    /// a person pasted into the Claude sign-in was echoed and never submitted (seen 2026-10-02, and replayed in a container
    /// both ways). Run directly, a CLI reads lines, so the platform's newline.
    /// </summary>
    public static string EnterFor(string fileName) =>
        string.Equals(Path.GetFileName(fileName), "script", StringComparison.Ordinal) ? CarriageReturn : Environment.NewLine;

    public ChannelReader<LoginLine> Output => _channel.Reader;

    public Task<int> Completion => _completion.Task;

    public bool HasExited => _process is null || SafeHasExited(_process);

    public async ValueTask SendLineAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_process is null || SafeHasExited(_process))
        {
            return;
        }

        try
        {
            // The text and the Enter go as two writes with a pause between. A TUI reads one burst of characters as a
            // PASTE, and an Enter inside a paste is just part of the text: the owner's 90-character sign-in code
            // followed by its Enter in one write was masked and never submitted (seen 2026-10-02), where a short code
            // in the same write had been. Sent on its own, the Enter is a key press.
            if (text.Length > 0)
            {
                await _process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken);
                await _process.StandardInput.FlushAsync(cancellationToken);
                await Task.Delay(EnterPause, cancellationToken);
            }

            await _process.StandardInput.WriteAsync(_enter.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
        }
        catch
        {
            // A closed stdin surfaces via the exit code.
        }
    }

    private async Task PumpAsync(Process process)
    {
        var stdout = ReadLinesAsync(process.StandardOutput, isError: false);
        var stderr = ReadLinesAsync(process.StandardError, isError: true);
        try
        {
            await Task.WhenAll(stdout, stderr);
        }
        catch
        {
            // Reader failures are non-fatal; the exit code still resolves.
        }

        try
        {
            await process.WaitForExitAsync();
        }
        catch
        {
            // Ignore.
        }

        _channel.Writer.TryComplete();
        _completion.TrySetResult(SafeExitCode(process));
    }

    private async Task ReadLinesAsync(StreamReader reader, bool isError)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                await _channel.Writer.WriteAsync(new LoginLine(line, isError));
            }
        }
        catch
        {
            // Pipe closed on kill; whatever streamed is enough.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Already gone.
        }

        try
        {
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Best-effort.
        }

        _process.Dispose();
    }

    private static bool SafeHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : -1;
        }
        catch
        {
            return -1;
        }
    }
}

/// <summary>Creates real process-backed login pipes.</summary>
public sealed class ProcessLoginPipeFactory : IAgentLoginPipeFactory
{
    public IAgentLoginPipe Create(string fileName, IReadOnlyList<string> arguments) =>
        new ProcessLoginPipe(fileName, arguments);
}
