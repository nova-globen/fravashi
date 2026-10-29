using AwesomeAssertions;
using Fravashi.Application.Prototypes;
using Fravashi.Domain.Wiki;
using Xunit;

namespace Fravashi.Application.Tests.Prototypes;

/// <summary>
/// Backlog 211's round-trip acceptance: read, write, read gives the same document, and writing is canonical
/// (a fixed field order, and a <c>setCss</c> operation's properties in ordinal order, whatever order they
/// were written in).
/// </summary>
public class PrototypeWriterTests
{
    private static FakeWikiLibrary Wiki() => new FakeWikiLibrary().With(7, WikiItemType.Design, version: 2);

    [Fact]
    public void Write_ends_in_one_trailing_newline()
    {
        var document = new PrototypeDocument(1, new PrototypeDesignRef(7, 2), []);

        PrototypeWriter.Write(document).Should().EndWith("}\n").And.NotEndWith("}\n\n");
    }

    [Fact]
    public void A_setCss_operations_properties_are_written_in_ordinal_order_whatever_order_they_were_given()
    {
        var properties = new Dictionary<string, string> { ["z-index"] = "1", ["color"] = "red", ["display"] = "none" };
        var document = new PrototypeDocument(
            1, new PrototypeDesignRef(7, 2), [new PrototypeSetCssOperation(new PrototypeAnchor("a", null), properties)]);

        var json = PrototypeWriter.Write(document);
        var colorAt = json.IndexOf("\"color\"", StringComparison.Ordinal);
        var displayAt = json.IndexOf("\"display\"", StringComparison.Ordinal);
        var zIndexAt = json.IndexOf("\"z-index\"", StringComparison.Ordinal);

        (colorAt < displayAt).Should().BeTrue();
        (displayAt < zIndexAt).Should().BeTrue();
    }

    [Fact]
    public async Task Read_write_read_gives_the_same_document()
    {
        const string Source = """
            {
              "prototype": 1,
              "design": {"number": 7, "version": 2},
              "overlay": [
                {"op": "setText", "anchor": {"id": "save"}, "text": "Saving..."},
                {"op": "setCss", "anchor": {"path": "/screens/0/children/2"}, "properties": {"color": "red"}},
                {"op": "setAttribute", "anchor": {"id": "save"}, "name": "aria-label", "value": "Save changes"},
                {"op": "addClass", "anchor": {"id": "save"}, "class": "is-primary"},
                {"op": "pageCss", "rule": "body { font-family: sans-serif; }"},
                {"op": "insertHtml", "anchor": {"id": "save"}, "placement": "after", "html": "<small>Draft</small>"}
              ]
            }
            """;

        var wiki = Wiki();
        var first = await PrototypeReader.ReadAsync(Source, wiki);
        first.IsValid.Should().BeTrue();

        var written = PrototypeWriter.Write(first.Document!);
        var second = await PrototypeReader.ReadAsync(written, wiki);

        second.IsValid.Should().BeTrue();
        PrototypeWriter.Write(second.Document!).Should().Be(written);
    }

    [Fact]
    public void Writing_is_canonical_regardless_of_field_order_at_the_root()
    {
        var a = new PrototypeDocument(1, new PrototypeDesignRef(7, 2), []);
        var b = new PrototypeDocument(1, new PrototypeDesignRef(7, 2), []);

        PrototypeWriter.Write(a).Should().Be(PrototypeWriter.Write(b));
    }
}
