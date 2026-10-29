using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.Onboarding.Auth;
using Fravashi.Testing.Network;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Onboarding.Auth;

/// <summary>
/// #372, seen by the owner on 2026-10-25 with two studio windows open: the first sign-in failed with "The sign-in state was
/// unknown or expired (possible CSRF)". The studio log shows, for each failed attempt, one discovery request that was
/// started and never answered (cancelled with its request) and then nothing: the first arrival of the callback consumed
/// the one-shot state and was aborted, and a second arrival of the same callback found no state. These hold the studio's
/// side of the fix: a callback is handled once, a matching second arrival gets the first one's OUTCOME, and nothing else
/// about the state check is loosened. They also hold that a sign-in clears the leftover refusal for good, and that the
/// studio's one sign-in in progress is visible to every window.
/// </summary>
public sealed class IdpSignInOnceTests
{
    private const string Issuer = "https://idp.example.test";
    private const string RedirectUri = "http://localhost:9090/auth/callback/idp";
    private const string Window = "Mozilla/5.0 Edg/141 FravashiWindow/1.0 tk/window-token";

    private readonly FakeNetworkClock _clock = new();
    private readonly MapSettings _settings = new();
    private readonly MapSecrets _secrets = new();
    private readonly IdpSessionGrace _grace;
    private readonly IdpSignInCoordinator _signIns;
    private readonly NonceStates _states;
    private readonly List<string> _tokenBodies = [];
    private TaskCompletionSource? _holdToken;
    private Func<Task>? _duringRefresh;

    public IdpSignInOnceTests()
    {
        _grace = new IdpSessionGrace(_clock);
        _signIns = new IdpSignInCoordinator(_clock);
        _states = new NonceStates(new InMemoryAuthCodeStateStore(_clock));
        _settings.Set(AuthSettings.IdpIssuer, Issuer);
        _settings.Set(AuthSettings.IdpClientId, "fravashi-studio");
    }

    private IdpAuthService Service() =>
        new(new HttpClient(new Handler(this)), _settings, _secrets, _states, new NoReload(),
            _clock, _ => null, _grace, _signIns);

    private int CodeExchanges => _tokenBodies.Count(body => body.Contains("grant_type=authorization_code", StringComparison.Ordinal));

    private async Task<string> BeginAsync(IdpAuthService idp, string origin = "window-a") =>
        (await idp.BeginSignInAsync(RedirectUri, origin, CancellationToken.None))!.State;

    [Fact]
    public async Task A_callback_arriving_twice_for_the_same_state_code_and_window_is_exchanged_once_and_both_are_signed_in()
    {
        var idp = Service();
        var state = await BeginAsync(idp);

        var first = await idp.HandleCallbackAsync("code-1", state, null, Window, CancellationToken.None);
        var second = await idp.HandleCallbackAsync("code-1", state, null, Window, CancellationToken.None);

        first.Success.Should().BeTrue(first.Detail);
        second.Should().BeSameAs(first);
        CodeExchanges.Should().Be(1);
    }

    /// <summary>The failure in the field: the first arrival's request is aborted while its exchange is under way.</summary>
    [Fact]
    public async Task An_aborted_first_arrival_still_completes_the_exchange_and_the_second_arrival_gets_its_success()
    {
        var idp = Service();
        var state = await BeginAsync(idp);
        _holdToken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var aborted = new CancellationTokenSource();

        var first = idp.HandleCallbackAsync("code-1", state, null, Window, aborted.Token);
        await aborted.CancelAsync();
        var thrown = await Record.ExceptionAsync(() => first);
        var second = idp.HandleCallbackAsync("code-1", state, null, Window, CancellationToken.None);
        _holdToken.SetResult();

        thrown.Should().BeAssignableTo<OperationCanceledException>();
        var outcome = await second;
        outcome.Success.Should().BeTrue(outcome.Detail);
        CodeExchanges.Should().Be(1);
        new IdpSessionState(_settings, _secrets, time: _clock, env: _ => null).SignedIn.Should().BeTrue();
    }

