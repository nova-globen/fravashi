using AwesomeAssertions;
using Fravashi.Application.Mirroring;
using Fravashi.Application.Wiki;
using Fravashi.Domain.Wiki;
using Fravashi.Infrastructure.Tests.Persistence;
using Fravashi.Infrastructure.Wiki;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Wiki;

/// <summary>
/// Backlog 83's push, over the real document store and the real publication table, against a site that can
/// be told to misbehave. What is held here is the set of things that must NOT happen: a second page, an
/// edit on the site overwritten unseen, a deleted page recreated unasked, a replaced body lost.
/// </summary>
public sealed class WikiPublisherTests : IDisposable
{
    private readonly SqliteTestContext _context = new();
    private readonly SqliteWikiLibrary _wiki;
    private readonly SqliteWikiPublicationStore _store;
    private readonly FakeSite _site = new();
    private readonly Target _target = new() { Space = "TEAM" };
    private readonly WikiPublisher _publisher;

    public WikiPublisherTests()
    {
        _wiki = new SqliteWikiLibrary(_context.Factory, TimeProvider.System, Fravashi.Domain.Projects.ProjectId.Default);
        _store = new SqliteWikiPublicationStore(_context.Factory);
        _publisher = new WikiPublisher(_wiki, _store, _site, _target, TimeProvider.System);
    }

    public void Dispose() => _context.Dispose();

    private async Task<WikiItem> DocumentAsync(string title = "Rollout plan", string body = "First paragraph.")
    {
        var created = await _wiki.CreateAsync(new NewWikiItem { Type = WikiItemType.Plan, Title = title, Body = body });
        created.IsValid.Should().BeTrue();
        return created.Value;
    }

    private async Task<WikiItem> EditAsync(WikiItem item, string body)
    {
        var updated = await _wiki.UpdateAsync(item.Id, new WikiItemEdit { Body = body }, item.Version);
        updated.IsValid.Should().BeTrue();
        return updated.Value;
    }

    [Fact]
    public async Task A_first_publish_creates_the_page_and_records_what_the_site_said()
    {
        var item = await DocumentAsync();

        var report = await _publisher.PublishAsync(item.Id);

        report.Refusal.Should().BeNull();
        _site.Pages.Should().ContainSingle();
        var page = _site.Pages.Values.Single();
        page.Title.Should().Be("Rollout plan");
        page.Body.Should().Contain("<p>First paragraph.</p>");

        var row = await _store.GetAsync(item.Id);
        row!.State.Should().Be(WikiPublicationState.Published);
        row.PageId.Should().Be(page.Id);
        row.SiteVersion.Should().Be(1);
        row.PushedItemVersion.Should().Be(item.Version);
        row.Space.Should().Be("TEAM");
    }

    // ---- backlog 49: the site gets the wiki's tree ---------------------------------------------------

    [Fact]
    public async Task A_document_under_a_published_one_is_created_under_its_page()
    {
        var folder = await DocumentAsync("plans", "The plans.");
        var child = (await _wiki.CreateAsync(new NewWikiItem { Type = WikiItemType.Plan, Title = "A plan", ParentId = folder.Id })).Value;
        await _publisher.PublishAsync(folder.Id);

        var report = await _publisher.PublishAsync(child.Id);

        report.Refusal.Should().BeNull();
        var childPage = (await _store.GetAsync(child.Id))!.PageId!;
        _site.ParentOf[childPage].Should().Be((await _store.GetAsync(folder.Id))!.PageId);
    }

    [Fact]
    public async Task A_document_whose_parent_is_not_on_the_site_is_refused_rather_than_put_at_the_top()
    {
        var folder = await DocumentAsync("plans", "The plans.");
        var child = (await _wiki.CreateAsync(new NewWikiItem { Type = WikiItemType.Plan, Title = "A plan", ParentId = folder.Id })).Value;

        var report = await _publisher.PublishAsync(child.Id);

        report.Refusal.Should().Contain("plans").And.Contain("first");
        _site.Calls.Should().BeEmpty();
        (await _store.GetAsync(child.Id)).Should().BeNull();
    }

