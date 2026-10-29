using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Web.Tests;

/// <summary>Runtime statuses/install results the tests dictate, with no process shell-out.</summary>
internal sealed class FakeAgentRuntimeService : IAgentRuntimeService
{
    public Dictionary<AgentTool, AgentRuntimeStatus> Statuses { get; } = new();
    public Dictionary<AgentTool, AgentRuntimeUpdateResult> InstallResults { get; } = new();
    public List<AgentTool> InstallCalls { get; } = new();

    public Task<AgentRuntimeStatus> GetStatusAsync(AgentTool tool, CancellationToken cancellationToken = default) =>
        Task.FromResult(Statuses.TryGetValue(tool, out var s)
            ? s
            : new AgentRuntimeStatus(tool, false, null, AgentRuntimeAuthState.NotInstalled, "not installed"));

    public Task<AgentRuntimeUpdateResult> InstallOrUpdateAsync(AgentTool tool, CancellationToken cancellationToken = default)
    {
        InstallCalls.Add(tool);
        var result = InstallResults.TryGetValue(tool, out var r)
            ? r
            : new AgentRuntimeUpdateResult(tool, AgentRuntimeUpdateOutcome.Succeeded, null, "1.0.0", null, DateTimeOffset.UtcNow);
        return Task.FromResult(result);
    }
}

/// <summary>In-memory credential store for onboarding component tests.</summary>
internal sealed class FakeAgentRuntimeCredentialStore : IAgentRuntimeCredentialStore
{
    public Dictionary<AgentTool, string> Keys { get; } = new();

    public void SaveApiKey(AgentTool tool, string apiKey) => Keys[tool] = apiKey;
    public void ClearApiKey(AgentTool tool) => Keys.Remove(tool);
    public bool HasApiKey(AgentTool tool) => Keys.ContainsKey(tool);

    /// <summary>The runtimes something other than a stored key authenticates (the broker, in the product).</summary>
    public HashSet<AgentTool> CarriedElsewhere { get; } = [];

    public bool UsesStoredKey(AgentTool tool) => !CarriedElsewhere.Contains(tool);

    /// <summary>Subscription tokens stored, e.g. from before the broker was turned on (backlog 267 leftover).</summary>
    public HashSet<AgentTool> SubscriptionTokens { get; } = [];

    public bool HasSubscriptionToken(AgentTool tool) => SubscriptionTokens.Contains(tool);
    public void ClearSubscriptionToken(AgentTool tool) => SubscriptionTokens.Remove(tool);

    public IReadOnlyDictionary<string, string> EnvironmentFor(AgentTool tool) =>
        Keys.TryGetValue(tool, out var key)
            ? new Dictionary<string, string> { [AgentRuntimeCatalog.For(tool).ApiKeyEnvVar] = key }
            : new Dictionary<string, string>();
}

/// <summary>A mapping suggester that returns a preset result and records the request.</summary>
internal sealed class FakeBoardMappingSuggester : IBoardMappingSuggester
{
    private readonly BoardMappingSuggestion _result;
    public BoardMappingSuggestionRequest? LastRequest { get; private set; }

    public FakeBoardMappingSuggester(BoardMappingSuggestion result) => _result = result;

    public Task<BoardMappingSuggestion> SuggestAsync(BoardMappingSuggestionRequest request, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        return Task.FromResult(_result);
    }
}

/// <summary>A login proxy handing out a single scripted session the test drives.</summary>
internal sealed class FakeAgentLoginProxy : IAgentLoginProxy
{
    public FakeAgentLoginSession? Started { get; private set; }

    public IAgentLoginSession Start(AgentTool tool)
    {
        Started = new FakeAgentLoginSession(tool);
        return Started;
    }
}

/// <summary>A scripted login session: the test pushes output lines and reads back forwarded input.</summary>
internal sealed class FakeAgentLoginSession : IAgentLoginSession
{
    private readonly Channel<AgentLoginChunk> _channel = Channel.CreateUnbounded<AgentLoginChunk>();
    private readonly TaskCompletionSource<int> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _outputEndSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeAgentLoginSession(AgentTool tool)
    {
        Tool = tool;
        Output = new EndSignallingReader(_channel.Reader, _outputEndSeen);
    }

    public AgentTool Tool { get; }
    public ChannelReader<AgentLoginChunk> Output { get; }
    public Task<int> Completion => _completion.Task;

