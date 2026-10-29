namespace Fravashi.Application.Onboarding.Auth;

/// <summary>
/// The per-installation sign-in configuration for a provider, as the wizard/settings UI sees it. Every value
/// here is installation configuration the owner enters once (there is no vendor-hosted Fravashi app): a
/// client id, whether a client secret is present, and the resolved OAuth endpoints (which default to the
/// real IdP URLs and are overridable for tests). The secret itself is never surfaced here - only
/// <see cref="HasSecret"/>.
/// </summary>
/// <param name="Provider">The onboarding provider (see <see cref="OnboardingProviders"/>).</param>
/// <param name="Strategy">Which grant this provider uses.</param>
/// <param name="ClientId">The registered OAuth client id, or null when the owner has not entered one.</param>
/// <param name="HasSecret">True when a client secret is stored (auth-code providers need one).</param>
public sealed record ProviderAuthConfig(
    string Provider,
    AuthStrategy Strategy,
    string? ClientId,
    bool HasSecret)
{
    /// <summary>
    /// True when SSO can run for this provider: a device-code provider needs a client id; an auth-code
    /// provider needs a client id <b>and</b> a secret. When false the wizard shows a disabled sign-in button
    /// with the honest reason and a "how to register" link - never a dead button.
    /// </summary>
    public bool IsConfigured => Strategy switch
    {
        AuthStrategy.DeviceCode => !string.IsNullOrWhiteSpace(ClientId),
        AuthStrategy.AuthCode => !string.IsNullOrWhiteSpace(ClientId) && HasSecret,
        _ => false,
    };

    /// <summary>The honest reason SSO is unavailable, for the disabled-button tooltip/help. Null when configured.</summary>
    public string? UnavailableReason => IsConfigured
        ? null
        : Strategy == AuthStrategy.AuthCode && !string.IsNullOrWhiteSpace(ClientId) && !HasSecret
            ? "A client secret has not been configured for this provider yet."
            : "A sign-in client id has not been configured for this provider yet.";
}

/// <summary>
/// Reads the sign-in configuration and OAuth endpoints for a provider from the settings table (client ids,
/// endpoint overrides) and the secret store (client secrets). Defaults to the real IdP endpoints; the
/// override keys make the whole feature honest-testable against a local fake IdP. See
/// <see cref="AuthSettings"/> for the key names.
/// </summary>
public interface IAuthConfigStore
{
    /// <summary>The grant a provider uses (PAT for anything without an SSO authenticator).</summary>
    AuthStrategy StrategyFor(string provider);

    /// <summary>The UI-facing configuration for a provider (client id present? secret present?).</summary>
    ProviderAuthConfig Get(string provider);
}
