using AwesomeAssertions;
using Fravashi.Application.Verification;
using Xunit;

namespace Fravashi.Application.Tests.Verification;

/// <summary>Backlog 222 (plan @46): reading Playwright's aria snapshot text and the observation file around it.</summary>
public class AriaSnapshotTests
{
    [Fact]
    public void Reads_role_name_attributes_inline_text_and_nesting()
    {
        var nodes = AriaSnapshot.Parse("""
            - main [ref=e2]:
              - heading "Team directory" [level=1] [ref=e3]
              - button "Refresh" [ref=e7]: R
              - checkbox "Send" [checked] [ref=e9]
              - generic [ref=e4]:
                - text: Name
            - paragraph: Last
            """);

        nodes.Select(n => n.Role).Should().Equal("main", "paragraph");
        var main = nodes[0];
        main.Ref.Should().Be("e2");
        main.Children.Select(n => (n.Role, n.Name, n.Text)).Should().Equal(
            ("heading", "Team directory", null),
            ("button", "Refresh", "R"),
            ("checkbox", "Send", null),
            ("generic", null, null));
        main.Children[0].Attributes.Should().Equal(new Dictionary<string, string?> { ["level"] = "1", ["ref"] = "e3" });
        main.Children[2].Attributes.Should().ContainKey("checked").WhoseValue.Should().BeNull();
        main.Children[3].Children.Single().Should().Match<ObservedNode>(n => n.Role == "text" && n.Text == "Name");
        nodes[1].Text.Should().Be("Last");
    }

    [Fact]
    public void Reads_quoted_keys_json_names_and_quoted_values()
    {
        var nodes = AriaSnapshot.Parse("""
            - 'button "Save: now" [ref=e1]'
            - 'heading "It''s \"here\"!"': text
            - paragraph: "Role: Engineer"
            - listitem: 'a: b'
            """);

        nodes[0].Should().Match<ObservedNode>(n => n.Role == "button" && n.Name == "Save: now" && n.Ref == "e1");
        nodes[1].Should().Match<ObservedNode>(n => n.Name == "It's \"here\"!" && n.Text == "text");
        nodes[2].Text.Should().Be("Role: Engineer");
        nodes[3].Text.Should().Be("a: b");
    }

    [Fact]
    public void Drops_property_lines_with_anything_under_them()
    {
        var nodes = AriaSnapshot.Parse("""
            - link "Home":
              - /url: /
            - textbox "Name":
              - /placeholder: Your name
            - /children: equal
            """);

        nodes.Select(n => n.Role).Should().Equal("link", "textbox");
        nodes.Should().OnlyContain(n => n.Children.Count == 0);
    }

    [Theory]
    [InlineData("- button\n\t- text: x", "a tab in the indentation")]
    [InlineData("button \"x\"", "a line that is not a list item")]
    [InlineData("- button \"x\"\n  - text: y", "a line indented under an item that has no children")]
    [InlineData("  - main:\n- text: y", "a line indented less than the first")]
    [InlineData("- \"just words\"", "an item with no role")]
    [InlineData("- button \"open", "an unterminated string")]
    [InlineData("- button \"x\" [ref=e1", "expected ']'")]
    public void Refuses_what_the_generator_never_writes(string text, string why)
    {
        var parse = () => AriaSnapshot.Parse(text);

        parse.Should().Throw<FormatException>().Which.Message.Should().Contain(why);
    }

    [Fact]
    public void An_observation_file_carries_phases_viewports_probes_and_unreached_phases()
    {
        var observation = DesignObservationText.Parse("""
            @screen 1
            @phase at rest
            @viewport wide 1280
            - button "Add" [ref=e1]
            @box e1 10 20 30.5 40
            @contrast e1 4.5
            @phase d opened by Add
            @viewport wide 1280
            @not-reached button "Add" (add) was not found
            """);

        observation.Screen.Should().Be(1);
        observation.Phases.Select(p => (p.Phase, p.Viewport, p.Width, p.NotReached)).Should().Equal(
            ("at rest", "wide", 1280, null),
            ("d opened by Add", "wide", 1280, "button \"Add\" (add) was not found"));
        var button = observation.Phases[0].Nodes!.Single();
        button.Box.Should().Be(new ObservedBox(10, 20, 30.5, 40));
        button.Contrast.Should().Be(4.5);
        observation.Phases[1].Nodes.Should().BeNull();
    }

    [Theory]
    [InlineData("@phase at rest\n- button \"A\" [ref=e1]\n@box e2 0 0 1 1", "measures e2")]
    [InlineData("- button \"A\"", "before any @phase")]
    [InlineData("@phase at rest\n@viewport wide", "a name and a width")]
    [InlineData("@phase at rest\n@frame 1", "unknown directive @frame")]
    [InlineData("@phase p\n- button \"A\"\n@not-reached gone", "not reached and also has a snapshot")]
    public void An_observation_file_refuses_what_it_cannot_place(string text, string why)
    {
        var parse = () => DesignObservationText.Parse(text);

        parse.Should().Throw<FormatException>().Which.Message.Should().Contain(why);
    }

    [Fact]
    public void A_union_box_holds_both()
    {
        new ObservedBox(0, 10, 50, 20).Union(new ObservedBox(60, 0, 10, 10)).Should().Be(new ObservedBox(0, 0, 70, 30));
    }
}
