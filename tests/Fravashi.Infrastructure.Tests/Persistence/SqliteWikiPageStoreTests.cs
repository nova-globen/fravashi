using Fravashi.Application.Wiki;
using Fravashi.Infrastructure.Persistence;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Persistence;

public sealed class SqliteWikiPageStoreTests
{
    private static WikiPage Page(string space, string slug, string body, bool conflict = false) =>
        new(Guid.NewGuid().ToString("n"), space, $"Title {slug}", slug, body, "markdown",
            "v1", "H" + body, "H" + body, conflict, DateTimeOffset.UtcNow);

    [Fact]
    public void Upsert_Insert_Then_GetBySlug_RoundTrips()
    {
        using var ctx = new SqliteTestContext();
        var store = new SqliteWikiPageStore(ctx.Factory);

        var page = Page("DOCS", "home", "hello");
        store.Upsert(page);

        var loaded = store.GetBySlug("DOCS", "home");
        loaded.Should().NotBeNull();
        loaded!.Body.Should().Be("hello");
        loaded.Format.Should().Be("markdown");
        loaded.SourceHash.Should().Be("Hhello");
        store.Get(page.Id)!.Slug.Should().Be("home");
        store.Count().Should().Be(1);
    }

    [Fact]
    public void Upsert_SameSpaceAndSlug_Overwrites_NotDuplicates()
    {
        using var ctx = new SqliteTestContext();
        var store = new SqliteWikiPageStore(ctx.Factory);

        store.Upsert(Page("DOCS", "home", "v1"));
        store.Upsert(Page("DOCS", "home", "v2"));

        store.Count().Should().Be(1);
        store.GetBySlug("DOCS", "home")!.Body.Should().Be("v2");
    }

    [Fact]
    public void Upsert_DistinctSlugs_AndSpaces_Coexist()
    {
        using var ctx = new SqliteTestContext();
        var store = new SqliteWikiPageStore(ctx.Factory);

        store.Upsert(Page("DOCS", "home", "a"));
        store.Upsert(Page("DOCS", "guide", "b"));
        store.Upsert(Page("OPS", "home", "c"));

        store.Count().Should().Be(3);
        store.List().Should().HaveCount(3);
    }

    [Fact]
    public void Remove_DeletesTheRow()
    {
        using var ctx = new SqliteTestContext();
        var store = new SqliteWikiPageStore(ctx.Factory);
        var page = Page("DOCS", "home", "x");
        store.Upsert(page);

        store.Remove(page.Id);

        store.Count().Should().Be(0);
        store.GetBySlug("DOCS", "home").Should().BeNull();
    }

    [Fact]
    public void HasConflict_RoundTrips()
    {
        using var ctx = new SqliteTestContext();
        var store = new SqliteWikiPageStore(ctx.Factory);
        store.Upsert(Page("DOCS", "home", "x", conflict: true));

        store.GetBySlug("DOCS", "home")!.HasConflict.Should().BeTrue();
    }
}
