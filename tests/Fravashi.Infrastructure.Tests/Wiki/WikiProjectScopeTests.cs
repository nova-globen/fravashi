using AwesomeAssertions;
using Fravashi.Application.Wiki;
using Fravashi.Domain.Projects;
using Fravashi.Domain.Wiki;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Tests.Persistence;
using Fravashi.Infrastructure.Wiki;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Wiki;

/// <summary>
/// Backlog 121, schema step 27: the wiki is a project's. Until this step it was the one store with no project
/// column, and a document's number and slug were unique across the whole install, so a second project's
/// documents would have sat in the first's library and <c>@1</c> would have meant one thing everywhere.
/// </summary>
public sealed class WikiProjectScopeTests : IDisposable
{
    private readonly SqliteTestContext _context = new();
    private readonly SqliteWikiLibraries _wikis;
    private readonly ProjectId _first = ProjectId.Default;
    private readonly ProjectId _second;

    public WikiProjectScopeTests()
    {
        _wikis = new SqliteWikiLibraries(_context.Factory, TimeProvider.System);
        _second = new SqliteProjectStore(_context.Factory, TimeProvider.System).Create("Second").Id;
    }

    public void Dispose() => _context.Dispose();

    private static NewWikiItem Draft(string title, string body = "", WikiItemType type = WikiItemType.Draft) =>
        new() { Type = type, Title = title, Body = body };

    [Fact]
    public async Task Each_project_numbers_its_own_documents_and_the_same_title_is_free_in_both()
    {
        var a = (await _wikis.For(_first).CreateAsync(Draft("Notes"))).Value;
        var b = (await _wikis.For(_second).CreateAsync(Draft("Notes"))).Value;

        a.Number.Should().Be(1);
        b.Number.Should().Be(1, "the second project's first document is its number one");
        b.Slug.Should().Be(a.Slug, "a slug is unique within a project, so neither needs a suffix");
        b.Id.Should().NotBe(a.Id);
    }

    [Fact]
    public async Task A_number_a_slug_and_a_listing_answer_for_one_project_only()
    {
        var mine = (await _wikis.For(_first).CreateAsync(Draft("Only in the first"))).Value;
        await _wikis.For(_second).CreateAsync(Draft("Only in the second"));

        (await _wikis.For(_first).GetByNumberAsync(1))!.Title.Should().Be("Only in the first");
        (await _wikis.For(_second).GetByNumberAsync(1))!.Title.Should().Be("Only in the second");
        (await _wikis.For(_second).GetBySlugAsync(mine.Slug)).Should().BeNull();
        (await _wikis.For(_second).ListAsync(new WikiItemQuery())).Select(i => i.Title)
            .Should().Equal("Only in the second");
    }

    [Fact]
    public async Task A_reference_written_in_another_project_is_not_a_backlink_here()
    {
        // "#7" in the second project's document means the second project's item 7.
        await _wikis.For(_second).CreateAsync(Draft("Mentions seven", "See #7 for why."));
        var here = (await _wikis.For(_first).CreateAsync(Draft("Also mentions seven", "And #7 here."))).Value;

        var backlinks = await _wikis.For(_first).BacklinksAsync(new WikiReference(WikiReferenceKind.WorkItem, 7));

        backlinks.Select(i => i.Id).Should().Equal(here.Id);
    }

    [Fact]
    public async Task Each_project_may_have_its_one_branding_document()
    {
        (await _wikis.For(_first).CreateAsync(Draft("Brand", type: WikiItemType.Branding))).IsValid.Should().BeTrue();
        (await _wikis.For(_second).CreateAsync(Draft("Brand", type: WikiItemType.Branding))).IsValid.Should().BeTrue();

        var again = await _wikis.For(_second).CreateAsync(Draft("Brand again", type: WikiItemType.Branding));
        again.IsValid.Should().BeFalse("one per project still holds");
    }

    [Fact]
    public async Task A_change_made_by_id_keeps_the_document_in_its_own_project()
    {
        await _wikis.For(_first).CreateAsync(Draft("Taken title"));
        var other = (await _wikis.For(_second).CreateAsync(Draft("Something"))).Value;

        // Renamed through the by-id view, which belongs to no project: the slug must be found free in the
        // document's OWN project, where that title is not taken, and the document must not move.
        var renamed = await _wikis.Documents.UpdateAsync(other.Id, new WikiItemEdit { Title = "Taken title" }, other.Version);

        renamed.IsValid.Should().BeTrue();
        renamed.Value.Slug.Should().Be("taken-title", "nothing in ITS project holds that slug");
        (await _wikis.For(_second).GetByNumberAsync(other.Number))!.Title.Should().Be("Taken title");
        (await _wikis.For(_first).ListAsync(new WikiItemQuery())).Should().ContainSingle();
    }

    [Fact]
    public async Task The_by_id_view_refuses_anything_that_means_something_only_inside_a_project()
    {
        var byId = (IWikiLibrary)_wikis.Documents;

        var number = () => byId.GetByNumberAsync(1);
        var list = () => byId.ListAsync(new WikiItemQuery());
        var create = () => byId.CreateAsync(Draft("Nowhere"));

        await number.Should().ThrowAsync<InvalidOperationException>();
        await list.Should().ThrowAsync<InvalidOperationException>();
        await create.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task An_install_from_before_the_step_keeps_its_documents_and_never_reuses_a_number()
    {
        // The state step 26 left: documents with no project column to speak of, and the single-row counter
        // AHEAD of the highest number because a document was deleted. The counter exists so that number is
        // never handed out again; restarting it at the migration would break exactly that.
        var wiki = _wikis.For(_first);
        await wiki.CreateAsync(Draft("Kept"));
        var doomed = (await wiki.CreateAsync(Draft("Deleted later"))).Value;
        await wiki.DeleteAsync(doomed.Id);

        using (var connection = _context.Factory.Open())
        {
            Run(connection, "DROP TABLE wiki_numbers;");
            Run(connection, "DROP INDEX ux_wiki_items_project_number; DROP INDEX ux_wiki_items_project_slug;");
            Run(connection, "ALTER TABLE wiki_items DROP COLUMN project_id;");
            Run(connection, "CREATE UNIQUE INDEX ux_wiki_items_number ON wiki_items (number);");
            Run(connection, "CREATE UNIQUE INDEX ux_wiki_items_slug ON wiki_items (slug);");
            Run(connection, "UPDATE wiki_state SET next_number = 3 WHERE id = 1;");
            Run(connection, $"UPDATE settings SET value = '26' WHERE key = '{SqliteSchema.SchemaVersionKey}';");
        }

        SqliteSchema.EnsureCreated(_context.Factory);

        (await wiki.GetByNumberAsync(1))!.Title.Should().Be("Kept");
        var next = (await wiki.CreateAsync(Draft("After the step"))).Value;
        next.Number.Should().Be(3, "two was a deleted document's number and must stay unused");
    }

    private static void Run(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
