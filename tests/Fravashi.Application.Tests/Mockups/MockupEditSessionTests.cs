using System.Text.Json.Nodes;
using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Xunit;

namespace Fravashi.Application.Tests.Mockups;

/// <summary>
/// #195: the session around the edits. Dirty means "not the tree that was opened", which is what lets #196's
/// save write nothing when nothing changed; undo and redo walk the edits back and forth exactly.
/// </summary>
public class MockupEditSessionTests
{
    public static TheoryData<string> Fixtures() =>
        new() { "team-directory.attempt-1.json", "team-directory.attempt-2.json", "team-directory.attempt-3.json" };

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mockups", "Fixtures", name));

    private static MockupEditSession Open(string json)
    {
        MockupEditSession.TryOpen(json, out var session, out var refusal).Should().BeTrue(string.Join("; ", refusal));
        return session!;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Opening_a_fixture_and_doing_nothing_is_not_dirty(string name)
    {
        var session = Open(Fixture(name));

        session.IsDirty.Should().BeFalse();
        session.CanUndo.Should().BeFalse();
        session.CanRedo.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void An_edit_then_its_undo_is_not_dirty(string name)
    {
        var session = Open(Fixture(name));
        var opened = session.Json;

        session.Apply(MockupEdit.SetTitle("Renamed")).Applied.Should().BeTrue();
        session.IsDirty.Should().BeTrue();

        session.Undo().Should().BeTrue();
        session.IsDirty.Should().BeFalse();
        session.Json.Should().Be(opened);
    }

    [Fact]
    public void An_invalid_fixture_opens_with_the_readers_errors_so_it_can_be_repaired()
    {
        var json = Fixture("team-directory.attempt-1.json");

        var session = Open(json);

        session.Document.Should().BeNull();
        session.Errors.Should().Equal(MockupReader.Read(json).Errors);
    }

    [Fact]
    public void A_valid_tree_is_held_in_the_writers_canonical_form()
    {
        var json = Fixture("team-directory.attempt-3.json");
        var document = MockupReader.Read(json).Document!;

        Open(json).Json.Should().Be(MockupWriter.Write(document));
        MockupEditSession.Open(document).Json.Should().Be(MockupWriter.Write(document));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("""{"screens": []}""")]
    [InlineData("""{"mockup": 2, "screens": []}""")]
    [InlineData("""{"mockup": 1, "title": "a", "title": "b", "screens": []}""")]
    public void What_is_no_tree_of_this_version_does_not_open_and_the_reader_says_why(string json)
    {
        MockupEditSession.TryOpen(json, out var session, out var refusal).Should().BeFalse();

        session.Should().BeNull();
        refusal.Should().Equal(MockupReader.Read(json).Errors);
    }

    [Fact]
    public void A_new_edit_clears_redo_and_undo_and_redo_say_when_there_is_nothing_to_do()
    {
        var session = Open(Fixture("team-directory.attempt-3.json"));

        session.Undo().Should().BeFalse();
        session.Redo().Should().BeFalse();

        session.Apply(MockupEdit.SetTitle("One"));
        session.Undo();
        session.CanRedo.Should().BeTrue();

        session.Apply(MockupEdit.SetTitle("Two"));
        session.CanRedo.Should().BeFalse();
        session.Redo().Should().BeFalse();
    }

    [Fact]
    public void Redo_reapplies_what_undo_took_back()
    {
        var session = Open(Fixture("team-directory.attempt-3.json"));
        session.Apply(MockupEdit.Remove(MockupTarget.ById("refresh")));
        session.Apply(MockupEdit.SetProperty(MockupTarget.ById("search"), "text", JsonValue.Create("Search")));
        var edited = session.Json;

        session.Undo();
        session.Undo();
        session.Redo().Should().BeTrue();
        session.Redo().Should().BeTrue();

        session.Json.Should().Be(edited);
        session.CanRedo.Should().BeFalse();
    }

    [Fact]
    public void Undo_brings_back_an_incomplete_tree_without_judging_it_again()
    {
        var session = Open(Fixture("team-directory.attempt-3.json"));
        session.Apply(MockupEdit.SetProperty(MockupTarget.ById("name-field"), "text", null));
        session.Apply(MockupEdit.SetTitle("Edited"));
        var incomplete = session.Json;

        session.Undo();
        session.Undo();
        session.Redo().Should().BeTrue();
        session.Redo().Should().BeTrue();

        session.Json.Should().Be(incomplete);
        session.Errors.Should().ContainSingle();
    }

    [Fact]
    public void An_edit_to_an_invalid_tree_is_judged_only_by_what_it_adds()
    {
        // Attempt 1 carries errors of its own; a title edit adds none and is allowed, a node with a bad id adds one.
        var session = Open(Fixture("team-directory.attempt-1.json"));
        var errors = session.Errors.Count;

        session.Apply(MockupEdit.SetTitle("Repaired later")).Applied.Should().BeTrue();
        session.Errors.Should().HaveCount(errors);

        var clash = session.Apply(MockupEdit.Insert(MockupTarget.At(0), 0, (JsonObject)JsonNode.Parse(
            """{"type": "text", "id": "1x", "text": "x"}""")!));
        clash.Applied.Should().BeFalse();
        clash.Refusal.Should().ContainSingle().Which.Message.Should().StartWith("An id \"1x\" is not a valid name");
    }
}