    [Theory]
    [InlineData("code-2", Window)]
    [InlineData("code-1", "Mozilla/5.0 some other browser")]
    [InlineData("code-1", null)]
    public async Task A_second_arrival_with_another_code_or_from_another_caller_is_refused_as_an_unknown_state(string code, string? caller)
    {
        var idp = Service();
        var state = await BeginAsync(idp);
        (await idp.HandleCallbackAsync("code-1", state, null, Window, CancellationToken.None)).Success.Should().BeTrue();

        var second = await idp.HandleCallbackAsync(code, state, null, caller, CancellationToken.None);

        second.Success.Should().BeFalse();
        second.Detail.Should().Be(IdpSignInCoordinator.UnknownStateWords);
        CodeExchanges.Should().Be(1);
    }

    [Fact]
    public async Task The_same_callback_again_after_the_replay_window_is_refused()
    {
        var idp = Service();
        var state = await BeginAsync(idp);
        (await idp.HandleCallbackAsync("code-1", state, null, Window, CancellationToken.None)).Success.Should().BeTrue();

        _clock.Advance(IdpSignInCoordinator.ReplayWindow + TimeSpan.FromSeconds(1));
        var late = await idp.HandleCallbackAsync("code-1", state, null, Window, CancellationToken.None);

        late.Success.Should().BeFalse();
        late.Detail.Should().Be(IdpSignInCoordinator.UnknownStateWords);
        CodeExchanges.Should().Be(1);
    }

    [Theory]
    [InlineData("never-issued")]
    [InlineData("")]
    [InlineData(null)]
    public async Task A_state_that_was_never_issued_is_refused_and_never_reaches_the_token_endpoint(string? state)
    {
        var idp = Service();
        await BeginAsync(idp);

        var result = await idp.HandleCallbackAsync("code-1", state, null, Window, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Detail.Should().Contain("CSRF");
        _tokenBodies.Should().BeEmpty();
    }

    [Fact]
    public async Task One_states_outcome_never_answers_another()
    {
        var idp = Service();
        var refused = await BeginAsync(idp);
        _states.WrongNonce = true;
        var first = await idp.HandleCallbackAsync("code-1", refused, null, Window, CancellationToken.None);
        _states.WrongNonce = false;
        var other = await BeginAsync(idp);

        var second = await idp.HandleCallbackAsync("code-1", other, null, Window, CancellationToken.None);

        first.Success.Should().BeFalse();
        first.Detail.Should().Contain("nonce");
        second.Success.Should().BeTrue(second.Detail);
        CodeExchanges.Should().Be(2);
    }

    [Fact]
    public async Task A_sign_in_started_in_one_window_is_in_progress_for_all_until_it_completes()
    {
        var idp = Service();
        var changes = 0;
        _signIns.Changed += () => changes++;

        var state = await BeginAsync(idp, origin: "folder-window");

        _signIns.InProgress!.Origin.Should().Be("folder-window");
        changes.Should().Be(1);

        (await idp.HandleCallbackAsync("code-1", state, null, Window, CancellationToken.None)).Success.Should().BeTrue();

        _signIns.InProgress.Should().BeNull();
        _signIns.Completions.Should().Be(1);
        changes.Should().Be(2);
    }

    [Fact]
    public async Task A_failed_callback_ends_its_own_attempt_and_not_a_newer_one()
    {
        var idp = Service();
        var older = await BeginAsync(idp, origin: "home-window");
        var newer = await BeginAsync(idp, origin: "folder-window");

        (await idp.HandleCallbackAsync(null, older, "access_denied", Window, CancellationToken.None)).Success.Should().BeFalse();
        _signIns.InProgress!.Origin.Should().Be("folder-window");

        (await idp.HandleCallbackAsync(null, newer, "access_denied", Window, CancellationToken.None)).Success.Should().BeFalse();
        _signIns.InProgress.Should().BeNull();
        _signIns.Completions.Should().Be(0);
    }

    [Fact]
    public async Task An_attempt_outliving_its_state_is_no_longer_shown_as_in_progress()
    {
        await BeginAsync(Service());

        _clock.Advance(IdpSignInCoordinator.AttemptLifetime + TimeSpan.FromSeconds(1));

        _signIns.InProgress.Should().BeNull();
    }

    // ---- the leftover refusal (wanted 2) -----------------------------------------------------------------------------

    [Fact]
    public async Task A_successful_sign_in_clears_the_refusal_the_refresh_left()
    {
        StoreExpiringSession();
        var idp = Service();
        (await idp.RefreshAsync(force: true)).Outcome.Should().Be(IdpRefreshOutcome.Refused);
        new IdpSessionState(_settings, _secrets, time: _clock, env: _ => null, grace: _grace).RefusalWords
            .Should().Contain("invalid_grant");

        var state = await BeginAsync(idp);
        (await idp.HandleCallbackAsync("code-1", state, null, Window, CancellationToken.None)).Success.Should().BeTrue();

        new IdpSessionState(_settings, _secrets, time: _clock, env: _ => null, grace: _grace).RefusalWords.Should().BeNull();
    }

    /// <summary>
    /// The gate renews an expired session on its way to a page, so a renewal of the OLD session can be in flight while the
    /// new sign-in lands; its refusal arriving afterwards must not put the words back.
    /// </summary>
    [Fact]
    public async Task A_refusal_for_the_old_session_that_lands_after_a_new_sign_in_is_not_recorded()
    {
        StoreExpiringSession();
        var idp = Service();
        var state = await BeginAsync(idp);
        _duringRefresh = async () =>
            (await idp.HandleCallbackAsync("code-1", state, null, Window, CancellationToken.None)).Success.Should().BeTrue();

        var renewal = await idp.RefreshAsync(force: true);

        renewal.Healthy.Should().BeTrue();
        _grace.Refusal.Should().BeNull();
        new IdpSessionState(_settings, _secrets, time: _clock, env: _ => null, grace: _grace).SignedIn.Should().BeTrue();
    }

    private void StoreExpiringSession() =>
        _secrets.Set(AuthSettings.IdpSessionSecretRef, CredentialEnvelope.Serialize(
            new StoredCredential("old-access", "old-refresh", _clock.GetUtcNow().AddMinutes(-1), AuthMethods.AuthCode)));

    private static string IdToken(string? nonce)
    {
        static string B64Url(string json) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return $"{B64Url("{\"alg\":\"RS256\"}")}.{B64Url(JsonSerializer.Serialize(new { sub = "user-1", nonce, name = "Ada" }))}.signature";
    }

