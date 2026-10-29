using System.Threading.Channels;
using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Application.Telemetry;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Telemetry;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Tests.Persistence;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Telemetry;

/// <summary>
/// Backlog 74: the producers that live where a session's whole life is visible. The registry is the
/// only thing in the studio that sees a session start, run, be blocked on a person, retry a turn and
/// end, so it is the only place these can be measured honestly.
/// <para>
/// Each assertion here is about the EVENT the registry produced, never about the transcript: the two
/// are deliberately different records, and keeping them apart is what lets the measurement be switched
/// off without the conversation losing anything.
/// </para>
/// </summary>
public class SessionProductEventTests : IDisposable
{
    private readonly SqliteTestContext _db = new();
    private readonly AgentApprovalBroker _broker = new(TimeSpan.FromSeconds(5));
    private readonly ScriptedRunner _runner = new();
    private readonly RecordingRecorder _telemetry = new();
    private readonly LiveSessionRegistry _registry;

    public SessionProductEventTests()
    {
        _registry = new LiveSessionRegistry(
            _runner, new SqliteSessionStore(_db.Factory), _broker, telemetry: _telemetry);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _registry.Dispose();
        _broker.Dispose();
        _db.Dispose();
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 13, 9, 0, 0, TimeSpan.Zero);

    private static async Task Eventually(Func<bool> condition, string because = "")
    {
        // A DEADLINE, not a fixed number of attempts. The budgets here used to be 2, 5, 10 and 15
        // seconds with no principle behind the numbers, and the 2 second ones failed on CI: run 103
        // and run 107 on 2026-09-19 went red on commits that changed only markdown, because the
        // runner was a shared host that also ran production, and a contended machine needs longer to
        // reach a turn boundary than this dev host does. A generous ceiling costs nothing when the
        // condition is met quickly, since the loop exits the moment it is true; it only matters on a
        // machine slow enough that the old limit was measuring the machine rather than the behaviour.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        condition().Should().BeTrue(because);
    }

    /// <summary>An ordinary in-copy change, which parks on the user because every change asks.</summary>
    private static AgentToolInput InCopyWrite =>
        AgentToolInput.FromStrings(new Dictionary<string, string>
        {
            ["file_path"] = "/workspaces/w1/readme.md",
        });

    private LiveAgentSession Start() =>
        _registry.Start(new SessionStartRequest("w1", "/workspaces/w1", "write the readme"));

    // ---- session start ---------------------------------------------------------------------------

    [Fact]
    public async Task A_CLI_that_announces_itself_records_a_successful_start_with_how_long_it_took()
    {
        var session = Start();
        _runner.Emit(new SessionRegistered(Now, "cli-1", "sonnet"));

        await Eventually(() => _telemetry.Events.Count > 0, "the start should have been measured");

        var started = _telemetry.Single(ProductEventKind.SessionStart);
        started.Outcome.Should().Be(ProductEventOutcome.Succeeded);
        started.Workspace.Should().Be(WorkspaceHash.Of("w1"));
        started.DurationMs.Should().NotBeNull("the wait between pressing send and the CLI answering is the point");
        started.DurationMs.Should().BeGreaterThanOrEqualTo(0);
        session.WorkspaceId.Should().Be("w1");
    }

    [Fact]
    public async Task A_session_that_ends_before_the_CLI_ever_answers_records_a_FAILED_start()
    {
        Start();
        _runner.Finish(AgentSessionState.Failed);

        await Eventually(() => _telemetry.Events.Count > 0);

        _telemetry.Single(ProductEventKind.SessionStart).Outcome.Should().Be(ProductEventOutcome.Failed);
        // And it is NOT also counted as an abort: a session that never started cannot have been aborted.
        _telemetry.Events.Should().NotContain(e => e.Kind == ProductEventKind.SessionAborted);
    }

