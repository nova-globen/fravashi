using System.Text.Json;
using System.Text.Json.Serialization;
using Fravashi.Domain;

namespace Fravashi.Application.Onboarding.Auth;

/// <summary>
/// The credential a provider connection resolves through its <c>credential_ref</c>. It is stored in the
/// <see cref="Persistence.ISecretStore"/> (Data-Protection-backed) as a JSON envelope so an OAuth credential
/// can carry a refresh token and an expiry alongside the access token - the secret store only holds a single
/// opaque string per reference, so the structure travels inside it.
/// <para>
/// <b>Back-compatible with the PAT path.</b> A plain PAT was (and still can be) stored as a bare string; the
/// <see cref="CredentialEnvelope.Parse"/> reader treats any value that is not this JSON envelope as a raw PAT
/// (<see cref="AuthMethods.Pat"/>). So existing installs keep working with no migration, and the
/// secret→adapter bridge projects <see cref="AccessToken"/> for both shapes.
/// </para>
/// </summary>
/// <param name="AccessToken">The token the adapter uses (an OAuth access token, or the PAT itself).</param>
/// <param name="RefreshToken">The OAuth refresh token, when the grant returned one; null for a PAT.</param>
/// <param name="ExpiresUtc">When the access token expires, when known; null for a PAT (no expiry).</param>
/// <param name="Method">One of <see cref="AuthMethods"/> - how the token was obtained.</param>
public sealed record StoredCredential(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresUtc,
    string Method)
{
    /// <summary>A raw PAT, wrapped so the same storage path serves both the SSO and the token strategies.</summary>
    public static StoredCredential ForPat(string token) => new(token, null, null, AuthMethods.Pat);

    /// <summary>True when the token is expired, or expires within <paramref name="skew"/> (default 2 min).</summary>
    public bool IsExpiredOrExpiring(DateTimeOffset now, TimeSpan? skew = null) =>
        ExpiresUtc is { } expiry && now >= expiry - (skew ?? TimeSpan.FromMinutes(2));

    /// <summary>True when a refresh is possible - an OAuth credential that carries a refresh token.</summary>
    public bool CanRefresh => Method != AuthMethods.Pat && !string.IsNullOrEmpty(RefreshToken);
}

/// <summary>Serializes and parses the <see cref="StoredCredential"/> envelope, with PAT back-compatibility.</summary>
public static class CredentialEnvelope
{
    // A marker so a parse can distinguish the envelope from a bare PAT without a fragile "looks like JSON" guess.
    private const string Marker = "fravashi.cred.v1";

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(StoredCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return JsonSerializer.Serialize(new Envelope(Marker, credential.AccessToken, credential.RefreshToken, credential.ExpiresUtc, credential.Method), Options);
    }

    /// <summary>
    /// Parses a stored value. A valid envelope is returned as-is; any other non-empty string is treated as a
    /// legacy raw PAT; an empty/whitespace string returns null.
    /// </summary>
    public static StoredCredential? Parse(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        var trimmed = stored.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<Envelope>(stored, Options);
                // The former marker too (a credential stored before the internal rename, the bridge release): otherwise the
                // whole envelope would be read as a raw PAT and every call with it would fail.
                if (envelope is { Marker: Marker or FormerNames.CredentialEnvelopeMarker, AccessToken: { Length: > 0 } access })
                {
                    return new StoredCredential(access, envelope.RefreshToken, envelope.ExpiresUtc, envelope.Method ?? AuthMethods.Pat);
                }
            }
            catch (JsonException)
            {
                // Not our envelope - fall through and treat the whole value as a raw PAT.
            }
        }

        return StoredCredential.ForPat(stored);
    }

    private sealed record Envelope(
        [property: JsonPropertyName("m")] string? Marker,
        [property: JsonPropertyName("access")] string AccessToken,
        [property: JsonPropertyName("refresh")] string? RefreshToken,
        [property: JsonPropertyName("exp")] DateTimeOffset? ExpiresUtc,
        [property: JsonPropertyName("method")] string? Method);
}
