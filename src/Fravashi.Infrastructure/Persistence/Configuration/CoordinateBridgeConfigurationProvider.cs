using Fravashi.Application.Onboarding;
using Fravashi.Application.Persistence;
using Microsoft.Extensions.Configuration;

namespace Fravashi.Infrastructure.Persistence.Configuration;

/// <summary>
/// Bridges the <b>non-secret coordinates</b> a verified provider connection carries - the Atlassian site domain
/// and account email, the Jira project key, the cloudId - into the .NET configuration pipeline under the exact
/// keys the Atlassian reader binds, so the Confluence wiki source the wizard verified is actually <i>live</i>
/// after a reload, not merely credentialled.
///
/// <para>
/// This is the non-secret counterpart to <see cref="SecretBridgeConfigurationProvider"/>. The split is
/// deliberate and load-bearing: the secret bridge resolves plaintext tokens from the Data Protection-backed
/// <see cref="ISecretStore"/> and exists precisely to keep those tokens out of the <c>settings</c> table and any
/// config dump; coordinates are non-secret and are derived from the connection row (<see cref="ProviderConnection.BaseUrl"/>)
/// plus the committed <c>Onboarding:{area}:*</c> settings. Keeping them here - secrets in the secret bridge,
/// coordinates in this non-secret bridge - makes the "which value comes from where" boundary explicit and keeps
/// the connection row the single source of truth for the site/org (§1-2): the domain and organization are
/// <i>derived</i> from the connection's URL on every <see cref="Load"/>, never copied into a second place that
/// could drift.
/// </para>
///
/// <para>
/// Like the secret bridge, this provider projects nothing until <see cref="Attach"/> wires its stores after the
/// DI container is built, and every <see cref="IConfigurationRoot.Reload"/> - including the one onboarding
/// triggers on completion - re-runs <see cref="Load"/>, so coordinates entered in the wizard surface at their
/// adapter keys without a restart. Values live only in this provider's in-memory
/// <see cref="ConfigurationProvider.Data"/> and, unlike the token, the coordinates are also independently
/// present in the <c>settings</c> table / the connection row (they are non-secret); this provider simply routes
/// them to the adapter's key names. A fresh install with no connection projects nothing, so the single-user
/// appsettings / user-secrets defaults are untouched - exactly as the token bridge behaves.
/// </para>
/// </summary>
public sealed class CoordinateBridgeConfigurationProvider : ConfigurationProvider
{
    // Atlassian adapter keys (AtlassianConfluenceReader binds the "Atlassian" section). JiraProject is a JIRA
    // coordinate and is projected only from the board path; the Confluence reader does NOT gate on it, so a
    // wiki-only install is fully configured without one (AtlassianConfig.IsConfluenceValid, backlog 12).
    internal const string JiraDomainKey = "Atlassian:JiraDomain";
    internal const string JiraEmailKey = "Atlassian:JiraEmail";
    internal const string JiraProjectKey = "Atlassian:JiraProject";
    internal const string CloudIdKey = "Atlassian:CloudId";

    private IProviderConnectionStore? _connections;
    private IConfigurationStore? _settings;

    /// <summary>
    /// Wires the stores the bridge reads, then loads once and signals a reload so bound options pick the
    /// coordinates up. Called from the host after the DI container is built (paired with the secret bridge).
    /// </summary>
    public void Attach(IProviderConnectionStore connections, IConfigurationStore settings)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Load();
        OnReload();
    }

    public override void Load()
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // Before Attach (initial configuration build) there is nothing to resolve. The adapters fall back to
        // whatever appsettings / the settings table provide, which for a fresh install is "not configured".
        if (_connections is null || _settings is null)
        {
            Data = data;
            return;
        }

        // Board first so a Jira board wins the shared Atlassian:* keys (and supplies JiraProject), then the
        // Confluence wiki fills any domain/email the board did not - supporting the Confluence-only shape
        // (Local board + Confluence wiki) without letting the wiki clobber a Jira-site board.
        foreach (var connection in _connections.GetByKind(ProviderKind.Board))
        {
            ProjectBoard(connection, data);
        }

        foreach (var connection in _connections.GetByKind(ProviderKind.Wiki))
        {
            ProjectConfluenceWiki(connection, data);
        }

        Data = data;
    }

    private void ProjectBoard(ProviderConnection connection, Dictionary<string, string?> data)
    {
        var provider = _settings!.Get("Onboarding:Board:Provider");

        if (string.Equals(provider, OnboardingProviders.Jira, StringComparison.OrdinalIgnoreCase))
        {
            SetIfPresent(data, JiraDomainKey, HostOf(connection.BaseUrl));
            SetIfPresent(data, JiraEmailKey, _settings.Get("Onboarding:Board:Email"));
            SetIfPresent(data, JiraProjectKey, _settings.Get("Onboarding:Board:Project"));
            // The cloudId a 3LO sign-in captured (accessible-resources) - non-secret, and what a Bearer token
            // needs to address the api.atlassian.com gateway. Written under the lowercase area slug by the
            // auth-code callback (Onboarding:board:CloudId), so read it with that exact casing.
            SetIfPresent(data, CloudIdKey, _settings.Get("Onboarding:board:CloudId"));
        }
    }

    private void ProjectConfluenceWiki(ProviderConnection connection, Dictionary<string, string?> data)
    {
        var provider = _settings!.Get("Onboarding:Wiki:Provider");
        if (!string.Equals(provider, OnboardingProviders.Confluence, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Confluence shares the Atlassian coordinates (domain + email, and - for a 3LO sign-in - the cloudId).
        // Only fill what a Jira board did not already project, so a site configured for both stays consistent
        // and the board wins ties. The wiki cloudId is written under the lowercase area slug (Onboarding:wiki:CloudId).
        FillIfAbsent(data, JiraDomainKey, HostOf(connection.BaseUrl));
        FillIfAbsent(data, JiraEmailKey, _settings.Get("Onboarding:Wiki:Email"));
        FillIfAbsent(data, CloudIdKey, _settings.Get("Onboarding:wiki:CloudId"));
    }

    private static void SetIfPresent(Dictionary<string, string?> data, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            data[key] = value;
        }
    }

    private static void FillIfAbsent(Dictionary<string, string?> data, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !data.ContainsKey(key))
        {
            data[key] = value;
        }
    }

    /// <summary>
    /// The host of a provider base URL - the Jira <c>JiraDomain</c> the adapter expands to
    /// <c>https://{JiraDomain}/rest/api/3</c>. Accepts a bare host as well as a full URL, and returns null when
    /// the value carries no resolvable host (a partial record stays honestly not-configured).
    /// </summary>
    public static string? HostOf(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        var normalized = baseUrl.Contains("://", StringComparison.Ordinal) ? baseUrl.Trim() : $"https://{baseUrl.Trim()}";
        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : null;
    }
}
