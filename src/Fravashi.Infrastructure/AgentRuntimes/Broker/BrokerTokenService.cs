using System.Net.Http.Json;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.AgentRuntimes.Broker;
using Fravashi.Application.Network;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;
using Microsoft.Extensions.Logging;
using Fravashi.Domain;

namespace Fravashi.Infrastructure.AgentRuntimes.Broker;

/// <summary>
/// <see cref="IBrokerTokenService"/> over <see cref="ISecretStore"/> and the broker's mint/revoke endpoints
/// (plan 04 B3). When the broker tier is active it turns the SSO identity into a short-lived broker token: read
/// the stored IdP access token (the identity proof), call <c>POST {BaseUrl}/broker/token</c>, and store the
/// minted token under <see cref="BrokerOptions.UserTokenSecretRef"/> - the exact ref the agent-runtime credential
/// layer reads as the bearer. Everything here is a no-op when <see cref="BrokerOptions.IsActive"/> is false: the
/// single-user default never signs in, mints, or revokes.
/// </summary>
public sealed class BrokerTokenService : IBrokerTokenService
{
    private readonly ISecretStore _secrets;
    private readonly BrokerOptions _broker;
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly ILogger<BrokerTokenService> _logger;
    private readonly BrokerTokenHealth _health;

    /// <param name="health">
    /// The process's one record of an outage or a refusal (backlog 235). The typed client is built per use, so this is
    /// what carries the backoff and the refusal from one attempt to the next; a test may leave it out.
    /// </param>
    public BrokerTokenService(
        ISecretStore secrets,
        BrokerOptions broker,
        HttpClient http,
        TimeProvider? clock = null,
        ILogger<BrokerTokenService>? logger = null,
        BrokerTokenHealth? health = null)
    {
        _secrets = secrets;
        _broker = broker ?? BrokerOptions.Disabled;
        _http = http;
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<BrokerTokenService>.Instance;
        _health = health ?? new BrokerTokenHealth(_clock);
    }

