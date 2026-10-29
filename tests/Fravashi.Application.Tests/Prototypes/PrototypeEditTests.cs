using System.Text.RegularExpressions;
using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Fravashi.Application.Prototypes;
using Fravashi.Application.Wiki;
using Fravashi.Domain.Projects;
using Fravashi.Domain.Wiki;
using Xunit;

namespace Fravashi.Application.Tests.Prototypes;

/// <summary>
/// Backlog 214's acceptance below the page (plan @39): each direct edit is ONE overlay operation, undo and redo work
/// per operation, a save writes against the version opened and writes nothing when there is nothing to write, a
/// saved edit is still applied when the page is rendered again from the same design, and an edit render carries the
/// same one script and the same policy as a view render.
/// </summary>
public class PrototypeEditTests
{
    private const int DesignNumber = 7;

    private const string Tree =
        """{"mockup": 1, "title": "Sign in", "screens": [{"type": "window", "id": "home", "text": "Home", "children": [{"type": "button", "id": "next", "text": "Next"}, {"type": "text", "text": "Plain"}]}]}""";

    private static readonly PrototypeAnchor Next = new("next", null);
    private static readonly PrototypeAnchor Plain = new(null, "/screens/0/children/1");

    private static MockupDocument Design()
    {
        var read = MockupReader.Read(Tree);
        read.IsValid.Should().BeTrue(string.Join("\n", read.Errors));
        return read.Document!;
    }

    private static PrototypeDocument Document(params PrototypeOperation[] overlay) =>
        new(PrototypeVocabulary.Version, new PrototypeDesignRef(DesignNumber, 1), overlay);

    private static string Body(PrototypeDocument document) =>
        "Prose before.\n\n```prototype\n" + PrototypeWriter.Write(document) + "```\n\nProse after.\n";

    private static PrototypeOperation Op(PrototypeEditResult edit)
    {
        edit.Refusal.Should().BeNull();
        return edit.Operation!;
    }

    // ---- the edit session ------------------------------------------------------------------------------------

    [Fact]
    public void Each_edit_appends_exactly_one_operation_and_undo_and_redo_move_one_at_a_time()
    {
        var existing = new PrototypeAddClassOperation(Next, "kept");
        var session = PrototypeEditSession.Open(Document(existing));
        session.IsDirty.Should().BeFalse();
        session.CanUndo.Should().BeFalse();

        var text = Op(PrototypeEdits.SetText(Next, "Go on"));
        var colour = Op(PrototypeEdits.SetStyle(Next, "color", "red"));
        session.Apply(text);
        session.Apply(colour);

        session.Overlay.Should().Equal(existing, text, colour);
        session.IsDirty.Should().BeTrue();

        session.Undo().Should().BeTrue();
        session.Overlay.Should().Equal(existing, text);
        session.CanRedo.Should().BeTrue();

        session.Redo().Should().BeTrue();
        session.Overlay.Should().Equal(existing, text, colour);

        session.Undo().Should().BeTrue();
        session.Undo().Should().BeTrue();
        session.Undo().Should().BeFalse("what the document held when it was opened is not this session's to take back");
        session.Overlay.Should().Equal(existing);
        session.IsDirty.Should().BeFalse("undoing every edit is the opened document again");
    }

    [Fact]
    public void A_new_edit_clears_redo()
    {
        var session = PrototypeEditSession.Open(Document());
        session.Apply(Op(PrototypeEdits.SetText(Next, "One")));
        session.Undo();
        session.CanRedo.Should().BeTrue();

        session.Apply(Op(PrototypeEdits.SetText(Next, "Two")));

        session.CanRedo.Should().BeFalse();
        session.Redo().Should().BeFalse();
    }

    // ---- the edits the panel offers --------------------------------------------------------------------------

