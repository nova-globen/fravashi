using Fravashi.Application.Mirroring;
using Fravashi.Domain.Projects;
using Fravashi.Infrastructure.Tracker.Atlassian;
using Microsoft.Extensions.Options;

namespace Fravashi.Infrastructure.Tracker;

/// <summary>
/// <see cref="IProviderGateways"/> over what onboarding configured (backlog 47). The install has ONE board
/// connection today, so every project follows it, or none does; a provider per project is a later increment and
/// the port already takes the project so that nothing downstream has to change when it arrives. The
/// configuration is read at each use, so a connection saved in a running studio takes effect at once.
/// </summary>
public sealed class ConfiguredProviderGateways(
    IOptionsMonitor<AtlassianConfig> options,
    AtlassianJiraGateway jira,
    Fravashi.Application.Projects.IProjectProviders? choices = null,
    AzureDevOps.AzureDevOpsBoardsGateway? azureDevOps = null)
    : IProviderGateways
{
    /// <inheritdoc />
    /// <remarks>
    /// Backlog 142: a project that chose no board follows none, whatever the install is connected to. Backlog 143: an
    /// Azure DevOps board is the install's board when onboarding saved one; the Atlassian coordinates can still be
    /// valid beside it (a Confluence wiki shares them), so the saved board kind decides, not which one answers.
    /// </remarks>
    public IProviderGateway? For(ProjectId project) =>
        choices?.For(project).Board == Fravashi.Application.Projects.ProviderChoice.None
            ? null
            : azureDevOps is { IsConfigured: true } ? azureDevOps
            : options.CurrentValue.IsJiraValid ? jira : null;
}
