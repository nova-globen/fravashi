using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fravashi.Application.AgentRuntimes.Broker;

/// <summary>
/// Client-side broker references and constants (plan 04 B3). The minted broker token lands in the secret store
/// under <see cref="BrokerOptions.UserTokenSecretRef"/> (the raw bearer the agent CLI reads); this metadata and
/// the identity proof live under their own references so the raw token stays a bare string.
/// </summary>
public static class BrokerReferences
{
    /// <summary>Secret-store reference for the SSO identity proof (a <c>CredentialEnvelope</c> - the IdP access token).</summary>
    public const string IdentitySecretRef = "broker:identity";

    /// <summary>Secret-store reference for the minted broker token's metadata (subject, expiry, mint time, id).</summary>
    public const string TokenMetadataSecretRef = "broker:user-token:meta";

    /// <summary>The scope the client requests when minting (matches the broker's forward-required scope).</summary>
    public const string DefaultScope = "messages";
}

/// <summary>
/// Metadata about the currently-stored minted broker token. The raw token is opaque to the client (an HMAC the
/// broker signs), so its expiry/subject are recorded here so the refresher knows when to re-mint and the Settings
/// status can report validity honestly without decoding the token.
/// </summary>
public sealed record BrokerTokenMetadata(string Subject, string TokenId, DateTimeOffset MintedUtc, DateTimeOffset ExpiresUtc)
{
    /// <summary>
    /// The model patterns the broker said this token covers (its mint answer's <c>allowedModels</c>), or null
    /// when it named none. <see cref="BrokerModelPolicy"/> reads it; null falls back to the free tier's one model.
    /// </summary>
    public IReadOnlyList<string>? AllowedModels { get; init; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>True when the token is expired, or expires within <paramref name="skew"/> (default 5 min).</summary>
    public bool IsExpiredOrExpiring(DateTimeOffset now, TimeSpan? skew = null) =>
        now >= ExpiresUtc - (skew ?? TimeSpan.FromMinutes(5));

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static BrokerTokenMetadata? Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BrokerTokenMetadata>(stored, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The outcome of a mint attempt. <see cref="Subject"/>/<see cref="ExpiresAt"/> are set on success.</summary>
public sealed record BrokerMintResult(bool Success, string? Subject, DateTimeOffset? ExpiresAt, string? Error)
{
    /// <summary>Why it failed (backlog 235): a refusal is never retried as if it were the network.</summary>
    public BrokerMintFailure Failure { get; init; } = Success ? BrokerMintFailure.None : BrokerMintFailure.Unusable;

    /// <summary>The minted token's id (never its value), on success.</summary>
    public string? TokenId { get; init; }

    /// <summary>
    /// <see cref="Error"/> as parts when it names a time (a mint that could not reach the broker says when it tries
    /// again), so a page says it in the person's zone (backlog 246); null otherwise.
    /// </summary>
    public Fravashi.Application.Time.StampedWords? ErrorWords { get; init; }

    public static BrokerMintResult Ok(string subject, DateTimeOffset expiresAt, string? tokenId = null) =>
        new(true, subject, expiresAt, null) { TokenId = tokenId };

    public static BrokerMintResult Fail(string error, BrokerMintFailure failure = BrokerMintFailure.Unusable) =>
        new(false, null, null, error) { Failure = failure };
}

/// <summary>The outcome of a revoke attempt. Local token state is cleared regardless of the remote call.</summary>
public sealed record BrokerRevokeResult(bool Success, string? Error)
{
    public static BrokerRevokeResult Ok() => new(true, null);
    public static BrokerRevokeResult Fail(string error) => new(false, error);
}

/// <summary>
/// The honest broker-connection status for the Settings UI. Every "yes" is derived from a real check, never an
/// assumption: <see cref="SignedIn"/> from a stored identity proof, <see cref="TokenValid"/> from a stored token
/// whose recorded expiry is still in the future (assertion-gap rule).
/// </summary>
public sealed record BrokerConnectionStatus(
    bool Active,
    bool SignedIn,
    string? Identity,
    bool TokenValid,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastMintUtc)
{
    /// <summary>The stored token's id (never its value), whatever its validity; null when none is stored.</summary>
    public string? TokenId { get; init; }

    /// <summary>
    /// What is wrong with minting right now, in plain words (backlog 235): "Broker unreachable since 14:02 (...),
    /// retrying" during an outage, or the broker's refusal. Null when nothing is wrong.
    /// </summary>
    public string? Trouble { get; init; }

    /// <summary><see cref="Trouble"/> as parts, so the card says its times in the person's zone (backlog 246).</summary>
    public Fravashi.Application.Time.StampedWords? TroubleWords { get; init; }

    /// <summary>The single-user default posture: brokering off. Nothing to sign into, nothing minted.</summary>
    public static readonly BrokerConnectionStatus Inactive = new(false, false, null, false, null, null);
}

/// <summary>
/// Mints, refreshes, and revokes the per-user broker token from the SSO identity (plan 04 B3). All operations are
/// gated on <see cref="BrokerOptions.IsActive"/> - the single-user default does nothing. The identity proof (the
/// IdP access token) is read from the secret store under <see cref="BrokerReferences.IdentitySecretRef"/>; the
/// minted token is written under <see cref="BrokerOptions.UserTokenSecretRef"/> for the agent CLI to read.
/// </summary>
public interface IBrokerTokenService
{
    /// <summary>Mints a fresh broker token from the stored SSO identity and stores it (+ metadata). Idempotent to re-run.</summary>
    Task<BrokerMintResult> MintAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-mints only if the stored token is missing or near expiry; returns null when no action was needed. Since
    /// backlog 235 it also holds back while a refusal stands for the stored identity (that answer is returned without
    /// asking again) and while an outage's next attempt is not due yet.
    /// </summary>
    Task<BrokerMintResult?> RefreshIfNeededAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// <see cref="RefreshIfNeededAsync"/>, and also mints when the stored token is the one <paramref name="replacing"/>
    /// names: a token the broker refused a conversation (backlog 235). <paramref name="now"/> skips an outage's backoff,
    /// for a caller that has just seen the broker's host answer. A refusal still stands either way.
    /// </summary>
    Task<BrokerMintResult?> RenewAsync(string? replacing, bool now, CancellationToken cancellationToken = default) =>
        RefreshIfNeededAsync(cancellationToken);

    /// <summary>Revokes the current broker token at the broker and clears it locally.</summary>
    Task<BrokerRevokeResult> RevokeAsync(CancellationToken cancellationToken = default);

    /// <summary>The current, honestly-derived broker-connection status for the Settings UI.</summary>
    BrokerConnectionStatus GetStatus();
}

/// <summary>JSON shape the broker's mint endpoint returns (matches <c>MintResponse</c> on the broker).</summary>
public sealed record BrokerMintResponse(
    [property: JsonPropertyName("token")] string? Token,
    [property: JsonPropertyName("tokenId")] string? TokenId,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt,
    /// <summary>The canonical, IdP-derived identity the broker attributed the token to; absent on older brokers.</summary>
    [property: JsonPropertyName("subject")] string? Subject = null,
    /// <summary>The model patterns this token covers (e.g. <c>claude-haiku-5-5*</c>); absent on older brokers.</summary>
    [property: JsonPropertyName("allowedModels")] IReadOnlyList<string>? AllowedModels = null);
