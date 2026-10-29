using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fravashi.Application.Onboarding;
using Fravashi.Application.Onboarding.Auth;

namespace Fravashi.Infrastructure.Onboarding.Auth;

/// <summary>
/// Atlassian OAuth 2.0 (3LO) authenticator for Jira + Confluence Cloud: an authorization-code grant with a
/// rotating refresh token. Needs an owner-registered app's client id + secret and a registered redirect URI
/// (Atlassian permits <c>http://localhost:&lt;port&gt;/…</c>). A random <c>state</c> guards the callback against
/// CSRF. On success the granted token is a <b>gateway-scoped</b> bearer token - it works against
/// <c>api.atlassian.com/ex/{jira|confluence}/{cloudId}</c>, not the <c>{site}.atlassian.net</c> base the current
/// probes/adapters use, which is why probe integration is a tracked follow-up (see docs/sso-setup.md).
/// <see cref="ListAccessibleResourcesAsync"/> is the real first call of that future integration and is used to
/// verify the sign-in and capture the cloudId.
/// </summary>
public sealed class AtlassianAuthCodeAuthenticator : IAuthCodeAuthenticator
{
    private readonly HttpClient _http;
    private readonly AuthConfigStore _config;

    public AtlassianAuthCodeAuthenticator(HttpClient http, AuthConfigStore config)
    {
        _http = http;
        _config = config;
    }

    public string Provider => OnboardingProviders.Confluence;

    public AuthCodeChallenge BeginAuthorization(string redirectUri)
    {
        var resolved = ResolveOrThrow();
        var state = NewState();
        var query = new Dictionary<string, string>
        {
            ["audience"] = resolved.Audience,
            ["client_id"] = resolved.ClientId,
            ["scope"] = resolved.Scope,
            ["redirect_uri"] = redirectUri,
            ["state"] = state,
            ["response_type"] = "code",
            ["prompt"] = "consent",
        };
        var url = BuildUrl(resolved.AuthorizeUrl, query);
        return new AuthCodeChallenge(url, state, redirectUri);
    }

    public async Task<AuthCodeExchange> ExchangeCodeAsync(string code, string redirectUri, CancellationToken cancellationToken = default)
    {
        var resolved = ResolveOrThrow();
        var body = new
        {
            grant_type = "authorization_code",
            client_id = resolved.ClientId,
            client_secret = resolved.ClientSecret,
            code,
            redirect_uri = redirectUri,
        };

        string responseBody;
        System.Net.HttpStatusCode status;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, resolved.TokenUrl)
            {
                Content = System.Net.Http.Json.JsonContent.Create(body),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            status = response.StatusCode;
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AuthCodeExchange.Fail($"The token exchange could not reach Atlassian: {ex.Message}");
        }

        var token = Deserialize<TokenResponse>(responseBody);
        if (token is not { AccessToken.Length: > 0 })
        {
            var reason = token?.ErrorDescription ?? token?.Error ?? $"the token endpoint returned {(int)status}";
            return AuthCodeExchange.Fail($"Atlassian did not return an access token - {reason}.");
        }

        var expiresUtc = token.ExpiresIn is > 0 ? DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn.Value) : (DateTimeOffset?)null;
        var credential = new StoredCredential(token.AccessToken, NullIfEmpty(token.RefreshToken), expiresUtc, AuthMethods.AuthCode);
        return AuthCodeExchange.Ok(credential, "Signed in - an access token and refresh token were stored.");
    }

    /// <summary>
    /// Lists the Atlassian sites the token can reach (<c>GET /oauth/token/accessible-resources</c>) - the real
    /// first call any 3LO integration makes, used here to verify the sign-in and capture each site's cloudId.
    /// </summary>
    public async Task<IReadOnlyList<AccessibleResource>> ListAccessibleResourcesAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        var resolved = ResolveOrThrow();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, resolved.ResourcesUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Deserialize<List<AccessibleResource>>(payload) ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private AtlassianAuthResolved ResolveOrThrow() =>
        _config.ResolveAtlassian()
        ?? throw new InvalidOperationException("Atlassian sign-in is not configured - set a client id and secret in Sign-in configuration.");

    private static string BuildUrl(string baseUrl, IReadOnlyDictionary<string, string> query)
    {
        var pairs = query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}");
        var separator = baseUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{baseUrl}{separator}{string.Join('&', pairs)}";
    }

    private static string NewState()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static T? Deserialize<T>(string body) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public sealed record AccessibleResource(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("url")] string? Url);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn,
        [property: JsonPropertyName("scope")] string? Scope,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? ErrorDescription);
}
