using AwesomeAssertions;
using Fravashi.Application.Prototypes;
using Fravashi.Application.Wiki;
using Fravashi.Domain.Projects;
using Fravashi.Domain.Wiki;
using Xunit;

namespace Fravashi.Application.Tests.Prototypes;

/// <summary>
/// Backlog 216's acceptance below the page (plan @41): regenerating from a newer design reapplies the overlay, moves
/// the pin against the version read, and lists what no longer places while keeping it stored; and settling one of
/// those (re-anchor, drop) is a session edit, undone and redone like any other.
/// </summary>
public class PrototypeRegenerateTests
{
    private const int DesignNumber = 7;

    private const string Version1 =
        """{"mockup": 1, "title": "Sign in", "screens": [{"type": "window", "id": "home", "text": "Home", "children": [{"type": "button", "id": "next", "text": "Next"}, {"type": "text", "id": "note", "text": "Plain"}]}]}""";

    /// <summary>Version 2 with the anchored button removed.</summary>
    private const string WithoutNext =
        """{"mockup": 1, "title": "Sign in", "screens": [{"type": "window", "id": "home", "text": "Home", "children": [{"type": "text", "id": "note", "text": "Plain"}]}]}""";

    /// <summary>Version 2 with the anchored button kept and the note reworded.</summary>
    private const string KeepsNext =
        """{"mockup": 1, "title": "Sign in", "screens": [{"type": "window", "id": "home", "text": "Home", "children": [{"type": "button", "id": "next", "text": "Next"}, {"type": "text", "id": "note", "text": "Reworded"}]}]}""";

    private static readonly PrototypeAnchor Next = new("next", null);
    private static readonly PrototypeAnchor Note = new("note", null);
    private static readonly PrototypeAnchor Home = new("home", null);

    private static readonly PrototypeSetTextOperation GoOn = new(Next, "Go on");
    private static readonly PrototypeAddClassOperation Loud = new(Note, "loud");

    private static WikiItem DesignAt(long version, string tree) => WikiItem.Create(new WikiItemState
    {
        Id = "design-7",
        Number = DesignNumber,
        Type = WikiItemType.Design,
        Title = "Sign in",
        Slug = "sign-in",
        Body = "The screen.\n\n```mockup\n" + tree + "\n```\n",
        Version = version,
    }).Value;

    private static PrototypeDocument Document(params PrototypeOperation[] overlay) =>
        new(PrototypeVocabulary.Version, new PrototypeDesignRef(DesignNumber, 1), overlay);

    private static string Body(PrototypeDocument document) =>
        "Prose before.\n\n```prototype\n" + PrototypeWriter.Write(document) + "```\n\nProse after.\n";

    private static async Task<PrototypeDocument> StoredAsync(OnePrototype wiki, long designVersion)
    {
        var read = await PrototypeBody.ReadAsync(wiki.Item.Body, new FakeWikiLibrary().With(DesignNumber, WikiItemType.Design, designVersion));
        read.IsValid.Should().BeTrue(string.Join("\n", read.Errors));
        return read.Document!;
    }

    // ---- regenerating ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_design_change_that_removes_an_anchored_node_lists_its_operation_as_unplaced_and_keeps_it_stored()
    {
        var document = Document(GoOn, Loud);
        var wiki = new OnePrototype(Body(document));
        var design = DesignAt(2, WithoutNext);

        var result = await PrototypeRegenerate.RunAsync(wiki, wiki.Item, document, design);

        result.Outcome.Should().Be(PrototypeRegenerateOutcome.Regenerated);
        result.Unplaced.Should().Equal(GoOn);
        result.Item!.Version.Should().Be(2);
        wiki.Writes.Should().Be(1);

        var stored = await StoredAsync(wiki, 2);
        stored.Design.Should().Be(new PrototypeDesignRef(DesignNumber, 2));
        PrototypeWriter.Write(stored).Should().Be(PrototypeWriter.Write(document with { Design = stored.Design }),
            "the overlay is written back whole, the operation that no longer places included");
        wiki.Item.Body.Should().StartWith("Prose before.\n\n```prototype\n").And.EndWith("```\n\nProse after.\n");

        // Rendered from the new pin, it is still listed, never dropped, and the other operation still applies.
        var page = PrototypeRenderer.Render(DesignMockup(WithoutNext), stored.Overlay);
        page.Unplaced.Should().ContainSingle().Which.Should().BeOfType<PrototypeSetTextOperation>().Which.Text.Should().Be("Go on");
        page.Html.Should().Contain("data-mk-anchor=\"note\" class=\"mk-text loud\"");
    }

