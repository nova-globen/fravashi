using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Xunit;

namespace Fravashi.Application.Tests.Mockups;

/// <summary>
/// Backlog 210 (plan @42): the advice a valid tree gets when it is mute or has no heading, and only then. Round 2
/// made the advice carry the fix (the node, by path and words, and the exact JSON to add), so these tests also
/// paste each snippet into the tree AS WRITTEN and read it back: a fix that does not read back valid is no fix.
/// </summary>
public sealed partial class MockupAdviceTests
{
    private const string Opening = "Valid, but this tree describes no behaviour (no states, on, at or a11y anywhere).";

    private const string Rest = "Call mockup_schema for the rest: labels, at, dialogs, and on with go, open, close and set.";

    private static string HeadingLine(string node, string path, string snippet = "by adding \"a11y\": {\"heading\": 1} to it") =>
        $"Valid, but no node in this tree is a heading (a11y.heading). Make {node} at {path} the main heading {snippet}. {Rest}";

    private static string SetLine(string node, string path, string id, bool addId) =>
        $"{Opening} For example, give {node} at {path} "
        + (addId ? $"\"id\": \"{id}\", " : string.Empty)
        + $"\"states\": {{\"loading\": {{}}}}, \"on\": {{\"click\": {{\"set\": {{\"{id}\": \"loading\"}}}}}} so a click shows it loading.";

    [Fact]
    public void The_advice_is_at_most_two_lines_and_names_mockup_schema_once_at_the_end()
    {
        foreach (var json in new[]
        {
            """{"mockup": 1, "screens": [{"type": "window", "id": "home", "text": "Home"}]}""",
            """{"mockup": 1, "screens": [{"type": "window", "id": "home", "children": [{"type": "button", "text": "Go"}]}]}""",
            """{"mockup": 1, "screens": [{"type": "window", "id": "home", "children": [{"type": "button", "id": "b", "text": "Go", "on": {"click": {"go": "home"}}}]}]}""",
        })
        {
            var advice = MockupAdvice.For(Read(json))!;
            var lines = advice.Split('\n');

            lines.Length.Should().BeInRange(1, 2);
            foreach (var line in lines)
            {
                line.Should().StartWith("Valid, but ");
                line.Length.Should().BeLessThan(400);
            }

            advice.Split("mockup_schema").Length.Should().Be(2, "mockup_schema is named exactly once");
            advice.Should().EndWith(Rest);
        }
    }

    [Fact]
    public void Every_feature_json_which_uses_everything_gets_no_advice() =>
        MockupAdvice.For(Fixture("every-feature.json")).Should().BeNull();

    [Fact]
    public void The_tree_208s_live_re_run_saved_is_told_which_node_to_make_the_heading_and_how() =>
        MockupAdvice.For(Fixture("book-a-room-208-re-run.json")).Should().Be(
            "Valid, but no node in this tree is a heading (a11y.heading). Make the text \"Book a room\" at "
            + "$.screens[0].children[0] the main heading by adding \"a11y\": {\"heading\": 1} to it. Call mockup_schema "
            + "for the rest: labels, at, dialogs, and on with go, open, close and set.");

    [Theory]
    [InlineData("""{"mockup": 1, "screens": [{"type": "window", "id": "home", "text": "Home"}]}""")]
    [InlineData("""{"mockup": 1, "viewports": [{"name": "wide", "width": 1280}], "screens": [{"type": "window", "id": "home", "children": [{"type": "text", "text": "Rooms"}, {"type": "dialog", "id": "d", "children": [{"type": "button", "text": "OK"}]}]}]}""")]
    public void A_tree_with_none_of_states_on_at_or_a11y_is_told_it_is_mute(string json) =>
        MockupAdvice.For(Read(json))!.Split('\n')[0].Should().StartWith(Opening);

