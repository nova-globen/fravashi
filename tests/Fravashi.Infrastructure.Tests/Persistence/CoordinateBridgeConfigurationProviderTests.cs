using Fravashi.Application.Onboarding;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Persistence.Configuration;
using Fravashi.Infrastructure.Tracker.Atlassian;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Persistence;

/// <summary>
/// Proves the non-secret coordinate→adapter bridge: the domain / email / project / organization a verified
/// connection carries surface at the exact IConfiguration keys the inherited adapters bind, after a reload, so
/// a credentialled tracker becomes fully <i>live</i>. Coordinates are non-secret, so they must live in the
/// settings table / connection row and NOT in the secret store; the token half stays in the secret bridge.
/// </summary>
public sealed class CoordinateBridgeConfigurationProviderTests
{
    private sealed record Stores(
        IProviderConnectionStore Connections,
        ISecretStore Secrets,
        IConfigurationStore Settings);

    private static Stores BuildStores(SqliteTestContext context) => new(
        new SqliteProviderConnectionStore(context.Factory),
        new SqliteSecretStore(context.Factory, new ReversibleTestProtector()),
        new SqliteConfigurationStore(context.Factory));

    /// <summary>Builds a configuration with BOTH bridges (secret + coordinate) attached, exactly as the host
    /// wires them, so a test can bind a full adapter config.</summary>
    private static IConfigurationRoot BuildBridgedConfiguration(SqliteTestContext context, Stores stores)
    {
        var builder = new ConfigurationBuilder().AddFravashiDatabase(context.DatabasePath);
        var secret = builder.AddFravashiSecretBridge();
        var coordinate = builder.AddFravashiCoordinateBridge();
        var configuration = builder.Build();
        secret.Provider.Attach(stores.Connections, stores.Secrets, stores.Settings);
        coordinate.Provider.Attach(stores.Connections, stores.Settings);
        configuration.Reload();
        return configuration;
    }

    // ── Jira board ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void JiraBoard_Coordinates_SurfaceAtAtlassianAdapterKeys_AfterReload()
    {
        using var context = new SqliteTestContext();
        var stores = BuildStores(context);

        stores.Settings.Set("Onboarding:Board:Provider", OnboardingProviders.Jira);
        stores.Settings.Set("Onboarding:Board:Email", "dev@example.com");
        stores.Settings.Set("Onboarding:Board:Project", "ENG");
        stores.Secrets.Set("board:token", "jira_api_token");
        stores.Connections.Upsert(new ProviderConnection(
            "board", ProviderKind.Board, "https://acme.atlassian.net", "board:token", "Verified", DateTimeOffset.UtcNow));

        var configuration = BuildBridgedConfiguration(context, stores);

        configuration["Atlassian:JiraDomain"].Should().Be("acme.atlassian.net");
        configuration["Atlassian:JiraEmail"].Should().Be("dev@example.com");
        configuration["Atlassian:JiraProject"].Should().Be("ENG");
        configuration["Atlassian:JiraKey"].Should().Be("jira_api_token");
    }

    [Fact]
    public void JiraBoard_BoundAtlassianConfig_IsFullyValid_WithBothBridges()
    {
        using var context = new SqliteTestContext();
        var stores = BuildStores(context);

        stores.Settings.Set("Onboarding:Board:Provider", OnboardingProviders.Jira);
        stores.Settings.Set("Onboarding:Board:Email", "dev@example.com");
        stores.Settings.Set("Onboarding:Board:Project", "ENG");
        stores.Secrets.Set("board:token", "jira_api_token");
        stores.Connections.Upsert(new ProviderConnection(
            "board", ProviderKind.Board, "https://acme.atlassian.net", "board:token", "Verified", DateTimeOffset.UtcNow));

        var configuration = BuildBridgedConfiguration(context, stores);

        var config = configuration.GetSection("Atlassian").Get<AtlassianConfig>()!;
        config.IsJiraValid.Should().BeTrue("domain + email + project + token are all projected");
        config.JiraBaseUrl.Should().Be("https://acme.atlassian.net/rest/api/3");
    }