    [Fact]
    public async Task A_design_change_that_keeps_the_node_keeps_the_operation_applied()
    {
        var document = Document(GoOn, Loud);
        var wiki = new OnePrototype(Body(document));

        var result = await PrototypeRegenerate.RunAsync(wiki, wiki.Item, document, DesignAt(2, KeepsNext));

        result.Outcome.Should().Be(PrototypeRegenerateOutcome.Regenerated);
        result.Unplaced.Should().BeEmpty();
        var stored = await StoredAsync(wiki, 2);
        stored.Design.Version.Should().Be(2);
        var page = PrototypeRenderer.Render(DesignMockup(KeepsNext), stored.Overlay);
        page.Unplaced.Should().BeEmpty();
        page.Html.Should().Contain(">Go on</button>").And.Contain(">Reworded<");
    }

    [Fact]
    public async Task A_newer_tree_that_does_not_read_writes_nothing_and_says_so()
    {
        var document = Document(GoOn);
        var wiki = new OnePrototype(Body(document));

        var result = await PrototypeRegenerate.RunAsync(wiki, wiki.Item, document, DesignAt(2, """{"mockup": 1, "screens": []}"""));

        result.Outcome.Should().Be(PrototypeRegenerateOutcome.Refused);
        result.Problem.Should().Contain("version 2 is not valid").And.Contain("Nothing was written");
        wiki.Writes.Should().Be(0);
    }

    [Fact]
    public async Task A_design_not_newer_than_the_pin_writes_nothing_and_no_design_is_refused()
    {
        var document = Document(GoOn);
        var wiki = new OnePrototype(Body(document));

        (await PrototypeRegenerate.RunAsync(wiki, wiki.Item, document, DesignAt(1, Version1))).Outcome
            .Should().Be(PrototypeRegenerateOutcome.Current);
        var none = await PrototypeRegenerate.RunAsync(wiki, wiki.Item, document, null);
        none.Outcome.Should().Be(PrototypeRegenerateOutcome.Refused);
        none.Problem.Should().NotBeNullOrWhiteSpace();
        wiki.Writes.Should().Be(0);
    }

    [Fact]
    public async Task A_prototype_saved_since_it_was_read_is_refused_and_nothing_is_written()
    {
        var document = Document(GoOn);
        var wiki = new OnePrototype(Body(document));
        var read = wiki.Item;
        (await wiki.UpdateAsync(read.Id, new WikiItemEdit { Body = read.Body + "Somebody else's line.\n" }, read.Version)).IsValid.Should().BeTrue();
        var theirs = wiki.Item.Body;

        var result = await PrototypeRegenerate.RunAsync(wiki, read, document, DesignAt(2, KeepsNext));

        result.Outcome.Should().Be(PrototypeRegenerateOutcome.Stale);
        wiki.Item.Body.Should().Be(theirs);
        wiki.Writes.Should().Be(1, "only the other person's save was written");
    }

    [Fact]
    public async Task An_edit_session_opened_before_a_regenerate_is_refused_on_save_as_stale()
    {
        var document = Document(GoOn);
        var wiki = new OnePrototype(Body(document));
        var opened = wiki.Item;
        var session = PrototypeEditSession.Open(document);
        session.Drop(0).Should().BeTrue();

        (await PrototypeRegenerate.RunAsync(wiki, opened, document, DesignAt(2, WithoutNext))).Outcome
            .Should().Be(PrototypeRegenerateOutcome.Regenerated);
        var regenerated = wiki.Item.Body;

        var saved = await PrototypeSave.SaveAsync(wiki, opened, session);

        saved.Outcome.Should().Be(PrototypeSaveOutcome.Stale);
        wiki.Item.Body.Should().Be(regenerated);
        session.IsDirty.Should().BeTrue();
    }

    // ---- settling in the edit session ------------------------------------------------------------------------

    [Fact]
    public void Re_anchoring_moves_the_operation_in_place_and_undo_and_redo_reverse_it_exactly()
    {
        var rule = new PrototypePageCssOperation("p { color: red; }");
        var session = PrototypeEditSession.Open(Document(GoOn, Loud, rule));

        session.Reanchor(0, Home).Should().BeTrue();

        session.Overlay.Should().Equal(new PrototypeSetTextOperation(Home, "Go on"), Loud, rule);
        session.IsDirty.Should().BeTrue();
        session.CanUndo.Should().BeTrue();

        session.Undo().Should().BeTrue();
        session.Overlay.Should().Equal(GoOn, Loud, rule);
        session.IsDirty.Should().BeFalse("undoing the move is the opened document again");
        session.Undo().Should().BeFalse("what the document held when it was opened is not Undo's to take back");

        session.Redo().Should().BeTrue();
        session.Overlay[0].Should().Be(new PrototypeSetTextOperation(Home, "Go on"));

        session.Reanchor(2, Home).Should().BeFalse("a page rule has no anchor to move");
        session.Reanchor(0, Home).Should().BeFalse("it is already there");
        session.Reanchor(3, Home).Should().BeFalse();
    }