    [Fact]
    public void A_mute_tree_gets_a_set_example_on_its_own_first_button_and_then_the_heading_fix()
    {
        var json = """
            {"mockup": 1, "screens": [{"type": "window", "id": "home", "children": [
              {"type": "text", "text": "Rooms"},
              {"type": "form", "children": [{"type": "field", "text": "Date"}, {"type": "button", "text": "Book now!"}]},
              {"type": "button", "text": "Cancel"}]}]}
            """;

        MockupAdvice.For(Read(json)).Should().Be(
            SetLine("the button \"Book now!\"", "$.screens[0].children[1].children[1]", "book-now", addId: true)
            + "\n" + HeadingLine("the text \"Rooms\"", "$.screens[0].children[0]"));
    }

    [Fact]
    public void A_button_that_has_an_id_keeps_it_and_the_set_names_it()
    {
        var json = """{"mockup": 1, "screens": [{"type": "window", "id": "home", "children": [{"type": "button", "id": "save", "text": "Save"}]}]}""";

        MockupAdvice.For(Read(json))!.Split('\n')[0]
            .Should().Be(SetLine("the button \"Save\"", "$.screens[0].children[0]", "save", addId: false));
    }

    [Theory]
    [InlineData("""{"type": "button", "text": "Book"}, {"type": "text", "id": "book", "text": "Booked"}""", "book-2")]
    [InlineData("""{"type": "button", "text": "Book"}, {"type": "text", "id": "book", "text": "A"}, {"type": "text", "id": "book-2", "text": "B"}""", "book-3")]
    [InlineData("""{"type": "button", "text": "42 rooms"}""", "button-42-rooms")]
    [InlineData("""{"type": "button", "text": "..."}""", "button")]
    [InlineData("""{"type": "button", "text": "Reserve this meeting room for the whole of next week"}""", "reserve-this-meeting-room-for-th")]
    public void An_id_derived_from_a_buttons_words_is_a_valid_name_unique_in_the_tree(string children, string id)
    {
        var json = $$"""{"mockup": 1, "screens": [{"type": "window", "id": "home", "children": [{{children}}]}]}""";

        var line = MockupAdvice.For(Read(json))!.Split('\n')[0];

        line.Should().Contain($"\"id\": \"{id}\", \"states\": {{\"loading\": {{}}}}, \"on\": {{\"click\": {{\"set\": {{\"{id}\": \"loading\"}}}}}}");
        MockupVocabulary.IsName(id).Should().BeTrue();
    }

    [Fact]
    public void A_mute_tree_with_no_button_says_so_and_still_gets_the_heading_fix_on_its_screen()
    {
        var json = """{"mockup": 1, "screens": [{"type": "window", "id": "home", "text": "Home"}]}""";

        MockupAdvice.For(Read(json)).Should().Be(
            $"{Opening} It has no button to build an example on: a node's \"on\" maps an event such as \"click\" to "
            + "{\"go\": a screen id}, {\"open\": a dialog id} or {\"set\": {a node id: one of that node's own \"states\"}}."
            + "\n" + HeadingLine("the window \"Home\"", "$.screens[0]"));
    }

    [Fact]
    public void Long_or_quoted_words_are_capped_and_escaped_so_the_line_stays_one_line()
    {
        var json = """{"mockup": 1, "screens": [{"type": "window", "id": "home", "children": [{"type": "text", "text": "A \"quoted\"\nword and then a great many more words after it"}, {"type": "button", "id": "b", "text": "Go", "on": {"click": {"go": "home"}}}]}]}""";

        var advice = MockupAdvice.For(Read(json))!;

        advice.Should().NotContain("\n");
        advice.Should().Contain("Make the text \"A \\\"quoted\\\"\\nword and then a great many mo...\" at $.screens[0].children[0]");
    }