    [Fact]
    public void A_style_is_one_property_of_the_items_list_and_one_plain_value()
    {
        var css = (PrototypeSetCssOperation)Op(PrototypeEdits.SetStyle(Plain, "font-size", " 18px "));
        css.Anchor.Should().Be(Plain);
        css.Properties.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, string>("font-size", "18px"));

        foreach (var property in PrototypeEdits.Styles)
        {
            PrototypeEdits.SetStyle(Next, property, "1px").Operation.Should().NotBeNull(property);
        }
    }

    [Theory]
    [InlineData("display", "none")]
    [InlineData("color", "")]
    [InlineData("color", "red; background: blue")]
    [InlineData("color", "red } body { color: blue")]
    [InlineData("color", "red</style><script>")]
    [InlineData("background-color", "url(https://example.com/x.png)")]
    [InlineData("color", "re\\64")]
    [InlineData("color", "red\nblue")]
    public void A_style_that_is_not_one_plain_value_of_a_listed_property_is_refused_in_words(string property, string value)
    {
        var edit = PrototypeEdits.SetStyle(Next, property, value);

        edit.Operation.Should().BeNull();
        edit.Refusal.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_style_value_past_its_length_is_refused()
    {
        PrototypeEdits.SetStyle(Next, "color", new string('a', PrototypeEdits.MaxStyleValue)).Operation.Should().NotBeNull();
        PrototypeEdits.SetStyle(Next, "color", new string('a', PrototypeEdits.MaxStyleValue + 1)).Operation.Should().BeNull();
    }

    [Fact]
    public void A_page_rule_is_one_pageCss_and_one_that_closes_the_style_element_is_refused()
    {
        var rule = (PrototypePageCssOperation)Op(PrototypeEdits.PageRule("  .mk-button { border-radius: 1rem; }  "));
        rule.Rule.Should().Be(".mk-button { border-radius: 1rem; }");

        PrototypeEdits.PageRule("p { color: red; } </style><script>alert(1)</script>").Operation.Should().BeNull();
        PrototypeEdits.PageRule("   ").Operation.Should().BeNull();
        PrototypeEdits.PageRule(new string('a', PrototypeEdits.MaxPageRule + 1)).Operation.Should().BeNull();
    }

    [Fact]
    public void Text_is_any_text_because_the_renderer_encodes_it()
    {
        var edit = (PrototypeSetTextOperation)Op(PrototypeEdits.SetText(Next, "<b>bold</b> & more"));

        var html = PrototypeRenderer.Render(Design(), [edit]).Html;

        html.Should().Contain("&lt;b&gt;bold&lt;/b&gt; &amp; more").And.NotContain("<b>bold</b>");
    }

    // ---- saving ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_save_with_no_edits_writes_nothing()
    {
        var wiki = new OnePrototype(Body(Document()));
        var session = PrototypeEditSession.Open(Document());

        var result = await PrototypeSave.SaveAsync(wiki, wiki.Item, session);

        result.Outcome.Should().Be(PrototypeSaveOutcome.Unchanged);
        wiki.Writes.Should().Be(0);

        session.Apply(Op(PrototypeEdits.SetText(Next, "Go on")));
        session.Undo();
        (await PrototypeSave.SaveAsync(wiki, wiki.Item, session)).Outcome.Should().Be(PrototypeSaveOutcome.Unchanged);
        wiki.Writes.Should().Be(0);
    }

    [Fact]
    public async Task A_save_writes_the_overlay_against_the_version_opened_and_keeps_the_prose_byte_for_byte()
    {
        var wiki = new OnePrototype(Body(Document()));
        var opened = wiki.Item;
        var session = PrototypeEditSession.Open(Document());
        session.Apply(Op(PrototypeEdits.SetText(Next, "Go on")));
        session.Apply(Op(PrototypeEdits.SetStyle(Plain, "color", "#c62828")));

        var result = await PrototypeSave.SaveAsync(wiki, opened, session);

        result.Outcome.Should().Be(PrototypeSaveOutcome.Saved);
        result.Item!.Version.Should().Be(opened.Version + 1);
        wiki.Writes.Should().Be(1);
        wiki.Item.Body.Should().Be(Body(session.Document));
        wiki.Item.Body.Should().StartWith("Prose before.\n\n```prototype\n").And.EndWith("```\n\nProse after.\n");
        session.IsDirty.Should().BeFalse("the clean point moves to what was written");
    }

    [Fact]
    public async Task A_save_is_refused_on_a_stale_version_and_writes_nothing()
    {
        var wiki = new OnePrototype(Body(Document()));
        var opened = wiki.Item;
        var session = PrototypeEditSession.Open(Document());
        session.Apply(Op(PrototypeEdits.SetText(Next, "Go on")));
        (await wiki.UpdateAsync(opened.Id, new WikiItemEdit { Body = opened.Body + "Somebody else's line.\n" }, opened.Version))
            .IsValid.Should().BeTrue();
        var theirs = wiki.Item.Body;

        var result = await PrototypeSave.SaveAsync(wiki, opened, session);

        result.Outcome.Should().Be(PrototypeSaveOutcome.Stale);
        result.Item.Should().BeNull();
        wiki.Item.Body.Should().Be(theirs);
        wiki.Writes.Should().Be(1, "only the other person's save was written");
        session.IsDirty.Should().BeTrue("the edits are still there to keep or to drop");
    }

    [Fact]
    public async Task A_saved_edit_is_still_applied_when_the_page_is_rendered_again_from_the_same_design()
    {
        var wiki = new OnePrototype(Body(Document()));
        var session = PrototypeEditSession.Open(Document());
        session.Apply(Op(PrototypeEdits.SetText(Next, "Go on")));
        session.Apply(Op(PrototypeEdits.SetStyle(Plain, "font-weight", "bold")));
        session.Apply(Op(PrototypeEdits.PageRule(".mk-button { border-radius: 1rem; }")));
        (await PrototypeSave.SaveAsync(wiki, wiki.Item, session)).Outcome.Should().Be(PrototypeSaveOutcome.Saved);

        // Read back exactly as the page does, then rendered from the design, twice.
        var read = await PrototypeBody.ReadAsync(wiki.Item.Body, new FakeWikiLibrary().With(DesignNumber, WikiItemType.Design));
        read.IsValid.Should().BeTrue(string.Join("\n", read.Errors));
        var first = PrototypeRenderer.Render(Design(), read.Document!.Overlay);
        var second = PrototypeRenderer.Render(Design(), read.Document!.Overlay);

        first.Unplaced.Should().BeEmpty();
        first.Html.Should().Be(second.Html);
        first.Html.Should().Contain(">Go on</button>");
        first.Html.Should().Contain("data-mk-anchor=\"/screens/0/children/1\" class=\"mk-text\" style=\"font-weight: bold\"");
        first.Html.Should().Contain(".mk-button { border-radius: 1rem; }");
    }

    // ---- the edit render -------------------------------------------------------------------------------------

    private static string ScriptOf(string html) =>
        Regex.Matches(html, "<script>(.*?)</script>", RegexOptions.Singleline).Should().ContainSingle().Which.Groups[1].Value;

    private static string PolicyOf(string html) =>
        Regex.Match(html, "<meta http-equiv=\"Content-Security-Policy\" content=\"([^\"]*)\">").Groups[1].Value;

    [Fact]
    public void An_edit_render_carries_the_same_one_script_and_the_same_policy_as_a_view_render()
    {
        var view = PrototypeRenderer.Render(Design(), []).Html;
        var edit = PrototypeRenderer.Render(Design(), [], new PrototypeRenderOptions(Edit: true, Selected: "next")).Html;

        ScriptOf(edit).Should().Be(ScriptOf(view)).And.Be(PrototypeRenderer.Script);
        PolicyOf(edit).Should().Be(PolicyOf(view));
        PolicyOf(edit).Should().Contain($"script-src '{PrototypeRenderer.ScriptHash}'");
        edit.Should().StartWith("<!DOCTYPE html>\n<html lang=\"en\" data-mk-edit=\"edit\">\n<head>\n<meta http-equiv=\"Content-Security-Policy\"");
        view.Should().StartWith("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n");
        view.Should().NotContain("data-mk-edit=\"edit\"");
    }

    [Fact]
    public void The_script_selects_by_anchor_in_edit_mode_and_posts_only_the_one_shape()
    {
        // The script is the interpreter of both modes; its edit branch is read here as text, because no JS engine is
        // reachable from these tests (plan @38). It wires nothing else in edit mode and posts nothing but the anchor.
        var script = PrototypeRenderer.Script;
        var branch = script[script.IndexOf("data-mk-edit", StringComparison.Ordinal)..script.IndexOf("return;", StringComparison.Ordinal)];

        branch.Should().Contain("\"click\"").And.Contain("}, true);").And.Contain("e.preventDefault();").And.Contain("e.stopPropagation();");
        branch.Should().Contain("var message = { mk: \"select\", anchor: picked.getAttribute(\"data-mk-anchor\") };")
            .And.Contain("window.parent.postMessage(message, \"*\")");
        Regex.Matches(script, "postMessage").Should().ContainSingle();

        // Backlog 238: a part adds exactly its name, and a column header its column, read from the attributes the
        // renderer writes; nothing else is ever put on the message.
        branch.Should().Contain("start.closest(\"[data-mk-part-of], [data-mk-anchor]\")")
            .And.Contain("message.anchor = picked.getAttribute(\"data-mk-part-of\");")
            .And.Contain("message.part = picked.getAttribute(\"data-mk-part\");")
            .And.Contain("if (message.part === \"columnHeader\") { message.column = picked.getAttribute(\"data-mk-col\"); }");
        Regex.Matches(branch, @"message\.\w+ =(?!=)").Should().HaveCount(3);
        script.IndexOf("data-mk-edit", StringComparison.Ordinal).Should()
            .BeLessThan(script.IndexOf("addEventListener(\"change\"", StringComparison.Ordinal), "edit mode returns before any wiring");
    }

    [Fact]
    public void An_edit_render_outlines_only_an_emitted_selected_anchor()
    {
        var selected = PrototypeRenderer.Render(Design(), [], new PrototypeRenderOptions(Edit: true, Selected: "next")).Html;
        var invented = PrototypeRenderer.Render(Design(), [], new PrototypeRenderOptions(Edit: true, Selected: "nowhere")).Html;
        var viewing = PrototypeRenderer.Render(Design(), [], new PrototypeRenderOptions(Selected: "next")).Html;

        selected.Should().Contain("[data-mk-anchor=\"next\"] { outline: 2px solid");
        invented.Should().NotContain("[data-mk-anchor=\"nowhere\"]");
        viewing.Should().NotContain("outline: 2px solid #2f6fdf !important");
    }

    [Fact]
    public void A_render_lists_every_anchor_it_emitted_with_the_overlay_anchor_it_means()
    {
        var result = PrototypeRenderer.Render(Design(), [new PrototypeSetTextOperation(Next, "Go on")]);

        result.Anchors.Keys.Should().BeEquivalentTo(["home", "next", "/screens/0/children/1"]);
        result.Anchors["next"].Should().Be(new PrototypeRenderedAnchor("next", Next, "button", "Go on"));
        result.Anchors["/screens/0/children/1"].Should().Be(new PrototypeRenderedAnchor("/screens/0/children/1", Plain, "text", "Plain"));
        result.Anchors["home"].Anchor.Should().Be(new PrototypeAnchor("home", null));
        foreach (var key in result.Anchors.Keys)
        {
            result.Html.Should().Contain($" data-mk-anchor=\"{System.Net.WebUtility.HtmlEncode(key)}\"");
        }
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
