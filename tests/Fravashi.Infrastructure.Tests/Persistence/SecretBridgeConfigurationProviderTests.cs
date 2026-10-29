using Fravashi.Application.Onboarding;
using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Persistence.Configuration;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Persistence;

/// <summary>
/// Proves the secret→adapter bridge: a token stored only in the secret store surfaces at the exact
/// IConfiguration key the inherited adapter binds, after a reload, and is never present in the persisted
/// <c>settings</c> table nor in any dump of persisted configuration.
/// </summary>
public sealed class SecretBridgeConfigurationProviderTests
{
    private sealed record Stores(
        IProviderConnectionStore Connections,
        ISecretStore Secrets,
        IConfigurationStore Settings);

    private static Stores BuildStores(SqliteTestContext context) => new(
        new SqliteProviderConnectionStore(context.Factory),
        new SqliteSecretStore(context.Factory, new ReversibleTestProtector()),
        new SqliteConfigurationStore(context.Factory));

    private static (IConfigurationRoot Configuration, SecretBridgeConfigurationSource Bridge) BuildConfiguration(
        SqliteTestContext context)
    {
        var builder = new ConfigurationBuilder().AddFravashiDatabase(context.DatabasePath);
        var bridge = builder.AddFravashiSecretBridge();
        return (builder.Build(), bridge);
    }

    // The two Azure DevOps rows (board and source, both asserting "AzureDevOps:PersonalAccessToken") were
    // retired on 2026-08-05, backlog item 11: ResolveTokenKey has no Azure DevOps arm, so that projection is
    // unbuilt and both rows failed from the day they were written. The source row was doubly moot, onboarding
    // has no source step any more. Coverage returns with the Azure DevOps adapter, backlog item 47.
    // The GitHub source row (asserting "GitHub:Token") went the same way on 2026-08-06, backlog item 13: that
    // projection was BUILT but unbound, so it published a plaintext token under a key nothing reads. See
    // SourceConnection_ProjectsNothing below for the guard that keeps it gone.
    [Theory]
    [InlineData(OnboardingProviders.Jira, ProviderKind.Board, "board:token", "Atlassian:JiraKey")]
    [InlineData(OnboardingProviders.Confluence, ProviderKind.Wiki, "wiki:token", "Atlassian:JiraKey")]
    public void StoredToken_SurfacesAtAdapterKey_AfterReload(string provider, ProviderKind kind, string credentialRef, string adapterKey)
    {
        using var context = new SqliteTestContext();
        var stores = BuildStores(context);
        const string token = "tok_super_secret_value";

        stores.Settings.Set($"Onboarding:{kind}:Provider", provider);
        stores.Secrets.Set(credentialRef, token);
        stores.Connections.Upsert(new ProviderConnection(
            kind.ToString().ToLowerInvariant(), kind, "https://example", credentialRef, "Verified", DateTimeOffset.UtcNow));

        var (configuration, bridge) = BuildConfiguration(context);
        bridge.Provider.Attach(stores.Connections, stores.Secrets, stores.Settings);
        configuration.Reload();

        configuration[adapterKey].Should().Be(token);
    }

    [Fact]
    public void StoredToken_IsAbsentFromSettingsTable_AndFromAnyPersistedConfigDump()
    {
        using var context = new SqliteTestContext();
        var stores = BuildStores(context);
        const string token = "tok_never_persisted_plaintext";

        stores.Settings.Set("Onboarding:Board:Provider", OnboardingProviders.Jira);
        stores.Secrets.Set("board:token", token);
        stores.Connections.Upsert(new ProviderConnection(
            "board", ProviderKind.Board, "https://site.atlassian.net", "board:token", "Verified", DateTimeOffset.UtcNow));

        var (configuration, bridge) = BuildConfiguration(context);
        bridge.Provider.Attach(stores.Connections, stores.Secrets, stores.Settings);
        configuration.Reload();

        // It IS live in the running configuration...
        configuration["Atlassian:JiraKey"].Should().Be(token);

        // ...but it is NOT in the persisted settings table (config-in-DB), nor anywhere in a dump of it.
        stores.Settings.Get("Atlassian:JiraKey").Should().BeNull();
        stores.Settings.GetAll().Values.Should().NotContain(token);

        // ...and a configuration built from ONLY the persisted database (no bridge) cannot see it.
        var persistedOnly = new ConfigurationBuilder().AddFravashiDatabase(context.DatabasePath).Build();
        persistedOnly["Atlassian:JiraKey"].Should().BeNull();
    }

