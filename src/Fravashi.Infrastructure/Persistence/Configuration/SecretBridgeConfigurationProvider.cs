using Fravashi.Application.Onboarding;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;
using Microsoft.Extensions.Configuration;

namespace Fravashi.Infrastructure.Persistence.Configuration;

/// <summary>
/// Bridges provider credentials from the <see cref="ISecretStore"/> into the .NET configuration pipeline
/// under the exact keys the inherited adapters bind, so a token collected by onboarding actually reaches
/// the adapter that needs it - without the studio's adapters being rewritten and without any plaintext
/// token ever touching the <c>settings</c> table or a file.
///
/// <para>
/// Onboarding stores each provider's token only in the secret store (Data Protection-backed) and records a
/// <see cref="ProviderConnection.CredentialRef"/> pointing at it. The adapters, however, read their token
/// from <see cref="IConfiguration"/>: the Atlassian board and wiki readers both bind
/// <c>Atlassian:JiraKey</c>. This provider resolves each connection's <c>credential_ref</c> at
/// <see cref="Load"/> time and projects the plaintext into memory under that key.
/// </para>
///
/// <para>
/// The provider is added to the pipeline at configuration-build time but its stores are wired in
/// <see cref="Attach"/> once the DI container is built (the secret store depends on Data Protection, which
/// the host owns). Until then <see cref="Load"/> yields nothing. Every <see cref="IConfigurationRoot.Reload"/>
/// - including the one onboarding triggers on completion - re-runs <see cref="Load"/>, so a token entered in
/// the wizard surfaces at its adapter key without a restart. The values live only in this provider's
/// in-memory <see cref="ConfigurationProvider.Data"/>; they are never written back to SQLite.
/// </para>
/// </summary>
public sealed class SecretBridgeConfigurationProvider : ConfigurationProvider
{
    private IProviderConnectionStore? _connections;
    private ISecretStore? _secrets;
    private IConfigurationStore? _settings;

    /// <summary>
    /// Wires the stores the bridge needs, then loads once and signals a reload so bound options pick the
    /// tokens up. Called from the host after the DI container is built.
    /// </summary>
    public void Attach(IProviderConnectionStore connections, ISecretStore secrets, IConfigurationStore settings)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Load();
        OnReload();
    }

    public override void Load()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // Before Attach (initial configuration build) there is nothing to resolve. The adapters fall back to
        // whatever appsettings / the settings table provide, which for a fresh install is "not configured".
        if (_connections is null || _secrets is null || _settings is null)
        {
            Data = data;
            return;
        }

        foreach (var connection in _connections.GetAll())
        {
            if (string.IsNullOrEmpty(connection.CredentialRef))
            {
                continue;
            }

            var key = ResolveTokenKey(connection);
            if (key is null)
            {
                continue;
            }

            // The stored value is a credential envelope (JSON) for SSO credentials and a bare string for a
            // legacy PAT; Parse handles both. We project the CURRENT access token - a refresh updates the
            // stored envelope and reloads, which re-runs this and picks the new token up.
            var credential = CredentialEnvelope.Parse(_secrets.Get(connection.CredentialRef));
            if (credential is null || string.IsNullOrEmpty(credential.AccessToken))
            {
                continue;
            }

            data[key] = credential.AccessToken;

            // Atlassian adapters send a PAT/API token as Basic against the site and a 3LO OAuth token as Bearer
            // against the api.atlassian.com gateway. Signal the scheme from the SAME credential the token came
            // from, so the scheme and token can never disagree; the coordinate bridge supplies the matching
            // cloudId the gateway URL needs. There is no Azure DevOps counterpart: ResolveTokenKey has no Azure
            // DevOps arm, so nothing ever projects AuthSettings.AzureDevOpsAuthScheme (backlog item 47).
            if (string.Equals(key, "Atlassian:JiraKey", StringComparison.Ordinal))
            {
                data[AuthSettings.AtlassianAuthScheme] = credential.Method == AuthMethods.Pat ? "Basic" : "Bearer";
            }
        }

        Data = data;
    }

    /// <summary>
    /// Maps a connection to the configuration key its adapter reads the token from, or null when no live
    /// adapter consumes a token for that provider. Only the Atlassian board (Jira) and wiki (Confluence)
    /// readers bind a token key today. A SOURCE connection projects nothing at all (backlog item 13): plan 2c
    /// retired the source step, git transport uses the host credential manager, and the one consumer left of a
    /// stored source credential (<c>GitHostAccess</c>, the transitional fallback) reads the secret store
    /// directly. The local providers, Azure DevOps in every kind, and the Azure DevOps Wiki / GitHub Wiki have
    /// no token-reading adapter in the studio today either; Azure DevOps arrives with backlog item 47.
    /// </summary>
    private string? ResolveTokenKey(ProviderConnection connection)
    {
        var provider = connection.Kind switch
        {
            ProviderKind.Board => _settings!.Get("Onboarding:Board:Provider"),
            ProviderKind.Wiki => _settings!.Get("Onboarding:Wiki:Provider"),
            _ => null,
        };

        if (string.IsNullOrEmpty(provider))
        {
            return null;
        }

        // Board: only Jira has a live token-reading adapter (the Confluence reader, which shares the Atlassian
        // credential under Atlassian:JiraKey).
        if (connection.Kind == ProviderKind.Board)
        {
            return provider switch
            {
                OnboardingProviders.Jira => "Atlassian:JiraKey",
                _ => null,
            };
        }

        // Wiki: only Confluence has a live token-reading adapter (the Atlassian reader, which shares the
        // Atlassian basic-auth credential). ADO Wiki / GitHub Wiki have no token-reading adapter yet.
        return provider switch
        {
            OnboardingProviders.Confluence => "Atlassian:JiraKey",
            _ => null,
        };
    }
}