    [Fact]
    public void JiraBoard_MissingProject_LeavesTheJiraAdapterNotReady()
    {
        using var context = new SqliteTestContext();
        var stores = BuildStores(context);

        // A partial record: no project key. The bridge must NOT invent one, and the JIRA adapter stays not-ready.
        // Only Jira: the project key is a Jira coordinate, and gating Confluence on it too was backlog 12.
        stores.Settings.Set("Onboarding:Board:Provider", OnboardingProviders.Jira);
        stores.Settings.Set("Onboarding:Board:Email", "dev@example.com");
        stores.Secrets.Set("board:token", "jira_api_token");
        stores.Connections.Upsert(new ProviderConnection(
            "board", ProviderKind.Board, "https://acme.atlassian.net", "board:token", "Verified", DateTimeOffset.UtcNow));

        var configuration = BuildBridgedConfiguration(context, stores);

        configuration["Atlassian:JiraProject"].Should().BeNull();
        configuration.GetSection("Atlassian").Get<AtlassianConfig>()!.IsJiraValid.Should().BeFalse();
    }

    // ── Atlassian 3LO (SSO) board: cloudId + scheme project into a live GATEWAY adapter ──────────────────────

    [Fact]
    public void JiraBoard_ThreeLegged_ProjectsTheCloudIdWithTheBearerScheme_YieldingGatewayAdapter()
    {
        using var context = new SqliteTestContext();
        var stores = BuildStores(context);

        // A 3LO Atlassian sign-in as the auth-code callback leaves it: the token is an OAuth access token
        // (method = authcode → Bearer), the cloudId is captured under the LOWERCASE area slug, and no email.
        stores.Settings.Set("Onboarding:Board:Provider", OnboardingProviders.Jira);
        stores.Settings.Set("Onboarding:Board:Project", "ENG");
        stores.Settings.Set("Onboarding:board:CloudId", "cloud-abc-123");
        stores.Secrets.Set("board:token", CredentialEnvelope.Serialize(
            new StoredCredential("oauth_access_token", "refresh", DateTimeOffset.UtcNow.AddHours(1), AuthMethods.AuthCode)));
        stores.Connections.Upsert(new ProviderConnection(
            "board", ProviderKind.Board, "https://acme.atlassian.net", "board:token", "Signed in", DateTimeOffset.UtcNow));

        var configuration = BuildBridgedConfiguration(context, stores);

        // The coordinate bridge projects the non-secret cloudId; the secret bridge projects the token + scheme.
        configuration[AuthSettings.AtlassianCloudId].Should().Be("cloud-abc-123");
        configuration[AuthSettings.AtlassianAuthScheme].Should().Be("Bearer");
        configuration["Atlassian:JiraKey"].Should().Be("oauth_access_token");

        var config = configuration.GetSection("Atlassian").Get<AtlassianConfig>()!;
        config.IsJiraValid.Should().BeTrue("token + domain + project + cloudId are present (email is not needed in 3LO)");
        config.JiraBaseUrl.Should().Be("https://api.atlassian.com/ex/jira/cloud-abc-123/rest/api/3");
        config.AuthenticationHeader.Scheme.Should().Be("Bearer");
    }

