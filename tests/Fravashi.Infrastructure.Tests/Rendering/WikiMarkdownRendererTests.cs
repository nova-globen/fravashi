using AwesomeAssertions;
using Fravashi.Application.Wiki;
using Fravashi.Domain.Wiki;
using Fravashi.Infrastructure.Rendering;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Rendering;

/// <summary>
/// Backlog 39's acceptance about rendering: every reference resolves to a real working link into the
/// corresponding studio surface, and one that points at nothing is VISIBLY broken rather than quietly
/// rendered as ordinary text, which would tell the reader nothing was meant by it.
/// </summary>
public class WikiMarkdownRendererTests
{
    private readonly WikiMarkdownRenderer _renderer = new();

    private static WikiReferenceMap Known(params WikiReference[] references)
    {
        var map = new WikiReferenceMap();
        foreach (var reference in references)
        {
            map.Add(reference, null);
        }

        return map;
    }

    [Theory]
    [InlineData(WikiReferenceKind.WorkItem, 432, "/workitems/432")]
    [InlineData(WikiReferenceKind.WikiItem, 7354, "/wiki/7354")]
    [InlineData(WikiReferenceKind.PullRequest, 6243, "/git/pr/6243")]
    public void Each_kind_links_into_the_studio_surface_that_shows_it(
        WikiReferenceKind kind, int number, string route)
    {
        var reference = new WikiReference(kind, number);

        var html = _renderer.ToHtml("See " + reference.Text + " for this.", Known(reference));

        html.Should().Contain($"href=\"{route}\"").And.Contain(reference.Text);
        html.Should().Contain(WikiMarkdownRenderer.ResolvedClass);
    }

    [Fact]
    public void A_reference_to_something_that_does_not_exist_is_visibly_broken()
    {
        var html = _renderer.ToHtml("See #432 for this.", WikiReferenceMap.Empty);

        html.Should().Contain(WikiMarkdownRenderer.BrokenClass).And.Contain("#432");
        html.Should().NotContain("href=\"/workitems/432\"", "a link that answers nothing is worse than a mark");
    }

    [Fact]
    public void A_resolved_reference_carries_what_the_thing_is_called()
    {
        var map = new WikiReferenceMap().Add(new WikiReference(WikiReferenceKind.WorkItem, 44), "Gate G1");

        var html = _renderer.ToHtml("#44", map);

        html.Should().Contain("title=\"Gate G1\"");
    }

    [Fact]
    public void A_title_carrying_a_quote_cannot_end_the_attribute_early()
    {
        var map = new WikiReferenceMap().Add(
            new WikiReference(WikiReferenceKind.WorkItem, 44), "The \"quoted\" one");

        var html = _renderer.ToHtml("#44", map);

        html.Should().Contain("href=\"/workitems/44\"");
        html.Should().NotContain("quoted\" one\"");
    }

    [Fact]
    public void A_reference_inside_code_is_left_exactly_as_written()
    {
        var html = _renderer.ToHtml(
            "Write `#432` to point at it.", Known(new WikiReference(WikiReferenceKind.WorkItem, 432)));

        html.Should().Contain("#432");
        html.Should().NotContain("href=\"/workitems/432\"");
        html.Should().NotContain(WikiMarkdownRenderer.BrokenClass);
    }

    [Fact]
    public void A_fenced_block_is_left_exactly_as_written()
    {
        var html = _renderer.ToHtml(
            "```\n#432\n```", Known(new WikiReference(WikiReferenceKind.WorkItem, 432)));

        html.Should().Contain("#432").And.NotContain("href=\"/workitems/432\"");
    }

    [Fact]
    public void An_email_address_is_not_turned_into_a_wiki_link()
    {
        var html = _renderer.ToHtml("Ask someone@7354 about it.", Known(new WikiReference(WikiReferenceKind.WikiItem, 7354)));

        html.Should().NotContain("href=\"/wiki/7354\"");
    }

    [Fact]
    public void Raw_html_in_a_body_cannot_put_markup_into_the_studio()
    {
        // A wiki page is authored content, and the page it renders into is the studio's own.
        var html = _renderer.ToHtml("<script>alert(1)</script>", WikiReferenceMap.Empty);

        html.Should().NotContain("<script>");
    }

    [Fact]
    public void The_rest_of_the_document_is_untouched_around_a_reference()
    {
        var rewritten = WikiMarkdownRenderer.Rewrite(
            "# Heading\n\nBefore #1 after.\n", Known(new WikiReference(WikiReferenceKind.WorkItem, 1)));

        rewritten.Should().StartWith("# Heading\n\nBefore [#1](/workitems/1)");
        rewritten.Should().EndWith(" after.\n");
    }

    [Fact]
    public void A_document_with_no_references_is_returned_unchanged()
    {
        const string text = "Just prose, with a # and an @ and a ! in it.";

        WikiMarkdownRenderer.Rewrite(text, WikiReferenceMap.Empty).Should().Be(text);
    }

    [Fact]
    public void Several_references_on_one_line_each_get_their_own_link()
    {
        var html = _renderer.ToHtml(
            "#1 and #2 and #3",
            Known(
                new WikiReference(WikiReferenceKind.WorkItem, 1),
                new WikiReference(WikiReferenceKind.WorkItem, 2),
                new WikiReference(WikiReferenceKind.WorkItem, 3)));

        html.Should().Contain("/workitems/1").And.Contain("/workitems/2").And.Contain("/workitems/3");
    }

    [Fact]
    public void A_mix_of_resolved_and_broken_shows_each_for_what_it_is()
    {
        var html = _renderer.ToHtml("#1 and #2", Known(new WikiReference(WikiReferenceKind.WorkItem, 1)));

        html.Should().Contain("href=\"/workitems/1\"");
        html.Should().Contain(WikiMarkdownRenderer.BrokenClass);
    }
}