    [Fact]
    public void Reload_PicksUpTokenStoredAfterStartup()
    {
        using var context = new SqliteTestContext();
        var stores = BuildStores(context);

        var (configuration, bridge) = BuildConfiguration(context);
        bridge.Provider.Attach(stores.Connections, stores.Secrets, stores.Settings);
        configuration["Atlassian:JiraKey"].Should().BeNull();

        // Simulate the onboarding wizard writing the selection, connection, and secret, then completing
        // (which triggers a configuration reload).
        stores.Settings.Set("Onboarding:Board:Provider", OnboardingProviders.Jira);
        stores.Secrets.Set("board:token", "late_token");
        stores.Connections.Upsert(new ProviderConnection(
            "board", ProviderKind.Board, "https://site.atlassian.net", "board:token", "Verified", DateTimeOffset.UtcNow));
        configuration.Reload();

        configuration["Atlassian:JiraKey"].Should().Be("late_token");
    }

    [Fact]
    public void BeforeAttach_BridgeProjectsNothing()
    {
        using var context = new SqliteTestContext();
        var stores = BuildStores(context);
        stores.Settings.Set("Onboarding:Board:Provider", OnboardingProviders.Jira);
        stores.Secrets.Set("board:token", "unused");
        stores.Connections.Upsert(new ProviderConnection(
            "board", ProviderKind.Board, "https://site.atlassian.net", "board:token", "Verified", DateTimeOffset.UtcNow));

        var (configuration, _) = BuildConfiguration(context);

        // Not attached: the secret store (which needs Data Protection) is not wired, so nothing is projected.
        configuration["Atlassian:JiraKey"].Should().BeNull();
    }

    [Fact]
    public void ConnectionWithoutCredentialRef_IsNotBridged()
    {
        using var context = new SqliteTestContext();
        var stores = BuildStores(context);
        stores.Settings.Set("Onboarding:Board:Provider", OnboardingProviders.Jira);
        stores.Connections.Upsert(new ProviderConnection(
            "board", ProviderKind.Board, "https://site.atlassian.net", CredentialRef: null, "Recorded", DateTimeOffset.UtcNow));

        var (configuration, bridge) = BuildConfiguration(context);
        bridge.Provider.Attach(stores.Connections, stores.Secrets, stores.Settings);
        configuration.Reload();

        configuration["Atlassian:JiraKey"].Should().BeNull();
    }

    [Fact]
    public void SourceConnection_ProjectsNothing()
    {
        using var context = new SqliteTestContext();
        var stores = BuildStores(context);
        const string token = "gho_source_credential";

        // A pre-2c install still carries these rows: onboarding's retired source step wrote the provider
        // setting and the `source:git` secret, and GitHostAccess still reads BOTH as its transitional fallback.
        // The bridge must nonetheless publish nothing for them (backlog item 13): no adapter binds a source
        // token key, so projecting one only put a live credential in the configuration pipeline for free.
        stores.Settings.Set("Onboarding:Source:Provider", OnboardingProviders.GitHub);
        stores.Secrets.Set("source:git", token);
        stores.Connections.Upsert(new ProviderConnection(
            "source", ProviderKind.Source, "https://github.com/o/r", "source:git", "Verified", DateTimeOffset.UtcNow));

        var (configuration, bridge) = BuildConfiguration(context);
        bridge.Provider.Attach(stores.Connections, stores.Secrets, stores.Settings);
        configuration.Reload();

        configuration["GitHub:Token"].Should().BeNull("no adapter binds a source token key");
        configuration.AsEnumerable().Should().NotContain(pair => pair.Value == token,
            "a source credential must not reach the configuration pipeline under ANY key");

        // The legacy rows themselves stay put: GitHostAccess is their live consumer.
        stores.Settings.Get("Onboarding:Source:Provider").Should().Be(OnboardingProviders.GitHub);
        stores.Secrets.Get("source:git").Should().Be(token);
    }

    [Fact]
    public void WikiProviderWithoutLiveAdapter_IsNotBridged()
    {
        using var context = new SqliteTestContext();
        var stores = BuildStores(context);
        // Azure DevOps Wiki has no token-reading adapter in the studio today, so its token is not projected.
        stores.Settings.Set("Onboarding:Wiki:Provider", OnboardingProviders.AzureDevOpsWiki);
        stores.Secrets.Set("wiki:token", "adowiki_token");
        stores.Connections.Upsert(new ProviderConnection(
            "wiki", ProviderKind.Wiki, "https://dev.azure.com/org/project", "wiki:token", "Verified", DateTimeOffset.UtcNow));

        var (configuration, bridge) = BuildConfiguration(context);
        bridge.Provider.Attach(stores.Connections, stores.Secrets, stores.Settings);
        configuration.Reload();

        configuration["Atlassian:JiraKey"].Should().BeNull();
    }
}