    [Fact]
    public async Task Publishing_everything_sends_parents_first_and_counts_what_happened()
    {
        var folder = await DocumentAsync("plans", "The plans.");
        var child = (await _wiki.CreateAsync(new NewWikiItem { Type = WikiItemType.Plan, Title = "A plan", ParentId = folder.Id })).Value;
        var other = await DocumentAsync("Vision", "Where we go.");
        await _publisher.PublishAsync(other.Id);
        var steps = new List<(int, int)>();

        // Given children before parents on purpose: the order is the publisher's to decide.
        var report = await _publisher.PublishAllAsync([child, other, folder], (done, total) => steps.Add((done, total)));

        report.Should().BeEquivalentTo(new WikiPublishAllReport(2, 0, 1, []));
        _site.ParentOf[(await _store.GetAsync(child.Id))!.PageId!].Should().Be((await _store.GetAsync(folder.Id))!.PageId);
        steps.Last().Should().Be((3, 3));

        var again = await _publisher.PublishAllAsync([child, other, folder]);
        again.Should().BeEquivalentTo(new WikiPublishAllReport(0, 0, 3, []));
    }

    // ---- backlog 145: a project moving to another wiki -----------------------------------------------

    [Fact]
    public async Task A_document_published_to_one_wiki_is_published_anew_on_the_next_and_the_old_page_is_kept_as_history()
    {
        var item = await DocumentAsync();
        await _publisher.PublishAsync(item.Id);
        var confluencePage = (await _store.GetAsync(item.Id))!.PageId;
        _site.Provider = "Azure DevOps Wiki";

        (await _publisher.GetAsync(item.Id)).Should().BeNull("the Confluence page is not this wiki's state");
        (await _publisher.CheckAsync(item.Id)).Refusal.Should().Contain("published to Confluence");

        var report = await _publisher.PublishAsync(item.Id);

        report.Refusal.Should().BeNull();
        var row = await _store.GetAsync(item.Id);
        row!.Provider.Should().Be("Azure DevOps Wiki");
        row.PageId.Should().NotBe(confluencePage);
        var history = await _publisher.ElsewhereAsync(item.Id);
        history.Should().ContainSingle(h => h.Provider == "Confluence" && h.PageId == confluencePage);
        _site.Calls.Count(c => c == "update").Should().Be(0, "nothing was sent to the old page");
    }

    [Fact]
    public async Task The_in_flight_row_is_committed_before_the_call_leaves()
    {
        var item = await DocumentAsync();
        _site.Peek = () => _store.GetAsync(item.Id);

        await _publisher.PublishAsync(item.Id);

        // Read from the real table, from INSIDE the provider call. If the row were written after the call,
        // a crash between the two would leave nothing, and nothing looks exactly like "never tried".
        _site.StateSeenDuringCreate.Should().Be(WikiPublicationState.InFlight);
    }

    [Fact]
    public async Task What_the_conversion_cannot_carry_is_reported_and_not_silently_dropped()
    {
        // A table is carried since the conversion read blocks; a picture still is not.
        var item = await DocumentAsync(body: "Before.\n\n![a picture](shot.png)\n");

        var report = await _publisher.PublishAsync(item.Id);

        report.Losses.Should().NotBeEmpty();
    }

    [Fact]
    public async Task With_no_space_named_nothing_is_sent()
    {
        _target.Space = null;
        var item = await DocumentAsync();

        var report = await _publisher.PublishAsync(item.Id);

        report.Refusal.Should().Contain("space");
        _site.Calls.Should().BeEmpty();
        (await _store.GetAsync(item.Id)).Should().BeNull();
    }

    [Fact]
    public async Task A_call_that_never_left_is_recorded_as_not_sent_and_publishing_again_creates_one_page()
    {
        var item = await DocumentAsync();
        _site.Next = Misbehaviour.NeverLeaves;

        await _publisher.PublishAsync(item.Id);
        (await _store.GetAsync(item.Id))!.State.Should().Be(WikiPublicationState.NotSent);

        await _publisher.PublishAsync(item.Id);

        _site.Pages.Should().ContainSingle();
        (await _store.GetAsync(item.Id))!.State.Should().Be(WikiPublicationState.Published);
    }

    [Fact]
    public async Task A_creation_that_landed_with_no_answer_is_never_made_twice()
    {
        var item = await DocumentAsync();
        _site.Next = Misbehaviour.LandsWithNoAnswer;

        await _publisher.PublishAsync(item.Id);
        (await _store.GetAsync(item.Id))!.State.Should().Be(WikiPublicationState.InFlight);
        _site.Pages.Should().ContainSingle("the site acted even though nobody heard it say so");

        await _publisher.PublishAsync(item.Id);

        _site.Pages.Should().ContainSingle("the second publish has to ask the site, not send the creation again");
        _site.Calls.Count(c => c == "create").Should().Be(1);
        var row = await _store.GetAsync(item.Id);
        row!.State.Should().Be(WikiPublicationState.Published);
        row.PageId.Should().Be(_site.Pages.Keys.Single());
    }

