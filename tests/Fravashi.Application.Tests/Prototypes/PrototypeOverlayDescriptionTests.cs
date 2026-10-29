using AwesomeAssertions;
using Fravashi.Application.Prototypes;
using Xunit;

namespace Fravashi.Application.Tests.Prototypes;

/// <summary>Backlog 211: "lists the overlay in words", since nothing renders yet.</summary>
public class PrototypeOverlayDescriptionTests
{
    [Fact]
    public void Each_operation_kind_is_described_naming_its_anchor()
    {
        PrototypeOverlayDescription.Describe(new PrototypeSetTextOperation(new PrototypeAnchor("save", null), "Saving..."))
            .Should().Be("Sets the text of \"save\" to \"Saving...\".");

        PrototypeOverlayDescription.Describe(new PrototypeAddClassOperation(new PrototypeAnchor("save", null), "is-primary"))
            .Should().Be("Adds the class \"is-primary\" to \"save\".");

        PrototypeOverlayDescription.Describe(new PrototypePageCssOperation("body { color: red; }"))
            .Should().Be("Adds a page-level rule: body { color: red; }");

        PrototypeOverlayDescription.Describe(new PrototypeInsertHtmlOperation(
                new PrototypeAnchor(null, "/screens/0"), PrototypePlacement.After, "<small>Draft</small>"))
            .Should().Be("Inserts HTML after the node at /screens/0.");
    }

    [Fact]
    public void SetCss_lists_its_properties_in_ordinal_order()
    {
        var properties = new Dictionary<string, string> { ["z-index"] = "1", ["color"] = "red" };

        PrototypeOverlayDescription.Describe(new PrototypeSetCssOperation(new PrototypeAnchor("save", null), properties))
            .Should().Be("Sets color: red, z-index: 1 on \"save\".");
    }
}
