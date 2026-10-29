using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fravashi.Application.AgentRuntimes.Broker;
using Fravashi.Application.Network;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;

namespace Fravashi.Infrastructure.Onboarding.Auth;

/// <summary>
/// The owner-IdP sign-in flow (plan Phase 3, decision 9): OIDC authorization code + PKCE against
/// the configured issuer (e.g. a Stomme instance). Token trust comes from the DIRECT TLS
/// exchange with the issuer's token endpoint, the broker's documented precedent, so the id_token
/// payload is decoded for claims and its nonce checked, never signature-verified offline. The
/// session lands as a <see cref="StoredCredential"/> envelope at
/// <see cref="AuthSettings.IdpSessionSecretRef"/> and is COPIED to the broker identity ref,
/// pre-wiring the mint proof (proposed decision 3 in docs/plans/fravashi-auth-gate.md).
/// </summary>
public sealed class IdpAuthService
{
    /// <summary>The state-ledger provider tag distinguishing gate sign-ins from onboarding ones.</summary>
    public const string ProviderTag = "Idp";

    /// <summary>
    /// How far ahead of expiry the session is renewed. It must be LONGER than the refresh loop's interval
    /// (<see cref="IdpSessionRefreshHostedService.Interval"/>), or a tick can land just before the window opens and
    /// the next one after the session has already expired: with the old two minutes against a five-minute loop,
    /// the installed v0.9.31 showed the sign-in page for three minutes on 2026-09-23.
    /// </summary>
    public static readonly TimeSpan RefreshAhead = TimeSpan.FromMinutes(7);

    /// <summary>Secret ref for the raw id_token, kept only as the end-session hint.</summary>
    public const string IdTokenSecretRef = "auth:idp:id-token";

    private readonly HttpClient _http;
    private readonly IConfigurationStore _settings;
    private readonly ISecretStore _secrets;
    private readonly IAuthCodeStateStore _states;
    private readonly IConfigurationReloader _reloader;
    private readonly TimeProvider _time;
    private readonly Func<string, string?> _env;
    private readonly IdpSessionGrace? _grace;
    private readonly IdpSignInCoordinator _signIns;

    public IdpAuthService(
        HttpClient http,
        IConfigurationStore settings,
        ISecretStore secrets,
        IAuthCodeStateStore states,
        IConfigurationReloader reloader,
        TimeProvider? time = null,
        Func<string, string?>? env = null,
        IdpSessionGrace? grace = null,
        IdpSignInCoordinator? signIns = null)
    {
        _http = http;
        _settings = settings;
        _secrets = secrets;
        _states = states;
        _reloader = reloader;
        _time = time ?? TimeProvider.System;
        _env = env ?? Environment.GetEnvironmentVariable;
        _grace = grace;
        _signIns = signIns ?? new IdpSignInCoordinator(_time);
    }

    /// <summary>The studio's one sign-in (#372): which attempt is in progress, and when one completes.</summary>
    public IdpSignInCoordinator SignIns => _signIns;

    public bool IsConfigured => IdpCoordinates.Configured(_settings, _env);

    /// <summary>The issuer's host and port, which the refresh loop's reachability probe aims at (backlog 234); null when unconfigured or malformed.</summary>
    public NetworkEndpoint? IssuerEndpoint =>
        Uri.TryCreate(Issuer, UriKind.Absolute, out var issuer) && issuer.Host.Length > 0
            ? new NetworkEndpoint(issuer.Host, issuer.Port)
            : null;

    private string? Issuer => IdpCoordinates.Issuer(_settings, _env);

    private string? ClientId => IdpCoordinates.ClientId(_settings, _env);

    public sealed record Discovery(
        [property: JsonPropertyName("authorization_endpoint")] string? AuthorizationEndpoint,
        [property: JsonPropertyName("token_endpoint")] string? TokenEndpoint,
        [property: JsonPropertyName("userinfo_endpoint")] string? UserInfoEndpoint,
        [property: JsonPropertyName("end_session_endpoint")] string? EndSessionEndpoint);