    [Fact]
    public async Task A_creation_that_left_and_did_not_land_is_sent_again_only_on_the_sites_word()
    {
        var item = await DocumentAsync();
        _site.Next = Misbehaviour.LostWithNoAnswer;

        await _publisher.PublishAsync(item.Id);
        _site.Pages.Should().BeEmpty();

        await _publisher.PublishAsync(item.Id);

        _site.Calls.Should().ContainInOrder("create", "find", "create");
        _site.Pages.Should().ContainSingle();
    }

    [Fact]
    public async Task While_the_site_cannot_be_asked_a_write_in_flight_is_not_sent_again()
    {
        var item = await DocumentAsync();
        _site.Next = Misbehaviour.LandsWithNoAnswer;
        await _publisher.PublishAsync(item.Id);

        _site.Next = Misbehaviour.NeverLeaves;
        var report = await _publisher.PublishAsync(item.Id);

        report.Refusal.Should().NotBeNull();
        _site.Calls.Count(c => c == "create").Should().Be(1);
        (await _store.GetAsync(item.Id))!.State.Should().Be(WikiPublicationState.InFlight);
    }

    [Fact]
    public async Task A_page_with_the_title_that_is_not_provably_ours_goes_to_a_person()
    {
        var item = await DocumentAsync();
        _site.Next = Misbehaviour.LostWithNoAnswer;
        await _publisher.PublishAsync(item.Id);
        var foreign = _site.Plant("TEAM", "Rollout plan", "<p>Somebody else wrote this.</p>");

        var report = await _publisher.PublishAsync(item.Id);

        report.Refusal.Should().NotBeNull();
        var row = await _store.GetAsync(item.Id);
        row!.State.Should().Be(WikiPublicationState.Unresolved);
        _site.Pages[foreign].Body.Should().Be("<p>Somebody else wrote this.</p>");
        _site.Calls.Should().NotContain("update");
    }

    [Fact]
    public async Task An_edit_here_is_sent_as_the_next_version_of_the_same_page()
    {
        var item = await DocumentAsync();
        await _publisher.PublishAsync(item.Id);
        var edited = await EditAsync(item, "Second thoughts.");

        await _publisher.PublishAsync(item.Id);

        var page = _site.Pages.Values.Single();
        page.Version.Should().Be(2);
        page.Body.Should().Contain("Second thoughts.");
        (await _store.GetAsync(item.Id))!.PushedItemVersion.Should().Be(edited.Version);
    }

