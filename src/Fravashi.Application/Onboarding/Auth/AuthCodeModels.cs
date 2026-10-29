namespace Fravashi.Application.Onboarding.Auth;

/// <summary>
/// The start of an authorization-code (Atlassian 3LO) flow: the URL to send the user to, and the opaque
/// <see cref="State"/> that ties the eventual callback back to this request. The state is a CSRF guard - the
/// callback endpoint rejects any <c>code</c> whose <c>state</c> it did not issue.
/// </summary>
/// <param name="AuthorizeUrl">The provider authorize URL with client id, scope, redirect, and state applied.</param>
/// <param name="State">The random anti-forgery value; the callback must echo it.</param>
/// <param name="RedirectUri">The redirect URI used (must match the registered one exactly).</param>
public sealed record AuthCodeChallenge(string AuthorizeUrl, string State, string RedirectUri);

/// <summary>The outcome of exchanging an authorization <c>code</c> for tokens.</summary>
/// <param name="Success">True when the token endpoint returned an access token.</param>
/// <param name="Credential">The stored-credential envelope on success; null otherwise.</param>
/// <param name="Detail">A human-readable message (the error on failure, or the granted scope on success).</param>
public sealed record AuthCodeExchange(bool Success, StoredCredential? Credential, string Detail)
{
    public static AuthCodeExchange Ok(StoredCredential credential, string detail) => new(true, credential, detail);
    public static AuthCodeExchange Fail(string detail) => new(false, null, detail);
}