    [Fact]
    public void JiraBoard_ThreeLegged_MissingCloudId_LeavesGatewayAdapterNotReady()
    {
        using var context = new SqliteTestContext();
        var stores = BuildStores(context);

        // An older 3LO sign-in that never captured a cloudId: Bearer scheme but no gateway address.
        stores.Settings.Set("Onboarding:Board:Provider", OnboardingProviders.Jira);
        stores.Settings.Set("Onboarding:Board:Project", "ENG");
        stores.Secrets.Set("board:token", CredentialEnvelope.Serialize(
            new StoredCredential("oauth_access_token", "refresh", DateTimeOffset.UtcNow.AddHours(1), AuthMethods.AuthCode)));
        stores.Connections.Upsert(new ProviderConnection(
            "board", ProviderKind.Board, "https://acme.atlassian.net", "board:token", "Signed in", DateTimeOffset.UtcNow));

        var configuration = BuildBridgedConfiguration(context, stores);

        configuration[AuthSettings.AtlassianAuthScheme].Should().Be("Bearer");
        configuration[AuthSettings.AtlassianCloudId].Should().BeNull("no cloudId was captured");
        var bound = configuration.GetSection("Atlassian").Get<AtlassianConfig>()!;
        bound.IsJiraValid.Should().BeFalse("a Bearer adapter with no cloudId cannot address the gateway");
        bound.IsConfluenceValid.Should().BeFalse("neither surface can address the gateway without a cloudId");
    }

    [Fact]
    public void JiraBoard_Pat_ProjectsBasicScheme_AndNoCloudId()
    {
        using var context = new SqliteTestContext();
        var stores = BuildStores(context);

        stores.Settings.Set("Onboarding:Board:Provider", OnboardingProviders.Jira);
        stores.Settings.Set("Onboarding:Board:Email", "dev@example.com");
        stores.Settings.Set("Onboarding:Board:Project", "ENG");
        stores.Secrets.Set("board:token", "jira_api_token"); // bare string = legacy PAT
        stores.Connections.Upsert(new ProviderConnection(
            "board", ProviderKind.Board, "https://acme.atlassian.net", "board:token", "Verified", DateTimeOffset.UtcNow));

        var configuration = BuildBridgedConfiguration(context, stores);

        configuration[AuthSettings.AtlassianAuthScheme].Should().Be("Basic", "a bare-string credential is a PAT");
        configuration[AuthSettings.AtlassianCloudId].Should().BeNull("a PAT addresses the site, not the gateway");
        configuration.GetSection("Atlassian").Get<AtlassianConfig>()!.JiraBaseUrl
            .Should().Be("https://acme.atlassian.net/rest/api/3");
    }

    [Fact]
    public void ConfluenceWiki_ThreeLegged_ProjectsCloudId()
    {
        using var context = new SqliteTestContext();
        var stores = BuildStores(context);

        stores.Settings.Set("Onboarding:Wiki:Provider", OnboardingProviders.Confluence);
        stores.Settings.Set("Onboarding:wiki:CloudId", "cloud-wiki-9");
        stores.Secrets.Set("wiki:token", CredentialEnvelope.Serialize(
            new StoredCredential("oauth_access_token", "refresh", DateTimeOffset.UtcNow.AddHours(1), AuthMethods.AuthCode)));
        stores.Connections.Upsert(new ProviderConnection(
            "wiki", ProviderKind.Wiki, "https://docs.atlassian.net", "wiki:token", "Signed in", DateTimeOffset.UtcNow));

        var configuration = BuildBridgedConfiguration(context, stores);

        configuration[AuthSettings.AtlassianCloudId].Should().Be("cloud-wiki-9");
        configuration[AuthSettings.AtlassianAuthScheme].Should().Be("Bearer");
    }

    [Fact]
    public void HostOf_ResolvesAtlassianSiteHost()
    {
        CoordinateBridgeConfigurationProvider.HostOf("https://acme.atlassian.net/wiki")
            .Should().Be("acme.atlassian.net");
    }

