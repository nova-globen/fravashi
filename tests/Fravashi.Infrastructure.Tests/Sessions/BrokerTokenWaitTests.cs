using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AwesomeAssertions;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.AgentRuntimes.Broker;
using Fravashi.Application.Network;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.AgentRuntimes;
using Fravashi.Infrastructure.AgentRuntimes.Broker;
using Fravashi.Infrastructure.Network;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Tests.AgentRuntimes;
using Fravashi.Testing.Network;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Backlog 235 (plan @60): a conversation never runs on a broker token it knows is stale, and a turn the broker refused
/// for an expired token waits for a fresh one and continues, on slice A's scripted network and fake clock. The
/// registry, the SQLite store, the token client, the credential store and the parser are the production ones; the
/// refused turn is the REAL pinned CLI's recorded output against a stand-in broker answering 401 expired. Only the
/// agent process is a double, and it records which bearer the credential store would hand a restarted child.
/// </summary>
public sealed class BrokerTokenWaitTests : IDisposable
{
    private const string Secret = "SECRET-bearer-value";
    private static readonly NetworkEndpoint BrokerHost = new("broker.example", 443);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-broker-wait-" + Guid.NewGuid().ToString("N"));

    private readonly SqliteSessionStore _store;
    private readonly AgentApprovalBroker _approvals = new(TimeSpan.FromSeconds(5));
    private readonly FakeNetworkClock _clock = new();
    private readonly FakeSecretStore _secrets = new();
    private readonly BrokerOptions _options = new() { Enabled = true, BaseUrl = "https://broker.example" };
    private readonly BrokerTokenHealth _health;
    private readonly Lines _log = new();
    private readonly ScriptedRunner _runner;
    private int _minted;

    public BrokerTokenWaitTests()
    {
        Directory.CreateDirectory(_directory);
        var factory = new SqliteConnectionFactory(
            new SqliteStoreOptions(Path.Combine(_directory, "fravashi.db"), Pooling: false));
        SqliteSchema.EnsureCreated(factory);
        _store = new SqliteSessionStore(factory);
        _health = new BrokerTokenHealth(_clock);
        var credentials = new AgentRuntimeCredentialStore(_secrets, _options);
        _runner = new ScriptedRunner(_clock, () =>
            credentials.EnvironmentFor(AgentTool.Claude).GetValueOrDefault("ANTHROPIC_AUTH_TOKEN"));
        _secrets.Set(BrokerReferences.IdentitySecretRef,
            CredentialEnvelope.Serialize(new StoredCredential("idp-access-token", null, null, AuthMethods.Device)));
    }

