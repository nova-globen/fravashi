using Fravashi.Infrastructure.Persistence;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Persistence;

public sealed class SqliteSecretStoreTests
{
    [Fact]
    public void Set_ThenGet_RoundTripsPlaintext()
    {
        using var context = new SqliteTestContext();
        var store = new SqliteSecretStore(context.Factory, new ReversibleTestProtector());

        store.Set("cred:gh", "ghp_secrettoken");

        store.Get("cred:gh").Should().Be("ghp_secrettoken");
    }

    [Fact]
    public void Set_StoresProtectedValue_NotPlaintext()
    {
        using var context = new SqliteTestContext();
        var store = new SqliteSecretStore(context.Factory, new ReversibleTestProtector());
        store.Set("cred:gh", "ghp_secrettoken");

        using var connection = new SqliteConnection($"Data Source={context.DatabasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT protected_value FROM secrets WHERE reference = 'cred:gh';";
        var stored = (string)command.ExecuteScalar()!;

        stored.Should().NotContain("ghp_secrettoken");
        stored.Should().StartWith("enc:");
    }

    [Fact]
    public void Set_ExistingReference_Overwrites()
    {
        using var context = new SqliteTestContext();
        var store = new SqliteSecretStore(context.Factory, new ReversibleTestProtector());

        store.Set("cred:gh", "old");
        store.Set("cred:gh", "new");

        store.Get("cred:gh").Should().Be("new");
    }

    [Fact]
    public void Get_UnknownReference_ReturnsNull()
    {
        using var context = new SqliteTestContext();
        var store = new SqliteSecretStore(context.Factory, new ReversibleTestProtector());

        store.Get("cred:missing").Should().BeNull();
    }

    [Fact]
    public void Remove_DeletesSecret()
    {
        using var context = new SqliteTestContext();
        var store = new SqliteSecretStore(context.Factory, new ReversibleTestProtector());
        store.Set("cred:gh", "token");

        store.Remove("cred:gh");

        store.Get("cred:gh").Should().BeNull();
    }
}
