using Fravashi.Application.Onboarding.Auth;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Persistence.Configuration;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Persistence;

/// <summary>
/// The vendor-defaults configuration layer (ADR 0004): the product's shipped OAuth client ids sit BELOW the
/// SQLite settings source, so a per-install (advanced) override in the database wins, while an unregistered
/// provider contributes no key.
/// </summary>
public sealed class VendorAppDefaultsConfigurationTests
{
    [Fact]
    public void A_database_advanced_override_wins_over_the_vendor_defaults_layer()
    {
        using var context = new SqliteTestContext();
        new SqliteConfigurationStore(context.Factory).Set(AuthSettings.GitHubClientId, "db-override");

        // The exact Program.cs order: vendor defaults added first (below), the database added second (above).
        var configuration = new ConfigurationBuilder()
            .AddFravashiVendorDefaults()
            .AddFravashiDatabase(context.DatabasePath)
            .Build();

        configuration[AuthSettings.GitHubClientId].Should().Be("db-override");
    }

    [Fact]
    public void The_vendor_defaults_layer_projects_only_the_shipped_client_ids()
    {
        var configuration = new ConfigurationBuilder()
            .AddFravashiVendorDefaults()
            .Build();

        // Both device-code vendor apps are registered and project their shipped client id (GitHub and Azure
        // DevOps / Entra); Atlassian ships none and contributes no key, so a later database override is never
        // masked (ADR 0004). Each layer equals its VendorAppDefaults source of truth.
        configuration[AuthSettings.GitHubClientId]
            .Should().Be(VendorAppDefaults.ClientIdDefaultFor(AuthSettings.GitHubClientId));
        configuration[AuthSettings.GitHubClientId].Should().NotBeNullOrEmpty();
        configuration[AuthSettings.AzureDevOpsClientId]
            .Should().Be(VendorAppDefaults.ClientIdDefaultFor(AuthSettings.AzureDevOpsClientId));
        configuration[AuthSettings.AzureDevOpsClientId].Should().NotBeNullOrEmpty();
        configuration[AuthSettings.AtlassianClientId].Should().BeNull();
    }
}
