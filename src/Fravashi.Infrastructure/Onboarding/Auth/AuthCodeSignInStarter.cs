using Fravashi.Application.Onboarding.Auth;

namespace Fravashi.Infrastructure.Onboarding.Auth;

/// <summary>
/// Starts an authorization-code sign-in for the wizard: builds the authorize URL, records the CSRF state and
/// the connection context so the callback can complete it, and returns the URL to open. Keeps the wizard
/// component thin and the state bookkeeping in one place.
/// </summary>
public sealed class AuthCodeSignInStarter
{
    private readonly IProviderAuthenticatorResolver _resolver;
    private readonly IAuthCodeStateStore _states;
    private readonly TimeProvider _time;

    public AuthCodeSignInStarter(IProviderAuthenticatorResolver resolver, IAuthCodeStateStore states, TimeProvider time)
    {
        _resolver = resolver;
        _states = states;
        _time = time;
    }

    /// <summary>
    /// Returns the authorize URL to open, or null when the provider has no auth-code authenticator. The caller
    /// supplies the exact registered <paramref name="redirectUri"/> and where the browser should return.
    /// </summary>
    public string? Begin(
        string provider,
        string area,
        string connectionId,
        string credentialRef,
        string? baseUrl,
        string redirectUri,
        string returnMode)
    {
        var authenticator = _resolver.AuthCodeFor(provider);
        if (authenticator is null)
        {
            return null;
        }

        var challenge = authenticator.BeginAuthorization(redirectUri);
        _states.Put(new AuthCodeStateEntry(
            challenge.State, provider, area, connectionId, credentialRef, baseUrl, redirectUri, returnMode, _time.GetUtcNow()));
        return challenge.AuthorizeUrl;
    }
}
