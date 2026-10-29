using Fravashi.Application.Persistence;
using Fravashi.Infrastructure.Persistence;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Persistence;

public sealed class SqliteProviderConnectionStoreTests
{
    private static ProviderConnection Sample(string id, ProviderKind kind) => new(
        Id: id,
        Kind: kind,
        BaseUrl: "https://example.com",
        CredentialRef: "cred:" + id,
        Status: "Untested",
        UpdatedUtc: new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Upsert_ThenGet_RoundTripsAllFields()
    {
        using var context = new SqliteTestContext();
        var store = new SqliteProviderConnectionStore(context.Factory);
        var connection = Sample("gh", ProviderKind.Source);

        store.Upsert(connection);

        store.Get("gh").Should().Be(connection);
    }

    [Fact]
    public void Upsert_ExistingId_Updates()
    {
        using var context = new SqliteTestContext();
        var store = new SqliteProviderConnectionStore(context.Factory);
        store.Upsert(Sample("gh", ProviderKind.Source));

        store.Upsert(Sample("gh", ProviderKind.Source) with { Status = "Verified" });

        store.Get("gh")!.Status.Should().Be("Verified");
        store.GetAll().Should().ContainSingle();
    }

    [Fact]
    public void GetByKind_FiltersByCategory()
    {
        using var context = new SqliteTestContext();
        var store = new SqliteProviderConnectionStore(context.Factory);
        store.Upsert(Sample("gh", ProviderKind.Source));
        store.Upsert(Sample("confluence", ProviderKind.Wiki));
        store.Upsert(Sample("ado-board", ProviderKind.Board));

        store.GetByKind(ProviderKind.Wiki).Should().ContainSingle(c => c.Id == "confluence");
    }

    [Fact]
    public void Remove_DeletesConnection()
    {
        using var context = new SqliteTestContext();
        var store = new SqliteProviderConnectionStore(context.Factory);
        store.Upsert(Sample("gh", ProviderKind.Source));

        store.Remove("gh");

        store.Get("gh").Should().BeNull();
    }

    [Fact]
    public void BaseUrlAndCredentialRef_MayBeNull_ForLocalProviders()
    {
        using var context = new SqliteTestContext();
        var store = new SqliteProviderConnectionStore(context.Factory);
        var local = new ProviderConnection("local-board", ProviderKind.Board, null, null, "Verified", DateTimeOffset.UtcNow);

        store.Upsert(local);

        var read = store.Get("local-board")!;
        read.BaseUrl.Should().BeNull();
        read.CredentialRef.Should().BeNull();
    }
}
