using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Fravashi.Application.Prototypes;
using Fravashi.Application.Verification;
using Xunit;

namespace Fravashi.Application.Tests.Verification;

/// <summary>
/// Backlog 221 (plan @45): the role table and the renderer cannot drift. Each role here is DERIVED from the markup
/// <see cref="PrototypeRenderer"/> really emits, by the HTML-AAM rules as Playwright's role engine applies them, and
/// compared with <see cref="DesignRoles"/>. A tag this derivation does not know fails the test, so a new element in the
/// renderer forces a decision about its role instead of slipping past. #223's live self-test is the browser's own word.
/// </summary>
public partial class DesignRolesTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mockups", "Fixtures", name));

    public static TheoryData<string> Fixtures() =>
        new()
        {
            "every-feature.json", "team-directory.attempt-3.json", "book-a-room-208-re-run.json",
        };

    private static MockupDocument Read(string json)
    {
        var read = MockupReader.Read(json);
        read.IsValid.Should().BeTrue(string.Join("; ", read.Errors));
        return read.Document!;
    }

    /// <summary>A design holding every component once and a field of every input.</summary>
    private static string EveryRole()
    {
        var fields = new StringBuilder();
        foreach (var input in Enum.GetValues<MockupInput>())
        {
            var name = MockupVocabulary.Name(input);
            var options = input is MockupInput.Select or MockupInput.Radio ? ", \"options\": [\"A\", \"B\"]" : string.Empty;
            fields.Append(CultureInfo.InvariantCulture, $", {{\"type\": \"field\", \"id\": \"in-{name}\", \"text\": \"F {name}\", \"input\": \"{name}\"{options}}}");
        }

        return $$$"""
            {"mockup": 1, "title": "Every role", "screens": [
              {"type": "window", "id": "w", "text": "W", "children": [
                {"type": "panel", "id": "p", "text": "P", "children": [{"type": "button", "id": "pb", "text": "PB"}]},
                {"type": "toolbar", "id": "tb", "children": [{"type": "button", "id": "tbb", "text": "TB"}]},
                {"type": "form", "id": "f", "children": [{"type": "button", "id": "fb", "text": "FB"}]},
                {"type": "container", "id": "c", "children": [{"type": "button", "id": "cb", "text": "CB"}]},
                {"type": "list", "id": "l", "items": ["a"]},
                {"type": "tree", "id": "tr", "items": ["a"]},
                {"type": "table", "id": "t", "columns": [{"key": "k", "text": "K"}], "items": [{"k": "v"}]},
                {"type": "button", "id": "b", "text": "B"},
                {"type": "link", "id": "a", "text": "A", "href": "https://example.invalid/"},
                {"type": "link", "id": "a-icon", "a11y": {"label": "Home"}},
                {"type": "text", "id": "x", "text": "X"},
                {"type": "text", "id": "h", "text": "H", "a11y": {"heading": 2}},
                {"type": "image", "id": "i", "a11y": {"label": "I"}},
                {"type": "dialog", "id": "d", "text": "D", "children": [{"type": "button", "id": "db", "text": "DB"}]},
                {"type": "tabs", "id": "tabs", "children": [
                  {"type": "panel", "id": "p1", "text": "One", "children": [{"type": "button", "id": "p1b", "text": "1"}]},
                  {"type": "panel", "id": "p2", "text": "Two", "children": [{"type": "button", "id": "p2b", "text": "2"}]}
                ]}{{{fields}}}
              ]}
            ]}
            """;
    }

    [Fact]
    public void Every_component_and_every_input_has_an_entry()
    {
        DesignRoles.Components.Keys.Should().BeEquivalentTo(Enum.GetValues<MockupComponent>());
        DesignRoles.Inputs.Keys.Should().BeEquivalentTo(Enum.GetValues<MockupInput>());
        DesignRoles.Components[MockupComponent.Field].Should().Be(DesignRoles.Inputs[MockupInput.Text]);
    }

    [Fact]
    public void The_three_roles_the_spike_got_wrong_are_the_ones_the_renderer_gives()
    {
        DesignRoles.Inputs[MockupInput.Number].Role.Should().Be("spinbutton");
        DesignRoles.Inputs[MockupInput.Radio].Role.Should().Be("group");
        DesignRoles.Tab.Role.Should().Be("tab");
    }

    [Fact]
    public void A_link_is_asked_as_a_link_not_a_button()
    {
        // Backlog 290: before the link component a design drew a link as a button and the check looked for a button.
        DesignRoles.Components[MockupComponent.Link].Should().Be(new DesignRole("link", true));
        DesignRoles.Of(new MockupNode { Type = MockupComponent.Link, Text = "Go" }).Role.Should().Be("link");
    }

    [Fact]
    public void Every_component_and_input_renders_as_the_role_the_table_holds()
    {
        var document = Read(EveryRole());
        var html = PrototypeRenderer.Render(document, []).Html;
        var checkedComponents = new HashSet<MockupComponent>();
        var checkedInputs = new HashSet<MockupInput>();

        foreach (var (node, key, parent) in Nodes(document))
        {
            Expected(node, parent).Should().Be(Rendered(html, node, key), $"node {key} ({MockupVocabulary.Name(node.Type)})");
            checkedComponents.Add(node.Type);
            if (node.Type == MockupComponent.Field)
            {
                checkedInputs.Add(node.Input ?? MockupInput.Text);
            }
        }

        checkedComponents.Should().BeEquivalentTo(Enum.GetValues<MockupComponent>());
        checkedInputs.Should().BeEquivalentTo(Enum.GetValues<MockupInput>());
    }

    [Fact]
    public void The_parts_that_are_not_a_nodes_own_element_render_as_the_table_holds()
    {
        var html = PrototypeRenderer.Render(Read(EveryRole()), []).Html;

        // The screen's title is the first element inside the screen.
        var screen = Element(html, "w");
        var title = StartTag().Match(html, screen.End);
        RoleOf(title.Groups["tag"].Value, title.Groups["attrs"].Value).Should().Be(DesignRoles.ScreenTitle.Role);

        // A text node with a heading level.
        var heading = Element(html, "h");
        RoleOf(heading.Tag, heading.Attrs).Should().Be(DesignRoles.Heading.Role);

        // The tabs: one tab per panel, each panel a tab panel.
        var tabs = Regex.Matches(html, "<button (?<attrs>[^>]*role=\"tab\"[^>]*)>");
        tabs.Should().HaveCount(2);
        foreach (Match tab in tabs)
        {
            RoleOf("button", tab.Groups["attrs"].Value).Should().Be(DesignRoles.Tab.Role);
        }

        // The column header.
        var table = Element(html, "t");
        var header = Regex.Match(html[table.End..], "<th(?<attrs>[^>]*)>");
        header.Success.Should().BeTrue();
        RoleOf("th", header.Groups["attrs"].Value).Should().Be(DesignRoles.ColumnHeader.Role);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Every_node_of_every_fixture_renders_as_the_role_the_table_holds(string name)
    {
        var document = Read(Fixture(name));
        var html = PrototypeRenderer.Render(document, []).Html;

        foreach (var (node, key, parent) in Nodes(document))
        {
            Expected(node, parent).Should().Be(Rendered(html, node, key), $"{name}: node {key}");
        }
    }

    /// <summary>The table's role for a node, a panel inside tabs being a tab panel.</summary>
    private static string Expected(MockupNode node, MockupNode? parent) =>
        parent?.Type == MockupComponent.Tabs ? DesignRoles.TabPanel.Role : DesignRoles.Of(node).Role;

    /// <summary>The role the renderer's markup gives a node: its control's for a field that is not a radio group.</summary>
    private static string Rendered(string html, MockupNode node, string key)
    {
        var element = Element(html, key);
        if (node.Type == MockupComponent.Field && node.Input != MockupInput.Radio)
        {
            var control = Regex.Match(html[element.End..], "<(?<tag>input|select|textarea)\\b(?<attrs>[^>]*)>");
            control.Success.Should().BeTrue($"field {key} renders a control");
            return RoleOf(control.Groups["tag"].Value, control.Groups["attrs"].Value);
        }

        return RoleOf(element.Tag, element.Attrs);
    }

    private sealed record Found(string Tag, string Attrs, int End);

    private static Found Element(string html, string key)
    {
        var match = Regex.Match(html, $"<(?<tag>[a-z0-9]+) data-mk-anchor=\"{Regex.Escape(key)}\"(?<attrs>[^>]*)>");
        match.Success.Should().BeTrue($"an element is anchored {key}");
        return new Found(match.Groups["tag"].Value, match.Groups["attrs"].Value, match.Index + match.Length);
    }

    /// <summary>
    /// The implicit ARIA role of the tags the renderer emits (HTML-AAM, as Playwright's role engine applies it: an
    /// explicit role wins, a section or form is a landmark only when named, password and date inputs are textboxes).
    /// </summary>
    private static string RoleOf(string tag, string attrs)
    {
        if (Attribute(attrs, "role") is { } role)
        {
            return role;
        }

        var named = Attribute(attrs, "aria-label") is not null;
        return tag switch
        {
            "section" => named ? "region" : "generic",
            "form" => named ? "form" : "generic",
            "div" or "span" or "label" => "generic",
            "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => "heading",
            "p" => "paragraph",
            "button" => "button",

            // An a is a link only with an href; without one it is generic.
            "a" => Attribute(attrs, "href") is not null ? "link" : "generic",
            "dialog" => "dialog",
            "ul" => "list",
            "table" => "table",
            "th" => "columnheader",
            "fieldset" => "group",
            "select" => "combobox",
            "textarea" => "textbox",
            "input" => Attribute(attrs, "type") switch
            {
                "search" => "searchbox",
                "number" => "spinbutton",
                "checkbox" => "checkbox",
                "radio" => "radio",
                null or "text" or "password" or "email" or "date" => "textbox",
                var type => throw new InvalidOperationException($"No role decided for <input type=\"{type}\">: decide one in DesignRoles and here."),
            },
            _ => throw new InvalidOperationException($"No role decided for <{tag}>: decide one in DesignRoles and here."),
        };
    }

    private static string? Attribute(string attrs, string name)
    {
        var match = Regex.Match(attrs, $"(?:^|\\s){Regex.Escape(name)}=\"(?<value>[^\"]*)\"");
        return match.Success ? match.Groups["value"].Value : null;
    }

    /// <summary>Every node, with its anchor spelled as <c>data-mk-anchor</c> is and its parent.</summary>
    private static IEnumerable<(MockupNode Node, string Key, MockupNode? Parent)> Nodes(MockupDocument document)
    {
        for (var s = 0; s < document.Screens.Count; s++)
        {
            foreach (var found in Nodes(document.Screens[s], $"/screens/{s}", null))
            {
                yield return found;
            }
        }
    }

    private static IEnumerable<(MockupNode Node, string Key, MockupNode? Parent)> Nodes(MockupNode node, string path, MockupNode? parent)
    {
        yield return (node, node.Id ?? path, parent);
        var children = node.Children ?? [];
        for (var i = 0; i < children.Count; i++)
        {
            foreach (var found in Nodes(children[i], $"{path}/children/{i}", node))
            {
                yield return found;
            }
        }
    }

    [GeneratedRegex("<(?<tag>[a-z0-9]+)(?<attrs>[^>]*)>")]
    private static partial Regex StartTag();
}
