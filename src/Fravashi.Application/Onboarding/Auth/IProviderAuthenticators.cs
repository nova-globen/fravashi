namespace Fravashi.Application.Onboarding.Auth;

/// <summary>
/// Acquires a credential for a provider family via the OAuth 2.0 device authorization grant (GitHub and
/// Microsoft Entra for Azure DevOps). Two calls: <see cref="StartAsync"/> to obtain the user code, then
/// <see cref="PollOnceAsync"/> repeatedly (the <see cref="DeviceCodeDriver"/> runs the loop) until a token is
/// issued or the flow ends. Concrete implementations are named for their IdP - an adapter named for its
/// provider is permitted by naming-conventions.
/// </summary>
public interface IDeviceCodeAuthenticator
{
    /// <summary>The onboarding provider this authenticator serves (see <see cref="OnboardingProviders"/>).</summary>
    string Provider { get; }

    /// <summary>Requests a device + user code from the provider's device-authorization endpoint.</summary>
    Task<DeviceCodeStart> StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Polls the token endpoint once for <paramref name="deviceCode"/> and classifies the response.</summary>
    Task<DevicePollResult> PollOnceAsync(string deviceCode, CancellationToken cancellationToken = default);
}

/// <summary>
/// Acquires a credential for a provider family via the OAuth 2.0 authorization-code grant / Atlassian 3LO:
/// build an authorize URL, let the user consent in the browser, then exchange the returned <c>code</c> at the
/// callback for an access + refresh token.
/// </summary>
public interface IAuthCodeAuthenticator
{
    string Provider { get; }

    /// <summary>Builds the authorize URL and the CSRF <c>state</c> for a redirect to <paramref name="redirectUri"/>.</summary>
    AuthCodeChallenge BeginAuthorization(string redirectUri);

    /// <summary>Exchanges an authorization <c>code</c> for tokens (the callback endpoint calls this).</summary>
    Task<AuthCodeExchange> ExchangeCodeAsync(string code, string redirectUri, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves the authenticator for a provider, or reports that the provider uses the PAT path. Backed by
/// keyed registrations; returns null when the provider has no SSO authenticator (Local / None).
/// </summary>
public interface IProviderAuthenticatorResolver
{
    IDeviceCodeAuthenticator? DeviceCodeFor(string provider);

    IAuthCodeAuthenticator? AuthCodeFor(string provider);
}

/// <summary>
/// Refreshes an OAuth credential whose access token is expired or expiring, using its stored refresh token,
/// then writes the new envelope back to the secret store and triggers a configuration reload so the
/// secret→adapter bridge re-projects the fresh access token. A no-op for PATs and for credentials without a
/// refresh token (GitHub device tokens do not expire).
/// </summary>
public interface IProviderTokenRefresher
{
    /// <summary>
    /// Refreshes the credential behind <paramref name="connectionId"/> if it is an OAuth credential due for
    /// refresh. Returns true when a refresh happened (and a reload was triggered), false when none was needed
    /// or possible.
    /// </summary>
    Task<bool> RefreshIfNeededAsync(string connectionId, CancellationToken cancellationToken = default);
}
