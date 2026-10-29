using System.Collections.Concurrent;
using System.Threading.Channels;
using AwesomeAssertions;
using Fravashi.Application.Network;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Network;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Testing.Network;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Backlog 229 (plan @55): a turn whose model call failed on the network is waited out and continued, on slice A's
/// scripted network and fake clock. The failed turn every test plays is the REAL one: the pinned CLI's recorded output
/// against an unresolvable API host, parsed by the production parser. The registry runs over the real SQLite store.
/// </summary>
public sealed class NetworkTurnWaitTests : IDisposable
{
    private static readonly NetworkEndpoint Api = new("api.anthropic.com", 443);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-network-wait-" + Guid.NewGuid().ToString("N"));

    private readonly SqliteSessionStore _store;
    private readonly AgentApprovalBroker _broker = new(TimeSpan.FromSeconds(5));
    private readonly FakeNetworkClock _clock = new();
    private readonly ScriptedRunner _runner;

    public NetworkTurnWaitTests()
    {
        Directory.CreateDirectory(_directory);
        var factory = new SqliteConnectionFactory(
            new SqliteStoreOptions(Path.Combine(_directory, "fravashi.db"), Pooling: false));
        SqliteSchema.EnsureCreated(factory);
        _store = new SqliteSessionStore(factory);
        _runner = new ScriptedRunner(_clock);
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

    // ---- the doubles -----------------------------------------------------------------------

    /// <summary>A runner whose every run is its own event stream; it records what it was asked to run and to send.</summary>
    private sealed class ScriptedRunner(FakeNetworkClock clock) : IAgentSessionRunner
    {
        private Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>();

        public ConcurrentQueue<AgentSessionRequest> Runs { get; } = new();

        public ConcurrentQueue<(string Text, DateTimeOffset At)> Sent { get; } = new();

        /// <summary>Called after each send, so a test can answer a continued turn.</summary>
        public Action<string>? OnSent { get; set; }

        public IAsyncEnumerable<AgentEvent> RunAsync(AgentSessionRequest request, CancellationToken cancellationToken = default)
        {
            _events = Channel.CreateUnbounded<AgentEvent>();
            Runs.Enqueue(request);
            return _events.Reader.ReadAllAsync(cancellationToken);
        }

        public Task<bool> SendAsync(AgentSessionId sessionId, string text, CancellationToken cancellationToken = default)
        {
            Sent.Enqueue((text, clock.GetUtcNow()));
            OnSent?.Invoke(text);
            return Task.FromResult(true);
        }

        public bool Stop(AgentSessionId sessionId)
        {
            End(new SessionEnded(clock.GetUtcNow(), AgentSessionState.Cancelled, null));
            return true;
        }

        public bool ChangeModel(AgentSessionId sessionId, string? model) => false;

        public void Emit(AgentEvent e) => _events.Writer.TryWrite(e);

        public void End(SessionEnded ended)
        {
            _events.Writer.TryWrite(ended);
            _events.Writer.TryComplete();
        }
    }

    /// <summary>A pause that moves the fake clock instead of waiting, so a five-minute outage takes no real time.</summary>
    private Task Advance(TimeSpan pause, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _clock.Advance(pause);
        return Task.CompletedTask;
    }

    /// <summary>A pause that never ends by itself, so the wait holds still while a test acts on it.</summary>
    private static Task Hold(TimeSpan pause, CancellationToken cancellationToken) =>
        Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

    private LiveSessionRegistry Registry(
        NetworkScript script, Func<TimeSpan, CancellationToken, Task>? delay = null, ScriptedConnector? connector = null) =>
        new(
            _runner,
            _store,
            _broker,
            clock: _clock.GetUtcNow,
            reachability: new NetworkReachability(Api, connector ?? new ScriptedConnector(script), _clock),
            delay: delay ?? Advance);

    private static IEnumerable<AgentEvent> RecordedTurn(string file, FakeNetworkClock clock)
    {
        var parser = new ClaudeStreamJsonParser(clock.GetUtcNow);
        var path = Path.Combine(AppContext.BaseDirectory, "Sessions", "Fixtures", "Network", file);

        // The init line is the recording's own; the session already registered under the test's id.
        return File.ReadAllLines(path).SelectMany(parser.Parse).Where(e => e is not SessionRegistered);
    }

    private const string NameNotResolved = "claude-api-error-name-not-resolved.jsonl";

    private async Task<LiveAgentSession> StartRegistered(LiveSessionRegistry registry)
    {
        var session = registry.Start(new SessionStartRequest("w1", "/workspaces/w1", "do the thing"));
        await Eventually(() => _runner.Runs.Count == 1);
        _runner.Emit(new SessionRegistered(_clock.GetUtcNow(), "cli-1", "claude-opus"));
        await Eventually(() => session.ProviderSessionId == "cli-1");
        return session;
    }

    private void FailTurnOnTheNetwork()
    {
        foreach (var e in RecordedTurn(NameNotResolved, _clock))
        {
            _runner.Emit(e);
        }
    }

    private static TurnCompleted Succeeded(DateTimeOffset at) => new(at, "done", false, "completed");

    private static async Task Eventually(Func<bool> condition)
    {
        // A deadline, as everywhere in this suite (backlog 107): a contended runner needs longer, never fewer checks.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(10); // poll interval inside a wait helper (backlog 107)
        }

        condition().Should().BeTrue();
    }

