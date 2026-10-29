using AwesomeAssertions;
using Fravashi.Application.Wiki;
using Fravashi.Domain.Wiki;
using Fravashi.Infrastructure.Tests.Persistence;
using Fravashi.Infrastructure.Wiki;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Wiki;

/// <summary>
/// Backlog 39's store: items of every type created, edited, linked and found, over the real schema. The two
/// derived things get the most attention here, because they are what a reader would otherwise have to
/// maintain by hand: the slug that follows the title, and the reference index that makes a link navigable
/// from the end that never mentioned it.
/// </summary>
public sealed class SqliteWikiLibraryTests : IDisposable
{
    private readonly SqliteTestContext _context = new();
    private readonly SqliteWikiLibrary _wiki;

    public SqliteWikiLibraryTests() => _wiki = new SqliteWikiLibrary(_context.Factory, TimeProvider.System, Fravashi.Domain.Projects.ProjectId.Default);

    public void Dispose() => _context.Dispose();

    private async Task<WikiItem> CreateAsync(
        string title, WikiItemType type = WikiItemType.Draft, string body = "", IReadOnlyList<WikiLink>? links = null)
    {
        var created = await _wiki.CreateAsync(new NewWikiItem
        {
            Type = type,
            Title = title,
            Body = body,
            Links = links ?? [],
        });

        created.IsValid.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Code)));
        return created.Value;
    }

    // ---- backlog 49: documents form a tree -------------------------------------------------------------

    [Fact]
    public async Task A_document_created_under_another_keeps_its_parent_and_a_move_changes_it()
    {
        var folder = await CreateAsync("adr", WikiItemType.Reference);
        var other = await CreateAsync("plans", WikiItemType.Reference);
        var created = await _wiki.CreateAsync(new NewWikiItem { Type = WikiItemType.Adr, Title = "0001", ParentId = folder.Id });

        created.Value.ParentId.Should().Be(folder.Id);
        (await _wiki.GetAsync(created.Value.Id))!.ParentId.Should().Be(folder.Id);

        var moved = await _wiki.UpdateAsync(created.Value.Id, new WikiItemEdit { Parent = new WikiParentChange(other.Id) }, 1);
        moved.Value.ParentId.Should().Be(other.Id);

        var top = await _wiki.UpdateAsync(created.Value.Id, new WikiItemEdit { Parent = new WikiParentChange(null) }, 2);
        top.Value.ParentId.Should().BeNull();

        var untouched = await _wiki.UpdateAsync(created.Value.Id, new WikiItemEdit { Title = "0001 renamed" }, 3);
        untouched.Value.ParentId.Should().BeNull("an edit that does not mention the parent leaves it where it is");
    }

    [Fact]
    public async Task A_document_cannot_be_put_under_itself_or_its_own_child()
    {
        var top = await CreateAsync("top");
        var child = (await _wiki.CreateAsync(new NewWikiItem { Type = WikiItemType.Draft, Title = "child", ParentId = top.Id })).Value;

        (await _wiki.UpdateAsync(top.Id, new WikiItemEdit { Parent = new WikiParentChange(top.Id) }, 1))
            .Errors.Should().ContainSingle(e => e.Code == WikiCodes.ParentCycle);
        (await _wiki.UpdateAsync(top.Id, new WikiItemEdit { Parent = new WikiParentChange(child.Id) }, 1))
            .Errors.Should().ContainSingle(e => e.Code == WikiCodes.ParentCycle);
    }

    [Fact]
    public async Task A_parent_from_another_project_or_nowhere_is_refused()
    {
        var elsewhere = await new SqliteWikiLibrary(_context.Factory, TimeProvider.System, new Fravashi.Domain.Projects.ProjectId("other"))
            .CreateAsync(new NewWikiItem { Type = WikiItemType.Draft, Title = "Theirs" });

        (await _wiki.CreateAsync(new NewWikiItem { Type = WikiItemType.Draft, Title = "Mine", ParentId = elsewhere.Value.Id }))
            .Errors.Should().ContainSingle(e => e.Code == WikiCodes.ParentNotFound);
        (await _wiki.CreateAsync(new NewWikiItem { Type = WikiItemType.Draft, Title = "Mine", ParentId = "nothing" }))
            .Errors.Should().ContainSingle(e => e.Code == WikiCodes.ParentNotFound);
    }

    [Fact]
    public async Task Deleting_a_document_moves_its_children_up_to_where_it_was()
    {
        var root = await CreateAsync("root");
        var middle = (await _wiki.CreateAsync(new NewWikiItem { Type = WikiItemType.Draft, Title = "middle", ParentId = root.Id })).Value;
        var leaf = (await _wiki.CreateAsync(new NewWikiItem { Type = WikiItemType.Draft, Title = "leaf", ParentId = middle.Id })).Value;

        (await _wiki.DeleteAsync(middle.Id)).Should().BeTrue();

        (await _wiki.GetAsync(leaf.Id))!.ParentId.Should().Be(root.Id);
    }

    [Fact]
    public async Task An_item_of_every_type_can_be_created_and_read_back()
    {
        foreach (var type in Enum.GetValues<WikiItemType>())
        {
            var created = await CreateAsync("A " + type + " document", type);

            var read = await _wiki.GetByNumberAsync(created.Number);
            read.Should().NotBeNull();
            read!.Type.Should().Be(type);
            read.Title.Should().Be("A " + type + " document");
        }
    }

    [Fact]
    public async Task Numbers_are_allocated_in_order_and_never_reused()
    {
        var first = await CreateAsync("First");
        var second = await CreateAsync("Second");

        second.Number.Should().Be(first.Number + 1);

        (await _wiki.DeleteAsync(second.Id)).Should().BeTrue();

        var third = await CreateAsync("Third");
        third.Number.Should().Be(second.Number + 1, "a number a reader may have written down is not handed out twice");
    }

    [Fact]
    public async Task The_slug_follows_the_title_and_stays_unique()
    {
        var first = await CreateAsync("Release pipeline notes");
        first.Slug.Should().Be("release-pipeline-notes");

        var second = await CreateAsync("Release pipeline notes");
        second.Slug.Should().Be("release-pipeline-notes-2");

        (await _wiki.GetBySlugAsync("release-pipeline-notes"))!.Id.Should().Be(first.Id);
    }

    [Fact]
    public async Task Renaming_moves_the_slug_and_keeps_the_number()
    {
        var item = await CreateAsync("The old name");

        var updated = await _wiki.UpdateAsync(item.Id, new WikiItemEdit { Title = "The new name" }, item.Version);

        updated.IsValid.Should().BeTrue();
        updated.Value.Slug.Should().Be("the-new-name");
        updated.Value.Number.Should().Be(item.Number, "prose points at the number, so the number cannot move");
    }

    // ---- backlog 209: a snapshot per version, so an old one can still be read back ---------------------

    [Fact]
    public async Task Asking_for_the_current_version_returns_the_document_itself()
    {
        var item = await CreateAsync("A design", body: "First shape.");

        (await _wiki.GetVersionAsync(item.Id, item.Version))!.Body.Should().Be("First shape.");
    }

    [Fact]
    public async Task Asking_for_an_earlier_version_returns_the_body_as_it_stood_then()
    {
        var item = await CreateAsync("A design", body: "First shape.");
        var edited = await _wiki.UpdateAsync(item.Id, new WikiItemEdit { Body = "Second shape." }, item.Version);
        edited.IsValid.Should().BeTrue();
        await _wiki.UpdateAsync(item.Id, new WikiItemEdit { Body = "Third shape." }, edited.Value.Version);

        var first = await _wiki.GetVersionAsync(item.Id, 1);
        first.Should().NotBeNull();
        first!.Body.Should().Be("First shape.");
        first.Version.Should().Be(1);

        var second = await _wiki.GetVersionAsync(item.Id, 2);
        second!.Body.Should().Be("Second shape.");
    }

    [Fact]
    public async Task A_version_higher_than_the_current_one_does_not_exist()
    {
        var item = await CreateAsync("A design", body: "Only shape.");

        (await _wiki.GetVersionAsync(item.Id, item.Version + 1)).Should().BeNull();
    }

    [Fact]
    public async Task A_version_of_a_document_that_does_not_exist_is_nothing()
    {
        (await _wiki.GetVersionAsync("no-such-id", 1)).Should().BeNull();
    }

    [Fact]
    public async Task An_edit_against_a_stale_version_is_refused_and_writes_nothing()
    {
        var item = await CreateAsync("Contested");
        var first = await _wiki.UpdateAsync(item.Id, new WikiItemEdit { Body = "One." }, item.Version);
        first.IsValid.Should().BeTrue();

        var second = await _wiki.UpdateAsync(item.Id, new WikiItemEdit { Body = "Two." }, item.Version);

        second.IsValid.Should().BeFalse();
        second.Errors[0].Code.Should().Be(WikiCodes.Conflict);
        (await _wiki.GetAsync(item.Id))!.Body.Should().Be("One.");
    }

    [Fact]
    public async Task A_field_not_mentioned_is_left_alone()
    {
        var item = await CreateAsync("Keeps its body", WikiItemType.Plan, "The body.");

        var updated = await _wiki.UpdateAsync(item.Id, new WikiItemEdit { Title = "Renamed" }, item.Version);

        updated.Value.Body.Should().Be("The body.");
        updated.Value.Type.Should().Be(WikiItemType.Plan);
    }

    // ---- links, both ways ---------------------------------------------------------------------

    [Fact]
    public async Task A_reference_written_in_a_body_is_found_from_the_other_end()
    {
        // Nothing on the work item says anything about this page, and the link still works both ways.
        var page = await CreateAsync("Why the gate exists", WikiItemType.Governance, "This explains #44.");

        var pointing = await _wiki.BacklinksAsync(new WikiReference(WikiReferenceKind.WorkItem, 44));

        pointing.Should().ContainSingle().Which.Id.Should().Be(page.Id);
    }

    [Fact]
    public async Task A_typed_link_is_found_from_the_other_end_too()
    {
        var page = await CreateAsync(
            "The plan",
            WikiItemType.Plan,
            links: [new WikiLink(WikiLinkKind.WorkItem, "44", "Gate G1")]);

        var pointing = await _wiki.BacklinksAsync(new WikiReference(WikiReferenceKind.WorkItem, 44));

        pointing.Should().ContainSingle().Which.Id.Should().Be(page.Id);
    }

    [Fact]
    public async Task The_reference_index_follows_an_edit_rather_than_accumulating()
    {
        var page = await CreateAsync("Notes", WikiItemType.Draft, "About #44.");

        var updated = await _wiki.UpdateAsync(page.Id, new WikiItemEdit { Body = "About #45 now." }, page.Version);
        updated.IsValid.Should().BeTrue();

        (await _wiki.BacklinksAsync(new WikiReference(WikiReferenceKind.WorkItem, 44))).Should().BeEmpty();
        (await _wiki.BacklinksAsync(new WikiReference(WikiReferenceKind.WorkItem, 45))).Should().ContainSingle();
    }

    [Fact]
    public async Task A_reference_inside_a_code_fence_creates_no_link()
    {
        // Which is what lets a document explain the syntax without pointing at whatever it names.
        await CreateAsync("How references work", WikiItemType.Reference, "Write:\n```\n#44\n```\n");

        (await _wiki.BacklinksAsync(new WikiReference(WikiReferenceKind.WorkItem, 44))).Should().BeEmpty();
    }

    [Fact]
    public async Task Wiki_to_wiki_references_are_found_both_ways()
    {
        var target = await CreateAsync("The target");
        var pointing = await CreateAsync("The pointer", WikiItemType.Draft, $"See @{target.Number}.");

        var found = await _wiki.BacklinksAsync(target.Reference);

        found.Should().ContainSingle().Which.Id.Should().Be(pointing.Id);
    }

    [Fact]
    public async Task Deleting_an_item_takes_its_links_with_it()
    {
        var page = await CreateAsync("Notes", WikiItemType.Draft, "About #44.");

        (await _wiki.DeleteAsync(page.Id)).Should().BeTrue();

        (await _wiki.BacklinksAsync(new WikiReference(WikiReferenceKind.WorkItem, 44))).Should().BeEmpty();
        (await _wiki.DeleteAsync(page.Id)).Should().BeFalse();
    }

    // ---- what it refuses ------------------------------------------------------------------------

    [Fact]
    public async Task A_project_holds_at_most_one_branding_item()
    {
        // The acceptance says "a branding item", singular: two would make "the project's colours" ambiguous.
        await CreateAsync("Brand", WikiItemType.Branding);

        var second = await _wiki.CreateAsync(new NewWikiItem { Type = WikiItemType.Branding, Title = "Brand again" });

        second.IsValid.Should().BeFalse();
        second.Errors[0].Code.Should().Be(WikiCodes.BrandingNotRepeatable);
    }

    [Fact]
    public async Task An_item_cannot_be_turned_into_a_second_branding_item_either()
    {
        await CreateAsync("Brand", WikiItemType.Branding);
        var other = await CreateAsync("Something else");

        var changed = await _wiki.UpdateAsync(
            other.Id, new WikiItemEdit { Type = WikiItemType.Branding }, other.Version);

        changed.IsValid.Should().BeFalse();
        changed.Errors[0].Code.Should().Be(WikiCodes.BrandingNotRepeatable);
    }

    [Fact]
    public async Task A_blank_title_is_refused()
    {
        var created = await _wiki.CreateAsync(new NewWikiItem { Type = WikiItemType.Draft, Title = "   " });

        created.IsValid.Should().BeFalse();
        created.Errors.Select(e => e.Code).Should().Contain(WikiCodes.TitleRequired);
    }

    [Fact]
    public async Task A_file_link_that_climbs_out_of_the_repository_is_refused()
    {
        var created = await _wiki.CreateAsync(new NewWikiItem
        {
            Type = WikiItemType.Draft,
            Title = "Escaping",
            Links = [new WikiLink(WikiLinkKind.RepositoryFile, "../../etc/passwd")],
        });

        created.IsValid.Should().BeFalse();
        created.Errors[0].Code.Should().Be(WikiCodes.LinkPathNotRelative);
    }

    // ---- finding them ---------------------------------------------------------------------------

    [Fact]
    public async Task A_listing_filters_by_type_and_by_text()
    {
        await CreateAsync("The release plan", WikiItemType.Plan, "How a version ships.");
        await CreateAsync("A stray thought", WikiItemType.Draft, "Nothing about releases.");

        (await _wiki.ListAsync(new WikiItemQuery { Types = [WikiItemType.Plan] }))
            .Should().ContainSingle().Which.Title.Should().Be("The release plan");

        (await _wiki.ListAsync(new WikiItemQuery { Text = "version ships" }))
            .Should().ContainSingle().Which.Title.Should().Be("The release plan");

        (await _wiki.ListAsync(new WikiItemQuery())).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_search_for_a_wildcard_character_finds_that_text_rather_than_everything()
    {
        await CreateAsync("Coverage", WikiItemType.Report, "We reached 100% of the cases.");
        await CreateAsync("Unrelated", WikiItemType.Draft, "Nothing numeric here.");

        (await _wiki.ListAsync(new WikiItemQuery { Text = "100%" }))
            .Should().ContainSingle().Which.Title.Should().Be("Coverage");
    }
}
