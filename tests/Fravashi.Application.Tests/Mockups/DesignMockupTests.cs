using AwesomeAssertions;
using Fravashi.Application.Mockups;
using Fravashi.Domain.Wiki;
using Xunit;

namespace Fravashi.Application.Tests.Mockups;

/// <summary>
/// Backlog 193: how a mockup is stored. A design document's markdown body carries exactly ONE fenced block whose
/// info string is <c>mockup</c>; these tests hold where that block is found, what is said when there is not
/// exactly one, and the two risks the plan named before choosing a fenced block (references and publishing).
/// </summary>
public class DesignMockupTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Mockups", "Fixtures", name));

    private const string Valid = """{"mockup": 1, "screens": [{"type": "window", "id": "home", "text": "Home"}]}""";

    [Fact]
    public void One_block_between_prose_is_found_exactly_and_its_tree_read()
    {
        var body = $"# Sign in\n\nWhat it is for.\n\n```mockup\n{Valid}\n```\n\nAfter.\n";

        var read = DesignMockup.Read(body);

        read.IsValid.Should().BeTrue();
        read.Errors.Should().BeEmpty();
        var block = read.Block!;
        block.Json.Should().Be(Valid + "\n");
        block.Line.Should().Be(5);
        block.Closed.Should().BeTrue();
        body[block.Start..(block.Start + block.Length)].Should().Be($"```mockup\n{Valid}\n```\n");
        read.Tree!.Document!.Screens.Should().ContainSingle();
    }

    [Fact]
    public void The_agent_authored_fixture_reads_the_same_inside_a_design_as_on_its_own()
    {
        var tree = Fixture("team-directory.attempt-3.json");

        var read = DesignMockup.Read($"The team directory.\n\n```mockup\n{tree}\n```\n");

        read.IsValid.Should().BeTrue();
        MockupWriter.Write(read.Tree!.Document!).Should().Be(MockupWriter.Write(MockupReader.Read(tree).Document!));
    }

    [Fact]
    public void A_refused_tree_carries_the_readers_errors_verbatim_with_their_paths()
    {
        var tree = Fixture("team-directory.attempt-1.json");

        var read = DesignMockup.Read($"```mockup\n{tree}\n```\n");

        read.IsValid.Should().BeFalse();
        read.Errors.Should().Equal(MockupReader.Read(tree).Errors);
        read.Errors.Should().OnlyContain(e => e.Path.StartsWith('$'));
    }

    [Fact]
    public void A_body_with_no_block_says_so_and_how_to_write_one()
    {
        var read = DesignMockup.Read("Only prose.\n\n```json\n{\"mockup\": 1}\n```\n");

        read.Blocks.Should().BeEmpty();
        read.Tree.Should().BeNull();
        var error = read.Errors.Should().ContainSingle().Subject;
        error.Path.Should().Be(DesignMockup.BodyPath);
        error.Message.Should().Contain("no mockup block").And.Contain("```mockup");
    }

    [Fact]
    public void An_empty_body_has_no_block()
    {
        DesignMockup.Read(string.Empty).Errors.Should().ContainSingle()
            .Which.Message.Should().Contain("no mockup block");
        DesignMockup.Read(null).Blocks.Should().BeEmpty();
    }

    [Fact]
    public void Two_blocks_are_both_found_neither_is_read_and_their_lines_are_named()
    {
        var read = DesignMockup.Read($"```mockup\n{Valid}\n```\n\nText.\n\n~~~mockup\n{Valid}\n~~~\n");

        read.Blocks.Should().HaveCount(2);
        read.Block.Should().BeNull();
        read.Tree.Should().BeNull();
        var error = read.Errors.Should().ContainSingle().Subject;
        error.Path.Should().Be(DesignMockup.BodyPath);
        error.Message.Should().Contain("2 mockup blocks (at lines 1, 7)");
    }

    [Fact]
    public void A_block_shown_inside_a_longer_fence_is_an_example_not_a_second_tree()
    {
        var body = $"How to write one:\n\n````markdown\n```mockup\n{{}}\n```\n````\n\n```mockup\n{Valid}\n```\n";

        var read = DesignMockup.Read(body);

        read.Blocks.Should().ContainSingle().Which.Json.Should().Be(Valid + "\n");
        read.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("~~~mockup\n{0}\n~~~\n")]
    [InlineData("   ```mockup\n{0}\n   ```\n")]
    [InlineData("````mockup title\n{0}\n`````\n")]
    [InlineData("```mockup\r\n{0}\r\n```\r\n")]
    public void Every_fence_commonmark_accepts_is_found(string shape)
    {
        var read = DesignMockup.Read(string.Format(System.Globalization.CultureInfo.InvariantCulture, shape, Valid));

        read.Errors.Should().BeEmpty();
        read.Block.Should().NotBeNull();
    }

    [Theory]
    [InlineData("    ```mockup\n{}\n    ```\n")]
    [InlineData("``mockup\n{}\n``\n")]
    [InlineData("```Mockup\n{}\n```\n")]
    [InlineData("```mockups\n{}\n```\n")]
    [InlineData("> ```mockup\n> {}\n> ```\n")]
    public void What_is_not_a_top_level_mockup_fence_is_not_found(string body) =>
        DesignMockup.FindBlocks(body).Should().BeEmpty();

    [Fact]
    public void A_shorter_or_different_fence_does_not_close_a_block()
    {
        var body = $"````mockup\n{Valid}\n```\n~~~~\n````\n";

        var block = DesignMockup.FindBlocks(body).Should().ContainSingle().Subject;

        block.Closed.Should().BeTrue();
        block.Json.Should().Be($"{Valid}\n```\n~~~~\n");
    }

    [Fact]
    public void A_block_never_closed_runs_to_the_end_is_said_and_its_tree_still_read()
    {
        var read = DesignMockup.Read($"Text.\n\n```mockup\n{Valid}\n");

        read.Block!.Closed.Should().BeFalse();
        read.Errors.Should().ContainSingle().Which.Message.Should().Contain("line 3 is never closed");
        read.Tree!.IsValid.Should().BeTrue();
        read.IsValid.Should().BeFalse();
    }

    // ---- the two risks the plan checked before choosing a fenced block ----------------------------------

    [Fact]
    public void A_hash_number_inside_the_tree_is_not_a_reference()
    {
        var tree = """{"mockup": 1, "screens": [{"type": "window", "id": "home", "text": "Ticket #3 and @4 and !5"}]}""";

        var references = WikiReferences.Find($"See #7.\n\n```mockup\n{tree}\n```\n");

        references.Select(r => r.Reference.Text).Should().Equal("#7");
    }

    [Fact]
    public void Publishing_carries_the_tree_byte_for_byte_and_reports_that_the_info_string_is_not_carried()
    {
        var tree = """{"mockup": 1, "title": "a < b && c > d ]]> e", "screens": [{"type": "window", "id": "home"}]}""";

        var storage = WikiFormatConversion.ToStorage($"Prose.\n\n```mockup\n{tree}\n```\n");

        storage.Losses.Should().ContainSingle().Which.Detail.Should().Contain("'mockup'");

        // Back from the site the JSON is intact, but the fence has no info string any more, so a design taken
        // back from the site has no mockup block. Recorded on #193: it is said on the page, never silent.
        var back = WikiFormatConversion.ToMarkdown(storage.Text);
        back.Text.Should().Contain(tree);
        DesignMockup.Read(back.Text).Errors.Should().ContainSingle()
            .Which.Message.Should().Contain("no mockup block");
    }
}
