using Fravashi.Application.Onboarding;
using Fravashi.Application.Onboarding.Auth;

namespace Fravashi.Infrastructure.Onboarding.Auth;

/// <summary>
/// Maps an onboarding provider to its SSO authenticator. GitHub and its wiki share the GitHub device-code
/// authenticator; Azure DevOps and its wiki share the Entra device-code authenticator; Jira and Confluence
/// share the Atlassian auth-code authenticator. Returns null for providers with no SSO path (Local / None),
/// which fall back to the PAT fields.
/// </summary>
public sealed class ProviderAuthenticatorResolver : IProviderAuthenticatorResolver
{
    private readonly GitHubDeviceCodeAuthenticator _gitHub;
    private readonly EntraDeviceCodeAuthenticator _entra;
    private readonly AtlassianAuthCodeAuthenticator _atlassian;

    public ProviderAuthenticatorResolver(
        GitHubDeviceCodeAuthenticator gitHub,
        EntraDeviceCodeAuthenticator entra,
        AtlassianAuthCodeAuthenticator atlassian)
    {
        _gitHub = gitHub;
        _entra = entra;
        _atlassian = atlassian;
    }

    public IDeviceCodeAuthenticator? DeviceCodeFor(string provider) => provider switch
    {
        OnboardingProviders.GitHub or OnboardingProviders.GitHubWiki => _gitHub,
        OnboardingProviders.AzureDevOps or OnboardingProviders.AzureDevOpsWiki => _entra,
        _ => null,
    };

    public IAuthCodeAuthenticator? AuthCodeFor(string provider) => provider switch
    {
        OnboardingProviders.Jira or OnboardingProviders.Confluence => _atlassian,
        _ => null,
    };
}