    [Theory]
    [InlineData("""{"mockup": 1, "screens": [{"type": "window", "id": "home", "children": [{"type": "button", "id": "b", "text": "Go", "on": {"click": {"go": "home"}}}]}]}""")]
    [InlineData("""{"mockup": 1, "screens": [{"type": "window", "id": "home", "children": [{"type": "button", "id": "b", "text": "Go", "states": {"loading": {}}}]}]}""")]
    [InlineData("""{"mockup": 1, "screens": [{"type": "window", "id": "home", "children": [{"type": "button", "id": "b", "a11y": {"label": "Go"}}]}]}""")]
    [InlineData("""{"mockup": 1, "viewports": [{"name": "narrow", "width": 390}], "screens": [{"type": "window", "id": "home", "layout": "row", "at": {"narrow": {"layout": "column"}}}]}""")]
    public void A_tree_with_behaviour_but_no_heading_is_told_so_in_one_line(string json)
    {
        var advice = MockupAdvice.For(Read(json))!;

        advice.Should().StartWith("Valid, but no node in this tree is a heading (a11y.heading).").And.NotContain("\n");
    }

    [Fact]
    public void A_first_screen_with_no_text_node_names_the_screen_itself()
    {
        var json = """{"mockup": 1, "screens": [{"type": "window", "id": "home", "children": [{"type": "button", "id": "b", "text": "Go", "on": {"click": {"go": "next"}}}]}, {"type": "window", "id": "next", "children": [{"type": "text", "text": "Next"}]}]}""";

        MockupAdvice.For(Read(json)).Should().Be(HeadingLine("the window with the id \"home\"", "$.screens[0]"));
    }

    [Fact]
    public void A_decorative_text_is_skipped_and_a_target_with_a11y_gets_the_heading_inside_it()
    {
        var json = """
            {"mockup": 1, "screens": [{"type": "window", "id": "home", "children": [
              {"type": "text", "text": "*", "a11y": {"decorative": true}},
              {"type": "text", "text": "Rooms", "a11y": {"live": "polite"}}]}]}
            """;

        MockupAdvice.For(Read(json)).Should().Be(
            HeadingLine("the text \"Rooms\"", "$.screens[0].children[1]", "by adding \"heading\": 1 inside its \"a11y\""));
    }

    [Fact]
    public void A_heading_only_inside_an_override_is_not_a_heading_at_rest()
    {
        var json = """
            {"mockup": 1, "viewports": [{"name": "wide", "width": 1280}], "screens": [{"type": "window", "id": "home", "children": [
              {"type": "text", "id": "t", "text": "Rooms", "at": {"wide": {"a11y": {"heading": 1}}}}]}]}
            """;

        MockupAdvice.For(Read(json)).Should().Be(HeadingLine("the text \"Rooms\"", "$.screens[0].children[0]"));
    }

    [Theory]
    [InlineData("""{"mockup": 1, "screens": [{"type": "window", "id": "home", "children": [{"type": "text", "text": "Rooms", "a11y": {"heading": 1}}]}]}""")]
    [InlineData("""{"mockup": 1, "screens": [{"type": "window", "id": "home", "children": []}, {"type": "window", "id": "next", "children": [{"type": "text", "text": "Next", "a11y": {"heading": 2}}]}]}""")]
    [InlineData("""{"mockup": 1, "screens": [{"type": "window", "id": "home", "children": [{"type": "dialog", "id": "d", "children": [{"type": "text", "text": "Book", "a11y": {"heading": 2}}]}]}]}""")]
    public void A_heading_anywhere_in_the_tree_silences_the_advice(string json) =>
        MockupAdvice.For(Read(json)).Should().BeNull();

