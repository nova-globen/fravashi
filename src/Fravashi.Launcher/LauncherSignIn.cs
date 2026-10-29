using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fravashi.Launcher;

/// <summary>Where and how the launcher signs in: what the studio last published about its own sign-in.</summary>
public sealed record LauncherSignInTarget(string Issuer, string ClientId, string Scope, int StudioPort)
{
    /// <summary>The redirect the identity provider already knows, because it is the studio's own.</summary>
    public string RedirectUri => $"http://localhost:{StudioPort}{LauncherSignIn.CallbackPath}";
}

/// <summary>A session the launcher obtained. Held in memory only, and handed to the studio once.</summary>
public sealed record LauncherSession(
    string Issuer,
    string ClientId,
    string AccessToken,
    string? RefreshToken,
    string? IdToken,
    DateTimeOffset? ExpiresUtc);

/// <summary>How a launcher sign-in ended. <see cref="Session"/> is null on every failure, with the reason said.</summary>
public sealed record LauncherSignInResult(LauncherSession? Session, string Outcome)
{
    public static LauncherSignInResult Failed(string outcome) => new(null, outcome);
}

/// <summary>
/// The launcher's sign-in (backlog 94, ADR 0026 as decided on 2026-09-23): before the container starts,
/// the launcher signs the person in with the SAME identity provider and client the studio uses, in their real
/// browser, and hands the result to the studio, which validates it and uses it, or shows its own sign-in page.
/// <para>
/// It is the OIDC authorization-code flow with PKCE, the same one the studio runs, done host-side. The redirect
/// is the studio's own <c>http://localhost:PORT/auth/callback/idp</c>, which the provider already accepts, and
/// the launcher can answer it only because the container has not started yet and the port is free. No client
/// secret is sent (the launcher never holds one): a provider that insists on one refuses the exchange, and the
/// studio's own page takes over. Nothing here ever throws or blocks the start: every failure is an outcome with
/// its reason, for the log.
/// </para>
/// </summary>
public sealed class LauncherSignIn(HttpClient http, Action<string> openBrowser, TimeProvider? clock = null)
{
    /// <summary>The studio's callback path, shared so the provider sees one registered redirect.</summary>
    public const string CallbackPath = "/auth/callback/idp";

    /// <summary>How long the person has to finish in the browser before the studio's own page takes over.</summary>
    public static readonly TimeSpan DefaultBrowserWait = TimeSpan.FromMinutes(3);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>
    /// Why the launcher should NOT sign in before this start, or null when it should. It signs in only when the
    /// studio would otherwise have to show its own sign-in page, because opening a browser on every start to
    /// replace a session the studio already keeps alive would be noise, and a second holder of the same refresh
    /// token could revoke the first. So: only when the studio has said where it signs in (a first run has not),
    /// only when its session was NOT usable when it last said so, and only when the studio is not already up
    /// (a running studio keeps its own gate, and its port is not ours to answer).
    /// </summary>
    public static string? SkipReason(bool coordinatesKnown, bool sessionUsable, bool studioAnswers) =>
        studioAnswers ? "the studio is already running and keeps its own session"
        : !coordinatesKnown ? "the studio has not said which identity provider it signs in with yet"
        : sessionUsable ? "the studio's own session was still usable when it last said"
        : null;

