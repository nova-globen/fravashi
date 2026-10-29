namespace Fravashi.Application.Onboarding.Auth;

/// <summary>
/// One pending authorization-code sign-in, held between the wizard issuing the authorize URL and the browser
/// returning to the callback. It ties the opaque <see cref="State"/> to the connection the token is for, so the
/// callback knows where to store the result - and, by requiring the state it issued, guards against CSRF.
/// </summary>
/// <param name="State">The anti-forgery value echoed in the callback.</param>
/// <param name="Provider">The onboarding provider being signed in (e.g. Confluence / Jira).</param>
/// <param name="Area">The wizard area: <c>source</c> / <c>wiki</c> / <c>board</c>.</param>
/// <param name="ConnectionId">The provider-connection id the credential is written to.</param>
/// <param name="CredentialRef">The secret-store reference the credential envelope is stored under.</param>
/// <param name="BaseUrl">The provider base URL captured at sign-in start, or null.</param>
/// <param name="RedirectUri">The redirect URI used (must match on exchange).</param>
/// <param name="ReturnMode">Where to send the browser after the callback (<c>wizard</c> or <c>settings</c>).</param>
/// <param name="CreatedUtc">When the state was issued (for expiry).</param>
/// <param name="CodeVerifier">The PKCE code verifier (RFC 7636) when the flow uses one; null otherwise.</param>
/// <param name="Nonce">The OIDC nonce echoed inside the id_token when the flow uses one; null otherwise.</param>
public sealed record AuthCodeStateEntry(
    string State,
    string Provider,
    string Area,
    string ConnectionId,
    string CredentialRef,
    string? BaseUrl,
    string RedirectUri,
    string ReturnMode,
    DateTimeOffset CreatedUtc,
    string? CodeVerifier = null,
    string? Nonce = null);

/// <summary>
/// Holds pending authorization-code sign-ins between the wizard and the out-of-circuit callback request. It is
/// a single-use CSRF ledger: <see cref="Consume"/> returns an entry at most once and only if it has not
/// expired, so a replayed or forged <c>state</c> fails. In-memory and process-scoped - fitting for the
/// single-user app, and it means a restart mid-sign-in simply requires signing in again.
/// </summary>
public interface IAuthCodeStateStore
{
    /// <summary>Records a pending sign-in keyed by its state.</summary>
    void Put(AuthCodeStateEntry entry);

    /// <summary>Returns and removes the entry for <paramref name="state"/>, or null if unknown/expired.</summary>
    AuthCodeStateEntry? Consume(string state);
}
