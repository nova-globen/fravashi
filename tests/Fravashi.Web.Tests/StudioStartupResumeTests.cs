using System.Threading.Channels;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Sessions.Tools;
using Fravashi.Web.Services;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 205: on an ordinary start, <c>Program.cs</c> calls <see cref="LiveSessionRegistry.RecoverInterruptedToolCalls"/>
/// (backlog 84) synchronously, before the host even attempts to bind Kestrel, because it starts no CLI and only
/// touches the ledger and the store - nothing stops it running that early, and backlog 84 needs it done before
/// ANY session, not only a resumed one, can reach the tool server. <see cref="LiveSessionRegistry.ResumeInterruptedConversations"/>
/// (backlog 156) is different: it STARTS a CLI child, whose first call to the tool server would race Kestrel's
/// bind if it ran at the same point, so <see cref="StudioStartupResume"/> instead waits for
/// <see cref="IHostApplicationLifetime.ApplicationStarted"/>, which fires only once the host has started every
/// hosted service, including the one that binds and starts the server. This test drives that signal by hand (a
/// lifetime nothing but the test controls) to prove recovery has already happened by the time resume is even
/// wired up, that resume genuinely waits for the host's signal, and that it resumes the conversation exactly once.
/// </summary>
public sealed class StudioStartupResumeTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-startup-resume-" + Guid.NewGuid().ToString("N"));

    private readonly SqliteSessionStore _store;
    private readonly AgentApprovalBroker _broker = new(TimeSpan.FromSeconds(5));
    private readonly ScriptedRunner _runner = new();

    public StudioStartupResumeTests()
    {
        Directory.CreateDirectory(_directory);
        var factory = new SqliteConnectionFactory(
            new SqliteStoreOptions(Path.Combine(_directory, "fravashi.db"), Pooling: false));
        SqliteSchema.EnsureCreated(factory);
        _store = new SqliteSessionStore(factory);
    }

    public void Dispose()
    {
        _broker.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Recovery_runs_before_start_and_resume_waits_for_the_host_to_report_started_then_runs_exactly_once()
    {
        // A tool call the previous process left open (backlog 84), written through its own ledger instance,
        // the way the previous process would have left it on disk.
        var interrupted = AgentSessionId.New();
        _store.Save(new SessionRecord(
            interrupted, "w-interrupted", AgentTool.Claude, "t", null, AgentSessionState.Running, DateTimeOffset.UtcNow, null));
        new FileToolCallLedger(_directory).Claim(interrupted, "toolu_open", "studio_version", "fp");

        using var registry = new LiveSessionRegistry(
            _runner, _store, _broker, sync: new CopyOnlySync(), toolLedger: new FileToolCallLedger(_directory));
        var now = DateTimeOffset.UtcNow;

        // Mid-turn, exactly as backlog 156's own test sets one up: a person's prompt and a tool call after
        // the last completed turn, and nothing after them.
        var midTurn = AgentSessionId.New();
        _store.Save(new SessionRecord(midTurn, "w-mid", AgentTool.Claude, "t", "prov-mid", AgentSessionState.Running, now, null));
        _store.Append(midTurn,
        [
            new MessageEmitted(now, TranscriptRole.User, "first"),
            new TurnCompleted(now, "done", false, "completed"),
            new MessageEmitted(now, TranscriptRole.User, "second"),
            new ToolCallStarted(now, "toolu_1", "Edit", "/workspaces/w-mid/a.cs"),
        ]);

        // Program.cs's own order: recovery runs synchronously here, before resume is even wired up, let
        // alone before the host reports it has started.
        Assert.Equal(1, registry.RecoverInterruptedToolCalls());
        Assert.Contains(
            _store.Transcript(interrupted).OfType<MessageEmitted>(),
            m => m.Role == TranscriptRole.System && m.Text.Contains("studio_version", StringComparison.Ordinal));

        var lifetime = new ManualHostLifetime();
        StudioStartupResume.RunAfterStart(lifetime, registry);

        // Registering the callback must not run it: this is the gap backlog 205 reported, a resumed CLI
        // dialing the tool server before Kestrel has bound.
        Assert.Null(registry.Find(midTurn));
        Assert.Null(_runner.LastRequest);

        lifetime.ReportStarted();

        await Eventually(() => _runner.LastRequest is not null);
        Assert.Equal(midTurn, _runner.LastRequest!.SessionId);
        Assert.Equal("prov-mid", _runner.LastRequest.ResumeSessionId);

        // Resumed exactly once: the registry now holds it running, and a further sweep (what a second
        // ApplicationStarted, or an ordinary start, would also run) finds nothing left to resume.
        Assert.NotNull(registry.Find(midTurn));
        Assert.Equal(0, registry.ResumeInterruptedConversations());
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    /// <summary>A lifetime the test fires by hand, standing in for the host reporting Kestrel has bound.</summary>
    private sealed class ManualHostLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void ReportStarted() => _started.Cancel();

        public void StopApplication()
        {
        }
    }

    /// <summary>A runner the test only reads from: whether, and with what request, it was asked to start.</summary>
    private sealed class ScriptedRunner : IAgentSessionRunner
    {
        private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>();

        public AgentSessionRequest? LastRequest { get; private set; }

        public IAsyncEnumerable<AgentEvent> RunAsync(AgentSessionRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return _events.Reader.ReadAllAsync(cancellationToken);
        }

        public Task<bool> SendAsync(AgentSessionId sessionId, string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> SendAsync(
            AgentSessionId sessionId,
            string text,
            IReadOnlyList<AgentImageAttachment> images,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public bool Stop(AgentSessionId sessionId) => true;

        public bool ChangeModel(AgentSessionId sessionId, string? model) => true;
    }

    private sealed class CopyOnlySync : Fravashi.Application.AgentSync.IWorkspaceAgentSync
    {
        public void EnsureStarted(string workspaceId)
        {
        }

        public string? CopyPathFor(string workspaceId) => "/workspaces/" + workspaceId;

        public object? StatusFor(string workspaceId) => null;
    }
}
