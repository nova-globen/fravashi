using Fravashi.Application.Onboarding;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;

namespace Fravashi.Infrastructure.Onboarding.Auth;

/// <summary>
/// Reads per-installation sign-in configuration from the settings table (client ids, endpoint overrides) and
/// the secret store (the Atlassian client secret). Endpoints default to the real IdP URLs
/// (<see cref="AuthSettings"/>); a test or self-hosted IdP overrides them through the settings keys, which is
/// what makes the whole SSO feature end-to-end testable without a live registration. The resolver/authenticators
/// call the infra-facing <c>Resolve*</c> methods on every use, so a configuration change takes effect without a
/// restart.
/// </summary>
public sealed class AuthConfigStore : IAuthConfigStore
{
    private readonly IConfigurationStore _settings;
    private readonly ISecretStore _secrets;

    public AuthConfigStore(IConfigurationStore settings, ISecretStore secrets)
    {
        _settings = settings;
        _secrets = secrets;
    }

    public AuthStrategy StrategyFor(string provider) => provider switch
    {
        OnboardingProviders.GitHub or OnboardingProviders.GitHubWiki => AuthStrategy.DeviceCode,
        OnboardingProviders.AzureDevOps or OnboardingProviders.AzureDevOpsWiki => AuthStrategy.DeviceCode,
        OnboardingProviders.Jira or OnboardingProviders.Confluence => AuthStrategy.AuthCode,
        _ => AuthStrategy.Pat,
    };

    public ProviderAuthConfig Get(string provider)
    {
        var strategy = StrategyFor(provider);
        var (clientIdKey, hasSecret) = provider switch
        {
            OnboardingProviders.GitHub or OnboardingProviders.GitHubWiki => (AuthSettings.GitHubClientId, false),
            OnboardingProviders.AzureDevOps or OnboardingProviders.AzureDevOpsWiki => (AuthSettings.AzureDevOpsClientId, false),
            OnboardingProviders.Jira or OnboardingProviders.Confluence => (AuthSettings.AtlassianClientId, !string.IsNullOrEmpty(_secrets.Get(AuthSettings.AtlassianClientSecretRef))),
            _ => (null, false),
        };

        var clientId = ClientId(clientIdKey);
        return new ProviderAuthConfig(provider, strategy, clientId, hasSecret);
    }

    // ── Infra-facing resolvers (return null when not configured) ───────────────────────────────────────────

    public GitHubAuthResolved? ResolveGitHub()
    {
        var clientId = ClientId(AuthSettings.GitHubClientId);
        if (clientId is null)
        {
            return null;
        }

        return new GitHubAuthResolved(
            clientId,
            Val(AuthSettings.GitHubDeviceCodeUrl, AuthSettings.GitHubDeviceCodeUrlDefault),
            Val(AuthSettings.GitHubTokenUrl, AuthSettings.GitHubTokenUrlDefault),
            Val(AuthSettings.GitHubScope, AuthSettings.GitHubScopeDefault));
    }

    public EntraAuthResolved? ResolveEntra()
    {
        var clientId = ClientId(AuthSettings.AzureDevOpsClientId);
        if (clientId is null)
        {
            return null;
        }

        var authority = Val(AuthSettings.AzureDevOpsAuthority, AuthSettings.AzureDevOpsAuthorityDefault).TrimEnd('/');
        var tenant = Val(AuthSettings.AzureDevOpsTenant, AuthSettings.AzureDevOpsTenantDefault);
        var deviceUrl = Trimmed(_settings.Get(AuthSettings.AzureDevOpsDeviceCodeUrl)) ?? $"{authority}/{tenant}/oauth2/v2.0/devicecode";
        var tokenUrl = Trimmed(_settings.Get(AuthSettings.AzureDevOpsTokenUrl)) ?? $"{authority}/{tenant}/oauth2/v2.0/token";

        return new EntraAuthResolved(
            clientId,
            deviceUrl,
            tokenUrl,
            Val(AuthSettings.AzureDevOpsScope, AuthSettings.AzureDevOpsScopeDefault));
    }

    public AtlassianAuthResolved? ResolveAtlassian()
    {
        // No vendor Atlassian app ships (ADR 0004: a 3LO secret is unshippable in a self-hosted product), so
        // the client id comes only from a per-install override; ClientId() falls back to the (absent) default.
        var clientId = ClientId(AuthSettings.AtlassianClientId);
        var secret = _secrets.Get(AuthSettings.AtlassianClientSecretRef);
        if (clientId is null || string.IsNullOrEmpty(secret))
        {
            return null;
        }

        return new AtlassianAuthResolved(
            clientId,
            secret,
            Val(AuthSettings.AtlassianAuthorizeUrl, AuthSettings.AtlassianAuthorizeUrlDefault),
            Val(AuthSettings.AtlassianTokenUrl, AuthSettings.AtlassianTokenUrlDefault),
            Val(AuthSettings.AtlassianResourcesUrl, AuthSettings.AtlassianResourcesUrlDefault),
            Val(AuthSettings.AtlassianScope, AuthSettings.AtlassianScopeDefault),
            Val(AuthSettings.AtlassianAudience, AuthSettings.AtlassianAudienceDefault));
    }

    private string Val(string key, string fallback) => Trimmed(_settings.Get(key)) ?? fallback;

    /// <summary>
    /// The client id for a settings key: the per-install (advanced) override in the settings table wins;
    /// otherwise the vendor-registered default baked into the product (<see cref="VendorAppDefaults"/>). Null
    /// when neither ships nor is set - the provider's sign-in stays honestly unavailable. See ADR 0004.
    /// </summary>
    private string? ClientId(string? clientIdKey) => clientIdKey is null
        ? null
        : Trimmed(_settings.Get(clientIdKey)) ?? VendorAppDefaults.ClientIdDefaultFor(clientIdKey);

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record GitHubAuthResolved(string ClientId, string DeviceCodeUrl, string TokenUrl, string Scope);

public sealed record EntraAuthResolved(string ClientId, string DeviceCodeUrl, string TokenUrl, string Scope);

public sealed record AtlassianAuthResolved(
    string ClientId,
    string ClientSecret,
    string AuthorizeUrl,
    string TokenUrl,
    string ResourcesUrl,
    string Scope,
    string Audience);
