using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Application.AgentRuntimes.Broker;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.Onboarding.Auth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Onboarding.Auth;

/// <summary>
/// Backlog 94, the studio's half: a session the launcher obtained before the container started is USED only after
/// the identity provider itself has said whom it belongs to, and anything short of that leaves no trace and falls
/// back to the sign-in page. A handed-over credential is one more way to arrive at a valid session, never a way
/// around validating one.
/// </summary>
public sealed class IdpHandoverTests
{
    private const string Issuer = "https://idp.example.test";
    private const string ClientId = "fravashi-studio";

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
        public void Reload()
        {
        }
    }

    private readonly MapSettings _settings = new();
    private readonly MapSecrets _secrets = new();
    private readonly List<string?> _userinfoBearers = [];

    private IdpAuthService Service(HttpStatusCode userinfo = HttpStatusCode.OK, string userinfoSubject = "user-1")
    {
        _settings.Map[AuthSettings.IdpIssuer] = Issuer;
        _settings.Map[AuthSettings.IdpClientId] = ClientId;
        var handler = new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    authorization_endpoint = $"{Issuer}/connect/authorize",
                    token_endpoint = $"{Issuer}/connect/token",
                    userinfo_endpoint = $"{Issuer}/connect/userinfo",
                }));
            }

            if (path.EndsWith("/connect/userinfo", StringComparison.Ordinal))
            {
                _userinfoBearers.Add(request.Headers.Authorization?.ToString());
                return userinfo == HttpStatusCode.OK
                    ? (HttpStatusCode.OK, JsonSerializer.Serialize(new { sub = userinfoSubject, name = "Ada", email = "d@example.test" }))
                    : (userinfo, "{}");
            }

            return (HttpStatusCode.NotFound, "{}");
        });
        return new IdpAuthService(
            new HttpClient(handler), _settings, _secrets, new InMemoryAuthCodeStateStore(), new NoopReloader(),
            env: _ => null);
    }

    private static string IdToken(string sub)
    {
        static string B64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("{\"alg\":\"none\"}")}.{B64(JsonSerializer.Serialize(new { sub, name = "Ada" }))}.sig";
    }

    private static HandedIdpSession Handed(
        string issuer = Issuer, string clientId = ClientId, string? idTokenSubject = "user-1", DateTimeOffset? expires = null) =>
        new(issuer, clientId, "access-1", "refresh-1", idTokenSubject is null ? null : IdToken(idTokenSubject),
            expires ?? DateTimeOffset.UtcNow.AddHours(1));

    [Fact]
    public async Task A_session_the_provider_vouches_for_is_stored_as_if_the_studio_had_signed_in_itself()
    {
        var service = Service();

        var result = await service.AdoptHandedSessionAsync(Handed());

        result.Success.Should().BeTrue(result.Detail);
        _userinfoBearers.Should().ContainSingle().Which.Should().Be("Bearer access-1");
        var stored = CredentialEnvelope.Parse(_secrets.Get(AuthSettings.IdpSessionSecretRef));
        stored!.AccessToken.Should().Be("access-1");
        stored.RefreshToken.Should().Be("refresh-1");
        _secrets.Get(BrokerReferences.IdentitySecretRef).Should().Be(_secrets.Get(AuthSettings.IdpSessionSecretRef),
            "the session doubles as the broker's mint proof, exactly as a studio sign-in does");
        _settings.Get(AuthSettings.IdpSignedInSubject).Should().Be("user-1");
        _settings.Get(AuthSettings.IdpSignedInName).Should().Be("Ada");
    }

    public static TheoryData<string, HandedIdpSession> Refused => new()
    {
        { "another provider", Handed(issuer: "https://elsewhere.example.test") },
        { "another client", Handed(clientId: "someone-else") },
        { "already expired", Handed(expires: DateTimeOffset.UtcNow.AddMinutes(-1)) },
        { "an id_token for someone else", Handed(idTokenSubject: "user-2") },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_session_that_is_not_this_studios_leaves_nothing_behind(string why, HandedIdpSession handed)
    {
        var result = await Service().AdoptHandedSessionAsync(handed);

        result.Success.Should().BeFalse(why);
        _secrets.Map.Should().BeEmpty(why);
        _settings.Get(AuthSettings.IdpSignedInSubject).Should().BeNull(why);
    }

    [Fact]
    public async Task A_session_the_provider_does_not_accept_is_refused()
    {
        var result = await Service(userinfo: HttpStatusCode.Unauthorized).AdoptHandedSessionAsync(Handed());

        result.Success.Should().BeFalse();
        result.Detail.Should().Contain("401");
        _secrets.Map.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_an_id_token_the_providers_answer_alone_decides()
    {
        var result = await Service().AdoptHandedSessionAsync(Handed(idTokenSubject: null));

        result.Success.Should().BeTrue(result.Detail);
        _secrets.Get(IdpAuthService.IdTokenSecretRef).Should().BeNull();
    }

    [Fact]
    public void The_studio_tells_the_host_its_own_provider_client_and_scope()
    {
        var coordinates = Service().Coordinates(sessionUsable: true);

        coordinates.Should().Be(new IdpSignInCoordinates(Issuer, ClientId, AuthSettings.IdpScopeDefault, true));
    }

    // ---- the start pass ------------------------------------------------------------------------------------

    private sealed class FakeChannel(HandedIdpSession? waiting) : IIdpHandoverChannel
    {
        public bool Available => true;

        public int Takes { get; private set; }

        public List<IdpSignInCoordinates> Published { get; } = [];

        public Task<HandedIdpSession?> TakeAsync(CancellationToken cancellationToken)
        {
            Takes++;
            var taken = waiting;
            waiting = null;
            return Task.FromResult(taken);
        }

        public Task PublishAsync(IdpSignInCoordinates coordinates, CancellationToken cancellationToken)
        {
            Published.Add(coordinates);
            return Task.CompletedTask;
        }
    }

    private IdpHandoverHostedService Pass(IdpAuthService idp, FakeChannel channel)
    {
        var services = new ServiceCollection();
        services.AddSingleton(idp);
        services.AddSingleton(channel);
        services.AddSingleton<IIdpHandoverChannel>(channel);
        services.AddSingleton(new IdpSessionState(_settings, _secrets, env: _ => null));
        return new IdpHandoverHostedService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<IdpHandoverHostedService>.Instance);
    }

    [Fact]
    public async Task At_start_a_studio_with_no_session_takes_the_launchers_and_says_it_is_usable()
    {
        var idp = Service();
        var channel = new FakeChannel(Handed());

        await Pass(idp, channel).FirstPassAsync(CancellationToken.None);

        channel.Takes.Should().Be(1);
        CredentialEnvelope.Parse(_secrets.Get(AuthSettings.IdpSessionSecretRef))!.AccessToken.Should().Be("access-1");
        channel.Published.Should().ContainSingle().Which.SessionUsable.Should().BeTrue();
    }

    [Fact]
    public async Task At_start_a_studio_that_still_holds_a_session_takes_nothing()
    {
        var idp = Service();
        _secrets.Set(AuthSettings.IdpSessionSecretRef, CredentialEnvelope.Serialize(
            new StoredCredential("own-access", "own-refresh", DateTimeOffset.UtcNow.AddHours(1), AuthMethods.AuthCode)));
        var channel = new FakeChannel(Handed());

        await Pass(idp, channel).FirstPassAsync(CancellationToken.None);

        channel.Takes.Should().Be(0, "the studio's own session is kept, and a second holder of a refresh chain is avoided");
        CredentialEnvelope.Parse(_secrets.Get(AuthSettings.IdpSessionSecretRef))!.AccessToken.Should().Be("own-access");
    }

    [Fact]
    public async Task At_start_a_refused_hand_over_says_the_session_is_not_usable_so_the_next_start_signs_in()
    {
        var idp = Service(userinfo: HttpStatusCode.Unauthorized);
        var channel = new FakeChannel(Handed());

        await Pass(idp, channel).FirstPassAsync(CancellationToken.None);

        _secrets.Map.Should().BeEmpty();
        channel.Published.Should().ContainSingle().Which.SessionUsable.Should().BeFalse();
    }
}
