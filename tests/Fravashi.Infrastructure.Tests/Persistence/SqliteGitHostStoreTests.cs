using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.Persistence;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Persistence;

/// <summary>The per-git-host credential registry (plan 2c, decision 8).</summary>
public sealed class SqliteGitHostStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "fravashi-githost-store-tests", Guid.NewGuid().ToString("N"));
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteGitHostStore _store;

    public SqliteGitHostStoreTests()
    {
        Directory.CreateDirectory(_directory);
        _factory = new SqliteConnectionFactory(
            new SqliteStoreOptions(Path.Combine(_directory, "test.db"), null, Pooling: false));
        SqliteSchema.EnsureCreated(_factory);
        _store = new SqliteGitHostStore(_factory);
    }

    [Fact]
    public void Upsert_RoundTrips_AndNormalizesTheHost()
    {
        _store.Upsert(new GitHostRecord("Forge.Example.Test", "Forgejo", "githost:forge.example.test:token", DateTimeOffset.UtcNow));

        var stored = _store.Get("FORGE.EXAMPLE.TEST");
        stored.Should().NotBeNull();
        stored!.Host.Should().Be("forge.example.test");
        stored.Provider.Should().Be("Forgejo");
        stored.CredentialRef.Should().Be("githost:forge.example.test:token");
    }

    [Fact]
    public void Upsert_Overwrites_TheExistingEntry()
    {
        _store.Upsert(new GitHostRecord("github.example.com", "Forgejo", "githost:github.example.com:token", DateTimeOffset.UtcNow));
        _store.Upsert(new GitHostRecord("github.example.com", "GitHub", "githost:github.example.com:token", DateTimeOffset.UtcNow));

        _store.GetAll().Should().ContainSingle().Which.Provider.Should().Be("GitHub");
    }

    [Fact]
    public void GetAll_OrdersByHost()
    {
        _store.Upsert(new GitHostRecord("zulu.example.com", "Forgejo", "githost:zulu.example.com:token", DateTimeOffset.UtcNow));
        _store.Upsert(new GitHostRecord("alpha.example.com", "GitHub", "githost:alpha.example.com:token", DateTimeOffset.UtcNow));

        _store.GetAll().Select(h => h.Host).Should().ContainInOrder("alpha.example.com", "zulu.example.com");
    }

    [Fact]
    public void Remove_Deletes_AndUnknownHostIsNull()
    {
        _store.Upsert(new GitHostRecord("forge.example.test", "Forgejo", "githost:forge.example.test:token", DateTimeOffset.UtcNow));
        _store.Remove("forge.example.test");

        _store.Get("forge.example.test").Should().BeNull();
        _store.GetAll().Should().BeEmpty();
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
