using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fravashi.Application.Onboarding.Auth;

namespace Fravashi.Infrastructure.Onboarding.Auth;

/// <summary>
/// The shared OAuth 2.0 device-authorization-grant engine used by both the GitHub and Microsoft Entra
/// authenticators - the two differ only in their endpoint URLs, scope, and one error-code spelling, so the
/// request/response plumbing lives here once. All requests are <c>application/x-www-form-urlencoded</c> with
/// <c>Accept: application/json</c> (GitHub returns form-encoded without it). The error map is the union of both
/// providers' codes, which are distinct strings, so one map serves both.
/// </summary>
public sealed class OAuthDeviceFlow
{
    private readonly HttpClient _http;

    public OAuthDeviceFlow(HttpClient http) => _http = http;

    public sealed record Config(string DeviceUrl, string TokenUrl, string ClientId, string Scope);

    public async Task<DeviceCodeStart> StartAsync(Config config, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, config.DeviceUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = config.ClientId,
                ["scope"] = config.Scope,
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"The device-code request was rejected ({(int)response.StatusCode}). {Summarize(body)}");
        }

        var payload = Deserialize<DeviceCodeResponse>(body)
            ?? throw new InvalidOperationException("The device-code response could not be read.");
        if (string.IsNullOrEmpty(payload.DeviceCode) || string.IsNullOrEmpty(payload.UserCode))
        {
            throw new InvalidOperationException($"The device-code response was incomplete. {Summarize(body)}");
        }

        return new DeviceCodeStart(
            payload.DeviceCode,
            payload.UserCode,
            payload.VerificationUri ?? payload.VerificationUriComplete ?? string.Empty,
            payload.Interval is > 0 ? payload.Interval.Value : 5,
            payload.ExpiresIn is > 0 ? payload.ExpiresIn.Value : 900,
            payload.Message);
    }

    public async Task<DevicePollResult> PollOnceAsync(Config config, string deviceCode, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        string body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, config.TokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = config.ClientId,
                    ["device_code"] = deviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                }),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DevicePollResult.Error($"The sign-in poll failed to reach the provider: {ex.Message}");
        }

        var token = Deserialize<TokenResponse>(body);
        if (token is { AccessToken.Length: > 0 })
        {
            var expiresUtc = token.ExpiresIn is > 0 ? DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn.Value) : (DateTimeOffset?)null;
            return DevicePollResult.Success(new StoredCredential(token.AccessToken, NullIfEmpty(token.RefreshToken), expiresUtc, AuthMethods.Device));
        }

        var error = token?.Error;
        var detail = string.IsNullOrEmpty(token?.ErrorDescription) ? error : token!.ErrorDescription;
        return error switch
        {
            "authorization_pending" => DevicePollResult.Pending(),
            "slow_down" => DevicePollResult.SlowDown(),
            "access_denied" or "authorization_declined" => DevicePollResult.Denied(detail ?? "The sign-in request was denied."),
            "expired_token" => DevicePollResult.Expired(detail ?? "The sign-in request expired."),
            null => DevicePollResult.Error($"The token response could not be read ({(int)response.StatusCode})."),
            _ => DevicePollResult.Error(detail ?? error),
        };
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string Summarize(string body) => body.Length > 300 ? body[..300] : body;

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

    private sealed record DeviceCodeResponse(
        [property: JsonPropertyName("device_code")] string? DeviceCode,
        [property: JsonPropertyName("user_code")] string? UserCode,
        [property: JsonPropertyName("verification_uri")] string? VerificationUri,
        [property: JsonPropertyName("verification_uri_complete")] string? VerificationUriComplete,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn,
        [property: JsonPropertyName("interval")] int? Interval,
        [property: JsonPropertyName("message")] string? Message);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? ErrorDescription);
}