    [Fact]
    public async Task Publishing_an_unchanged_document_sends_nothing()
    {
        var item = await DocumentAsync();
        await _publisher.PublishAsync(item.Id);
        _site.Calls.Clear();

        await _publisher.PublishAsync(item.Id);

        _site.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_edit_on_the_site_is_not_overwritten_by_an_edit_here()
    {
        var item = await DocumentAsync();
        await _publisher.PublishAsync(item.Id);
        var pageId = _site.Pages.Keys.Single();
        _site.EditOnSite(pageId, "<p>Edited in the browser.</p>");
        await EditAsync(item, "Edited in Fravashi.");

        await _publisher.PublishAsync(item.Id);

        _site.Pages[pageId].Body.Should().Be("<p>Edited in the browser.</p>");
        (await _store.GetAsync(item.Id))!.State.Should().Be(WikiPublicationState.Conflict);

        // And it stays a person's question: publishing again is not the way through.
        var again = await _publisher.PublishAsync(item.Id);
        again.Refusal.Should().NotBeNull();
        _site.Pages[pageId].Body.Should().Be("<p>Edited in the browser.</p>");
    }

    [Fact]
    public async Task On_a_site_whose_versions_are_not_numbers_a_check_says_the_page_changed_and_quotes_none()
    {
        // Backlog 149: Azure DevOps versions are hashed ETags; "the site has version 11028708" means nothing.
        _site.VersionsAreReadable = false;
        var item = await DocumentAsync(body: "Mine.");
        await _publisher.PublishAsync(item.Id);
        _site.EditOnSite(_site.Pages.Keys.Single(), "<p>Theirs.</p>");

        var check = await _publisher.CheckAsync(item.Id);

        check.Publication!.State.Should().Be(WikiPublicationState.SiteChanged);

        // What the panel shows is the state's sentence followed by the detail. Seen in the installed v0.9.43 on
        // 2026-10-01: the detail repeated the state's sentence word for word, so the page said it twice.
        var shown = WikiPublishDiscipline.Sentence(check.Publication, item) + " " + check.Publication.Detail;
        shown.Split("changed on the site").Length.Should().Be(2, "the page saying it changed is said once: {0}", shown);
        shown.Should().NotContain("version");

        // And a record an earlier build STORED with that repeat (the owner's install has one) is shown once too.
        var stored = check.Publication with { Detail = "The page was changed on the site after the last publish." };
        WikiPublishDiscipline.DetailToShow(stored, item).Should().BeNull();
        WikiPublishDiscipline.DetailToShow(stored with { Detail = "The site has version 5; the last publish made version 4." }, item)
            .Should().NotBeNull("a detail that says something new is still shown");
    }

    [Fact]
    public async Task A_check_notices_an_edit_on_the_site_and_taking_it_keeps_what_it_replaced()
    {
        var item = await DocumentAsync(body: "Mine.");
        await _publisher.PublishAsync(item.Id);
        var pageId = _site.Pages.Keys.Single();
        _site.EditOnSite(pageId, "<p>Theirs.</p>");

        var check = await _publisher.CheckAsync(item.Id);
        check.Publication!.State.Should().Be(WikiPublicationState.SiteChanged);
        check.Publication.Detail.Should().Contain("version 2").And.Contain("version 1", "Confluence shows its numbers");
        (await _wiki.GetAsync(item.Id))!.Body.Should().Be("Mine.", "a check never changes the document");

        var taken = await _publisher.TakeSiteVersionAsync(item.Id);

        taken.Refusal.Should().BeNull();
        (await _wiki.GetAsync(item.Id))!.Body.Should().Contain("Theirs.");
        var row = await _store.GetAsync(item.Id);
        row!.State.Should().Be(WikiPublicationState.Published);
        row.ReplacedBody.Should().Be("Mine.");
        row.SiteVersion.Should().Be(2);

        await _publisher.RestoreReplacedAsync(item.Id);

        (await _wiki.GetAsync(item.Id))!.Body.Should().Be("Mine.");
        (await _store.GetAsync(item.Id))!.ReplacedBody.Should().BeNull();
    }

    [Fact]
    public async Task Overwriting_the_site_is_a_separate_explicit_act_and_claims_the_sites_current_version()
    {
        var item = await DocumentAsync(body: "Mine.");
        await _publisher.PublishAsync(item.Id);
        var pageId = _site.Pages.Keys.Single();
        _site.EditOnSite(pageId, "<p>Theirs.</p>");
        await _publisher.CheckAsync(item.Id);

        var report = await _publisher.OverwriteSiteAsync(item.Id);

        report.Refusal.Should().BeNull();
        _site.Pages[pageId].Body.Should().Contain("Mine.");
        _site.Pages[pageId].Version.Should().Be(3);
        (await _store.GetAsync(item.Id))!.State.Should().Be(WikiPublicationState.Published);
    }

    [Fact]
    public async Task A_page_deleted_on_the_site_is_reported_and_never_recreated_unasked()
    {
        var item = await DocumentAsync();
        await _publisher.PublishAsync(item.Id);
        _site.Pages.Clear();

        (await _publisher.CheckAsync(item.Id)).Publication!.State.Should().Be(WikiPublicationState.Gone);
        (await _wiki.GetAsync(item.Id)).Should().NotBeNull("the document here is kept");

        var publish = await _publisher.PublishAsync(item.Id);
        publish.Refusal.Should().NotBeNull();
        _site.Pages.Should().BeEmpty();

        await _publisher.PublishAsNewAsync(item.Id);
        _site.Pages.Should().ContainSingle();
        (await _store.GetAsync(item.Id))!.State.Should().Be(WikiPublicationState.Published);
    }

    [Fact]
    public async Task A_refusal_is_recorded_in_the_sites_words_and_a_corrected_document_can_be_sent()
    {
        var item = await DocumentAsync();
        _site.Plant("TEAM", "Rollout plan", "<p>Already here.</p>");

        await _publisher.PublishAsync(item.Id);

        var row = await _store.GetAsync(item.Id);
        row!.State.Should().Be(WikiPublicationState.Refused);
        row.Detail.Should().Contain("already exists");
        _site.Pages.Should().ContainSingle();
    }

    private sealed class Target : IWikiPublishTarget
    {
        public string? Space { get; set; }
    }

    private enum Misbehaviour
    {
        None,
        NeverLeaves,
        LandsWithNoAnswer,
        LostWithNoAnswer,
    }

    /// <summary>A site with the two properties the rules lean on: titles are unique per space, and a new
    /// version must be exactly one more than the current one.</summary>
    private sealed class FakeSite : IWikiPageGateway
    {
        private int _nextId = 100;

        public Dictionary<string, SitePage> Pages { get; } = [];

        public Dictionary<string, string> SpaceOf { get; } = [];

        /// <summary>The parent page each created page was put under, when one was named.</summary>
        public Dictionary<string, string> ParentOf { get; } = [];

        public List<string> Calls { get; } = [];

        public Misbehaviour Next { get; set; }

        public Func<Task<WikiPublication?>>? Peek { get; set; }

        public WikiPublicationState? StateSeenDuringCreate { get; private set; }

        public string Provider { get; set; } = "Confluence";

        public bool IsConfigured => true;

        /// <summary>Backlog 149: false models Azure DevOps, whose version numbers are hashed ETags.</summary>
        public bool VersionsAreReadable { get; set; } = true;

        public string Plant(string space, string title, string body)
        {
            var id = (_nextId++).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Pages[id] = new SitePage(id, title, 1, body, null);
            SpaceOf[id] = space;
            return id;
        }

        public void EditOnSite(string pageId, string body) =>
            Pages[pageId] = Pages[pageId] with { Body = body, Version = Pages[pageId].Version + 1 };

        public async Task<ProviderResult<SitePage>> CreateAsync(string space, string title, string body, string? parentPageId = null, CancellationToken ct = default)
        {
            Calls.Add("create");
            var row = Peek is null ? null : await Peek();
            StateSeenDuringCreate ??= row?.State;

            var mode = Take();
            if (mode == Misbehaviour.NeverLeaves)
            {
                return ProviderResult<SitePage>.Unreachable("no route");
            }

            if (mode == Misbehaviour.LostWithNoAnswer)
            {
                return ProviderResult<SitePage>.Indeterminate("timed out");
            }

            if (Pages.Any(p => SpaceOf[p.Key] == space && p.Value.Title == title))
            {
                return ProviderResult<SitePage>.Refused("A page with this title already exists in the space.");
            }

            var id = Plant(space, title, body);
            if (parentPageId is not null)
            {
                ParentOf[id] = parentPageId;
            }
            return mode == Misbehaviour.LandsWithNoAnswer
                ? ProviderResult<SitePage>.Indeterminate("timed out")
                : ProviderResult<SitePage>.Ok(Pages[id]);
        }

        public Task<ProviderResult<SitePage>> UpdateAsync(string pageId, string title, string body, int newVersion, CancellationToken ct = default)
        {
            Calls.Add("update");
            if (Take() == Misbehaviour.NeverLeaves)
            {
                return Task.FromResult(ProviderResult<SitePage>.Unreachable("no route"));
            }

            if (!Pages.TryGetValue(pageId, out var page))
            {
                return Task.FromResult(ProviderResult<SitePage>.Gone("no such page"));
            }

            if (newVersion != page.Version + 1)
            {
                return Task.FromResult(ProviderResult<SitePage>.Conflict("stale version"));
            }

            Pages[pageId] = page with { Title = title, Body = body, Version = newVersion };
            return Task.FromResult(ProviderResult<SitePage>.Ok(Pages[pageId]));
        }

        public Task<ProviderResult<SitePage>> GetAsync(string pageId, CancellationToken ct = default)
        {
            Calls.Add("get");
            if (Take() == Misbehaviour.NeverLeaves)
            {
                return Task.FromResult(ProviderResult<SitePage>.Unreachable("no route"));
            }

            return Task.FromResult(Pages.TryGetValue(pageId, out var page)
                ? ProviderResult<SitePage>.Ok(page)
                : ProviderResult<SitePage>.Gone("no such page"));
        }

        public Task<ProviderResult<SitePage?>> FindByTitleAsync(string space, string title, CancellationToken ct = default)
        {
            Calls.Add("find");
            if (Take() == Misbehaviour.NeverLeaves)
            {
                return Task.FromResult(ProviderResult<SitePage?>.Unreachable("no route"));
            }

            var match = Pages.FirstOrDefault(p => SpaceOf[p.Key] == space && p.Value.Title == title).Value;
            return Task.FromResult(ProviderResult<SitePage?>.Ok(match));
        }

        private Misbehaviour Take()
        {
            var mode = Next;
            Next = Misbehaviour.None;
            return mode;
        }
    }
}
