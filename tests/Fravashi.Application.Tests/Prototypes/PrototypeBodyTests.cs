using AwesomeAssertions;
using Fravashi.Application.Prototypes;
using Fravashi.Domain.Wiki;
using Xunit;

namespace Fravashi.Application.Tests.Prototypes;

/// <summary>
/// Backlog 211: how a prototype is stored, mirroring <see cref="Mockups.DesignMockupTests"/> for the same
/// reason a design's own test does: where the one fenced block is found, and what is said when there is not
/// exactly one.
/// </summary>
public class PrototypeBodyTests
{
    private static FakeWikiLibrary Wiki() => new FakeWikiLibrary().With(1, WikiItemType.Design, version: 1);

    private const string Valid = """{"prototype": 1, "design": {"number": 1, "version": 1}, "overlay": []}""";

    [Fact]
    public async Task One_block_between_prose_is_found_exactly_and_its_document_read()
    {
        var body = $"A generated page.\n\nWhat it is for.\n\n```prototype\n{Valid}\n```\n\nAfter.\n";

        var read = await PrototypeBody.ReadAsync(body, Wiki());

        read.IsValid.Should().BeTrue();
        read.Errors.Should().BeEmpty();
        var block = read.Block!;
        block.Json.Should().Be(Valid + "\n");
        block.Line.Should().Be(5);
        block.Closed.Should().BeTrue();
        body[block.Start..(block.Start + block.Length)].Should().Be($"```prototype\n{Valid}\n```\n");
        read.Document!.Overlay.Should().BeEmpty();
    }

    [Fact]
    public async Task No_block_says_there_is_none()
    {
        var read = await PrototypeBody.ReadAsync("Just prose.\n", Wiki());

        read.IsValid.Should().BeFalse();
        read.Errors.Should().ContainSingle();
        read.Errors[0].Path.Should().Be(PrototypeBody.BodyPath);
        read.Errors[0].Message.Should().Contain("no prototype block");
        read.Block.Should().BeNull();
    }

    [Fact]
    public async Task Two_blocks_are_refused_naming_their_lines_and_neither_is_read()
    {
        var body = $"```prototype\n{Valid}\n```\n\n```prototype\n{Valid}\n```\n";

        var read = await PrototypeBody.ReadAsync(body, Wiki());

        read.IsValid.Should().BeFalse();
        read.Errors.Should().ContainSingle();
        read.Errors[0].Message.Should().Contain("2 prototype blocks");
        read.Document.Should().BeNull();
        read.Blocks.Should().HaveCount(2);
    }

    [Fact]
    public async Task An_unclosed_block_is_read_to_the_end_and_says_so()
    {
        var body = $"```prototype\n{Valid}\n";

        var read = await PrototypeBody.ReadAsync(body, Wiki());

        read.Errors.Should().ContainSingle(e => e.Message.Contains("never closed"));
        read.Block!.Closed.Should().BeFalse();
    }

    [Fact]
    public async Task A_broken_document_carries_the_readers_errors_verbatim_with_their_paths()
    {
        const string Broken = """{"prototype": 1, "design": {"number": 999, "version": 1}, "overlay": []}""";

        var read = await PrototypeBody.ReadAsync($"```prototype\n{Broken}\n```\n", Wiki());

        read.IsValid.Should().BeFalse();
        read.Errors.Should().Equal(await ReaderErrorsAsync(Broken));
        read.Errors.Should().OnlyContain(e => e.Path.StartsWith('$'));
    }

    private static async Task<IReadOnlyList<PrototypeError>> ReaderErrorsAsync(string json) =>
        (await PrototypeReader.ReadAsync(json, Wiki())).Errors;

    [Fact]
    public async Task A_fence_inside_another_fence_is_content_not_a_second_block()
    {
        var body = $"````markdown\n```prototype\n{Valid}\n```\n````\n";

        var read = await PrototypeBody.ReadAsync(body, Wiki());

        read.Blocks.Should().BeEmpty();
    }
}