    [Fact]
    public async Task A_session_that_started_and_then_was_cancelled_records_an_abort_not_a_start_failure()
    {
        Start();
        _runner.Emit(new SessionRegistered(Now, "cli-1", null));
        await Eventually(() => _telemetry.Events.Count == 1);

        _runner.Finish(AgentSessionState.Cancelled);
        await Eventually(() => _telemetry.Events.Count == 2);

        _telemetry.Single(ProductEventKind.SessionStart).Outcome.Should().Be(ProductEventOutcome.Succeeded);
        _telemetry.Single(ProductEventKind.SessionAborted).Outcome.Should().Be(ProductEventOutcome.Cancelled);
    }

    [Fact]
    public async Task A_session_that_completes_normally_is_not_an_abort()
    {
        Start();
        _runner.Emit(new SessionRegistered(Now, "cli-1", null));
        _runner.Finish(AgentSessionState.Completed);

        await Eventually(() => _telemetry.Events.Count == 1);
        await Task.Delay(50);

        _telemetry.Events.Should().ContainSingle()
            .Which.Kind.Should().Be(ProductEventKind.SessionStart);
    }

    // ---- retries ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_turn_whose_document_kept_failing_validation_records_the_attempts_the_harness_burned()
    {
        Start();
        _runner.Emit(new SessionRegistered(Now, "cli-1", null));
        _runner.Emit(new TurnCompleted(Now, "done", IsError: false, "completed", ValidationFailures: 4));

        await Eventually(() => _telemetry.Events.Count == 2);

        var retried = _telemetry.Single(ProductEventKind.TurnRetried);
        retried.Count.Should().Be(4);
        retried.Workspace.Should().Be(WorkspaceHash.Of("w1"));
    }

    [Fact]
    public async Task A_typed_sessions_turn_that_answered_in_prose_is_counted_as_untyped()
    {
        // Decided 2026-09-14: outcome ii is committed, marked and COUNTED, never resent. Read off the
        // document's absence alone: this turn reports plain success on every other field.
        var runner = new ScriptedRunner();
        using var registry = new LiveSessionRegistry(
            runner, new SqliteSessionStore(_db.Factory), _broker, telemetry: _telemetry,
            documentKinds: AgentTurnDocumentSchema.DefaultKinds);
        registry.Start(new SessionStartRequest("w1", "/workspaces/w1", "write the readme"));
        runner.Emit(new SessionRegistered(Now, "cli-1", null));
        runner.Emit(new TurnCompleted(Now, null, IsError: false, "completed"));

        await Eventually(() => _telemetry.Events.Count == 2);

        var untyped = _telemetry.Single(ProductEventKind.TurnUntyped);
        untyped.Count.Should().Be(1);
        untyped.Workspace.Should().Be(WorkspaceHash.Of("w1"));
    }

    [Fact]
    public async Task A_typed_turn_that_did_end_in_a_document_is_not_counted_as_untyped()
    {
        var runner = new ScriptedRunner();
        using var registry = new LiveSessionRegistry(
            runner, new SqliteSessionStore(_db.Factory), _broker, telemetry: _telemetry,
            documentKinds: AgentTurnDocumentSchema.DefaultKinds);
        registry.Start(new SessionStartRequest("w1", "/workspaces/w1", "write the readme"));
        runner.Emit(new SessionRegistered(Now, "cli-1", null));
        runner.Emit(new TurnCompleted(
            Now, null, IsError: false, "completed",
            AgentTurnDocumentReader.Read("""{"version":"1","kind":"answer","message":"56"}""")));

        await Eventually(() => _telemetry.Events.Count == 1);
        await Task.Delay(50);

        _telemetry.Events.Should().ContainSingle().Which.Kind.Should().Be(ProductEventKind.SessionStart);
    }

    [Fact]
    public async Task A_turn_that_needed_no_retry_records_nothing()
    {
        Start();
        _runner.Emit(new SessionRegistered(Now, "cli-1", null));
        _runner.Emit(new TurnCompleted(Now, "done", IsError: false, "completed"));

        await Eventually(() => _telemetry.Events.Count == 1);
        await Task.Delay(50);

        _telemetry.Events.Should().ContainSingle();
    }

