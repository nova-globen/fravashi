using System.Net;
using System.Text.Json;
using Fravashi.Application.AgentRuntimes.Broker;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.Onboarding.Auth;
using AwesomeAssertions;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Onboarding.Auth;

/// <summary>Plan Phase 3 Task 3: gate truth table and the refresh loop's behavior.</summary>
public sealed class IdpSessionTests
{
    private const string Issuer = "https://idp.example.test";

    private readonly IConfigurationStore _settings = Substitute.For<IConfigurationStore>();
    private readonly ISecretStore _secrets = Substitute.For<ISecretStore>();

    private void Configure(bool configured = true)
    {
        _settings.Get(AuthSettings.IdpIssuer).Returns(configured ? Issuer : null);
        _settings.Get(AuthSettings.IdpClientId).Returns(configured ? "fravashi-studio" : null);
    }

    private void StoreSession(StoredCredential credential) =>
        _secrets.Get(AuthSettings.IdpSessionSecretRef).Returns(CredentialEnvelope.Serialize(credential));

    [Fact]
    public void Unconfigured_means_the_gate_never_enforces()
    {
        Configure(configured: false);

        new IdpSessionState(_settings, _secrets).Required.Should().BeFalse();
    }

    [Fact]
    public void Configured_without_a_session_is_required_and_signed_out()
    {
        Configure();
        _secrets.Get(AuthSettings.IdpSessionSecretRef).Returns((string?)null);

        var state = new IdpSessionState(_settings, _secrets);

        state.Required.Should().BeTrue();
        state.SignedIn.Should().BeFalse();
    }

    [Fact]
    public void A_valid_session_is_signed_in_and_an_expired_one_is_not()
    {
        Configure();

        StoreSession(new StoredCredential("t", "r", DateTimeOffset.UtcNow.AddMinutes(30), AuthMethods.AuthCode));
        new IdpSessionState(_settings, _secrets).SignedIn.Should().BeTrue();

        StoreSession(new StoredCredential("t", "r", DateTimeOffset.UtcNow.AddMinutes(-1), AuthMethods.AuthCode));
        new IdpSessionState(_settings, _secrets).SignedIn.Should().BeFalse();
    }

    [Fact]
    public async Task Refresh_IsANoOp_WhileTheSessionIsFresh()
    {
        Configure();
        var secrets = new Dictionary<string, string>
        {
            [AuthSettings.IdpSessionSecretRef] = CredentialEnvelope.Serialize(
                new StoredCredential("t", "r", DateTimeOffset.UtcNow.AddHours(1), AuthMethods.AuthCode)),
        };
        var (service, handler) = RefreshService(secrets, refuse: false);

        (await service.RefreshIfNeededAsync(CancellationToken.None)).Should().BeTrue();
        handler.CallCount.Should().Be(0); // not even discovery was needed
    }

    [Fact]
    public async Task Refresh_RenewsAnExpiringSession_AndTheBrokerProof()
    {
        Configure();
        var secrets = new Dictionary<string, string>
        {
            [AuthSettings.IdpSessionSecretRef] = CredentialEnvelope.Serialize(
                new StoredCredential("old", "refresh-1", DateTimeOffset.UtcNow.AddSeconds(30), AuthMethods.AuthCode)),
        };
        var (service, _) = RefreshService(secrets, refuse: false);

        (await service.RefreshIfNeededAsync(CancellationToken.None)).Should().BeTrue();

        var renewed = CredentialEnvelope.Parse(secrets[AuthSettings.IdpSessionSecretRef]);
        renewed!.AccessToken.Should().Be("fresh-access");
        renewed.RefreshToken.Should().Be("refresh-2"); // rotated
        secrets[BrokerReferences.IdentitySecretRef].Should().Be(secrets[AuthSettings.IdpSessionSecretRef]);
    }

    [Fact]
    public void The_refresh_window_is_wider_than_the_refresh_loop_so_no_tick_can_miss_it()
    {
        IdpAuthService.RefreshAhead.Should().BeGreaterThan(IdpSessionRefreshHostedService.Interval);
    }

    [Fact]
    public async Task Refresh_RenewsASessionWithSixMinutesLeft_BeforeTheLoopCouldMissIt()
    {
        // Six minutes left and a five-minute loop: under the old two-minute window this tick did nothing and the
        // next one came after expiry (seen in the installed v0.9.31, 2026-09-23).
        Configure();
        var secrets = new Dictionary<string, string>
        {
            [AuthSettings.IdpSessionSecretRef] = CredentialEnvelope.Serialize(
                new StoredCredential("old", "refresh-1", DateTimeOffset.UtcNow.AddMinutes(6), AuthMethods.AuthCode)),
        };
        var (service, _) = RefreshService(secrets, refuse: false);

        (await service.RefreshIfNeededAsync(CancellationToken.None)).Should().BeTrue();
        CredentialEnvelope.Parse(secrets[AuthSettings.IdpSessionSecretRef])!.AccessToken.Should().Be("fresh-access");
    }

    [Fact]
    public async Task Refresh_LeavesTheEnvelopeAlone_WhenTheIdpRefuses()
    {
        Configure();
        var original = CredentialEnvelope.Serialize(
            new StoredCredential("old", "refresh-1", DateTimeOffset.UtcNow.AddSeconds(30), AuthMethods.AuthCode));
        var secrets = new Dictionary<string, string> { [AuthSettings.IdpSessionSecretRef] = original };
        var (service, _) = RefreshService(secrets, refuse: true);

        (await service.RefreshIfNeededAsync(CancellationToken.None)).Should().BeFalse();
        secrets[AuthSettings.IdpSessionSecretRef].Should().Be(original);
    }

    private (IdpAuthService Service, StubHttpMessageHandler Handler) RefreshService(
        Dictionary<string, string> secretMap, bool refuse)
    {
        var settings = Substitute.For<IConfigurationStore>();
        settings.Get(AuthSettings.IdpIssuer).Returns(Issuer);
        settings.Get(AuthSettings.IdpClientId).Returns("fravashi-studio");

        var secrets = Substitute.For<ISecretStore>();
        secrets.Get(Arg.Any<string>()).Returns(call => secretMap.GetValueOrDefault(call.Arg<string>()));
        secrets.When(s => s.Set(Arg.Any<string>(), Arg.Any<string>()))
            .Do(call => secretMap[call.ArgAt<string>(0)] = call.ArgAt<string>(1));

        var handler = new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    token_endpoint = $"{Issuer}/connect/token",
                }));
            }

            if (refuse)
            {
                return (HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
            }

            return (HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                access_token = "fresh-access",
                refresh_token = "refresh-2",
                expires_in = 3600,
            }));
        });

        var service = new IdpAuthService(
            new HttpClient(handler), settings, secrets,
            new InMemoryAuthCodeStateStore(), Substitute.For<IConfigurationReloader>());
        return (service, handler);
    }
}