    [Fact]
    public void Every_operation_with_an_anchor_can_be_re_anchored_and_keeps_the_rest_of_itself()
    {
        PrototypeOperation[] all =
        [
            GoOn,
            new PrototypeSetCssOperation(Next, new Dictionary<string, string> { ["color"] = "red" }),
            new PrototypeSetAttributeOperation(Next, "title", "Hi"),
            Loud,
            new PrototypeInsertHtmlOperation(Next, PrototypePlacement.After, "<p>x</p>"),
        ];
        var session = PrototypeEditSession.Open(Document(all));

        for (var i = 0; i < all.Length; i++)
        {
            session.Reanchor(i, Home).Should().BeTrue(all[i].Op);
        }

        PrototypeWriter.Write(session.Document).Should().NotContain("\"next\"").And.NotContain("\"note\"");
        session.Overlay.Select(o => o.Op).Should().Equal(all.Select(o => o.Op));
        ((PrototypeInsertHtmlOperation)session.Overlay[4]).Html.Should().Be("<p>x</p>");
    }

    [Fact]
    public void Dropping_removes_the_operation_and_undo_puts_it_back_at_its_place()
    {
        var session = PrototypeEditSession.Open(Document(GoOn, Loud));
        session.Apply(new PrototypeSetTextOperation(Note, "Edited"));

        session.Drop(0).Should().BeTrue();
        session.Overlay.Should().Equal(Loud, new PrototypeSetTextOperation(Note, "Edited"));

        session.Undo().Should().BeTrue();
        session.Overlay.Should().Equal(GoOn, Loud, new PrototypeSetTextOperation(Note, "Edited"));
        session.Redo().Should().BeTrue();
        session.Overlay.Should().Equal(Loud, new PrototypeSetTextOperation(Note, "Edited"));

        session.Undo();
        session.Undo().Should().BeTrue("the appended edit comes off next");
        session.Overlay.Should().Equal(GoOn, Loud);
        session.IsDirty.Should().BeFalse();
        session.Drop(5).Should().BeFalse();
    }

    [Fact]
    public void A_new_change_clears_redo_whatever_kind_it_is()
    {
        var session = PrototypeEditSession.Open(Document(GoOn, Loud));
        session.Drop(1);
        session.Undo();
        session.CanRedo.Should().BeTrue();

        session.Reanchor(0, Home);

        session.CanRedo.Should().BeFalse();
    }

    private static Fravashi.Application.Mockups.MockupDocument DesignMockup(string tree)
    {
        var read = Fravashi.Application.Mockups.MockupReader.Read(tree);
        read.IsValid.Should().BeTrue(string.Join("\n", read.Errors));
        return read.Document!;
    }

    /// <summary>One prototype in a store that refuses a stale version, as the real one does.</summary>
    private sealed class OnePrototype : IWikiDocuments
    {
        public OnePrototype(string body) => Item = WikiItem.Create(new WikiItemState
        {
            Id = "prototype-1",
            Number = 8,
            Type = WikiItemType.Prototype,
            Title = "Sign in prototype",
            Slug = "sign-in-prototype",
            Body = body,
        }).Value;

        public WikiItem Item { get; private set; }

        public int Writes { get; private set; }

        public Task<WikiItem?> GetAsync(string id, CancellationToken ct = default) => Task.FromResult<WikiItem?>(Item);

        public Task<WikiItem?> GetVersionAsync(string id, long version, CancellationToken ct = default) =>
            Task.FromResult(version == Item.Version ? Item : null);

        public Task<WikiResult<WikiItem>> UpdateAsync(string id, WikiItemEdit edit, long expectedVersion, CancellationToken ct = default)
        {
            if (expectedVersion != Item.Version)
            {
                return Task.FromResult(WikiResult<WikiItem>.Refused(WikiCodes.Conflict, "version", "The version is stale."));
            }

            Writes++;
            Item = WikiItem.Create(Item.ToState() with { Body = edit.Body ?? Item.Body, Version = Item.Version + 1 }).Value;
            return Task.FromResult(WikiResult<WikiItem>.Ok(Item));
        }

        public Task<bool> DeleteAsync(string id, CancellationToken ct = default) => Task.FromResult(false);

        public Task<ProjectId?> ProjectOfAsync(string id, CancellationToken ct = default) => Task.FromResult<ProjectId?>(null);
    }
}
