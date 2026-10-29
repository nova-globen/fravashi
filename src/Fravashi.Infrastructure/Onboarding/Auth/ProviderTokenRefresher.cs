using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fravashi.Application.Onboarding;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;

namespace Fravashi.Infrastructure.Onboarding.Auth;

/// <summary>
/// Refreshes an expiring OAuth credential using its rotating refresh token, then writes the new envelope back
/// to the secret store and triggers a configuration reload so the secret→adapter bridge re-projects the fresh
/// access token at the adapter key. Only Entra (Azure DevOps) and Atlassian issue refresh tokens; GitHub
/// device tokens and PATs are no-ops. Both providers rotate the refresh token, so the returned refresh token
/// is persisted every time.
/// </summary>
public sealed class ProviderTokenRefresher : IProviderTokenRefresher
{
    private readonly IProviderConnectionStore _connections;
    private readonly ISecretStore _secrets;
    private readonly IConfigurationStore _settings;
    private readonly IConfigurationReloader _reloader;
    private readonly AuthConfigStore _config;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;

    public ProviderTokenRefresher(
        IProviderConnectionStore connections,
        ISecretStore secrets,
        IConfigurationStore settings,
        IConfigurationReloader reloader,
        AuthConfigStore config,
        HttpClient http,
        TimeProvider? time = null)
    {
        _connections = connections;
        _secrets = secrets;
        _settings = settings;
        _reloader = reloader;
        _config = config;
        _http = http;
        _time = time ?? TimeProvider.System;
    }

    public async Task<bool> RefreshIfNeededAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        var connection = _connections.Get(connectionId);
        if (connection?.CredentialRef is not { Length: > 0 } credentialRef)
        {
            return false;
        }

        var credential = CredentialEnvelope.Parse(_secrets.Get(credentialRef));
        if (credential is null || !credential.CanRefresh || !credential.IsExpiredOrExpiring(_time.GetUtcNow()))
        {
            return false;
        }

        var provider = _settings.Get($"Onboarding:{connection.Kind}:Provider");
        var refreshed = await RefreshAsync(provider, credential.RefreshToken!, cancellationToken).ConfigureAwait(false);
        if (refreshed is null)
        {
            return false;
        }

        _secrets.Set(credentialRef, CredentialEnvelope.Serialize(refreshed));
        _reloader.Reload();
        return true;
    }

    private Task<StoredCredential?> RefreshAsync(string? provider, string refreshToken, CancellationToken cancellationToken) => provider switch
    {
        OnboardingProviders.AzureDevOps or OnboardingProviders.AzureDevOpsWiki => RefreshEntraAsync(refreshToken, cancellationToken),
        OnboardingProviders.Jira or OnboardingProviders.Confluence => RefreshAtlassianAsync(refreshToken, cancellationToken),
        _ => Task.FromResult<StoredCredential?>(null),
    };

    private async Task<StoredCredential?> RefreshEntraAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var resolved = _config.ResolveEntra();
        if (resolved is null)
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, resolved.TokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = resolved.ClientId,
                ["refresh_token"] = refreshToken,
                ["scope"] = resolved.Scope,
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await SendRefreshAsync(request, AuthMethods.Device, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StoredCredential?> RefreshAtlassianAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var resolved = _config.ResolveAtlassian();
        if (resolved is null)
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, resolved.TokenUrl)
        {
            Content = System.Net.Http.Json.JsonContent.Create(new
            {
                grant_type = "refresh_token",
                client_id = resolved.ClientId,
                client_secret = resolved.ClientSecret,
                refresh_token = refreshToken,
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await SendRefreshAsync(request, AuthMethods.AuthCode, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StoredCredential?> SendRefreshAsync(HttpRequestMessage request, string method, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var token = JsonSerializer.Deserialize<TokenResponse>(body);
            if (token is not { AccessToken.Length: > 0 })
            {
                return null;
            }

            var expiresUtc = token.ExpiresIn is > 0 ? _time.GetUtcNow().AddSeconds(token.ExpiresIn.Value) : (DateTimeOffset?)null;
            // Both providers rotate the refresh token; keep the old one only if none came back.
            var refresh = string.IsNullOrEmpty(token.RefreshToken) ? null : token.RefreshToken;
            return new StoredCredential(token.AccessToken, refresh, expiresUtc, method);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn);
}