    /// <summary>The issuer's discovery document; null when unreachable or malformed (hard-online: the caller surfaces it).</summary>
    public async Task<Discovery?> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var issuer = Issuer;
        if (string.IsNullOrWhiteSpace(issuer))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{issuer.TrimEnd('/')}/.well-known/openid-configuration");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Deserialize<Discovery>(payload);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Builds the authorize URL and parks state + PKCE verifier + nonce in the ledger; null when unconfigured or the IdP is unreachable.</summary>
    public Task<AuthCodeChallenge?> BeginSignInAsync(string redirectUri, CancellationToken cancellationToken = default) =>
        BeginSignInAsync(redirectUri, origin: string.Empty, cancellationToken);

    /// <summary>
    /// As <see cref="BeginSignInAsync(string, CancellationToken)"/>, and records the attempt as the studio's sign-in in
    /// progress, started by the page <paramref name="origin"/> names (#372), so the other windows say so.
    /// </summary>
    public async Task<AuthCodeChallenge?> BeginSignInAsync(string redirectUri, string origin, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return null;
        }

        var discovery = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        if (discovery is not { AuthorizationEndpoint.Length: > 0 })
        {
            return null;
        }

        var state = Pkce.NewVerifier();
        var verifier = Pkce.NewVerifier();
        var nonce = Pkce.NewVerifier();
        var query = new Dictionary<string, string>
        {
            ["client_id"] = ClientId!,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = _settings.Get(AuthSettings.IdpScope) ?? AuthSettings.IdpScopeDefault,
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = Pkce.ChallengeS256(verifier),
            ["code_challenge_method"] = "S256",
        };

        _states.Put(new AuthCodeStateEntry(
            state, ProviderTag, "idp", "idp", AuthSettings.IdpSessionSecretRef,
            Issuer, redirectUri, "gate", _time.GetUtcNow(),
            CodeVerifier: verifier, Nonce: nonce));
        _signIns.Begin(state, origin ?? string.Empty);