    // ---- approvals -------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, ProductEventOutcome.Approved)]
    [InlineData(false, ProductEventOutcome.DeniedByUser)]
    public async Task An_approval_the_user_answered_records_their_decision_and_the_latency(
        bool approved, ProductEventOutcome expected)
    {
        var session = Start();
        _runner.Emit(new SessionRegistered(Now, "cli-1", null));
        await Eventually(() => _telemetry.Events.Count == 1);

        _broker.RegisterSession(session.Id);
        var call = _broker.RequestAsync(
            session.Id, "Write", "t1", InCopyWrite);
        await Eventually(() => _broker.PendingFor(session.Id).Count == 1);

        _broker.Answer(_broker.PendingFor(session.Id)[0].RequestId, approved, "because").Should().BeTrue();
        await call;
        await Eventually(() => _telemetry.Events.Count == 2);

        var resolved = _telemetry.Single(ProductEventKind.ApprovalResolved);
        resolved.Outcome.Should().Be(expected);
        resolved.DurationMs.Should().NotBeNull("the latency is how long the agent sat blocked on a person");
        resolved.Workspace.Should().Be(WorkspaceHash.Of("w1"));
    }

    [Fact]
    public async Task A_refusal_the_studio_imposed_is_never_recorded_as_the_users()
    {
        // The same distinction the transcript makes (backlog 114). A count of "the user denied this"
        // that includes refusals nobody was asked about would misdescribe the gate.
        var session = Start();
        _runner.Emit(new SessionRegistered(Now, "cli-1", null));
        await Eventually(() => _telemetry.Events.Count == 1);

        _broker.RegisterSession(session.Id);
        var call = _broker.RequestAsync(
            session.Id, "Write", "t1", InCopyWrite);
        await Eventually(() => _broker.PendingFor(session.Id).Count == 1);

        // Releasing the session refuses every parked call on the studio's own account.
        _broker.ReleaseSession(session.Id);
        await call;
        await Eventually(() => _telemetry.Events.Count == 2);

        _telemetry.Single(ProductEventKind.ApprovalResolved).Outcome
            .Should().Be(ProductEventOutcome.DeniedByStudio);
    }

    // ---- the switch ------------------------------------------------------------------------------

    [Fact]
    public async Task A_registry_with_no_recorder_wired_measures_nothing_and_still_runs_the_session()
    {
        using var plain = new LiveSessionRegistry(
            new ScriptedRunner(), new SqliteSessionStore(_db.Factory), new AgentApprovalBroker());

        var session = plain.Start(new SessionStartRequest("w2", "/workspaces/w2", "hello"));

        await Task.Delay(50);
        session.Transcript.Should().NotBeEmpty("the conversation is unaffected by telemetry being absent");
        _telemetry.Events.Should().BeEmpty();
    }

    // ---- doubles ---------------------------------------------------------------------------------

    private sealed class RecordingRecorder : IProductEventRecorder
    {
        private readonly List<ProductEvent> _events = [];

        public bool IsEnabled => true;

        public IReadOnlyList<ProductEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return [.. _events];
                }
            }
        }

        public void Record(ProductEvent productEvent)
        {
            lock (_events)
            {
                _events.Add(productEvent);
            }
        }

        public ProductEvent Single(ProductEventKind kind) => Events.Single(e => e.Kind == kind);
    }

    /// <summary>A runner the test drives by hand, one event at a time.</summary>
    private sealed class ScriptedRunner : IAgentSessionRunner
    {
        private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>();

        public IAsyncEnumerable<AgentEvent> RunAsync(
            AgentSessionRequest request, CancellationToken cancellationToken = default) =>
            _events.Reader.ReadAllAsync(cancellationToken);

        public Task<bool> SendAsync(AgentSessionId sessionId, string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public bool Stop(AgentSessionId sessionId)
        {
            Finish(AgentSessionState.Cancelled);
            return true;
        }

        public bool ChangeModel(AgentSessionId sessionId, string? model) => true;

        public void Emit(AgentEvent e) => _events.Writer.TryWrite(e);

        public void Finish(AgentSessionState state)
        {
            Emit(new SessionEnded(Now, state, null));
            _events.Writer.TryComplete();
        }
    }
}