    // ── Non-secret invariant ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Coordinates_ArePresentOnlyAsNonSecretConfig_NeverInTheSecretStore()
    {
        using var context = new SqliteTestContext();
        var stores = BuildStores(context);

        stores.Settings.Set("Onboarding:Board:Provider", OnboardingProviders.Jira);
        stores.Settings.Set("Onboarding:Board:Email", "dev@example.com");
        stores.Settings.Set("Onboarding:Board:Project", "ENG");
        stores.Secrets.Set("board:token", "jira_api_token");
        stores.Connections.Upsert(new ProviderConnection(
            "board", ProviderKind.Board, "https://acme.atlassian.net", "board:token", "Verified", DateTimeOffset.UtcNow));

        var configuration = BuildBridgedConfiguration(context, stores);

        // Live in configuration under the adapter keys...
        configuration["Atlassian:JiraDomain"].Should().Be("acme.atlassian.net");

        // ...but the coordinates are NOT secrets: the secret store holds only the token, never a coordinate.
        stores.Secrets.Get("Atlassian:JiraDomain").Should().BeNull();
        stores.Secrets.Get("Atlassian:JiraEmail").Should().BeNull();
        stores.Secrets.Get("Atlassian:JiraProject").Should().BeNull();

        // ...and the adapter KEY itself is not persisted in the settings table (only the Onboarding:* facts are);
        // it exists solely as the bridge's in-memory projection derived from the connection row.
        stores.Settings.Get("Atlassian:JiraDomain").Should().BeNull();
    }

    // ── Confluence wiki + board-wins-ties ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ConfluenceWiki_Projects_DomainAndEmail()
    {
        using var context = new SqliteTestContext();
        var stores = BuildStores(context);

        stores.Settings.Set("Onboarding:Wiki:Provider", OnboardingProviders.Confluence);
        stores.Settings.Set("Onboarding:Wiki:Email", "wiki@example.com");
        stores.Secrets.Set("wiki:token", "confluence_token");
        stores.Connections.Upsert(new ProviderConnection(
            "wiki", ProviderKind.Wiki, "https://docs.atlassian.net", "wiki:token", "Verified", DateTimeOffset.UtcNow));

        var configuration = BuildBridgedConfiguration(context, stores);

        configuration["Atlassian:JiraDomain"].Should().Be("docs.atlassian.net");
        configuration["Atlassian:JiraEmail"].Should().Be("wiki@example.com");
        configuration["Atlassian:JiraKey"].Should().Be("confluence_token");
    }

    [Fact]
    public void JiraBoard_WinsSharedAtlassianKeys_OverConfluenceWiki()
    {
        using var context = new SqliteTestContext();
        var stores = BuildStores(context);

        // Board (Jira) and wiki (Confluence) both map to Atlassian:* - the board must win the shared keys.
        stores.Settings.Set("Onboarding:Board:Provider", OnboardingProviders.Jira);
        stores.Settings.Set("Onboarding:Board:Email", "board@example.com");
        stores.Settings.Set("Onboarding:Board:Project", "ENG");
        stores.Settings.Set("Onboarding:Wiki:Provider", OnboardingProviders.Confluence);
        stores.Settings.Set("Onboarding:Wiki:Email", "wiki@example.com");
        stores.Secrets.Set("board:token", "board_token");
        stores.Connections.Upsert(new ProviderConnection(
            "board", ProviderKind.Board, "https://board.atlassian.net", "board:token", "Verified", DateTimeOffset.UtcNow));
        stores.Connections.Upsert(new ProviderConnection(
            "wiki", ProviderKind.Wiki, "https://wiki.atlassian.net", "wiki:token", "Verified", DateTimeOffset.UtcNow));

        var configuration = BuildBridgedConfiguration(context, stores);

        configuration["Atlassian:JiraDomain"].Should().Be("board.atlassian.net");
        configuration["Atlassian:JiraEmail"].Should().Be("board@example.com");
    }

    // ── Reload / attach / fresh-install behaviour ──────────────────────────────────────────────────────────

