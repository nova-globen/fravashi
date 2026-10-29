using System.Net.Http.Headers;
using System.Text;
using Fravashi.Application.Onboarding;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;

namespace Fravashi.Infrastructure.Tracker.AzureDevOps;

/// <summary>Where an Azure DevOps board is and how to authenticate to it, as configured right now.</summary>
/// <param name="Organization">The organisation URL with no trailing slash, e.g. <c>https://dev.azure.com/acme</c>.</param>
/// <param name="Project">The project's name.</param>
/// <param name="Authorization">The header every call carries: Basic with a PAT, or Bearer with a sign-in token.</param>
public sealed record AzureDevOpsBoardSettings(string Organization, string Project, AuthenticationHeaderValue Authorization);

/// <summary>
/// The board connection onboarding saved, read at each use (backlog 143): the <c>board</c> connection's base URL is the
/// organisation, <c>Onboarding:Board:Project</c> the project, and the connection's credential either a PAT or a sign-in
/// token with a refresh token. Neither configuration bridge projects any of it for Azure DevOps, so this reads the
/// stores directly rather than inventing adapter keys nobody else reads.
/// </summary>
public sealed class AzureDevOpsBoardConnection(
    IProviderConnectionStore connections,
    IConfigurationStore settings,
    ISecretStore secrets,
    IProviderTokenRefresher? refresher = null)
{
    /// <summary>The id of the connection onboarding's board step writes.</summary>
    public const string ConnectionId = "board";

    /// <summary>The settings as they are now, or null when the board is not an Azure DevOps board with a project and a credential.</summary>
    public AzureDevOpsBoardSettings? Current()
    {
        if (!string.Equals(settings.Get("Onboarding:Board:Provider"), OnboardingProviders.AzureDevOps, StringComparison.Ordinal))
        {
            return null;
        }

        var connection = connections.Get(ConnectionId);
        var project = settings.Get("Onboarding:Board:Project");
        if (connection?.BaseUrl is not { Length: > 0 } organization || string.IsNullOrWhiteSpace(project)
            || connection.CredentialRef is not { Length: > 0 } reference
            || CredentialEnvelope.Parse(secrets.Get(reference)) is not { } credential)
        {
            return null;
        }

        var header = credential.Method == AuthMethods.Pat
            ? new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + credential.AccessToken)))
            : new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        return new AzureDevOpsBoardSettings(organization.TrimEnd('/'), project.Trim(), header);
    }

    /// <summary>Refreshes a sign-in token that is about to expire; a PAT needs nothing.</summary>
    public Task RefreshIfNeededAsync(CancellationToken ct) =>
        refresher?.RefreshIfNeededAsync(ConnectionId, ct) ?? Task.CompletedTask;
}