    /// <summary>Completes once a reader of <see cref="Output"/> has been told the stream ended.</summary>
    public Task OutputEndSeen => _outputEndSeen.Task;
    public bool HasExited { get; private set; }
    public string? TokenRefusal { get; set; }
    public List<string> SentInput { get; } = new();

    public Task SendInputAsync(string text, CancellationToken cancellationToken = default)
    {
        SentInput.Add(text);
        return Task.CompletedTask;
    }

    /// <summary>Pushes one output line to the stream (parsed by the panel).</summary>
    public void Emit(string text, string? url = null, string? deviceCode = null) =>
        _channel.Writer.TryWrite(new AgentLoginChunk(DateTimeOffset.UtcNow, text, AgentLoginChunkKind.Output, url, deviceCode));

    /// <summary>Ends the session with an exit code, then completes the stream (the order the real session keeps).</summary>
    public void Finish(int exitCode = 0)
    {
        HasExited = true;
        _completion.TrySetResult(exitCode);
        _channel.Writer.TryComplete();
    }

    /// <summary>
    /// #316: completes the stream while the exit code is not known yet, the order the release runner happened to hit.
    /// <see cref="SetExitCode"/> supplies the code afterwards.
    /// </summary>
    public void EndOutput()
    {
        HasExited = true;
        _channel.Writer.TryComplete();
    }

    public void SetExitCode(int exitCode) => _completion.TrySetResult(exitCode);

    public ValueTask DisposeAsync()
    {
        HasExited = true;
        _completion.TrySetResult(-1);
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    /// <summary>Reads the channel as it is and says when a reader has been told the stream ended.</summary>
    private sealed class EndSignallingReader(ChannelReader<AgentLoginChunk> inner, TaskCompletionSource endSeen)
        : ChannelReader<AgentLoginChunk>
    {
        public override Task Completion => inner.Completion;

        public override bool TryRead([MaybeNullWhen(false)] out AgentLoginChunk item) => inner.TryRead(out item);

        public override async ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
        {
            var more = await inner.WaitToReadAsync(cancellationToken);
            if (!more)
            {
                endSeen.TrySetResult();
            }

            return more;
        }
    }
}

/// <summary>
/// Backlog 10: the Node banner's two collaborators, with no process and no network. The command runner
/// answers <c>node --version</c> with whatever the test says the image has; the provisioner reports the
/// progress the test scripts and finishes when the test says so.
/// </summary>
internal sealed class FakeNodeCommandRunner(string? imageVersionOutput = "v22.14.0\n") : IAgentCommandRunner
{
    public int Calls { get; private set; }

    public Task<AgentCommandResult> RunAsync(AgentCommand command, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(imageVersionOutput is null
            ? AgentCommandResult.NotStarted("no node")
            : new AgentCommandResult(true, 0, imageVersionOutput, string.Empty, false));
    }
}

internal sealed class FakeNodeProvisioner : Fravashi.Infrastructure.AgentRuntimes.INodeProvisioner
{
    private TaskCompletionSource<Fravashi.Infrastructure.AgentRuntimes.NodeProvisionResult> _completion = new();

    public List<string> Requested { get; } = [];

    public IProgress<Fravashi.Infrastructure.AgentRuntimes.NodeProvisionProgress>? Progress { get; private set; }

    /// <summary>Runs on the calling thread when the install starts, so a test can script the install's own effect (e.g. lay the version down).</summary>
    public Action<string>? OnInstall { get; set; }

    public Task<Fravashi.Infrastructure.AgentRuntimes.NodeProvisionResult> InstallAsync(
        string requestedVersion,
        IProgress<Fravashi.Infrastructure.AgentRuntimes.NodeProvisionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Requested.Add(requestedVersion);
        Progress = progress;
        _completion = new TaskCompletionSource<Fravashi.Infrastructure.AgentRuntimes.NodeProvisionResult>();
        OnInstall?.Invoke(requestedVersion);
        return _completion.Task;
    }

    public void Report(Fravashi.Infrastructure.AgentRuntimes.NodeProvisionProgress progress) => Progress?.Report(progress);

    public void Finish(Fravashi.Infrastructure.AgentRuntimes.NodeProvisionResult result) => _completion.TrySetResult(result);
}