    [Fact]
    public void Reload_PicksUpCoordinatesStoredAfterStartup()
    {
        using var context = new SqliteTestContext();
        var stores = BuildStores(context);

        var builder = new ConfigurationBuilder().AddFravashiDatabase(context.DatabasePath);
        var coordinate = builder.AddFravashiCoordinateBridge();
        var configuration = builder.Build();
        coordinate.Provider.Attach(stores.Connections, stores.Settings);
        configuration["Atlassian:JiraDomain"].Should().BeNull();

        // The wizard writes the selection + coordinates + connection, then completion triggers a reload.
        stores.Settings.Set("Onboarding:Board:Provider", OnboardingProviders.Jira);
        stores.Settings.Set("Onboarding:Board:Email", "late@example.com");
        stores.Settings.Set("Onboarding:Board:Project", "LATE");
        stores.Connections.Upsert(new ProviderConnection(
            "board", ProviderKind.Board, "https://late.atlassian.net", "board:token", "Verified", DateTimeOffset.UtcNow));
        configuration.Reload();

        configuration["Atlassian:JiraDomain"].Should().Be("late.atlassian.net");
        configuration["Atlassian:JiraProject"].Should().Be("LATE");
    }

    [Fact]
    public void FreshInstall_NoConnection_LeavesAppsettingsDefaultsUntouched()
    {
        using var context = new SqliteTestContext();
        var stores = BuildStores(context);

        // A single-user default provided via appsettings/user-secrets (here an in-memory source standing in).
        var builder = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Atlassian:JiraDomain"] = "appsettings.example.net" })
            .AddFravashiDatabase(context.DatabasePath);
        var coordinate = builder.AddFravashiCoordinateBridge();
        var configuration = builder.Build();
        coordinate.Provider.Attach(stores.Connections, stores.Settings);
        configuration.Reload();

        // No connection exists → the bridge projects nothing → the appsettings default wins, unchanged.
        configuration["Atlassian:JiraDomain"].Should().Be("appsettings.example.net");
    }

    [Fact]
    public void ConnectionExists_DbProjectionWins_OverAppsettingsDefault()
    {
        using var context = new SqliteTestContext();
        var stores = BuildStores(context);

        stores.Settings.Set("Onboarding:Board:Provider", OnboardingProviders.Jira);
        stores.Connections.Upsert(new ProviderConnection(
            "board", ProviderKind.Board, "https://connection.atlassian.net", "board:token", "Verified", DateTimeOffset.UtcNow));

        var builder = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Atlassian:JiraDomain"] = "appsettings.example.net" })
            .AddFravashiDatabase(context.DatabasePath);
        var coordinate = builder.AddFravashiCoordinateBridge();
        var configuration = builder.Build();
        coordinate.Provider.Attach(stores.Connections, stores.Settings);
        configuration.Reload();

        // A connection exists → its derived domain wins over the appsettings default (mirrors the token bridge).
        configuration["Atlassian:JiraDomain"].Should().Be("connection.atlassian.net");
    }

    [Fact]
    public void BeforeAttach_BridgeProjectsNothing()
    {
        using var context = new SqliteTestContext();
        var stores = BuildStores(context);
        stores.Settings.Set("Onboarding:Board:Provider", OnboardingProviders.Jira);
        stores.Connections.Upsert(new ProviderConnection(
            "board", ProviderKind.Board, "https://acme.atlassian.net", "board:token", "Verified", DateTimeOffset.UtcNow));

        var builder = new ConfigurationBuilder().AddFravashiDatabase(context.DatabasePath);
        builder.AddFravashiCoordinateBridge();
        var configuration = builder.Build();

        configuration["Atlassian:JiraDomain"].Should().BeNull();
    }

    [Fact]
    public void LocalBoard_ProjectsNoCoordinates()
    {
        using var context = new SqliteTestContext();
        var stores = BuildStores(context);
        stores.Settings.Set("Onboarding:Board:Provider", OnboardingProviders.Local);
        stores.Connections.Upsert(new ProviderConnection(
            "board", ProviderKind.Board, null, null, "Local", DateTimeOffset.UtcNow));

        var configuration = BuildBridgedConfiguration(context, stores);

        configuration["Atlassian:JiraDomain"].Should().BeNull();
    }
}
