using System.Net;
using System.Text;
using System.Text.Json;
using Fravashi.Application.AgentRuntimes.Broker;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.Onboarding.Auth;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Onboarding.Auth;

/// <summary>
/// Plan Phase 3 Task 2: the IdP sign-in flow against a stubbed issuer. No live IdP is ever
/// contacted; the stub plays discovery + token endpoints.
/// </summary>
public sealed class IdpAuthServiceTests
{
    private const string Issuer = "https://idp.example.test";
    private const string RedirectUri = "http://localhost:9090/auth/callback/idp";

    private sealed class MapSettings : IConfigurationStore
    {
        public Dictionary<string, string?> Map { get; } = new(StringComparer.Ordinal);
        public string? Get(string key) => Map.GetValueOrDefault(key);
        public IReadOnlyDictionary<string, string?> GetAll() => Map;
        public void Set(string key, string? value) => Map[key] = value;
        public void SetMany(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var pair in values)
            {
                Map[pair.Key] = pair.Value;
            }
        }
        public void Remove(string key) => Map.Remove(key);
    }

    private sealed class MapSecrets : ISecretStore
    {
        public Dictionary<string, string> Map { get; } = new(StringComparer.Ordinal);
        public void Set(string reference, string plaintext) => Map[reference] = plaintext;
        public string? Get(string reference) => Map.GetValueOrDefault(reference);
        public void Remove(string reference) => Map.Remove(reference);
    }

    private sealed class NoopReloader : IConfigurationReloader
    {
        public int Reloads { get; private set; }
        public void Reload() => Reloads++;
    }

    private readonly MapSettings _settings = new();
    private readonly MapSecrets _secrets = new();
    private readonly InMemoryAuthCodeStateStore _states = new();
    private readonly NoopReloader _reloader = new();
    private readonly List<string> _tokenRequestBodies = [];

    private IdpAuthService Service(string? nonceInIdToken = null, bool discoveryFails = false)
    {
        _settings.Map.TryAdd(AuthSettings.IdpIssuer, Issuer);
        _settings.Map.TryAdd(AuthSettings.IdpClientId, "fravashi-studio");

        var handler = new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
            {
                if (discoveryFails)
                {
                    return (HttpStatusCode.ServiceUnavailable, "{}");
                }

                return (HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    authorization_endpoint = $"{Issuer}/connect/authorize",
                    token_endpoint = $"{Issuer}/connect/token",
                    userinfo_endpoint = $"{Issuer}/connect/userinfo",
                    end_session_endpoint = $"{Issuer}/connect/endsession",
                }));
            }

            if (path.EndsWith("/connect/token", StringComparison.Ordinal))
            {
                _tokenRequestBodies.Add(request.Content!.ReadAsStringAsync().Result);
                return (HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    access_token = "access-123",
                    refresh_token = "refresh-456",
                    id_token = MakeIdToken("user-1", nonceInIdToken, "Ada", "ada@example.test"),
                    expires_in = 3600,
                }));
            }

            return (HttpStatusCode.NotFound, "{}");
        });
        return new IdpAuthService(new HttpClient(handler), _settings, _secrets, _states, _reloader);
    }

    private static string MakeIdToken(string sub, string? nonce, string? name, string? email)
    {
        static string B64Url(string json) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var payload = JsonSerializer.Serialize(new { sub, nonce, name, email });
        return $"{B64Url("{\"alg\":\"RS256\"}")}.{B64Url(payload)}.signature";
    }

    [Fact]
    public async Task Begin_BuildsAnAuthorizeUrl_WithPkceAndNonce_AndParksTheVerifier()
    {
        var service = Service();

        var challenge = await service.BeginSignInAsync(RedirectUri, CancellationToken.None);

        challenge.Should().NotBeNull();
        challenge!.AuthorizeUrl.Should().StartWith($"{Issuer}/connect/authorize?");
        challenge.AuthorizeUrl.Should().Contain("client_id=fravashi-studio");
        challenge.AuthorizeUrl.Should().Contain("response_type=code");
        challenge.AuthorizeUrl.Should().Contain("code_challenge_method=S256");
        challenge.AuthorizeUrl.Should().Contain(Uri.EscapeDataString("openid profile email offline_access"));

        var entry = _states.Consume(challenge.State);
        entry.Should().NotBeNull();
        entry!.CodeVerifier.Should().NotBeNullOrEmpty();
        entry.Nonce.Should().NotBeNullOrEmpty();
        challenge.AuthorizeUrl.Should().Contain($"code_challenge={Pkce.ChallengeS256(entry.CodeVerifier!)}");
        challenge.AuthorizeUrl.Should().Contain($"nonce={entry.Nonce}");
    }

    [Fact]
    public async Task Begin_ReturnsNull_WhenUnconfigured_OrDiscoveryFails()
    {
        var unconfigured = new IdpAuthService(
            new HttpClient(new StubHttpMessageHandler(_ => (HttpStatusCode.OK, "{}"))),
            new MapSettings(), _secrets, _states, _reloader);
        (await unconfigured.BeginSignInAsync(RedirectUri, CancellationToken.None)).Should().BeNull();

        (await Service(discoveryFails: true).BeginSignInAsync(RedirectUri, CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task Callback_WithAnUnknownState_FailsAsCsrf()
    {
        var result = await Service().HandleCallbackAsync("code", "never-issued", null, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Detail.Should().Contain("CSRF");
    }

    [Fact]
    public async Task Callback_WithANonceMismatch_Fails()
    {
        var service = Service(nonceInIdToken: "the-wrong-nonce");
        var challenge = await service.BeginSignInAsync(RedirectUri, CancellationToken.None);

        var result = await service.HandleCallbackAsync("code-1", challenge!.State, null, CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Detail.Should().Contain("nonce");
        _secrets.Map.Should().NotContainKey(AuthSettings.IdpSessionSecretRef);
    }

    [Fact]
    public async Task Callback_HappyPath_StoresTheSessionIdentityAndBrokerProof()
    {
        IdpAuthService service = null!;
        service = ServiceWithDynamicNonce(() => service);
        var challenge = await service.BeginSignInAsync(RedirectUri, CancellationToken.None);

        var result = await service.HandleCallbackAsync("code-1", challenge!.State, null, CancellationToken.None);

        result.Success.Should().BeTrue(result.Detail);
        result.Detail.Should().Contain("Ada");

        var session = CredentialEnvelope.Parse(_secrets.Map[AuthSettings.IdpSessionSecretRef]);
        session!.AccessToken.Should().Be("access-123");
        session.RefreshToken.Should().Be("refresh-456");
        session.ExpiresUtc.Should().NotBeNull();
        _secrets.Map[BrokerReferences.IdentitySecretRef].Should().Be(_secrets.Map[AuthSettings.IdpSessionSecretRef]);
        _secrets.Map.Should().ContainKey(IdpAuthService.IdTokenSecretRef);
        _settings.Map[AuthSettings.IdpSignedInSubject].Should().Be("user-1");
        _settings.Map[AuthSettings.IdpSignedInName].Should().Be("Ada");
        _reloader.Reloads.Should().BeGreaterThan(0);

        // The exchange carried PKCE but NO client secret (none stored).
        _tokenRequestBodies.Should().ContainSingle();
        _tokenRequestBodies[0].Should().Contain("code_verifier=");
        _tokenRequestBodies[0].Should().NotContain("client_secret=");
    }

    [Fact]
    public async Task Callback_IncludesTheClientSecret_OnlyWhenStored()
    {
        _secrets.Map[AuthSettings.IdpClientSecretRef] = "confidential-secret";
        IdpAuthService service = null!;
        service = ServiceWithDynamicNonce(() => service);
        var challenge = await service.BeginSignInAsync(RedirectUri, CancellationToken.None);

        var result = await service.HandleCallbackAsync("code-1", challenge!.State, null, CancellationToken.None);

        result.Success.Should().BeTrue(result.Detail);
        _tokenRequestBodies[^1].Should().Contain("client_secret=confidential-secret");
    }

    [Fact]
    public async Task ClearSession_RemovesEverything_IncludingTheBrokerProof()
    {
        _secrets.Map[AuthSettings.IdpSessionSecretRef] = "x";
        _secrets.Map[IdpAuthService.IdTokenSecretRef] = "y";
        _secrets.Map[BrokerReferences.IdentitySecretRef] = "z";
        _settings.Map[AuthSettings.IdpSignedInSubject] = "user-1";
        var service = Service();

        service.ClearSession();

        _secrets.Map.Should().BeEmpty();
        _settings.Map.Should().NotContainKey(AuthSettings.IdpSignedInSubject);
    }

    [Fact]
    public async Task EndSessionUrl_CarriesTheHint_AndPostLogoutTarget()
    {
        _secrets.Map[IdpAuthService.IdTokenSecretRef] = "the-id-token";
        var url = await Service().BuildEndSessionUrlAsync("http://localhost:9090/", CancellationToken.None);

        url.Should().StartWith($"{Issuer}/connect/endsession?");
        url.Should().Contain("id_token_hint=the-id-token");
        url.Should().Contain(Uri.EscapeDataString("http://localhost:9090/"));
    }

    /// <summary>
    /// The happy-path stub must place the SESSION'S REAL NONCE inside the id_token; the nonce is
    /// only known after Begin runs, so the stub reads it back from the pending state ledger via
    /// the live service reference.
    /// </summary>
    private IdpAuthService ServiceWithDynamicNonce(Func<IdpAuthService> self)
    {
        _settings.Map.TryAdd(AuthSettings.IdpIssuer, Issuer);
        _settings.Map.TryAdd(AuthSettings.IdpClientId, "fravashi-studio");
        string? currentNonce = null;

        var handler = new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    authorization_endpoint = $"{Issuer}/connect/authorize",
                    token_endpoint = $"{Issuer}/connect/token",
                }));
            }

            if (path.EndsWith("/connect/token", StringComparison.Ordinal))
            {
                _tokenRequestBodies.Add(request.Content!.ReadAsStringAsync().Result);
                return (HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    access_token = "access-123",
                    refresh_token = "refresh-456",
                    id_token = MakeIdToken("user-1", currentNonce, "Ada", "ada@example.test"),
                    expires_in = 3600,
                }));
            }

            return (HttpStatusCode.NotFound, "{}");
        });

        var service = new IdpAuthService(
            new HttpClient(handler), _settings, _secrets, new CapturingStates(n => currentNonce = n), _reloader);
        return service;
    }

    /// <summary>A pass-through ledger that also reports each parked nonce to the stub.</summary>
    private sealed class CapturingStates(Action<string?> onNonce) : IAuthCodeStateStore
    {
        private readonly InMemoryAuthCodeStateStore _inner = new();

        public void Put(AuthCodeStateEntry entry)
        {
            onNonce(entry.Nonce);
            _inner.Put(entry);
        }

        public AuthCodeStateEntry? Consume(string state) => _inner.Consume(state);
    }
}
