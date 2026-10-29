using System.Threading.Channels;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.AgentRuntimes;
using Microsoft.Extensions.Logging;

namespace Fravashi.Infrastructure.Tests.AgentRuntimes;

/// <summary>A logger that keeps every formatted line, so a test can assert what a field diagnosis would find.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Lines { get; } = new();

    public IEnumerable<string> Warnings =>
        Lines.Where(l => l.Level == LogLevel.Warning).Select(l => l.Message);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Add((logLevel, formatter(state, exception)));
}

/// <summary>An <see cref="IAgentCommandRunner"/> whose result is decided by a handler, capturing every call.</summary>
internal sealed class FakeAgentCommandRunner : IAgentCommandRunner
{
    public Func<AgentCommand, AgentCommandResult> Handler { get; set; } =
        _ => new AgentCommandResult(true, 0, string.Empty, string.Empty, false);

    public List<AgentCommand> Calls { get; } = new();

    public Task<AgentCommandResult> RunAsync(AgentCommand command, CancellationToken cancellationToken = default)
    {
        Calls.Add(command);
        return Task.FromResult(Handler(command));
    }
}

/// <summary>In-memory secret store.</summary>
internal sealed class FakeSecretStore : ISecretStore
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public void Set(string reference, string plaintext) => Values[reference] = plaintext;
    public string? Get(string reference) => Values.TryGetValue(reference, out var v) ? v : null;
    public void Remove(string reference) => Values.Remove(reference);
}

/// <summary>In-memory configuration store.</summary>
internal sealed class FakeConfigurationStore : IConfigurationStore
{
    public Dictionary<string, string?> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;
    public IReadOnlyDictionary<string, string?> GetAll() => Values;
    public void Set(string key, string? value) => Values[key] = value;
    public void SetMany(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var (k, v) in values)
        {
            Values[k] = v;
        }
    }

    public void Remove(string key) => Values.Remove(key);
}

/// <summary>A scripted login pipe the test feeds lines to and reads forwarded input from.</summary>
internal sealed class FakeLoginPipe : IAgentLoginPipe
{
    private readonly Channel<LoginLine> _channel = Channel.CreateUnbounded<LoginLine>();
    private readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ChannelReader<LoginLine> Output => _channel.Reader;
    public Task<int> Completion => _completion.Task;
    public bool HasExited { get; private set; }
    public List<string> Sent { get; } = new();

    public ValueTask SendLineAsync(string text, CancellationToken cancellationToken = default)
    {
        Sent.Add(text);
        return ValueTask.CompletedTask;
    }

    public void Emit(string text, bool isError = false) => _channel.Writer.TryWrite(new LoginLine(text, isError));

    public void Finish(int exitCode)
    {
        HasExited = true;
        _channel.Writer.TryComplete();
        _completion.TrySetResult(exitCode);
    }

    public ValueTask DisposeAsync()
    {
        HasExited = true;
        _channel.Writer.TryComplete();
        _completion.TrySetResult(-1);
        return ValueTask.CompletedTask;
    }
}
