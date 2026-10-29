using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fravashi.Infrastructure.Bridge;
using Microsoft.Extensions.Configuration;

namespace Fravashi.Infrastructure.Onboarding.Auth;

/// <summary>A session the launcher obtained before the container started (backlog 94). Validated before any use.</summary>
public sealed record HandedIdpSession(
    string Issuer,
    string ClientId,
    string AccessToken,
    string? RefreshToken,
    string? IdToken,
    DateTimeOffset? ExpiresUtc);

/// <summary>What the studio tells the host about its sign-in, so the next start knows whether to sign in first.</summary>
public sealed record IdpSignInCoordinates(string Issuer, string ClientId, string Scope, bool SessionUsable);

/// <summary>
/// How the studio reaches the launcher's side of the sign-in hand-over (backlog 94). A port so the studio's logic
/// is tested without a bridge; the one implementation is <see cref="BridgeIdpHandoverChannel"/>.
/// </summary>
public interface IIdpHandoverChannel
{
    /// <summary>False when there is no host to talk to (a dev <c>dotnet run</c>): then nothing is ever handed over.</summary>
    bool Available { get; }

    /// <summary>The session the launcher left, exactly once; null when there is none.</summary>
    Task<HandedIdpSession?> TakeAsync(CancellationToken cancellationToken);

    /// <summary>Tells the host where the studio signs in and whether its own session is usable.</summary>
    Task PublishAsync(IdpSignInCoordinates coordinates, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IIdpHandoverChannel"/> over the host bridge: <c>POST /bridge/identity/take</c> and
/// <c>POST /bridge/identity/coordinates</c>, with the bridge token the launcher gave this container. Only the
/// studio holds that token (an agent child's environment is an allowlist that leaves it out, backlog 72), which is
/// why the bridge is a fit place for a credential to pass through.
/// </summary>
public sealed class BridgeIdpHandoverChannel(HttpClient http, IConfiguration configuration) : IIdpHandoverChannel
{
    private const string TokenHeader = "X-Fravashi-Bridge";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly BridgeWorkspaceContext _bridge = new(configuration);

    public bool Available => _bridge.IsBridgeConfigured;

    public async Task<HandedIdpSession?> TakeAsync(CancellationToken cancellationToken)
    {
        if (!Available)
        {
            return null;
        }

        using var request = Request(HttpMethod.Post, "bridge/identity/take");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<HandedIdpSession>(Json, cancellationToken).ConfigureAwait(false);
    }

    public async Task PublishAsync(IdpSignInCoordinates coordinates, CancellationToken cancellationToken)
    {
        if (!Available)
        {
            return;
        }

        using var request = Request(HttpMethod.Post, "bridge/identity/coordinates");
        request.Content = JsonContent.Create(new
        {
            coordinates.Issuer,
            coordinates.ClientId,
            coordinates.Scope,
            coordinates.SessionUsable,
            UpdatedUtc = DateTimeOffset.UtcNow,
        }, options: Json);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        // A host too old to know the endpoint answers 404; that only means the next start signs in the old way.
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(new Uri(_bridge.BaseUrl!.TrimEnd('/') + "/"), path));
        request.Headers.Add(TokenHeader, _bridge.Token);
        return request;
    }
}