    public async Task<BrokerMintResult> MintAsync(CancellationToken cancellationToken = default)
    {
        if (!_broker.IsActive)
        {
            return BrokerMintResult.Fail("The remote credential broker is not enabled (single-user default).", BrokerMintFailure.Inactive);
        }

        var identity = CredentialEnvelope.Parse(_secrets.Get(BrokerReferences.IdentitySecretRef));
        if (identity is null || string.IsNullOrWhiteSpace(identity.AccessToken))
        {
            return BrokerMintResult.Fail(NotSignedInWords, BrokerMintFailure.NotSignedIn);
        }

        // The broker derives the canonical user id from the IdP; the claimed userId is only a hint. Send the
        // last-known subject if we have one so the audit reads sensibly even before the first mint resolves it.
        var claimedUserId = BrokerTokenMetadata.Parse(_secrets.Get(BrokerReferences.TokenMetadataSecretRef))?.Subject
                            ?? "broker-user";

        BrokerMintResponse? minted;
        try
        {
            using var response = await _http.PostAsJsonAsync(
                MintUrl(),
                new { userId = claimedUserId, identityProof = identity.AccessToken, scope = BrokerReferences.DefaultScope },
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return await AnsweredNoAsync(response, BrokerTokenHealth.Fingerprint(identity.AccessToken), cancellationToken)
                    .ConfigureAwait(false);
            }

            minted = await response.Content.ReadFromJsonAsync<BrokerMintResponse>(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Backlog 235: slice A's classifier decides what is the network. A mint is safe to repeat (a second token is
            // harmless), so a request that may have arrived is retried exactly like one that never left. HttpClient's
            // own timeout is a cancellation the caller did not ask for, which is why this catch is not "not cancelled".
            var failure = NetworkFailure.Classify(ex, cancellationToken);
            if (failure.IsNetwork)
            {
                var outage = _health.Unreachable($"could not reach {BrokerHost()}: {failure.Words}");
                _logger.LogWarning(
                    "Minting a broker token could not reach the broker: {Kind} ({Detail}); next attempt at {Next:o}.",
                    failure.Kind, failure.Detail, outage.NextAttemptAt);
                return CouldNotReach(outage);
            }

            // It answered, with something that is not a mint response (a body that is not JSON, say).
            _health.Answered();
            _logger.LogWarning("The broker's mint response could not be read: {Error}", ex.GetType().Name);
            return BrokerMintResult.Fail("The broker returned an unusable mint response.");
        }

        if (minted is not { Token.Length: > 0, TokenId.Length: > 0 } || minted.ExpiresAt is not { } expiresAt)
        {
            _health.Answered();
            return BrokerMintResult.Fail("The broker returned an unusable mint response.");
        }

        // The broker resolves the real identity from the IdP; prefer what it reports over our own claim, so the
        // Settings status names who the token actually belongs to (an older broker omits it, hence the fallback).
        var subject = string.IsNullOrWhiteSpace(minted.Subject) ? claimedUserId : minted.Subject!;
        var metadata = new BrokerTokenMetadata(subject, minted.TokenId, _clock.GetUtcNow(), expiresAt)
        {
            AllowedModels = minted.AllowedModels,
        };

        // Store the raw token where the agent CLI reads it, and the metadata beside it for refresh + status.
        _secrets.Set(_broker.UserTokenSecretRef, minted.Token);
        _secrets.Set(BrokerReferences.TokenMetadataSecretRef, metadata.Serialize());

        _logger.LogInformation("Minted a broker token (id {TokenId}) valid until {ExpiresAt:o}.", minted.TokenId, expiresAt);
        _health.Minted(minted.TokenId);
        return BrokerMintResult.Ok(subject, expiresAt, minted.TokenId);
    }

    /// <summary>The words when no identity proof is stored.</summary>
    public const string NotSignedInWords =
        "Not signed in - sign in with your identity provider before minting a broker token.";

    /// <summary>
    /// The broker answered and did not mint (backlog 235). A 5xx, 408 or 429 says it cannot serve right now, which is
    /// retried on the backoff like the network. Anything else is the broker saying no, and nothing asks again by itself:
    /// a refusal retried every few minutes is a loop the broker's own containment counts against the person (backlog 64).
    /// <para>
    /// Two kinds of no, remembered differently, because the identity provider renews the access token about hourly
    /// (<c>IdpAuthService.RefreshIfNeededAsync</c> rewrites the mint proof each time). A 401 refuses the PROOF, so it is
    /// keyed to that access token and a renewed sign-in asks again: that is where backlog 234's refresh plugs in. Every
    /// other refusal (403 <c>not_allowlisted</c>, <c>not_entitled</c>, a suspension) refuses the PERSON, whom a renewed
    /// access token does not change, so it stands until the person presses Mint token now.
    /// </para>
    /// </summary>
    private async Task<BrokerMintResult> AnsweredNoAsync(
        HttpResponseMessage response, string proofFingerprint, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        if (status >= 500 || status is 408 or 429)
        {
            var outage = _health.Unreachable($"the broker answered {status}");
            _logger.LogWarning("The broker could not mint right now ({Status}); next attempt at {Next:o}.", status, outage.NextAttemptAt);
            return CouldNotReach(outage);
        }

        var (error, subject) = await ReadRefusalAsync(response, cancellationToken).ConfigureAwait(false);
        var words = RefusalWords(status, error, subject);
        _health.Refuse(words, status == 401 ? proofFingerprint : null);
        _logger.LogWarning("The broker refused to mint a token: {Status} {Error}.", status, error ?? "(no reason given)");
        return BrokerMintResult.Fail(words, BrokerMintFailure.Refused);
    }

    /// <summary>
    /// What a refusal says, in plain words. 401 <c>identity_rejected</c> is the sign-in itself no longer being accepted,
    /// which is backlog 234's ground: this only says so, and a renewed sign-in (a changed identity proof) is what asks
    /// again. Any other refusal stands until the person asks.
    /// </summary>
    public static string RefusalWords(int status, string? error, string? subject)
    {
        var who = string.IsNullOrWhiteSpace(subject) ? "your identity" : subject;
        const string Again =
            $"{ProductName.Short} will not ask again by itself; Mint token now (Settings > Connections > Credential broker) asks again.";
        return error switch
        {
            "identity_rejected" =>
                "The broker no longer accepts your sign-in (identity_rejected). Sign in again under Settings > Connections > "
                + $"Credential broker; {ProductName.Short} asks again only once the sign-in is renewed.",
            _ when status == 401 =>
                $"The broker did not accept your sign-in ({status}{(string.IsNullOrWhiteSpace(error) ? string.Empty : " " + error)}); "
                + $"{ProductName.Short} asks again only once the sign-in is renewed.",
            "not_allowlisted" =>
                $"The broker refused to mint a token: {who} is not on its allowlist (not_allowlisted). An operator has to entitle it. " + Again,
            "not_entitled" =>
                $"The broker refused to mint a token: {who} is not entitled to it (not_entitled). An operator has to entitle it. " + Again,
            { Length: > 0 } reason =>
                $"The broker refused to mint a token ({status} {reason}). " + Again,
            _ => $"The broker refused to mint a token ({status}). " + Again,
        };
    }

    // Backlog 246: the words as parts too, so a page says the next attempt in the person's zone; Error is unchanged.
    private static BrokerMintResult CouldNotReach(BrokerOutage outage) =>
        BrokerMintResult.Fail(outage.MintWords.Plain(), BrokerMintFailure.Unreachable) with { ErrorWords = outage.MintWords };

    // The broker's refusal body: { "error": "...", "subject": "..." }. Both are short codes and the caller's own id,
    // never a secret; they are capped anyway, because a page in front of the broker could answer anything.
    private static async Task<(string? Error, string? Subject)> ReadRefusalAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return (Field(document.RootElement, "error"), Field(document.RootElement, "subject"));
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, null);
        }

        static string? Field(System.Text.Json.JsonElement root, string name) =>
            root.ValueKind == System.Text.Json.JsonValueKind.Object
            && root.TryGetProperty(name, out var value)
            && value.ValueKind == System.Text.Json.JsonValueKind.String
            && value.GetString() is { Length: > 0 } text
                ? (text.Length > 80 ? text[..80] : text)
                : null;
    }

    private string BrokerHost() =>
        Uri.TryCreate(_broker.BaseUrl?.Trim(), UriKind.Absolute, out var url) ? url.Host : "the broker";

    /// <summary>
    /// How far ahead of expiry the broker token is re-minted: LONGER than the refresh loop's interval
    /// (<see cref="BrokerTokenRefreshHostedService.Interval"/>) with a margin, so a tick can never land just before the
    /// window and the next one after expiry. The same gap left the identity session expired for three minutes in the
    /// installed v0.9.31 (2026-09-23); this window equalled its interval exactly, one jitter away from the same.
    /// </summary>
    public static readonly TimeSpan RenewAhead = TimeSpan.FromMinutes(7);

    public Task<BrokerMintResult?> RefreshIfNeededAsync(CancellationToken cancellationToken = default) =>
        RenewAsync(replacing: null, now: false, cancellationToken);

    /// <summary>
    /// Mints when the stored token is missing or near expiry, or when it is the token <paramref name="replacing"/>
    /// names (one the broker refused a conversation, backlog 235). Returns null when nothing was due. It does NOT call
    /// the broker while a refusal stands for the stored identity (the refusal is returned instead), nor while an
    /// outage's next attempt is not due yet unless <paramref name="now"/> says the network was just seen to answer.
    /// </summary>
    public async Task<BrokerMintResult?> RenewAsync(string? replacing, bool now, CancellationToken cancellationToken = default)
    {
        if (!_broker.IsActive)
        {
            return null;
        }

        // Nothing to refresh into if the user never signed in.
        if (CredentialEnvelope.Parse(_secrets.Get(BrokerReferences.IdentitySecretRef)) is not { } identity)
        {
            return replacing is null ? null : BrokerMintResult.Fail(NotSignedInWords, BrokerMintFailure.NotSignedIn);
        }

        var token = _secrets.Get(_broker.UserTokenSecretRef);
        var metadata = BrokerTokenMetadata.Parse(_secrets.Get(BrokerReferences.TokenMetadataSecretRef));

        var needsMint = string.IsNullOrWhiteSpace(token)
                        || metadata is null
                        || metadata.IsExpiredOrExpiring(_clock.GetUtcNow(), RenewAhead)
                        || (replacing is not null && string.Equals(metadata.TokenId, replacing, StringComparison.Ordinal));
        if (!needsMint)
        {
            return null;
        }

        // Backlog 235: a refusal is never retried as if it were the network. A refusal of the person stands until they
        // press Mint token now; a refusal of the proof (401) stands until the proof is renewed, which is where backlog
        // 234's refresh plugs in (see AnsweredNoAsync).
        if (_health.Refused(BrokerTokenHealth.Fingerprint(identity.AccessToken)) && _health.Refusal is { } refused)
        {
            return BrokerMintResult.Fail(refused.Words, BrokerMintFailure.Refused);
        }

        if (!now && _health.BackingOff(_clock.GetUtcNow()) && _health.Outage is { } outage)
        {
            return CouldNotReach(outage);
        }

        return await MintAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<BrokerRevokeResult> RevokeAsync(CancellationToken cancellationToken = default)
    {
        var token = _secrets.Get(_broker.UserTokenSecretRef);

        // Always clear the local token so the agent CLI cannot keep using it, even if the remote call fails.
        void ClearLocal()
        {
            _secrets.Remove(_broker.UserTokenSecretRef);
            _secrets.Remove(BrokerReferences.TokenMetadataSecretRef);
        }

        if (!_broker.IsActive || string.IsNullOrWhiteSpace(token))
        {
            ClearLocal();
            return BrokerRevokeResult.Ok();
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, RevokeUrl())
            {
                Content = JsonContent.Create(new { allMyTokens = true }),
            };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            ClearLocal();
            return response.IsSuccessStatusCode
                ? BrokerRevokeResult.Ok()
                : BrokerRevokeResult.Fail($"The broker returned {(int)response.StatusCode}; the token was cleared locally.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Revoking the broker token failed to reach the broker; clearing it locally.");
            ClearLocal();
            return BrokerRevokeResult.Fail("Could not reach the broker; the token was cleared locally.");
        }
    }

    public BrokerConnectionStatus GetStatus()
    {
        if (!_broker.IsActive)
        {
            return BrokerConnectionStatus.Inactive;
        }

        var signedIn = CredentialEnvelope.Parse(_secrets.Get(BrokerReferences.IdentitySecretRef)) is not null;
        var token = _secrets.Get(_broker.UserTokenSecretRef);
        var metadata = BrokerTokenMetadata.Parse(_secrets.Get(BrokerReferences.TokenMetadataSecretRef));

        // Assertion-gap rule: "valid" only when a token is actually stored AND its recorded expiry is in the future.
        var tokenValid = !string.IsNullOrWhiteSpace(token)
                         && metadata is not null
                         && _clock.GetUtcNow() < metadata.ExpiresUtc;

        var trouble = _health.Sentence;
        return new BrokerConnectionStatus(
            Active: true,
            SignedIn: signedIn,
            Identity: metadata?.Subject,
            TokenValid: tokenValid,
            ExpiresAt: metadata?.ExpiresUtc,
            LastMintUtc: metadata?.MintedUtc)
        {
            TokenId = string.IsNullOrWhiteSpace(token) ? null : metadata?.TokenId,
            Trouble = trouble?.Plain(),
            TroubleWords = trouble,
        };
    }

    /// <summary>The process's record of an outage or a refusal, for the refresher's schedule.</summary>
    public BrokerTokenHealth Health => _health;

    private Uri MintUrl() => new(new Uri(_broker.BaseUrl!.TrimEnd('/') + "/"), "broker/token");

    private Uri RevokeUrl() => new(new Uri(_broker.BaseUrl!.TrimEnd('/') + "/"), "broker/revoke");
}