    private static IEnumerable<string> SystemLines(LiveAgentSession session) =>
        session.Transcript.OfType<MessageEmitted>().Where(m => m.Role == TranscriptRole.System).Select(m => m.Text);

    private static string Expected(NetworkFailureKind kind) =>
        NetworkWait.ContinuePrompt(new NetworkFailure(kind, ""), Api.Host);

    // ---- the four faults ---------------------------------------------------------------------

    public static TheoryData<string, int> Faults() => new()
    {
        // The fault, and the earliest second after the failure at which the network answers again.
        { "F1", 60 },
        { "F2", 45 },
        { "F3", 30 },
        { "F4", 300 },
    };

    private NetworkScript Script(string fault) => fault switch
    {
        "F1" => NetworkScript.F1(_clock),
        "F2" => NetworkScript.F2(_clock),
        "F3" => NetworkScript.F3(_clock),
        "F4" => NetworkScript.F4(_clock),
        _ => throw new ArgumentOutOfRangeException(nameof(fault)),
    };

    [Theory]
    [MemberData(nameof(Faults))]
    public async Task A_turn_that_failed_on_the_network_is_continued_once_the_probe_answers_and_not_before(string fault, int backAfterSeconds)
    {
        var failedAt = _clock.GetUtcNow();
        var script = Script(fault);
        var connector = new ScriptedConnector(script);
        using var registry = Registry(script, connector: connector);
        var session = await StartRegistered(registry);

        // The script's first call is the CLI's own request, the one the fault cut off (F2's reset after write, F3 and
        // F4's hung call); the probe's calls come after it.
        script.Next();
        FailTurnOnTheNetwork();

        await Eventually(() => !_runner.Sent.IsEmpty);
        await Eventually(() => session.TurnInFlight);
        _runner.Sent.Should().ContainSingle("one continue, never a resend of the failed turn");
        var (text, at) = _runner.Sent.Single();
        text.Should().Be(Expected(NetworkFailureKind.NameNotResolved));
        at.Should().BeOnOrAfter(failedAt + TimeSpan.FromSeconds(backAfterSeconds), "nothing is sent before the probe answers");
        connector.Calls.Count.Should().BeGreaterThan(1, "the probe was asked again, with backoff, while it failed");
        connector.Calls.Zip(connector.Calls.Skip(1), (a, b) => b.At - a.At)
            .Should().OnlyContain(gap => gap >= TimeSpan.FromSeconds(5) && gap <= TimeSpan.FromSeconds(65));

        // The transcript says what happened, then what the studio did about it, in that order.
        var lines = session.Transcript.OfType<MessageEmitted>().Select(m => (m.Role, m.Text)).ToList();
        var dropped = lines.FindIndex(l => l.Role == TranscriptRole.System
            && l.Text == NetworkWait.DroppedNotice(new NetworkFailure(NetworkFailureKind.NameNotResolved, ""), Api.Host));
        var back = lines.FindIndex(l => l.Role == TranscriptRole.System && l.Text == NetworkWait.BackNotice(Api.Host));
        var prompt = lines.FindIndex(l => l.Role == TranscriptRole.StudioPrompt && l.Text == text);
        dropped.Should().BeGreaterThan(-1);
        back.Should().BeGreaterThan(dropped);
        prompt.Should().BeGreaterThan(back);
        session.NetworkWait.Should().BeNull();
        session.IsRunning.Should().BeTrue();
        _runner.Runs.Should().ContainSingle("a live process is continued in place, not restarted");
    }