    /// <summary>
    /// Every advised tree above, with each snippet pasted in EXACTLY as the line wrote it, reads back valid, and
    /// with every line applied it gets no advice at all.
    /// </summary>
    [Theory]
    [InlineData("book-a-room-208-re-run.json")]
    [InlineData("""{"mockup": 1, "screens": [{"type": "window", "id": "home", "text": "Home"}]}""")]
    [InlineData("""{"mockup": 1, "screens": [{"type": "window", "id": "home", "children": [{"type": "text", "text": "Rooms"}, {"type": "form", "children": [{"type": "field", "text": "Date"}, {"type": "button", "text": "Book now!"}]}]}]}""")]
    [InlineData("""{"mockup": 1, "screens": [{"type": "window", "id": "home", "children": [{"type": "button", "text": "Book"}, {"type": "text", "id": "book", "text": "Booked"}]}]}""")]
    [InlineData("""{"mockup": 1, "screens": [{"type": "window", "id": "home", "children": [{"type": "button", "id": "save", "text": "Save"}, {"type": "dialog", "id": "d", "children": [{"type": "text", "text": "Sure?"}]}]}]}""")]
    [InlineData("""{"mockup": 1, "screens": [{"type": "window", "id": "home", "children": [{"type": "button", "id": "b", "text": "Go", "on": {"click": {"go": "home"}}}]}]}""")]
    [InlineData("""{"mockup": 1, "screens": [{"type": "window", "id": "home", "children": [{"type": "text", "text": "*", "a11y": {"decorative": true}}, {"type": "text", "text": "Rooms", "a11y": {"live": "polite"}}]}]}""")]
    public void Each_snippet_pasted_as_written_reads_back_valid_and_all_of_them_silence_the_advice(string treeOrFixture)
    {
        var json = treeOrFixture.EndsWith(".json", StringComparison.Ordinal) ? FixtureText(treeOrFixture) : treeOrFixture;
        var root = JsonNode.Parse(json)!.AsObject();

        var advice = MockupAdvice.For(Read(json))!;
        advice.Should().NotBeNull();

        foreach (var line in advice.Split('\n'))
        {
            if (SetSnippet().Match(line) is { Success: true } set)
            {
                Merge(At(root, set.Groups["path"].Value), JsonNode.Parse($"{{{set.Groups["json"].Value}}}")!.AsObject());
            }
            else if (HeadingSnippet().Match(line) is { Success: true } heading)
            {
                var node = At(root, heading.Groups["path"].Value);
                var add = JsonNode.Parse($"{{{heading.Groups["json"].Value}}}")!.AsObject();
                Merge(heading.Groups["inside"].Success ? node["a11y"]!.AsObject() : node, add);
            }
            else
            {
                line.Should().Contain("no button to build an example on", "every other line carries a snippet");
                continue;
            }

            MockupReader.Read(root.ToJsonString()).Errors.Should().BeEmpty(line);
        }

        // With no button the heading alone is enough: an a11y heading is behaviour, so the tree is no longer mute.
        MockupAdvice.For(Read(root.ToJsonString())).Should().BeNull();
    }

    [GeneratedRegex("""give .+ at (?<path>\$\S+) (?<json>.+) so a click shows it loading\.$""")]
    private static partial Regex SetSnippet();

    [GeneratedRegex("""at (?<path>\$\S+) the main heading by adding (?<json>.+?) (to it|(?<inside>inside its "a11y"))\.""")]
    private static partial Regex HeadingSnippet();

    [GeneratedRegex("""\.(?<name>\w+)\[(?<index>\d+)\]""")]
    private static partial Regex PathStep();

    private static JsonObject At(JsonObject root, string path)
    {
        JsonNode node = root;
        foreach (Match step in PathStep().Matches(path))
        {
            node = node[step.Groups["name"].Value]![int.Parse(step.Groups["index"].Value, System.Globalization.CultureInfo.InvariantCulture)]!;
        }

        return node.AsObject();
    }

    private static void Merge(JsonObject into, JsonObject add)
    {
        foreach (var (name, value) in add.ToList())
        {
            add.Remove(name);
            into[name] = value;
        }
    }

    private static MockupDocument Read(string json)
    {
        var read = MockupReader.Read(json);
        read.Errors.Should().BeEmpty(string.Join("; ", read.Errors));
        return read.Document!;
    }

    private static string FixtureText(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mockups", "Fixtures", name));

    private static MockupDocument Fixture(string name) => Read(FixtureText(name));
}
