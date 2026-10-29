using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Fravashi.Application.Wiki;
using Fravashi.Domain.Projects;
using Fravashi.Domain.Wiki;
using Xunit;

namespace Fravashi.Application.Tests.Mockups;

/// <summary>
/// #196: saving a design from the editor. The round-trip rule of #51 made concrete: a save with no edits writes
/// nothing, only the block's content is ever replaced, and a version somebody else moved is refused.
/// </summary>
public class DesignMockupSaveTests
{
    private const string Tree = """
        {"mockup": 1, "screens": [{"type": "window", "id": "home", "text": "Home", "children": [
          {"type": "text", "id": "one", "text": "One"}, {"type": "text", "id": "two", "text": "Two"}]}]}

        """;

    private const string Before = "# The home screen\n\nWhat it is for: #12 and @3.\n\n";
    private const string After = "\nAnything after the tree is prose too.\n";

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mockups", "Fixtures", name));

    private static MockupEditSession Open(string json)
    {
        MockupEditSession.TryOpen(json, out var session, out var refusal).Should().BeTrue(string.Join("; ", refusal));
        return session!;
    }

    private static (string Body, MockupBlock Block) Body(string fenceOpen, string json, string fenceClose)
    {
        var body = Before + fenceOpen + "\n" + json + fenceClose + After;
        var block = DesignMockup.Read(body).Block;
        block.Should().NotBeNull();
        return (body, block!);
    }

    [Theory]
    [InlineData("```mockup", "```")]
    [InlineData("~~~~ mockup and words", "~~~~~")]
    [InlineData("  ```mockup", "```")]
    public void Only_the_blocks_content_is_replaced_and_the_prose_and_fences_are_kept_byte_for_byte(string open, string close)
    {
        var (body, block) = Body(open, Tree, close);

        var written = DesignMockupSave.WithTree(body, block, "{\"new\": true}\n");

        written.Should().Be(Before + open + "\n" + "{\"new\": true}\n" + close + After);
    }

    [Fact]
    public void Prose_with_windows_line_ends_is_kept_as_it_was()
    {
        var before = "Intro\r\n\r\n";
        var body = before + "```mockup\r\n" + Tree + "```\r\nAfter\r\n";
        var block = DesignMockup.Read(body).Block!;

        var written = DesignMockupSave.WithTree(body, block, "{}\n");

        written.Should().Be(before + "```mockup\r\n{}\n```\r\nAfter\r\n");
    }

    [Fact]
    public void A_block_never_closed_is_closed_with_a_fence_like_its_opening_one()
    {
        var body = "Intro\n\n````mockup\n" + Tree;
        var block = DesignMockup.Read(body).Block!;
        block.Closed.Should().BeFalse();

        var written = DesignMockupSave.WithTree(body, block, "{}");

        written.Should().Be("Intro\n\n````mockup\n{}\n````\n");
        DesignMockup.Read(written).Block!.Closed.Should().BeTrue();
    }

    [Fact]
    public void An_empty_block_gets_the_tree_between_its_fences()
    {
        var body = "Intro\n```mockup\n```\nAfter\n";
        var block = DesignMockup.Read(body).Block!;

        DesignMockupSave.WithTree(body, block, "{}\n").Should().Be("Intro\n```mockup\n{}\n```\nAfter\n");
    }

    [Theory]
    [InlineData("team-directory.attempt-1.json")]
    [InlineData("team-directory.attempt-2.json")]
    [InlineData("team-directory.attempt-3.json")]
    public async Task Saving_an_agent_written_tree_with_no_edits_writes_nothing(string fixture)
    {
        // The fixtures are as the agent wrote them, not canonical, and two of them the reader refuses.
        var (body, _) = Body("```mockup", Fixture(fixture), "```");
        var wiki = new OneDocument(body);
        var session = Open(DesignMockup.Read(body).Block!.Json);

        var result = await DesignMockupSave.SaveAsync(wiki, wiki.Item, session);

        result.Outcome.Should().Be(DesignSaveOutcome.Unchanged);
        wiki.Writes.Should().Be(0);
        wiki.Item.Version.Should().Be(1);
        wiki.Item.Body.Should().Be(body);
    }