    public async Task<LauncherSignInResult> SignInAsync(
        LauncherSignInTarget target, TimeSpan browserWait, CancellationToken cancellationToken, Action? opened = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        // The port FIRST: a studio that is serving it needs no help, and nothing should be fetched or opened for a
        // sign-in that could never be answered.
        using var callback = LoopbackCallback.TryListen(target.StudioPort, CallbackPath);
        if (callback is null)
        {
            return LauncherSignInResult.Failed($"port {target.StudioPort} is in use, so the studio is already serving it");
        }

        try
        {
            var discovery = await DiscoverAsync(target.Issuer, cancellationToken).ConfigureAwait(false);
            if (discovery is not { AuthorizationEndpoint.Length: > 0, TokenEndpoint.Length: > 0 })
            {
                return LauncherSignInResult.Failed("the identity provider's discovery document could not be read");
            }

            var state = NewSecret();
            var verifier = NewSecret();
            var nonce = NewSecret();
            var authorize = discovery.AuthorizationEndpoint + (discovery.AuthorizationEndpoint.Contains('?') ? "&" : "?")
                + string.Join('&', new Dictionary<string, string>
                {
                    ["client_id"] = target.ClientId,
                    ["redirect_uri"] = target.RedirectUri,
                    ["response_type"] = "code",
                    ["scope"] = target.Scope,
                    ["state"] = state,
                    ["nonce"] = nonce,
                    ["code_challenge"] = Challenge(verifier),
                    ["code_challenge_method"] = "S256",
                }.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(browserWait);
            var waiting = callback.WaitAsync(wait.Token);
            opened?.Invoke();
            openBrowser(authorize);

            IReadOnlyDictionary<string, string> answer;
            try
            {
                answer = await waiting.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return LauncherSignInResult.Failed($"nobody finished signing in within {browserWait.TotalMinutes:0} minutes");
            }

            if (!answer.TryGetValue("state", out var returned) || !string.Equals(returned, state, StringComparison.Ordinal))
            {
                return LauncherSignInResult.Failed("the redirect carried a state this sign-in did not send");
            }

            if (answer.TryGetValue("error", out var error))
            {
                return LauncherSignInResult.Failed($"the identity provider refused the sign-in ({error})");
            }

            if (!answer.TryGetValue("code", out var code) || code.Length == 0)
            {
                return LauncherSignInResult.Failed("the redirect carried no authorization code");
            }

            return await ExchangeAsync(target, discovery.TokenEndpoint, code, verifier, nonce, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return LauncherSignInResult.Failed($"the identity provider could not be reached ({ex.GetType().Name})");
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException)
        {
            // Not the provider: a local socket. Said as what it is, because the first version blamed the provider
            // for a failure on this machine's own listener (2026-09-23).
            return LauncherSignInResult.Failed($"the local sign-in listener failed ({ex.GetType().Name}: {ex.Message})");
        }
    }

    private async Task<LauncherSignInResult> ExchangeAsync(
        LauncherSignInTarget target, string tokenEndpoint, string code, string verifier, string nonce, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = target.RedirectUri,
                ["client_id"] = target.ClientId,
                ["code_verifier"] = verifier,
            }),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var token = Read<TokenResponse>(body);
        if (!response.IsSuccessStatusCode || token is not { AccessToken.Length: > 0 })
        {
            return LauncherSignInResult.Failed(
                $"the token exchange was refused ({(int)response.StatusCode}{(token?.Error is { } e ? ", " + e : string.Empty)})");
        }

        // The nonce ties the id_token to THIS sign-in; the studio does the check that matters (asking the provider
        // who the access token belongs to), and this one only stops a token from another sign-in being carried in.
        if (token.IdToken is { Length: > 0 } idToken && Nonce(idToken) is { } carried
            && !string.Equals(carried, nonce, StringComparison.Ordinal))
        {
            return LauncherSignInResult.Failed("the id_token's nonce did not match this sign-in");
        }

        var expires = token.ExpiresIn is > 0 ? _clock.GetUtcNow().AddSeconds(token.ExpiresIn.Value) : (DateTimeOffset?)null;
        return new LauncherSignInResult(
            new LauncherSession(
                target.Issuer,
                target.ClientId,
                token.AccessToken,
                string.IsNullOrEmpty(token.RefreshToken) ? null : token.RefreshToken,
                string.IsNullOrEmpty(token.IdToken) ? null : token.IdToken,
                expires),
            "signed in");
    }

    private async Task<Discovery?> DiscoverAsync(string issuer, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{issuer.TrimEnd('/')}/.well-known/openid-configuration");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? Read<Discovery>(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))
            : null;
    }

    /// <summary>A URL-safe random value: 32 bytes, base64url.</summary>
    internal static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>The S256 code challenge for a verifier.</summary>
    internal static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The nonce claim of an id_token's payload, or null when it cannot be read.</summary>
    internal static string? Nonce(string idToken)
    {
        var parts = idToken.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.TryGetProperty("nonce", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    private static T? Read<T>(string body) where T : class
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

    private sealed record Discovery(
        [property: JsonPropertyName("authorization_endpoint")] string? AuthorizationEndpoint,
        [property: JsonPropertyName("token_endpoint")] string? TokenEndpoint);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("id_token")] string? IdToken,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn,
        [property: JsonPropertyName("error")] string? Error);
}