    public void Dispose()
    {
        _approvals.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- the doubles -----------------------------------------------------------------------

    /// <summary>
    /// A runner whose every run is its own event stream. A credential restart records the bearer the credential store
    /// hands a new child at that moment, which is exactly what <c>ClaudeSessionRunner.BuildStartInfo</c> reads.
    /// </summary>
    private sealed class ScriptedRunner(FakeNetworkClock clock, Func<string?> bearer) : IAgentSessionRunner
    {
        private Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>();

        public ConcurrentQueue<AgentSessionRequest> Runs { get; } = new();

        public ConcurrentQueue<string> Sent { get; } = new();

        /// <summary>The bearer each credential restart would hand the new child.</summary>
        public ConcurrentQueue<string?> Restarts { get; } = new();

        /// <summary>Restarts and sends, in the order they happened.</summary>
        public ConcurrentQueue<string> Order { get; } = new();

        public IAsyncEnumerable<AgentEvent> RunAsync(AgentSessionRequest request, CancellationToken cancellationToken = default)
        {
            _events = Channel.CreateUnbounded<AgentEvent>();
            Runs.Enqueue(request);
            return _events.Reader.ReadAllAsync(cancellationToken);
        }

        public Task<bool> SendAsync(AgentSessionId sessionId, string text, CancellationToken cancellationToken = default)
        {
            Sent.Enqueue(text);
            Order.Enqueue("send");
            return Task.FromResult(true);
        }

        public bool Stop(AgentSessionId sessionId)
        {
            End(new SessionEnded(clock.GetUtcNow(), AgentSessionState.Cancelled, null));
            return true;
        }

        public bool ChangeModel(AgentSessionId sessionId, string? model) => false;

        public bool RefreshCredentials(AgentSessionId sessionId)
        {
            Restarts.Enqueue(bearer());
            Order.Enqueue("restart");
            return true;
        }

        public void Emit(AgentEvent e) => _events.Writer.TryWrite(e);

        public void End(SessionEnded ended)
        {
            _events.Writer.TryWrite(ended);
            _events.Writer.TryComplete();
        }
    }

    /// <summary>A thread-safe logger keeping every formatted line with its exception, so no line can hide a token.</summary>
    private sealed class Lines : ILogger<LiveSessionRegistry>, ILogger<BrokerTokenService>
    {
        public ConcurrentQueue<string> All { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            All.Enqueue(formatter(state, exception) + " " + exception);
    }

    private void SeedToken(string tokenId, TimeSpan expiresIn)
    {
        _secrets.Set(BrokerOptions.DefaultUserTokenSecretRef, $"tok-{Secret}-{tokenId}");
        _secrets.Set(BrokerReferences.TokenMetadataSecretRef,
            new BrokerTokenMetadata("oidc:abc", tokenId, _clock.GetUtcNow(), _clock.GetUtcNow() + expiresIn).Serialize());
    }

    private HttpResponseMessage Mints(HttpRequestMessage request)
    {
        var n = Interlocked.Increment(ref _minted);
        var body = JsonSerializer.Serialize(new
        {
            token = $"tok-{Secret}-{n}",
            tokenId = $"jti-{n}",
            expiresAt = _clock.GetUtcNow().AddHours(1),
            subject = "oidc:abc",
        });
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static Func<HttpRequestMessage, HttpResponseMessage> Refuses(string error) =>
        _ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent($"{{\"error\":\"{error}\",\"subject\":\"oidc:abc\"}}", Encoding.UTF8, "application/json"),
        };

    private BrokerTokenService Tokens(ScriptedHttpHandler handler) =>
        new(_secrets, _options, new HttpClient(handler), _clock, _log, _health);

    /// <summary>The pauses the waits asked for, each moving the fake clock, with what the session's banner said then.</summary>
    private readonly ConcurrentQueue<(TimeSpan Pause, string? Words)> _pauses = new();

    private LiveSessionRegistry Registry(
        NetworkScript probe, BrokerTokenService tokens, bool applies = true, Func<LiveAgentSession?>? watched = null) =>
        new(
            _runner,
            _store,
            _approvals,
            clock: _clock.GetUtcNow,
            logger: _log,
            reachability: new NetworkReachability(BrokerHost, new ScriptedConnector(probe), _clock),
            delay: (pause, token) =>
            {
                token.ThrowIfCancellationRequested();
                _pauses.Enqueue((pause, watched?.Invoke()?.NetworkWait?.Words));
                _clock.Advance(pause);
                return Task.CompletedTask;
            },
            credentials: new BrokerCredentialRenewal(() => tokens, _health, applies));

    private static NetworkScript DownFor(FakeNetworkClock clock, TimeSpan down) =>
        new(clock, new NetworkPhase(NetworkBehaviour.NoRoute, For: down), new NetworkPhase(NetworkBehaviour.Healthy));

    private static NetworkScript Up(FakeNetworkClock clock) => new(clock, new NetworkPhase(NetworkBehaviour.Healthy));

    private async Task<LiveAgentSession> StartRegistered(LiveSessionRegistry registry)
    {
        var session = registry.Start(new SessionStartRequest("w1", "/workspaces/w1", "do the thing"));
        await Eventually(() => _runner.Runs.Count == 1);
        _runner.Emit(new SessionRegistered(_clock.GetUtcNow(), "cli-1", "claude-opus"));
        await Eventually(() => session.ProviderSessionId == "cli-1");
        return session;
    }

    /// <summary>The first turn of a recording, through the production parser, without the recording's own init line.</summary>
    private IEnumerable<AgentEvent> RecordedTurn(string file)
    {
        var parser = new ClaudeStreamJsonParser(_clock.GetUtcNow);
        var path = Path.Combine(AppContext.BaseDirectory, "Sessions", "Fixtures", "Network", file);
        var lines = File.ReadAllLines(path);
        var end = Array.FindIndex(lines, l => l.Contains("\"type\":\"result\"", StringComparison.Ordinal));
        return lines.Take(end + 1).SelectMany(parser.Parse).Where(e => e is not SessionRegistered);
    }

    private const string TokenExpired = "claude-api-error-broker-token-expired.jsonl";
    private const string NameNotResolved = "claude-api-error-name-not-resolved.jsonl";

    private void Play(string file)
    {
        foreach (var e in RecordedTurn(file))
        {
            _runner.Emit(e);
        }
    }

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

    /// <summary>The token's value is in no transcript event, no session words, no banner and no log line.</summary>
    private void NothingLeaked(LiveAgentSession session)
    {
        foreach (var e in session.Transcript.Concat(_store.Transcript(session.Id)))
        {
            e.ToString().Should().NotContain(Secret, "a transcript must never carry the token");
        }

        (session.FailureReason ?? string.Empty).Should().NotContain(Secret);
        foreach (var (_, words) in _pauses)
        {
            (words ?? string.Empty).Should().NotContain(Secret);
        }

        foreach (var line in _log.All)
        {
            line.Should().NotContain(Secret, "a log line must never carry the token");
        }
    }

    // ---- the CLI's own words -----------------------------------------------------------------

    [Fact]
    public void The_recorded_broker_refusal_is_a_refused_token_and_not_a_network_failure()
    {
        var events = RecordedTurn(TokenExpired).ToList();
        var turn = events.OfType<TurnCompleted>().Single();
        var cliSaid = events.OfType<ApiErrorReported>().Single().Text;

        cliSaid.Should().Be("Failed to authenticate. API Error: 401 {\"error\":\"expired\"}");
        CredentialTurnFailure.Refused(turn, cliSaid).Should().BeTrue();
        NetworkTurnFailure.Of(turn, cliSaid).IsNetwork.Should().BeFalse("a refusal is never the network");

        // The network recording is not a refusal, and neither is the agent's own prose about a 401.
        var network = RecordedTurn(NameNotResolved).ToList();
        CredentialTurnFailure.Refused(
            network.OfType<TurnCompleted>().Single(), network.OfType<ApiErrorReported>().Single().Text).Should().BeFalse();
        CredentialTurnFailure.Refused(turn with { IsError = false }, cliSaid).Should().BeFalse();
        CredentialTurnFailure.Refused(turn, cliApiError: null).Should().BeFalse("the result string is never read");
        CredentialTurnFailure.Refused(turn, "API Error: 4010 something else").Should().BeFalse();
    }

    // ---- a turn refused for an expired token -----------------------------------------------

    [Fact]
    public async Task A_turn_refused_for_an_expired_token_waits_through_the_outage_and_continues_on_a_fresh_token()
    {
        SeedToken("jti-old", TimeSpan.FromSeconds(30));
        LiveAgentSession? session = null;
        var brokerDown = DownFor(_clock, TimeSpan.FromSeconds(90));
        using var registry = Registry(Up(_clock), Tokens(new ScriptedHttpHandler(brokerDown, Mints)), watched: () => session);
        session = await StartRegistered(registry);

        // The process started on jti-old; it expires, and the next model call is refused by the broker.
        _clock.Advance(TimeSpan.FromMinutes(1));
        Play(TokenExpired);

        await Eventually(() => _runner.Sent.Count == 1);

        // 15 s doubling while the broker is down until 90 s (attempts at 60 and 75 s fail; 105 s mints), never a tight loop.
        _pauses.Select(p => p.Pause.TotalSeconds).Should().Equal(15, 30);
        _pauses.Select(p => p.Words).Should().AllSatisfy(words =>
        {
            words.Should().StartWith("Waiting for a fresh broker token since");
            words.Should().Contain("Could not mint a broker token (could not reach broker.example: no network route)");
        });

        // The fresh token reached the new child, BEFORE the prompt was written, and the prompt is the studio's own.
        _runner.Restarts.Should().Equal($"tok-{Secret}-1");
        _runner.Order.Should().Equal("restart", "send");
        _runner.Sent.Should().Equal(NetworkWait.TokenContinuePrompt);
        SystemLines(session).Should().ContainInOrder(NetworkWait.TokenRefusedNotice, NetworkWait.TokenBackNotice);
        session.NetworkWait.Should().BeNull();
        NothingLeaked(session);
    }

    [Fact]
    public async Task A_refused_mint_ends_the_wait_in_words_and_is_not_asked_again()
    {
        SeedToken("jti-old", TimeSpan.FromSeconds(30));
        var handler = new ScriptedHttpHandler(Up(_clock), Refuses("not_allowlisted"));
        using var registry = Registry(Up(_clock), Tokens(handler));
        var session = await StartRegistered(registry);

        _clock.Advance(TimeSpan.FromMinutes(1));
        Play(TokenExpired);

        await Eventually(() => SystemLines(session).Any(l => l.StartsWith("Fravashi stopped waiting", StringComparison.Ordinal)));
        SystemLines(session).Should().Contain(l =>
            l.Contains("oidc:abc is not on its allowlist (not_allowlisted)", StringComparison.Ordinal)
            && l.EndsWith("Send a message to continue this conversation.", StringComparison.Ordinal));

        await Task.Delay(200); // give a wrongly looping wait the chance to ask again
        handler.Journal.Should().HaveCount(1, "a refusal is never retried as if it were the network");
        _runner.Sent.Should().BeEmpty();
        _runner.Restarts.Should().BeEmpty();
        session.NetworkWait.Should().BeNull();
        session.IsRunning.Should().BeTrue("the conversation is not ended by it");
        NothingLeaked(session);
    }

    // ---- a network wait whose token lapsed meanwhile ----------------------------------------

    [Fact]
    public async Task A_token_that_expired_during_a_network_outage_is_minted_again_before_the_conversation_continues()
    {
        // F4's shape against the broker: down for 5 minutes. The process holds jti-old, which lapses at 2 minutes.
        SeedToken("jti-old", TimeSpan.FromMinutes(2));
        var start = _clock.GetUtcNow();
        var handler = new ScriptedHttpHandler(DownFor(_clock, TimeSpan.FromMinutes(5)), Mints);
        using var registry = Registry(DownFor(_clock, TimeSpan.FromMinutes(5)), Tokens(handler));
        var session = await StartRegistered(registry);

        Play(NameNotResolved);
        await Eventually(() => _runner.Sent.Count == 1);

        // Nothing was minted, restarted or sent while the network was down; the token was minted after it lapsed.
        var mint = handler.Journal.Single();
        mint.At.Should().BeOnOrAfter(start + TimeSpan.FromMinutes(5));
        _runner.Restarts.Should().Equal($"tok-{Secret}-1");
        _runner.Order.Should().Equal("restart", "send");
        _runner.Sent.Should().Equal(
            NetworkWait.ContinuePrompt(new NetworkFailure(NetworkFailureKind.NameNotResolved, ""), BrokerHost.Host));
        NothingLeaked(session);
    }

    // ---- a stale process before a person's turn --------------------------------------------

    [Fact]
    public async Task A_process_started_before_a_renewal_is_restarted_on_the_fresh_token_before_a_person_s_turn()
    {
        SeedToken("jti-old", TimeSpan.FromHours(1));
        var tokens = Tokens(new ScriptedHttpHandler(Up(_clock), Mints));
        using var registry = Registry(Up(_clock), tokens);
        var session = await StartRegistered(registry);

        // A turn on the token the process started with: nothing to restart.
        (await registry.SendAsync(session.Id, "first")).Should().BeTrue();
        _runner.Restarts.Should().BeEmpty();
        _runner.Emit(new TurnCompleted(_clock.GetUtcNow(), "done", false, "completed"));
        await Eventually(() => !session.TurnInFlight);

        // The refresher renews the token; the next turn restarts the process on it first, once.
        (await tokens.MintAsync()).Success.Should().BeTrue();
        (await registry.SendAsync(session.Id, "second")).Should().BeTrue();
        _runner.Emit(new TurnCompleted(_clock.GetUtcNow(), "done", false, "completed"));
        await Eventually(() => !session.TurnInFlight);
        (await registry.SendAsync(session.Id, "third")).Should().BeTrue();

        _runner.Restarts.Should().Equal($"tok-{Secret}-1");
        _runner.Order.Should().Equal("send", "restart", "send", "send");
        NothingLeaked(session);
    }

    [Fact]
    public async Task Where_the_broker_does_not_carry_the_runtime_a_refused_turn_ends_as_before()
    {
        SeedToken("jti-old", TimeSpan.FromSeconds(30));
        var handler = new ScriptedHttpHandler(Up(_clock), Mints);
        using var registry = Registry(Up(_clock), Tokens(handler), applies: false);
        var session = await StartRegistered(registry);

        _clock.Advance(TimeSpan.FromMinutes(1));
        Play(TokenExpired);
        await Eventually(() => session.Transcript.OfType<TurnCompleted>().Any());
        await Task.Delay(200);

        session.NetworkWait.Should().BeNull();
        SystemLines(session).Should().NotContain(NetworkWait.TokenRefusedNotice);
        handler.Journal.Should().BeEmpty();
        _runner.Restarts.Should().BeEmpty();
    }
}
