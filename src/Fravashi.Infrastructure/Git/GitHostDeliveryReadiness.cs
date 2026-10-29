using Fravashi.Application.Autopilot;
using Fravashi.Application.Git;
using Fravashi.Infrastructure.PullRequests;

namespace Fravashi.Infrastructure.Git;

/// <summary>
/// <see cref="IAutopilotDeliveryReadiness"/> over the scoped <see cref="IGitService"/> and
/// <see cref="GitHostAccess"/> (plan 2c decision 8): the same origin-remote-to-credential resolution the
/// PR panel uses, asked here for one yes/no answer instead of a provider call.
/// </summary>
public sealed class GitHostDeliveryReadiness(IGitService git, GitHostAccess access) : IAutopilotDeliveryReadiness
{
    public async Task<AutopilotDeliveryReadiness> CheckAsync(CancellationToken ct = default)
    {
        var url = await git.GetRemoteUrlAsync(ct).ConfigureAwait(false);
        var info = RemoteInfo.Parse(url);
        if (info.Host.Length == 0)
        {
            return new AutopilotDeliveryReadiness(HasToken: false, Host: null);
        }

        var resolution = access.Resolve(info);
        return new AutopilotDeliveryReadiness(HasToken: resolution.Credential is not null, Host: resolution.Host);
    }
}
