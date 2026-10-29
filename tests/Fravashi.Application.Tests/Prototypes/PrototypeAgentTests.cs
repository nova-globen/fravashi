using System.Text.RegularExpressions;
using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Fravashi.Application.Prototypes;
using Fravashi.Domain.Wiki;
using Xunit;

namespace Fravashi.Application.Tests.Prototypes;

/// <summary>
/// Backlog 215 below the tools (plan @40): every overlay example in the guide is a document the reader accepts, the
/// operations an agent hands <c>prototype_adjust</c> are read by the same code as a document's overlay (same
/// operations, same refusals, paths rooted at <c>$.operations</c>), and a generated prototype's body reads back.
/// </summary>
public class PrototypeAgentTests
{
    private static readonly Regex FencedPrototype = new(
        """```prototype\r?\n(?<body>.*?)\r?\n```""", RegexOptions.Singleline | RegexOptions.Compiled);

    private static IReadOnlyList<string> GuideExamples() =>
        [.. FencedPrototype.Matches(MockupGuide.Text).Select(m => m.Groups["body"].Value)];

    private static FakeWikiLibrary Wiki() => new FakeWikiLibrary().With(12, WikiItemType.Design);

    private static string Document(string overlay) =>
        $$"""{"prototype": 1, "design": {"number": 12, "version": 1}, "overlay": {{overlay}}}""";

    // ---- the guide ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Every_overlay_example_in_the_guide_passes_the_prototype_reader_with_no_errors()
    {
        var examples = GuideExamples();
        examples.Should().HaveCount(3);

        foreach (var example in examples)
        {
            var read = await PrototypeReader.ReadAsync(example, Wiki());
            read.Errors.Should().BeEmpty($"this example should be valid:\n{example}");
            read.Document!.Overlay.Should().NotBeEmpty();
        }
    }

    [Fact]
    public async Task The_guides_overlay_examples_use_every_operation_and_both_kinds_of_anchor()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var anchors = new List<PrototypeAnchor>();
        foreach (var example in GuideExamples())
        {
            var document = (await PrototypeReader.ReadAsync(example, Wiki())).Document!;
            foreach (var operation in document.Overlay)
            {
                used.Add(operation.Op);
                anchors.AddRange(operation switch
                {
                    PrototypeSetTextOperation op => [op.Anchor],
                    PrototypeSetCssOperation op => [op.Anchor],
                    PrototypeSetAttributeOperation op => [op.Anchor],
                    PrototypeAddClassOperation op => [op.Anchor],
                    PrototypeInsertHtmlOperation op => [op.Anchor],
                    _ => [],
                });
            }
        }

        used.Should().BeEquivalentTo(PrototypeVocabulary.Operations);
        anchors.Should().Contain(a => a.Id != null).And.Contain(a => a.Path != null);
    }

    [Fact]
    public void The_guide_sends_structure_to_the_design()
    {
        MockupGuide.Text.Should().Contain("# The prototype overlay");
        MockupGuide.Text.Should().Contain("belongs in the DESIGN");
        MockupGuide.Text.Should().Contain("prototype_adjust").And.Contain("prototype_render");
    }

    // ---- the operations reader ---------------------------------------------------------------------------------

    private const string Operations = """
        [
          { "op": "setText", "anchor": { "id": "save" }, "text": "Save changes" },
          { "op": "setCss", "anchor": { "path": "/screens/0/children/0" }, "properties": { "color": "red" } },
          { "op": "pageCss", "rule": ".x { color: blue; }" },
          { "op": "insertHtml", "anchor": { "id": "save" }, "placement": "after", "html": "<em>hint</em>" }
        ]
        """;

    [Fact]
    public async Task Operations_read_on_their_own_are_exactly_what_the_same_list_reads_as_in_a_document()
    {
        var alone = PrototypeReader.ReadOperations(Operations);
        var inDocument = await PrototypeReader.ReadAsync(Document(Operations), Wiki());

        alone.IsValid.Should().BeTrue(string.Join("\n", alone.Errors));
        inDocument.IsValid.Should().BeTrue(string.Join("\n", inDocument.Errors));

        var design = new PrototypeDesignRef(12, 1);
        PrototypeWriter.Write(new PrototypeDocument(1, design, alone.Operations!))
            .Should().Be(PrototypeWriter.Write(inDocument.Document!));
    }

    [Theory]
    [InlineData("""[{ "op": "insertHtml", "anchor": { "id": "save" }, "placement": "after", "html": "<img src=x onerror=alert(1)>" }]""", "html")]
    [InlineData("""[{ "op": "insertHtml", "anchor": { "id": "save" }, "placement": "inside", "html": "<script>alert(1)</script>" }]""", "html")]
    [InlineData("""[{ "op": "setAttribute", "anchor": { "id": "save" }, "name": "onclick", "value": "x" }]""", "name")]
    [InlineData("""[{ "op": "setAttribute", "anchor": { "id": "save" }, "name": "data-mk-on", "value": "x" }]""", "name")]
    [InlineData("""[{ "op": "pageCss", "rule": "</style><script>alert(1)</script>" }]""", "rule")]
    public async Task A_guarded_operation_is_refused_with_the_documents_own_sentence_at_its_operations_path(string list, string field)
    {
        var alone = PrototypeReader.ReadOperations(list);
        var inDocument = await PrototypeReader.ReadAsync(Document(list), Wiki());

        alone.IsValid.Should().BeFalse();
        var error = alone.Errors.Should().ContainSingle().Subject;
        error.Path.Should().Be($"$.operations[0].{field}");
        error.Message.Should().Be(inDocument.Errors.Should().ContainSingle().Subject.Message);
    }

    [Theory]
    [InlineData("[]", "at least one operation")]
    [InlineData("""{ "op": "setText" }""", "\"operations\" is a list of operations")]
    [InlineData("[{ \"op\": \"remove\" }]", "is not an operation")]
    [InlineData("[not json", "not valid JSON")]
    public void A_list_that_is_empty_or_not_a_list_of_operations_is_refused(string list, string says)
    {
        var read = PrototypeReader.ReadOperations(list);

        read.IsValid.Should().BeFalse();
        read.Errors.Should().Contain(e => e.Message.Contains(says, StringComparison.Ordinal));
        read.Errors.Should().OnlyContain(e => e.Path.StartsWith(PrototypeReader.OperationsPath, StringComparison.Ordinal));
    }

    // ---- a generated body --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_generated_prototype_body_cites_its_design_and_reads_back_as_the_same_document()
    {
        var document = new PrototypeDocument(PrototypeVocabulary.Version, new PrototypeDesignRef(12, 1), []);

        var body = PrototypeBody.NewBody(document);
        var read = await PrototypeBody.ReadAsync(body, Wiki());

        body.Should().StartWith("A prototype of @12, generated from version 1 of the design.\n\n```prototype\n");
        read.IsValid.Should().BeTrue(string.Join("\n", read.Errors));
        PrototypeWriter.Write(read.Document!).Should().Be(PrototypeWriter.Write(document));
    }
}
