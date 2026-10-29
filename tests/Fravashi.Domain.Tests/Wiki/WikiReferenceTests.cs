using AwesomeAssertions;
using Fravashi.Domain.Wiki;
using Xunit;

namespace Fravashi.Domain.Tests.Wiki;

/// <summary>
/// Backlog 39's reference syntax: <c>#432</c> a work item, <c>!6243</c> a pull request, <c>@7354</c> a wiki
/// item. This is the one place the syntax is decided, so the cases that decide it live here: what counts, what
/// deliberately does not, and the two places a reference must be invisible (code, and a link destination).
/// </summary>
public class WikiReferenceTests
{
    private static IReadOnlyList<WikiReference> Refs(string text) =>
        [.. WikiReferences.Find(text).Select(f => f.Reference)];

    [Theory]
    [InlineData("#432", WikiReferenceKind.WorkItem, 432)]
    [InlineData("!6243", WikiReferenceKind.PullRequest, 6243)]
    [InlineData("@7354", WikiReferenceKind.WikiItem, 7354)]
    public void Each_marker_reads_as_its_own_kind(string text, WikiReferenceKind kind, int number)
    {
        Refs(text).Should().ContainSingle().Which.Should().Be(new WikiReference(kind, number));
    }

    [Fact]
    public void A_reference_reads_mid_sentence_and_keeps_its_place()
    {
        var found = WikiReferences.Find("This closes #432 and supersedes @12, see !7.");

        found.Select(f => f.Reference).Should().Equal(
            new WikiReference(WikiReferenceKind.WorkItem, 432),
            new WikiReference(WikiReferenceKind.WikiItem, 12),
            new WikiReference(WikiReferenceKind.PullRequest, 7));

        var first = found[0];
        "This closes #432 and supersedes @12, see !7.".Substring(first.Start, first.Length).Should().Be("#432");
    }

    [Theory]
    [InlineData("abc#4")]
    [InlineData("someone@7354")]
    [InlineData("v1#4")]
    [InlineData("_#4")]
    public void A_marker_glued_to_a_word_is_not_a_reference(string text)
    {
        // An email address is the case that matters most: "someone@7354" must never become a wiki link.
        Refs(text).Should().BeEmpty();
    }

    [Theory]
    [InlineData("#4abc")]
    [InlineData("#4_5")]
    public void Digits_that_run_into_a_word_are_not_a_reference(string text) => Refs(text).Should().BeEmpty();

    [Theory]
    [InlineData("# A heading")]
    [InlineData("#")]
    [InlineData("#abc")]
    [InlineData("!")]
    [InlineData("@")]
    public void A_marker_with_no_digits_is_not_a_reference(string text) => Refs(text).Should().BeEmpty();

    [Fact]
    public void A_heading_is_never_a_reference_because_a_heading_has_a_space()
    {
        Refs("# 432 things to do\n\nAnd #432 is the item.").Should()
            .ContainSingle().Which.Number.Should().Be(432);
    }

    [Theory]
    [InlineData("##4")]
    [InlineData("#@4")]
    [InlineData("!#4")]
    public void A_marker_directly_after_another_marker_is_not_a_reference(string text) =>
        Refs(text).Should().BeEmpty();

    [Fact]
    public void Zero_and_a_number_too_large_to_have_been_allocated_are_not_references()
    {
        Refs("#0 and #99999999999999 and #-4").Should().BeEmpty();
    }

    // ---- the two places a reference has to be invisible --------------------------------------

    [Fact]
    public void Nothing_in_an_inline_code_span_is_a_reference()
    {
        // A document explaining this syntax has to be able to show it.
        Refs("Write `#432` to point at it.").Should().BeEmpty();
    }

    [Fact]
    public void Nothing_in_a_fenced_block_is_a_reference()
    {
        var text = "Before #1\n```\n#432\n@12\n```\nAfter #2";

        Refs(text).Select(r => r.Number).Should().Equal(1, 2);
    }

    [Fact]
    public void A_tilde_fence_and_a_longer_fence_are_both_understood()
    {
        Refs("~~~\n#432\n~~~\n#2").Select(r => r.Number).Should().Equal(2);
        Refs("````\n```\n#432\n````\n#2").Select(r => r.Number).Should().Equal(2);
    }

    [Fact]
    public void An_unclosed_fence_swallows_the_rest_of_the_document()
    {
        // Which is what every markdown parser does with it, so the scan must agree or the rendering and the
        // stored links would disagree about the same file.
        Refs("#1\n```\n#432\nstill inside").Select(r => r.Number).Should().Equal(1);
    }

    [Fact]
    public void An_unclosed_inline_span_does_not_swallow_the_rest_of_the_line()
    {
        Refs("a ` b #432").Should().ContainSingle().Which.Number.Should().Be(432);
    }

    [Fact]
    public void A_fragment_in_a_link_destination_is_not_a_work_item()
    {
        Refs("[the section](/docs/plan#432)").Should().BeEmpty();
    }

    [Fact]
    public void A_reference_in_a_links_text_still_counts()
    {
        Refs("[see #432](/board)").Should().ContainSingle().Which.Number.Should().Be(432);
    }

    [Fact]
    public void A_reference_after_a_link_is_still_found()
    {
        Refs("[x](/a) then #432").Should().ContainSingle().Which.Number.Should().Be(432);
    }

    // ---- writing them back out ---------------------------------------------------------------

    [Fact]
    public void A_reference_renders_as_exactly_what_would_parse_back_to_it()
    {
        foreach (var kind in Enum.GetValues<WikiReferenceKind>())
        {
            var reference = new WikiReference(kind, 99);

            Refs(reference.Text).Should().ContainSingle().Which.Should().Be(reference);
        }
    }

    [Fact]
    public void Every_kind_has_a_marker_and_every_marker_has_a_kind()
    {
        foreach (var kind in Enum.GetValues<WikiReferenceKind>())
        {
            WikiReference.KindFor(WikiReference.MarkerFor(kind)).Should().Be(kind);
        }

        WikiReference.KindFor('x').Should().BeNull();
    }
}
