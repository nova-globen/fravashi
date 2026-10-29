using Fravashi.Infrastructure.Persistence;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Persistence;

public sealed class SqliteConfigurationStoreTests
{
    [Fact]
    public void Set_ThenGet_RoundTripsValue()
    {
        using var context = new SqliteTestContext();
        var store = new SqliteConfigurationStore(context.Factory);

        store.Set("Tracker:Provider", "AzureDevOps");

        store.Get("Tracker:Provider").Should().Be("AzureDevOps");
    }

    [Fact]
    public void Set_ExistingKey_Overwrites()
    {
        using var context = new SqliteTestContext();
        var store = new SqliteConfigurationStore(context.Factory);

        store.Set("Repository:Root", "/one");
        store.Set("Repository:Root", "/two");

        store.Get("Repository:Root").Should().Be("/two");
    }

    [Fact]
    public void Get_UnknownKey_ReturnsNull()
    {
        using var context = new SqliteTestContext();
        var store = new SqliteConfigurationStore(context.Factory);

        store.Get("Nope").Should().BeNull();
    }

    [Fact]
    public void SetMany_WritesAllKeysInOneTransaction()
    {
        using var context = new SqliteTestContext();
        var store = new SqliteConfigurationStore(context.Factory);

        store.SetMany(new Dictionary<string, string?>
        {
            ["Atlassian:BaseUrl"] = "https://example.atlassian.net",
            ["Tracker:Provider"] = "Jira",
        });

        var all = store.GetAll();
        all["Atlassian:BaseUrl"].Should().Be("https://example.atlassian.net");
        all["Tracker:Provider"].Should().Be("Jira");
    }

    [Fact]
    public void Remove_DeletesKey()
    {
        using var context = new SqliteTestContext();
        var store = new SqliteConfigurationStore(context.Factory);
        store.Set("Temp:Key", "value");

        store.Remove("Temp:Key");

        store.Get("Temp:Key").Should().BeNull();
    }
}
