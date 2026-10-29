using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Fravashi.Application.Prototypes;
using Fravashi.Application.Verification;
using Xunit;

namespace Fravashi.Application.Tests.Prototypes;

/// <summary>
/// Backlog 239 (plan @51): a button the design names only by its accessibility label shows that label as a muted
/// placeholder instead of an empty box, while its accessible name stays exactly the design's. How a real browser names
/// it is <c>PrototypePlaceholderLiveTests</c>'s, and #223's live tier still matches every fixture's prototype.
/// </summary>
public class PrototypePlaceholderTests
{
    private const string Placeholder = "<span class=\"mk-placeholder\" aria-hidden=\"true\">";

    private static MockupDocument Valid(string json)
    {
        var read = MockupReader.Read(json);
        read.IsValid.Should().BeTrue(string.Join("\n", read.Errors));
        return read.Document!;
    }

    private static MockupDocument OneButton(string button) => Valid($$"""
        {"mockup": 1, "screens": [{"type": "window", "id": "home", "children": [{{button}}]}]}
        """);

    [Fact]
    public void A_button_named_only_by_its_label_shows_the_label_as_a_hidden_from_the_tree_placeholder()
    {
        var html = PrototypeRenderer.Render(OneButton("""{"type": "button", "id": "refresh", "a11y": {"label": "Refresh"}}"""), []).Html;

        html.Should().Contain(
            "<button data-mk-anchor=\"refresh\" class=\"mk-button\" type=\"button\" aria-label=\"Refresh\">"
            + Placeholder + "Refresh</span></button>");
        html.Should().Contain(":where(.mk-placeholder) { color: #5a5a63; font-style: italic; }",
            "a look rule, at zero specificity like the rest of the look (backlog 237)");
    }

    [Fact]
    public void A_button_with_text_of_its_own_or_from_the_overlay_shows_no_placeholder()
    {
        var own = PrototypeRenderer.Render(
            OneButton("""{"type": "button", "id": "save", "text": "Save", "a11y": {"label": "Save the person"}}"""), []).Html;
        own.Should().Contain("aria-label=\"Save the person\">Save</button>");
        own.Should().NotContain(Placeholder);

        var overlaid = PrototypeRenderer.Render(
            OneButton("""{"type": "button", "id": "refresh", "a11y": {"label": "Refresh"}}"""),
            [new PrototypeSetTextOperation(new PrototypeAnchor("refresh", null), "Reload")]).Html;
        overlaid.Should().Contain("aria-label=\"Refresh\">Reload</button>");
        overlaid.Should().NotContain(Placeholder);

        var variant = PrototypeRenderer.Render(
            OneButton("""{"type": "button", "id": "go", "a11y": {"label": "Go"}, "states": {"loading": {"text": "Going"}}}"""), []).Html;
        variant.Should().NotContain(Placeholder, "a state's text is visible text, so the button is not empty");
    }

    [Fact]
    public void The_placeholder_says_the_name_the_page_really_has_after_the_overlay()
    {
        var html = PrototypeRenderer.Render(
            OneButton("""{"type": "button", "id": "refresh", "a11y": {"label": "Refresh"}}"""),
            [new PrototypeSetAttributeOperation(new PrototypeAnchor("refresh", null), "aria-label", "Reload all")]).Html;

        html.Should().Contain("aria-label=\"Reload all\">" + Placeholder + "Reload all</span></button>");
    }

    [Fact]
    public void The_structural_expectations_of_a_label_only_button_are_unchanged()
    {
        // #221 reads the design, never the page: every-feature's "help" is a label-only button, asked by role and
        // name, and the driver opens "about" by that same name. The placeholder must leave the page answering to it.
        var document = Valid(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mockups", "Fixtures", "every-feature.json")));
        var set = DesignExpectations.For(document);

        var help = set.Screens.SelectMany(s => s.Phases).SelectMany(p => p.Views).SelectMany(v => v.Expectations)
            .Where(e => e.Anchor == "help").Distinct().ToList();
        help.Should().NotBeEmpty();
        help.Should().AllSatisfy(e =>
        {
            e.Role.Should().Be("button");
            e.Name.Should().Be("Help");
        });
        set.Screens.SelectMany(s => s.Phases).SelectMany(p => p.Steps).Where(s => s.Anchor == "help")
            .Should().NotBeEmpty().And.AllSatisfy(s => s.Name.Should().Be("Help"));

        var html = PrototypeRenderer.Render(document, []).Html;
        html.Should().Contain("aria-label=\"Help\" data-mk-on=\"{&quot;click&quot;:{&quot;open&quot;:&quot;about&quot;}}\">"
            + Placeholder + "Help</span></button>");
    }
}
