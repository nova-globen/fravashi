using Fravashi.Application.Onboarding.Auth;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Application.Tests;

/// <summary>
/// The shipped OAuth client-id defaults (ADR 0004). Nova Globen registers one app per identity provider for
/// the whole product and bakes the non-secret client ids here; a user never registers an app. Atlassian ships
/// no default (3LO needs a secret that can't be shipped in a self-hosted product). Empty placeholders project
/// nothing, so an unregistered provider never masks a per-install override and its sign-in stays honestly
/// "not available in this build yet".
/// </summary>
public sealed class VendorAppDefaultsTests
{
    [Fact]
    public void No_atlassian_default_ships_because_3lo_needs_an_unshippable_secret()
    {
        VendorAppDefaults.ClientIdDefaultFor(AuthSettings.AtlassianClientId).Should().BeNull();
    }

    [Fact]
    public void AsConfiguration_projects_only_the_non_empty_shipped_client_ids()
    {
        // Both device-code vendor apps are now registered and ship a client id (GitHub and Azure DevOps / Entra);
        // Atlassian ships none. An empty default never projects, so an unregistered provider never masks a later
        // per-install override - the projection carries only the two non-empty keys.
        VendorAppDefaults.AsConfiguration().Should().OnlyContain(kv =>
            kv.Key == AuthSettings.GitHubClientId || kv.Key == AuthSettings.AzureDevOpsClientId);
        VendorAppDefaults.AsConfiguration().Should().NotContain(kv => string.IsNullOrWhiteSpace(kv.Value));
        VendorAppDefaults.AsConfiguration().Should().Contain(kv =>
            kv.Key == AuthSettings.GitHubClientId && !string.IsNullOrWhiteSpace(kv.Value));
        VendorAppDefaults.AsConfiguration().Should().Contain(kv =>
            kv.Key == AuthSettings.AzureDevOpsClientId && !string.IsNullOrWhiteSpace(kv.Value));
    }

    [Fact]
    public void ClientIdDefaultFor_returns_null_for_a_non_client_id_key()
    {
        VendorAppDefaults.ClientIdDefaultFor("Some:Other:Key").Should().BeNull();
    }
}
