using Fravashi.Application.Onboarding;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;
using Fravashi.Domain.PullRequests;
using Fravashi.Infrastructure.PullRequests;

namespace Fravashi.Infrastructure.Git;

/// <summary>What the studio knows about talking to one origin remote (plan 2c).</summary>
public sealed record GitHostResolution(
    PullRequestProvider Provider, StoredCredential? Credential, string Host);

/// <summary>
/// THE place a parsed origin remote becomes (provider, API credential): an explicit git_hosts
/// entry first (decision 8), then built-in host detection, then the transitional legacy
/// single-source fallback (the pre-2c `source:git` credential and the onboarding Forgejo flag),
/// so an existing install keeps working until the owner adds host entries.
/// </summary>
public sealed class GitHostAccess(
    IGitHostStore hosts,
    ISecretStore secrets,
    IProviderConnectionStore connections,
    IConfigurationStore settings)
{
    private const string LegacyProviderKey = "Onboarding:Source:Provider";

    public GitHostResolution Resolve(RemoteInfo remote)
    {
        if (remote.Host.Length == 0)
        {
            return new GitHostResolution(PullRequestProvider.None, null, string.Empty);
        }

        if (hosts.Get(remote.Host) is { } entry)
        {
            return new GitHostResolution(
                ProviderFromName(entry.Provider),
                CredentialEnvelope.Parse(secrets.Get(entry.CredentialRef)),
                remote.Host);
        }

        if (remote.Provider != PullRequestProvider.None)
        {
            return new GitHostResolution(remote.Provider, LegacyCredential(), remote.Host);
        }

        if (string.Equals(settings.Get(LegacyProviderKey), OnboardingProviders.Forgejo, StringComparison.OrdinalIgnoreCase))
        {
            return new GitHostResolution(PullRequestProvider.Forgejo, LegacyCredential(), remote.Host);
        }

        return new GitHostResolution(PullRequestProvider.None, null, remote.Host);
    }

    private static PullRequestProvider ProviderFromName(string name) => name switch
    {
        OnboardingProviders.GitHub => PullRequestProvider.GitHub,
        OnboardingProviders.AzureDevOps => PullRequestProvider.AzureDevOps,
        OnboardingProviders.Forgejo => PullRequestProvider.Forgejo,
        _ => PullRequestProvider.None,
    };

    private StoredCredential? LegacyCredential()
    {
        var connection = connections.GetByKind(ProviderKind.Source).FirstOrDefault();
        var reference = connection?.CredentialRef;
        return string.IsNullOrWhiteSpace(reference) ? null : CredentialEnvelope.Parse(secrets.Get(reference));
    }
}
