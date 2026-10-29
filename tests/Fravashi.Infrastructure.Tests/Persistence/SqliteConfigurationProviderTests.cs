using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Persistence.Configuration;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Persistence;

public sealed class SqliteConfigurationProviderTests
{
    [Fact]
    public void Configuration_SurfacesSettingsTableValues()
    {
        using var context = new SqliteTestContext();
        new SqliteConfigurationStore(context.Factory).SetMany(new Dictionary<string, string?>
        {
            ["Tracker:Provider"] = "AzureDevOps",
            ["Repository:Root"] = "/repo",
        });

        var configuration = new ConfigurationBuilder()
            .AddFravashiDatabase(context.DatabasePath)
            .Build();

        configuration["Tracker:Provider"].Should().Be("AzureDevOps");
        configuration.GetSection("Repository")["Root"].Should().Be("/repo");
    }

    [Fact]
    public void Configuration_OnFreshDatabase_HasNoUserKeys_AndBootstrapsSchema()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fravashi-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "fresh.db");

        // The provider bootstraps the schema on Load even though nothing created the database first.
        var configuration = new ConfigurationBuilder()
            .AddFravashiDatabase(databasePath)
            .Build();

        configuration["Tracker:Provider"].Should().BeNull();
        File.Exists(databasePath).Should().BeTrue();
    }

    [Fact]
    public void Reload_PicksUpValuesWrittenAfterStartup()
    {
        using var context = new SqliteTestContext();
        var configuration = new ConfigurationBuilder()
            .AddFravashiDatabase(context.DatabasePath)
            .Build();
        configuration["Tracker:Provider"].Should().BeNull();

        new SqliteConfigurationStore(context.Factory).Set("Tracker:Provider", "Jira");
        configuration.Reload();

        configuration["Tracker:Provider"].Should().Be("Jira");
    }

    [Fact]
    public void Configuration_ExcludesInternalBookkeepingKeys()
    {
        using var context = new SqliteTestContext();

        var configuration = new ConfigurationBuilder()
            .AddFravashiDatabase(context.DatabasePath)
            .Build();

        configuration[SqliteSchema.SchemaVersionKey].Should().BeNull();
    }
}