        return new AuthCodeChallenge(BuildUrl(discovery.AuthorizationEndpoint, query), state, redirectUri);
    }

    public Task<AuthCodeExchange> HandleCallbackAsync(
        string? code, string? state, string? error, CancellationToken cancellationToken = default) =>
        HandleCallbackAsync(code, state, error, caller: null, cancellationToken);

    /// <summary>
    /// Handles the identity provider's redirect back. The state is checked and consumed once (#372,
    /// <see cref="IdpSignInCoordinator.HandleOnceAsync"/>): an unknown or expired state is refused before anything else,
    /// and only the same state with the same code from the same <paramref name="caller"/>, arriving again within a minute,
    /// is answered with the first arrival's outcome instead of being refused. The exchange runs on its own budget, so
    /// <paramref name="cancellationToken"/> (the request's) only stops this caller waiting for it.
    /// </summary>
    public async Task<AuthCodeExchange> HandleCallbackAsync(
        string? code, string? state, string? error, string? caller, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(state))
        {
            return AuthCodeExchange.Fail(IdpSignInCoordinator.UnknownStateWords);
        }

        var outcome = _signIns.HandleOnceAsync(
            state,
            code,
            caller,
            consume: issued => _states.Consume(issued) is { Provider: ProviderTag } entry ? entry : null,
            exchange: (entry, budget) => ExchangeAsync(entry, code, error, budget));
        return await outcome.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<AuthCodeExchange> ExchangeAsync(
        AuthCodeStateEntry entry, string? code, string? error, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(error))
        {
            return AuthCodeExchange.Fail($"The identity provider refused the sign-in: {error}.");
        }

        if (string.IsNullOrEmpty(code))
        {
            return AuthCodeExchange.Fail("The identity provider returned no authorization code.");
        }

        var discovery = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        if (discovery is not { TokenEndpoint.Length: > 0 })
        {
            return AuthCodeExchange.Fail("The identity provider's token endpoint could not be discovered.");
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = entry.RedirectUri,
            ["client_id"] = ClientId ?? string.Empty,
        };
        if (entry.CodeVerifier is { Length: > 0 } verifier)
        {
            form["code_verifier"] = verifier;
        }

        if (_secrets.Get(AuthSettings.IdpClientSecretRef) is { Length: > 0 } clientSecret)
        {
            form["client_secret"] = clientSecret;
        }

        string responseBody;
        System.Net.HttpStatusCode status;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, discovery.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(form),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            status = response.StatusCode;
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AuthCodeExchange.Fail($"The token exchange could not reach the identity provider: {ex.Message}");
        }

        var token = Deserialize<TokenResponse>(responseBody);
        if (token is not { AccessToken.Length: > 0 })
        {
            var reason = token?.ErrorDescription ?? token?.Error ?? $"the token endpoint returned {(int)status}";
            return AuthCodeExchange.Fail($"The identity provider did not return an access token: {reason}.");
        }

        if (token.IdToken is not { Length: > 0 } idToken)
        {
            return AuthCodeExchange.Fail("The identity provider returned no id_token; openid scope is required.");
        }

        var claims = DecodeIdTokenPayload(idToken);
        if (claims is null || !string.Equals(claims.Nonce, entry.Nonce, StringComparison.Ordinal))
        {
            return AuthCodeExchange.Fail("The id_token's nonce did not match this sign-in attempt.");
        }

        if (string.IsNullOrWhiteSpace(claims.Subject))
        {
            return AuthCodeExchange.Fail("The id_token carried no subject claim.");
        }

        var expiresUtc = token.ExpiresIn is > 0
            ? _time.GetUtcNow().AddSeconds(token.ExpiresIn.Value)
            : (DateTimeOffset?)null;
        var credential = new StoredCredential(
            token.AccessToken, NullIfEmpty(token.RefreshToken), expiresUtc, AuthMethods.AuthCode);
        var envelope = CredentialEnvelope.Serialize(credential);

        _secrets.Set(AuthSettings.IdpSessionSecretRef, envelope);
        _secrets.Set(IdTokenSecretRef, idToken);
        // Decision 9: the studio session doubles as the broker mint proof.
        _secrets.Set(BrokerReferences.IdentitySecretRef, envelope);
        var display = claims.Name ?? claims.PreferredUsername ?? claims.Subject;
        _settings.Set(AuthSettings.IdpSignedInSubject, claims.Subject);
        _settings.Set(AuthSettings.IdpSignedInName, display);
        _settings.Set(AuthSettings.IdpSignedInEmail, claims.Email);
        _reloader.Reload();
        _grace?.Renewed();

        return AuthCodeExchange.Ok(credential, $"Signed in as {display}.");
    }

    /// <summary>
    /// Establishes the session from one the LAUNCHER obtained before the container started (backlog 94, ADR 0026 as
    /// as decided). Nothing about it is trusted because of where it came from: it must name this studio's
    /// own issuer and client, it must not have expired, and the identity provider itself must say, through its
    /// userinfo endpoint, whom the access token belongs to, and when an id_token came with it, that it is the same
    /// subject. Only then is it stored exactly as a session this studio obtained itself would be. Any refusal
    /// leaves nothing behind, and the caller falls back to the sign-in page.
    /// </summary>
    public async Task<AuthCodeExchange> AdoptHandedSessionAsync(
        HandedIdpSession handed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handed);
        if (!IsConfigured)
        {
            return AuthCodeExchange.Fail("This studio has no identity provider configured.");
        }

        if (!string.Equals(handed.Issuer.TrimEnd('/'), Issuer!.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(handed.ClientId, ClientId, StringComparison.Ordinal))
        {
            return AuthCodeExchange.Fail("The handed session was obtained from a different identity provider or client.");
        }

        if (string.IsNullOrWhiteSpace(handed.AccessToken))
        {
            return AuthCodeExchange.Fail("The handed session carries no access token.");
        }

        if (handed.ExpiresUtc is { } expires && expires <= _time.GetUtcNow())
        {
            return AuthCodeExchange.Fail("The handed session has already expired.");
        }

        var discovery = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        if (discovery is not { UserInfoEndpoint.Length: > 0 })
        {
            return AuthCodeExchange.Fail("The identity provider's userinfo endpoint could not be discovered.");
        }

        UserInfo? who;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, discovery.UserInfoEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", handed.AccessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return AuthCodeExchange.Fail(
                    $"The identity provider did not accept the handed session ({(int)response.StatusCode}).");
            }

            who = Deserialize<UserInfo>(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AuthCodeExchange.Fail($"The identity provider could not be reached to check the handed session: {ex.Message}");
        }

        if (who is not { Subject.Length: > 0 })
        {
            return AuthCodeExchange.Fail("The identity provider named no subject for the handed session.");
        }

        var claims = handed.IdToken is { Length: > 0 } idToken ? DecodeIdTokenPayload(idToken) : null;
        if (handed.IdToken is { Length: > 0 } && !string.Equals(claims?.Subject, who.Subject, StringComparison.Ordinal))
        {
            return AuthCodeExchange.Fail("The handed id_token names a different subject than the access token.");
        }

        var credential = new StoredCredential(
            handed.AccessToken, NullIfEmpty(handed.RefreshToken), handed.ExpiresUtc, AuthMethods.AuthCode);
        var envelope = CredentialEnvelope.Serialize(credential);
        _secrets.Set(AuthSettings.IdpSessionSecretRef, envelope);
        if (handed.IdToken is { Length: > 0 } hint)
        {
            _secrets.Set(IdTokenSecretRef, hint);
        }

        _secrets.Set(BrokerReferences.IdentitySecretRef, envelope);
        var display = who.Name ?? who.PreferredUsername ?? claims?.Name ?? claims?.PreferredUsername ?? who.Subject;
        _settings.Set(AuthSettings.IdpSignedInSubject, who.Subject);
        _settings.Set(AuthSettings.IdpSignedInName, display);
        _settings.Set(AuthSettings.IdpSignedInEmail, who.Email ?? claims?.Email);
        _reloader.Reload();
        _grace?.Renewed();

        return AuthCodeExchange.Ok(credential, $"Signed in as {display}.");
    }

    /// <summary>What this studio tells the host about its sign-in (backlog 94); null when no provider is configured.</summary>
    public IdpSignInCoordinates? Coordinates(bool sessionUsable) =>
        IsConfigured
            ? new IdpSignInCoordinates(Issuer!, ClientId!, _settings.Get(AuthSettings.IdpScope) ?? AuthSettings.IdpScopeDefault, sessionUsable)
            : null;

    private sealed record UserInfo(
        [property: JsonPropertyName("sub")] string? Subject,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("preferred_username")] string? PreferredUsername,
        [property: JsonPropertyName("email")] string? Email);

    /// <summary>
    /// Renews the session before it expires (grant_type=refresh_token). True when the session is
    /// healthy afterwards; false when there is nothing to refresh, the IdP refused, or it could not be
    /// reached. Since backlog 234 the last of those opens the sign-in grace (<see cref="IdpSessionGrace"/>);
    /// see <see cref="RefreshAsync"/> for the three-way answer. While the grace's backoff holds this does
    /// NOT call the identity provider, so a page load never waits on a dead network; the loop does.
    /// </summary>
    public async Task<bool> RefreshIfNeededAsync(CancellationToken cancellationToken = default) =>
        (await RefreshAsync(force: false, cancellationToken).ConfigureAwait(false)).Healthy;

    /// <summary>
    /// Renews the session when it is due, and answers how that went (backlog 234, plan @64): renewed, refused (any 4xx
    /// but 408 and 429, or an answer with no access token: the session is left to expire and the gate closes, as
    /// before), or unreachable (slice A's <see cref="NetworkFailure"/> says the network failed, or the IdP answered
    /// 5xx, 408 or 429: the grace opens). <paramref name="force"/> asks even while the backoff holds; the refresh loop
    /// passes it because the loop is what keeps the backoff. Every reason is a host plus plain words or a status, never a token.
    /// </summary>
    public async Task<IdpRefreshResult> RefreshAsync(bool force, CancellationToken cancellationToken = default)
    {
        var asked = _secrets.Get(AuthSettings.IdpSessionSecretRef);
        var credential = CredentialEnvelope.Parse(asked);
        if (credential is null)
        {
            return new(IdpRefreshOutcome.NoSession, "no session is stored");
        }

        var now = _time.GetUtcNow();
        if (!credential.IsExpiredOrExpiring(now, RefreshAhead))
        {
            return new(IdpRefreshOutcome.Fresh);
        }

        if (!credential.CanRefresh)
        {
            return new(IdpRefreshOutcome.NoSession, "the session carries no refresh token");
        }

        if (!force && _grace?.Outage is { } down && now < down.NextAttemptAt)
        {
            return new(IdpRefreshOutcome.Unreachable, down.Reason);
        }

        var result = await AskToRenewAsync(credential, cancellationToken).ConfigureAwait(false);
        if (result.Outcome is IdpRefreshOutcome.Refused or IdpRefreshOutcome.Unreachable
            && !string.Equals(_secrets.Get(AuthSettings.IdpSessionSecretRef), asked, StringComparison.Ordinal))
        {
            // #372: a sign-in replaced the session while this renewal was on its way, so the answer is about a session
            // that no longer exists. Recording it would put "refused to renew ... Sign in again" back up right after the
            // sign-in had cleared it.
            return new(IdpRefreshOutcome.Fresh, "a newer sign-in replaced the session while it was being renewed");
        }

        switch (result.Outcome)
        {
            case IdpRefreshOutcome.Renewed:
                _grace?.Renewed();
                break;
            case IdpRefreshOutcome.Unreachable:
                _grace?.Unreachable(result.Reason ?? "the identity provider could not be reached");
                break;
            case IdpRefreshOutcome.Refused:
                _grace?.Refuse($"The identity provider refused to renew the session ({result.Reason}). Sign in again.");
                break;
        }

        return result;
    }

    private async Task<IdpRefreshResult> AskToRenewAsync(StoredCredential credential, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(Issuer))
        {
            return new(IdpRefreshOutcome.Refused, "no identity provider is configured");
        }

        var issuerHost = IssuerEndpoint?.Host ?? "the identity provider";
        Discovery? discovery;
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{Issuer!.TrimEnd('/')}/.well-known/openid-configuration");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Answered(issuerHost, response.StatusCode, error: null, "its discovery document");
            }

            discovery = Deserialize<Discovery>(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (NetworkFailure.Classify(ex, cancellationToken) is { IsNetwork: true } failure)
        {
            return new(IdpRefreshOutcome.Unreachable, $"could not reach {issuerHost}: {failure.Words}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(IdpRefreshOutcome.Refused, $"its discovery document could not be read: {ex.GetType().Name}");
        }

        if (discovery is not { TokenEndpoint.Length: > 0 })
        {
            return new(IdpRefreshOutcome.Refused, "its discovery document names no token endpoint");
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = credential.RefreshToken!,
            ["client_id"] = ClientId ?? string.Empty,
        };
        if (_secrets.Get(AuthSettings.IdpClientSecretRef) is { Length: > 0 } clientSecret)
        {
            form["client_secret"] = clientSecret;
        }

        var tokenHost = Uri.TryCreate(discovery.TokenEndpoint, UriKind.Absolute, out var tokenUri) ? tokenUri.Host : issuerHost;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, discovery.TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(form),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var token = Deserialize<TokenResponse>(body);
            if (!response.IsSuccessStatusCode)
            {
                return Answered(tokenHost, response.StatusCode, token?.Error, "its token endpoint");
            }

            if (token is not { AccessToken.Length: > 0 })
            {
                return new(IdpRefreshOutcome.Refused, "it answered without an access token");
            }

            var expiresUtc = token.ExpiresIn is > 0
                ? _time.GetUtcNow().AddSeconds(token.ExpiresIn.Value)
                : (DateTimeOffset?)null;
            // The refresh token rotates when the IdP returns a new one; otherwise the old stays.
            var renewed = new StoredCredential(
                token.AccessToken,
                NullIfEmpty(token.RefreshToken) ?? credential.RefreshToken,
                expiresUtc,
                AuthMethods.AuthCode);
            var envelope = CredentialEnvelope.Serialize(renewed);
            _secrets.Set(AuthSettings.IdpSessionSecretRef, envelope);
            // Backlog 235's plug: a renewed proof has a new fingerprint, so a broker refusal of the old proof asks again.
            _secrets.Set(BrokerReferences.IdentitySecretRef, envelope);
            if (token.IdToken is { Length: > 0 } freshIdToken)
            {
                _secrets.Set(IdTokenSecretRef, freshIdToken);
            }

            return new(IdpRefreshOutcome.Renewed);
        }
        catch (Exception ex) when (NetworkFailure.Classify(ex, cancellationToken) is { IsNetwork: true } failure)
        {
            return new(IdpRefreshOutcome.Unreachable, $"could not reach {tokenHost}: {failure.Words}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(IdpRefreshOutcome.Refused, $"the renewal failed: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// An answer that was not a renewal. 5xx, 408 and 429 say the IdP is there but cannot serve now, which is the
    /// network's side of the line (the broker's #235 drew it the same way); any other status is the IdP saying no.
    /// Only the OAuth error CODE is carried, never its free-text description.
    /// </summary>
    private static IdpRefreshResult Answered(string host, HttpStatusCode status, string? error, string what)
    {
        var code = (int)status;
        if (code >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
        {
            return new(IdpRefreshOutcome.Unreachable, $"{host} answered {code} for {what}");
        }

        var named = error is { Length: > 0 } && error.Length <= 64 && error.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')
            ? $"{error}, "
            : string.Empty;
        return new(IdpRefreshOutcome.Refused, $"{named}HTTP {code} from {what}");
    }

    /// <summary>The IdP's end-session URL with the id_token hint, or null when it offers none.</summary>
    public async Task<string?> BuildEndSessionUrlAsync(string postLogoutRedirectUri, CancellationToken cancellationToken = default)
    {
        var discovery = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        if (discovery is not { EndSessionEndpoint.Length: > 0 })
        {
            return null;
        }

        var query = new Dictionary<string, string>
        {
            ["post_logout_redirect_uri"] = postLogoutRedirectUri,
        };
        if (_secrets.Get(IdTokenSecretRef) is { Length: > 0 } idToken)
        {
            query["id_token_hint"] = idToken;
        }

        return BuildUrl(discovery.EndSessionEndpoint, query);
    }

    /// <summary>Removes the session, the id_token hint, the identity rows, and the broker mint proof.</summary>
    public void ClearSession()
    {
        _secrets.Remove(AuthSettings.IdpSessionSecretRef);
        _secrets.Remove(IdTokenSecretRef);
        _secrets.Remove(BrokerReferences.IdentitySecretRef);
        _settings.Remove(AuthSettings.IdpSignedInSubject);
        _settings.Remove(AuthSettings.IdpSignedInName);
        _settings.Remove(AuthSettings.IdpSignedInEmail);
        _reloader.Reload();
        _grace?.Renewed();
    }

    /// <summary>
    /// Decodes the id_token's PAYLOAD segment (base64url JSON). Deliberately no signature check:
    /// the token arrived over the direct TLS exchange with the issuer (proposed decision 2).
    /// </summary>
    public static IdTokenClaims? DecodeIdTokenPayload(string idToken)
    {
        var parts = idToken.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var json = Convert.FromBase64String(payload);
            return JsonSerializer.Deserialize<IdTokenClaims>(json);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    public sealed record IdTokenClaims(
        [property: JsonPropertyName("sub")] string? Subject,
        [property: JsonPropertyName("nonce")] string? Nonce,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("preferred_username")] string? PreferredUsername,
        [property: JsonPropertyName("email")] string? Email);

    private static string BuildUrl(string baseUrl, IReadOnlyDictionary<string, string> query)
    {
        var pairs = query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}");
        var separator = baseUrl.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{baseUrl}{separator}{string.Join('&', pairs)}";
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

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("id_token")] string? IdToken,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? ErrorDescription);
}
