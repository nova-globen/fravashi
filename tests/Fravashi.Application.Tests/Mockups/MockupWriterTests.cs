using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Xunit;

namespace Fravashi.Application.Tests.Mockups;

/// <summary>
/// ADR 0021: the tree "diffs cleanly in review". That holds only when one tree has one spelling, so the
/// writer's form is canonical: written, read and written again it is the same bytes, and two authors who
/// wrote the same tree in different property orders get the same file.
/// </summary>
public class MockupWriterTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mockups", "Fixtures", name));

    private static MockupDocument Read(string json)
    {
        var result = MockupReader.Read(json);
        result.Errors.Should().BeEmpty();
        return result.Document!;
    }

    [Fact]
    public void The_canonical_form_reads_back_and_writes_the_same_bytes()
    {
        var once = MockupWriter.Write(Read(Fixture("team-directory.attempt-3.json")));
        var twice = MockupWriter.Write(Read(once));

        twice.Should().Be(once);
    }

    [Fact]
    public void Two_spellings_of_one_tree_are_written_identically()
    {
        var first = """
            {"mockup": 1, "screens": [{"type": "window", "id": "main", "text": "Home",
              "children": [{"type": "button", "text": "Save", "id": "save",
                "on": {"click": {"set": {"save": "loading", "note": "hidden"}}},
                "states": {"loading": {"text": "Saving..."}, "disabled": {}}},
                {"type": "text", "id": "note", "text": "Saved", "states": {"hidden": {}}}]}],
             "data": {"b": [{"y": 1, "x": 2}], "a": []}}
            """;
        var second = """
            {"data": {"a": [], "b": [{"x": 2, "y": 1}]}, "mockup": 1,
             "screens": [{"children": [{"states": {"disabled": {}, "loading": {"text": "Saving..."}},
                "on": {"click": {"set": {"note": "hidden", "save": "loading"}}},
                "id": "save", "type": "button", "text": "Save"},
                {"states": {"hidden": {}}, "text": "Saved", "id": "note", "type": "text"}],
              "text": "Home", "id": "main", "type": "window"}]}
            """;

        MockupWriter.Write(Read(first)).Should().Be(MockupWriter.Write(Read(second)));
    }

    [Fact]
    public void A_links_href_is_written_after_its_text_and_reads_back()
    {
        // Backlog 290: href sits after text in the canonical order whatever order it was written in.
        var written = MockupWriter.Write(Read("""
            {"mockup": 1, "screens": [{"type": "window", "id": "main", "children": [
              {"href": "/", "type": "link", "text": "Home", "id": "home"}]}]}
            """));

        written.Should().Contain("\"type\": \"link\",\n          \"id\": \"home\",\n          \"text\": \"Home\",\n          \"href\": \"/\"\n");
        MockupWriter.Write(Read(written)).Should().Be(written);
    }

    [Theory]
    [InlineData("team-directory.attempt-3.json")]
    [InlineData("book-a-room-208-re-run.json")]
    public void A_stored_tree_without_links_writes_no_href(string fixture)
    {
        // Backlog 290: a design stored before links existed reads and writes exactly as before.
        MockupWriter.Write(Read(Fixture(fixture))).Should().NotContain("\"href\"");
    }

    [Fact]
    public void The_canonical_form_is_indented_with_newlines_and_writes_text_as_itself()
    {
        var written = MockupWriter.Write(Read(
            """{"mockup": 1, "title": "Café <&> 'Ada'", "screens": [{"type": "window", "id": "main"}]}"""));

        written.Should().Be(
            "{\n  \"mockup\": 1,\n  \"title\": \"Café <&> 'Ada'\",\n  \"screens\": [\n    {\n"
            + "      \"type\": \"window\",\n      \"id\": \"main\"\n    }\n  ]\n}\n");
    }

    [Fact]
    public void An_override_is_written_without_the_type_it_inherits()
    {
        var written = MockupWriter.Write(Read(
            """{"mockup": 1, "viewports": [{"name": "narrow", "width": 390}], "screens": [{"type": "window", "id": "main", "layout": "row", "at": {"narrow": {"layout": "column"}}}]}"""));

        written.Should().Contain("\"at\": {\n        \"narrow\": {\n          \"layout\": \"column\"\n        }");
        MockupWriter.Write(Read(written)).Should().Be(written);
    }
}