    [Fact]
    public async Task While_it_waits_the_session_says_since_when_and_what_failed()
    {
        var script = NetworkScript.F4(_clock);
        using var registry = Registry(script, delay: Hold);
        var session = await StartRegistered(registry);
        var failedAt = _clock.GetUtcNow();

        FailTurnOnTheNetwork();

        await Eventually(() => session.NetworkWait?.Probe is not null);
        var wait = session.NetworkWait!;
        wait.Since.Should().Be(failedAt);
        wait.Host.Should().Be("api.anthropic.com");
        wait.Failure.Kind.Should().Be(NetworkFailureKind.NameNotResolved);
        wait.ProcessEnded.Should().BeFalse();
        wait.Words.Should().Be(
            $"Waiting for the network since {failedAt.ToLocalTime():HH:mm}: could not reach api.anthropic.com: name not resolved. "
            + "Fravashi continues this conversation by itself when it is back.");
        wait.Probe!.Reachable.Should().BeFalse();
        SystemLines(session).Should().Contain(
            "The network dropped while the agent was working (could not reach api.anthropic.com: name not resolved). "
            + "Fravashi will continue when it is back.");
        _runner.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task The_agent_clis_own_retries_are_shown_while_they_run_and_never_stored()
    {
        using var registry = Registry(NetworkScript.F1(_clock), delay: Hold);
        var session = await StartRegistered(registry);
        _runner.Emit(new TurnActivity(_clock.GetUtcNow(), AgentTurnPhase.Submitted));
        var retries = RecordedTurn(NameNotResolved, _clock).OfType<ApiRetrying>().ToList();

        retries.Select(r => r.Attempt).Should().Equal(Enumerable.Range(1, 10));
        _runner.Emit(retries[2]);

        await Eventually(() => session.Retrying is { Attempt: 3, MaxRetries: 10 });
        _store.Transcript(session.Id).OfType<ApiRetrying>().Should().BeEmpty();
    }

    // ---- an ended process ------------------------------------------------------------------

    [Fact]
    public async Task A_process_that_exits_with_the_network_ends_resumable_and_is_resumed_with_its_conversation()
    {
        var failedAt = _clock.GetUtcNow();
        using var registry = Registry(NetworkScript.F3(_clock));
        var session = await StartRegistered(registry);

        // The CLI's own words on stderr, as the runner reports a failed exit.
        _runner.End(new SessionEnded(_clock.GetUtcNow(), AgentSessionState.Failed,
            "API Error: Connection error. getaddrinfo EAI_AGAIN api.anthropic.com"));

        await Eventually(() => _runner.Runs.Count == 2);
        var ended = _store.Transcript(session.Id).OfType<SessionEnded>().First();
        ended.Resumable.Should().BeTrue();
        ended.FailureReason.Should().Contain("could not reach api.anthropic.com: name not resolved");

        var resumed = _runner.Runs.Last();
        resumed.SessionId.Should().Be(session.Id);
        resumed.ResumeSessionId.Should().Be("cli-1", "the dead process is resumed with --resume on its conversation");
        resumed.Prompt.Should().Be(Expected(NetworkFailureKind.NameNotResolved));
        _clock.GetUtcNow().Should().BeOnOrAfter(failedAt + TimeSpan.FromSeconds(30));
        session.IsRunning.Should().BeTrue();
        session.Transcript.OfType<MessageEmitted>()
            .Should().Contain(m => m.Role == TranscriptRole.StudioPrompt && m.Text == resumed.Prompt);
    }

    [Fact]
    public async Task A_process_that_exits_after_a_network_failed_turn_is_judged_by_that_turn()
    {
        using var registry = Registry(NetworkScript.F1(_clock), delay: Hold);
        var session = await StartRegistered(registry);
        FailTurnOnTheNetwork();
        await Eventually(() => session.NetworkWait is not null);

        _runner.End(new SessionEnded(_clock.GetUtcNow(), AgentSessionState.Failed, "The agent CLI exited with code 1."));

        await Eventually(() => session.IsResumable);
        await Eventually(() => session.NetworkWait is { ProcessEnded: true });
        _runner.Runs.Should().ContainSingle("the same wait resumes the process once the network is back, not before");
    }

    [Fact]
    public async Task A_resume_the_guards_refuse_is_said_and_leaves_the_conversation_resumable()
    {
        var hold = new TaskCompletionSource();
        using var registry = Registry(
            NetworkScript.F1(_clock),
            delay: async (pause, token) =>
            {
                await hold.Task.WaitAsync(token);
                _clock.Advance(pause);
            });
        var session = await StartRegistered(registry);
        _runner.End(new SessionEnded(_clock.GetUtcNow(), AgentSessionState.Failed, "getaddrinfo ENOTFOUND api.anthropic.com"));
        await Eventually(() => session.NetworkWait is { ProcessEnded: true });

        // Somebody started another conversation in the same folder meanwhile: one per folder, as for any start.
        registry.Start(new SessionStartRequest("w1", "/workspaces/w1", "something else"));
        await Eventually(() => _runner.Runs.Count == 2);
        hold.SetResult();

        await Eventually(() => SystemLines(session).Any(l => l.StartsWith("The network is back, but this conversation could not be resumed:", StringComparison.Ordinal)));
        _runner.Runs.Should().HaveCount(2);
        session.IsResumable.Should().BeTrue();
    }

    // ---- what is not a network failure -----------------------------------------------------

    public static TheoryData<string> NotTheNetwork() => new()
    {
        "API Error: 401 {\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}",
        "API Error: 400 {\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"prompt is too long\"}}",
        "API Error: Repeated 529 Overloaded errors",
        "Credit balance is too low",

        // The agent's own prose, last in a turn that failed for another reason: it names network errors, and it is
        // not the CLI's to decide by (the CLI marks its own API error message, and this one is not marked).
        "Your client fails with connect ECONNREFUSED 10.0.0.5:5432, then getaddrinfo EAI_AGAIN and a socket hang up.",
    };

    [Theory]
    [MemberData(nameof(NotTheNetwork))]
    public async Task A_turn_that_failed_for_any_other_reason_ends_as_it_did_before(string said)
    {
        var connector = new ScriptedConnector(NetworkScript.F1(_clock));
        using var registry = Registry(NetworkScript.F1(_clock), connector: connector);
        var session = await StartRegistered(registry);

        _runner.Emit(new MessageEmitted(_clock.GetUtcNow(), TranscriptRole.Assistant, said));
        _runner.Emit(new TurnCompleted(_clock.GetUtcNow(), said, true, "api_error"));
        await Eventually(() => session.Transcript.OfType<TurnCompleted>().Any());
        await Task.Delay(100);

        session.NetworkWait.Should().BeNull();
        connector.Calls.Should().BeEmpty("nothing is probed for a failure that is not the network's");
        _runner.Sent.Should().BeEmpty();
        SystemLines(session).Should().BeEmpty();
    }

    [Fact]
    public async Task A_process_that_exits_for_another_reason_ends_failed_and_not_resumable_as_before()
    {
        using var registry = Registry(NetworkScript.F1(_clock));
        var session = await StartRegistered(registry);

        _runner.End(new SessionEnded(_clock.GetUtcNow(), AgentSessionState.Failed, "Invalid API key - Please run /login"));

        await Eventually(() => !session.IsRunning);
        session.IsResumable.Should().BeFalse();
        session.FailureReason.Should().Be("Invalid API key - Please run /login");
        session.NetworkWait.Should().BeNull();
        _runner.Runs.Should().ContainSingle();
    }

    [Fact]
    public async Task A_budget_ending_is_never_a_network_failure_whatever_it_says()
    {
        using var registry = Registry(NetworkScript.F1(_clock));
        var session = await StartRegistered(registry);

        _runner.Emit(new TurnCompleted(_clock.GetUtcNow(), null, true, AgentTurnEnding.BudgetExhausted, ErrorText: "Reached maximum budget (ECONNRESET)"));
        await Eventually(() => session.Transcript.OfType<TurnCompleted>().Any());
        await Task.Delay(100);

        session.NetworkWait.Should().BeNull();
        _runner.Sent.Should().BeEmpty();
    }

    // ---- the bounds ------------------------------------------------------------------------

    [Fact]
    public async Task After_thirty_minutes_without_the_network_it_stops_waiting_and_sends_nothing()
    {
        var down = new NetworkScript(_clock, new NetworkPhase(NetworkBehaviour.NoRoute));
        using var registry = Registry(down);
        var session = await StartRegistered(registry);
        var failedAt = _clock.GetUtcNow();

        FailTurnOnTheNetwork();

        await Eventually(() => SystemLines(session).Contains(NetworkWait.GaveUpNotice(processEnded: false)));
        _clock.GetUtcNow().Should().BeOnOrAfter(failedAt + NetworkWait.GiveUpAfter);
        _clock.GetUtcNow().Should().BeBefore(failedAt + NetworkWait.GiveUpAfter + TimeSpan.FromSeconds(70));
        _runner.Sent.Should().BeEmpty();
        session.NetworkWait.Should().BeNull();
        session.IsRunning.Should().BeTrue("a live process is not killed to offer a button; the next message continues it");
    }

    [Fact]
    public async Task An_ended_process_stays_resumable_when_the_wait_gives_up()
    {
        var down = new NetworkScript(_clock, new NetworkPhase(NetworkBehaviour.NameFails));
        using var registry = Registry(down);
        var session = await StartRegistered(registry);

        _runner.End(new SessionEnded(_clock.GetUtcNow(), AgentSessionState.Failed, "getaddrinfo EAI_AGAIN api.anthropic.com"));

        await Eventually(() => SystemLines(session).Contains(NetworkWait.GaveUpNotice(processEnded: true)));
        _runner.Runs.Should().ContainSingle();
        session.IsResumable.Should().BeTrue();
        registry.CanContinue(session.Id).Should().BeTrue();
    }

    [Fact]
    public async Task It_continues_by_itself_at_most_three_times_in_a_row()
    {
        var healthy = new NetworkScript(_clock, new NetworkPhase(NetworkBehaviour.Healthy));
        using var registry = Registry(healthy);
        var session = await StartRegistered(registry);

        // Every continued turn fails on the network again.
        _runner.OnSent = _ => FailTurnOnTheNetwork();
        FailTurnOnTheNetwork();

        var failure = new NetworkFailure(NetworkFailureKind.NameNotResolved, "");
        await Eventually(() => SystemLines(session).Contains(NetworkWait.TooManyNotice(failure, Api.Host, processEnded: false)));
        await Task.Delay(100);
        _runner.Sent.Should().HaveCount(NetworkWait.MaxContinues);
        session.NetworkWait.Should().BeNull();
    }

    [Fact]
    public async Task A_turn_that_succeeds_resets_the_count()
    {
        var healthy = new NetworkScript(_clock, new NetworkPhase(NetworkBehaviour.Healthy));
        using var registry = Registry(healthy);
        var session = await StartRegistered(registry);
        var failures = 0;
        _runner.OnSent = _ =>
        {
            // Two failures, a success, then two more failures: never three in a row.
            if (++failures == 2)
            {
                _runner.Emit(Succeeded(_clock.GetUtcNow()));
                _runner.Emit(new TurnActivity(_clock.GetUtcNow(), AgentTurnPhase.Submitted));
            }

            if (failures < 5)
            {
                FailTurnOnTheNetwork();
            }
        };
        FailTurnOnTheNetwork();

        await Eventually(() => _runner.Sent.Count == 5);
        await Task.Delay(100);
        SystemLines(session).Should().NotContain(l => l.Contains("automatic attempts", StringComparison.Ordinal));
    }

    // ---- what the person can do meanwhile --------------------------------------------------

    [Fact]
    public async Task Stop_while_waiting_ends_the_conversation_and_sends_nothing()
    {
        using var registry = Registry(NetworkScript.F4(_clock), delay: Hold);
        var session = await StartRegistered(registry);
        FailTurnOnTheNetwork();
        await Eventually(() => session.NetworkWait?.Probe is not null);

        registry.Stop(session.Id).Should().BeTrue();

        await Eventually(() => !session.IsRunning);
        session.State.Should().Be(AgentSessionState.Cancelled);
        session.NetworkWait.Should().BeNull();
        _clock.Advance(TimeSpan.FromHours(1));
        await Task.Delay(100);
        _runner.Sent.Should().BeEmpty();
        _runner.Runs.Should().ContainSingle();
    }

    [Fact]
    public async Task Stop_while_an_ended_process_waits_ends_only_the_wait()
    {
        using var registry = Registry(NetworkScript.F4(_clock), delay: Hold);
        var session = await StartRegistered(registry);
        _runner.End(new SessionEnded(_clock.GetUtcNow(), AgentSessionState.Failed, "connect ETIMEDOUT 160.79.104.10:443"));
        await Eventually(() => session.NetworkWait is { ProcessEnded: true });

        registry.Stop(session.Id).Should().BeTrue();

        session.NetworkWait.Should().BeNull();
        SystemLines(session).Should().Contain(NetworkWait.StoppedNotice);
        session.IsResumable.Should().BeTrue();
        await Task.Delay(100);
        _runner.Runs.Should().ContainSingle();
    }

    [Fact]
    public async Task A_message_typed_while_waiting_is_the_continuation_and_the_studio_adds_none()
    {
        using var registry = Registry(NetworkScript.F1(_clock), delay: Hold);
        var session = await StartRegistered(registry);
        FailTurnOnTheNetwork();
        await Eventually(() => session.NetworkWait is not null);

        (await registry.SendAsync(session.Id, "go on")).Should().BeTrue();

        session.NetworkWait.Should().BeNull();
        await Task.Delay(100);
        _runner.Sent.Select(s => s.Text).Should().Equal("go on");
    }

    [Fact]
    public async Task Retry_now_continues_only_when_the_probe_answers()
    {
        using var registry = Registry(NetworkScript.F1(_clock), delay: Hold);
        var session = await StartRegistered(registry);
        FailTurnOnTheNetwork();
        await Eventually(() => session.NetworkWait?.Probe is not null);

        (await registry.RetryNetworkNowAsync(session.Id)).Should().BeFalse("the name still does not resolve");
        _runner.Sent.Should().BeEmpty();
        session.NetworkWait.Should().NotBeNull();

        _clock.Advance(TimeSpan.FromSeconds(61));
        (await registry.RetryNetworkNowAsync(session.Id)).Should().BeTrue();

        _runner.Sent.Select(s => s.Text).Should().Equal(Expected(NetworkFailureKind.NameNotResolved));
        session.NetworkWait.Should().BeNull();
        (await registry.RetryNetworkNowAsync(session.Id)).Should().BeFalse("there is nothing left to retry");
        _runner.Sent.Should().ContainSingle();
    }

    // ---- what is probed ---------------------------------------------------------------------

    [Fact]
    public void The_probe_asks_about_the_model_api_unless_the_broker_carries_the_runtime()
    {
        SessionsServiceCollectionExtensions.AgentModelEndpoint(Application.AgentRuntimes.BrokerOptions.Disabled)
            .Should().Be(new NetworkEndpoint("api.anthropic.com", 443));

        // Enabled with no base URL is an inert broker, and the CLI then calls the model API itself.
        SessionsServiceCollectionExtensions.AgentModelEndpoint(new Application.AgentRuntimes.BrokerOptions { Enabled = true })
            .Should().Be(new NetworkEndpoint("api.anthropic.com", 443));

        SessionsServiceCollectionExtensions.AgentModelEndpoint(
                new Application.AgentRuntimes.BrokerOptions { Enabled = true, BaseUrl = "https://broker.example.test" })
            .Should().Be(new NetworkEndpoint("broker.example.test", 443));
        SessionsServiceCollectionExtensions.AgentModelEndpoint(
                new Application.AgentRuntimes.BrokerOptions { Enabled = true, BaseUrl = " http://broker.corp.internal:8080/v1 " })
            .Should().Be(new NetworkEndpoint("broker.corp.internal", 8080));
    }

    [Fact]
    public async Task Without_a_probe_a_failed_turn_ends_exactly_as_before()
    {
        using var registry = new LiveSessionRegistry(_runner, _store, _broker, clock: _clock.GetUtcNow);
        var session = await StartRegistered(registry);

        FailTurnOnTheNetwork();
        await Eventually(() => session.Transcript.OfType<TurnCompleted>().Any());
        await Task.Delay(100);

        session.NetworkWait.Should().BeNull();
        _runner.Sent.Should().BeEmpty();
        SystemLines(session).Should().BeEmpty();
    }
}