    [Fact]
    public async Task An_edit_then_its_undo_writes_nothing()
    {
        var (body, block) = Body("```mockup", Tree, "```");
        var wiki = new OneDocument(body);
        var session = Open(block.Json);
        session.Apply(MockupEdit.Move(MockupTarget.ById("two"), MockupTarget.At(0), 0)).Applied.Should().BeTrue();
        session.Undo();

        (await DesignMockupSave.SaveAsync(wiki, wiki.Item, session)).Outcome.Should().Be(DesignSaveOutcome.Unchanged);
        wiki.Writes.Should().Be(0);
    }

    [Fact]
    public async Task An_edit_is_written_canonically_against_the_version_opened_and_the_prose_is_untouched()
    {
        var agentWritten = Fixture("team-directory.attempt-3.json");
        var (body, _) = Body("```mockup", agentWritten, "```");
        var wiki = new OneDocument(body);
        var session = Open(DesignMockup.Read(body).Block!.Json);
        session.Apply(MockupEdit.Move(MockupTarget.ById("people-table"), MockupTarget.ById("directory"), 0)).Applied.Should().BeTrue();

        var result = await DesignMockupSave.SaveAsync(wiki, wiki.Item, session);

        result.Outcome.Should().Be(DesignSaveOutcome.Saved);
        wiki.Writes.Should().Be(1);
        result.Item!.Version.Should().Be(2);
        var saved = result.Item.Body;
        saved.Should().StartWith(Before + "```mockup\n");
        saved.Should().EndWith("```" + After);

        // The block is the writer's canonical form of the edited tree, so it reads back as exactly what was edited.
        var stored = DesignMockup.Read(saved);
        stored.IsValid.Should().BeTrue();
        stored.Block!.Json.Should().Be(session.Json);
        MockupWriter.Write(stored.Tree!.Document!).Should().Be(session.Json);

        // Everywhere the edit did not reach, the tree is what the agent wrote, in canonical form.
        var original = MockupReader.Read(agentWritten).Document!;
        var edited = stored.Tree.Document!;
        edited.Data.Should().HaveCount(original.Data.Count);
        Write(edited, "add-dialog").Should().Be(Write(original, "add-dialog"));
        Write(edited, "profile").Should().Be(Write(original, "profile"));

        // And the session is clean again, against what was saved.
        session.IsDirty.Should().BeFalse();
    }

    [Fact]
    public async Task A_save_against_a_version_somebody_else_moved_is_refused_and_writes_nothing()
    {
        var (body, block) = Body("```mockup", Tree, "```");
        var wiki = new OneDocument(body);
        var opened = wiki.Item;
        var session = Open(block.Json);
        session.Apply(MockupEdit.Remove(MockupTarget.ById("two"))).Applied.Should().BeTrue();
        (await wiki.UpdateAsync(opened.Id, new WikiItemEdit { Body = body + "Somebody else's line.\n" }, opened.Version)).IsValid.Should().BeTrue();

        var result = await DesignMockupSave.SaveAsync(wiki, opened, session);

        result.Outcome.Should().Be(DesignSaveOutcome.Stale);
        result.Errors.Should().ContainSingle(e => e.Code == WikiCodes.Conflict);
        wiki.Item.Body.Should().Be(body + "Somebody else's line.\n");
        session.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void Marking_saved_moves_the_clean_point_and_undo_past_it_is_dirty_again()
    {
        var session = Open(Tree);
        session.Apply(MockupEdit.Remove(MockupTarget.ById("two"))).Applied.Should().BeTrue();

        session.MarkSaved();
        session.IsDirty.Should().BeFalse();
        session.CanUndo.Should().BeTrue();

        session.Undo();
        session.IsDirty.Should().BeTrue();
    }

    private static string Write(MockupDocument document, string screenOrDialogId)
    {
        var node = Find(document.Screens, screenOrDialogId)!;
        return MockupWriter.Write(new MockupDocument(1, null, [], [], [node]));
    }

    private static MockupNode? Find(IReadOnlyList<MockupNode> nodes, string id)
    {
        foreach (var node in nodes)
        {
            if (node.Id == id)
            {
                return node;
            }

            if (node.Children is { } children && Find(children, id) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>One stored design with the wiki's version rule: a stale version is refused with conflict.</summary>
    private sealed class OneDocument : IWikiDocuments
    {
        public OneDocument(string body) => Item = WikiItem.Create(new WikiItemState
        {
            Id = "design-1",
            Number = 1,
            Type = WikiItemType.Design,
            Title = "Home",
            Slug = "home",
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
