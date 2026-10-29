using Fravashi.Application.Onboarding;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.Onboarding.Auth;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Tests.Persistence;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Onboarding.Auth;

/// <summary>
/// The sign-in configuration store - provider→strategy mapping, the honest "configured?" gate that drives the
/// disabled-without-a-client-id button, and endpoint-override resolution.
/// </summary>
public sealed class AuthConfigStoreTests
{
    private static AuthConfigStore Build(SqliteTestContext ctx, out IConfigurationStore settings, out ISecretStore secrets)
    {
        settings = new SqliteConfigurationStore(ctx.Factory);
        secrets = new SqliteSecretStore(ctx.Factory, new ReversibleTestProtector());
        return new AuthConfigStore(settings, secrets);
    }

    [Theory]
    [InlineData(OnboardingProviders.GitHub, AuthStrategy.DeviceCode)]
    [InlineData(OnboardingProviders.GitHubWiki, AuthStrategy.DeviceCode)]
    [InlineData(OnboardingProviders.AzureDevOps, AuthStrategy.DeviceCode)]
    [InlineData(OnboardingProviders.AzureDevOpsWiki, AuthStrategy.DeviceCode)]
    [InlineData(OnboardingProviders.Jira, AuthStrategy.AuthCode)]
    [InlineData(OnboardingProviders.Confluence, AuthStrategy.AuthCode)]
    [InlineData(OnboardingProviders.Local, AuthStrategy.Pat)]
    [InlineData(OnboardingProviders.None, AuthStrategy.Pat)]
    public void StrategyFor_maps_each_provider(string provider, AuthStrategy expected)
    {
        using var ctx = new SqliteTestContext();
        Build(ctx, out _, out _).StrategyFor(provider).Should().Be(expected);
    }

    [Fact]
    public void A_device_code_provider_config_with_a_blanked_client_id_is_unconfigured()
    {
        // Both device-code vendor apps now ship a baked-in client id (ADR 0004), so no real provider resolves to
        // an absent client id through the store. The honest "not available in this build yet" gate is therefore
        // proven at the record level: a device-code config whose client id is blanked to null is unconfigured
        // with a "client id" reason, and gains configuration the moment a client id is present.
        var blanked = new ProviderAuthConfig(OnboardingProviders.AzureDevOps, AuthStrategy.DeviceCode, ClientId: null, HasSecret: false);
        blanked.IsConfigured.Should().BeFalse();
        blanked.UnavailableReason.Should().Contain("client id");

        var configured = blanked with { ClientId = "entra-client" };
        configured.IsConfigured.Should().BeTrue();
        configured.UnavailableReason.Should().BeNull();
    }

    [Fact]
    public void Without_a_database_value_the_azure_devops_client_id_falls_back_to_the_shipped_vendor_default()
    {
        using var ctx = new SqliteTestContext();
        var store = Build(ctx, out _, out _);

        // Nova Globen has registered the Entra app (ADR 0004), so with no DB override the Azure DevOps client id
        // resolves to the baked-in vendor default: sign-in is configured and the device-code flow is resolvable
        // through the same path, with no per-install registration.
        var shipped = VendorAppDefaults.ClientIdDefaultFor(AuthSettings.AzureDevOpsClientId);
        shipped.Should().NotBeNullOrEmpty();
        store.Get(OnboardingProviders.AzureDevOps).ClientId.Should().Be(shipped);
        store.Get(OnboardingProviders.AzureDevOps).IsConfigured.Should().BeTrue();
        store.Get(OnboardingProviders.AzureDevOps).UnavailableReason.Should().BeNull();
        store.ResolveEntra()!.ClientId.Should().Be(shipped);
    }

    [Fact]
    public void AuthCode_provider_needs_both_a_client_id_and_a_secret()
    {
        using var ctx = new SqliteTestContext();
        var store = Build(ctx, out var settings, out var secrets);

        settings.Set(AuthSettings.AtlassianClientId, "atl-client");
        var withoutSecret = store.Get(OnboardingProviders.Jira);
        withoutSecret.IsConfigured.Should().BeFalse();
        withoutSecret.UnavailableReason.Should().Contain("secret");

        secrets.Set(AuthSettings.AtlassianClientSecretRef, "atl-secret");
        store.Get(OnboardingProviders.Jira).IsConfigured.Should().BeTrue();
    }

    [Fact]
    public void A_database_client_id_wins_over_the_shipped_vendor_default()
    {
        using var ctx = new SqliteTestContext();
        var store = Build(ctx, out var settings, out _);
        settings.Set(AuthSettings.GitHubClientId, "db-client");

        // The advanced (DB) override is used verbatim; it is never masked by the baked-in default.
        store.ResolveGitHub()!.ClientId.Should().Be("db-client");
        store.Get(OnboardingProviders.GitHub).ClientId.Should().Be("db-client");
    }

    [Fact]
    public void Without_a_database_value_the_client_id_falls_back_to_the_shipped_vendor_default()
    {
        using var ctx = new SqliteTestContext();
        var store = Build(ctx, out _, out _);

        // Nova Globen has registered the GitHub app (ADR 0004), so with no DB override the client id resolves
        // to the baked-in vendor default and GitHub sign-in is configured and resolvable through the same path.
        var shipped = VendorAppDefaults.ClientIdDefaultFor(AuthSettings.GitHubClientId);
        shipped.Should().NotBeNullOrEmpty();
        store.Get(OnboardingProviders.GitHub).ClientId.Should().Be(shipped);
        store.Get(OnboardingProviders.GitHub).IsConfigured.Should().BeTrue();
        store.ResolveGitHub()!.ClientId.Should().Be(shipped);
    }

    [Fact]
    public void ResolveEntra_builds_the_endpoint_from_authority_and_tenant_by_default()
    {
        using var ctx = new SqliteTestContext();
        var store = Build(ctx, out var settings, out _);
        settings.Set(AuthSettings.AzureDevOpsClientId, "entra");

        var resolved = store.ResolveEntra();

        resolved.Should().NotBeNull();
        resolved!.DeviceCodeUrl.Should().Be("https://login.microsoftonline.com/organizations/oauth2/v2.0/devicecode");
        resolved.Scope.Should().Contain("499b84ac-1321-427f-aa17-267ca6975798/.default");
        resolved.Scope.Should().Contain("offline_access");
    }

    [Fact]
    public void ResolveEntra_honours_an_endpoint_override()
    {
        using var ctx = new SqliteTestContext();
        var store = Build(ctx, out var settings, out _);
        settings.Set(AuthSettings.AzureDevOpsClientId, "entra");
        settings.Set(AuthSettings.AzureDevOpsDeviceCodeUrl, "https://fake-idp/devicecode");

        store.ResolveEntra()!.DeviceCodeUrl.Should().Be("https://fake-idp/devicecode");
    }
}