    /// <summary>The identity provider: discovery, and a token endpoint that refuses refreshes and grants codes.</summary>
    private sealed class Handler(IdpSignInOnceTests test) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, $$"""{"authorization_endpoint":"{{Issuer}}/connect/authorize","token_endpoint":"{{Issuer}}/connect/token"}""");
            }

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            lock (test._tokenBodies)
            {
                test._tokenBodies.Add(body);
            }

            if (body.Contains("grant_type=refresh_token", StringComparison.Ordinal))
            {
                if (test._duringRefresh is { } during)
                {
                    await during();
                }

                return Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
            }

            if (test._holdToken is { } hold)
            {
                await hold.Task.WaitAsync(cancellationToken);
            }

            var nonce = test._states.WrongNonce ? "the-wrong-nonce" : test._states.LastNonce;
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                access_token = "new-access",
                refresh_token = "new-refresh",
                id_token = IdToken(nonce),
                expires_in = 3600,
            }));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    /// <summary>The real ledger, which also tells the stub the nonce it must put in the id_token.</summary>
    private sealed class NonceStates(InMemoryAuthCodeStateStore inner) : IAuthCodeStateStore
    {
        public string? LastNonce { get; private set; }

        public bool WrongNonce { get; set; }

        public void Put(AuthCodeStateEntry entry)
        {
            LastNonce = entry.Nonce;
            inner.Put(entry);
        }

        public AuthCodeStateEntry? Consume(string state) => inner.Consume(state);
    }

    private sealed class MapSettings : IConfigurationStore
    {
        private readonly Dictionary<string, string?> _map = new(StringComparer.Ordinal);

        public string? Get(string key) => _map.GetValueOrDefault(key);

        public IReadOnlyDictionary<string, string?> GetAll() => _map;

        public void Set(string key, string? value) => _map[key] = value;

        public void SetMany(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var pair in values)
            {
                _map[pair.Key] = pair.Value;
            }
        }

        public void Remove(string key) => _map.Remove(key);
    }

    private sealed class MapSecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);

        public void Set(string reference, string plaintext) => _map[reference] = plaintext;

        public string? Get(string reference) => _map.GetValueOrDefault(reference);

        public void Remove(string reference) => _map.Remove(reference);
    }

    private sealed class NoReload : IConfigurationReloader
    {
        public void Reload()
        {
        }
    }
}
