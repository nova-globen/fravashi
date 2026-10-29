using Fravashi.Domain.Wiki;
using Fravashi.Infrastructure.Rendering;
using Fravashi.Web.Components.Wiki;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Work item #365: the rules the wiki page uses to help a person find their way, each on its own: the list grouped by
/// kind, a document's headings, and file paths made into previewable links.
/// </summary>
public sealed class WikiReadingTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static WikiItem Document(int number, WikiItemType type, int minutesAgo) =>
        WikiItem.Create(new WikiItemState
        {
            Id = "w" + number,
            Number = number,
            Type = type,
            Title = "Document " + number,
            Slug = "document-" + number,
            CreatedUtc = Noon.AddDays(-1),
            UpdatedUtc = Noon.AddMinutes(-minutesAgo),
        }).Value;

    [Fact]
    public void Documents_are_grouped_by_kind_in_the_types_order_with_the_most_recently_changed_first()
    {
        var groups = WikiReading.Group(
        [
            Document(1, WikiItemType.Evidence, 50),
            Document(2, WikiItemType.Plan, 30),
            Document(3, WikiItemType.Plan, 5),
            Document(4, WikiItemType.Design, 1),
            Document(5, WikiItemType.Plan, 60),
        ]);

        Assert.Equal([WikiItemType.Plan, WikiItemType.Evidence, WikiItemType.Design], groups.Select(g => g.Type).ToArray());
        Assert.Equal([3, 2, 5], groups[0].Documents.Select(d => d.Number).ToArray());
        Assert.Equal([1], groups[1].Documents.Select(d => d.Number).ToArray());
    }

    [Fact]
    public void Recent_is_the_most_recently_changed_across_every_kind()
    {
        var recent = WikiReading.Recent(
            [Document(1, WikiItemType.Evidence, 50), Document(2, WikiItemType.Plan, 5), Document(3, WikiItemType.Design, 20)], 2);

        Assert.Equal([2, 3], recent.Select(d => d.Number).ToArray());
    }

    [Fact]
    public void Headings_are_read_from_the_rendered_body_with_the_ids_the_renderer_gave_them()
    {
        var html = new WikiMarkdownRenderer().ToHtml("# Why\n\nText.\n\n## The *first* step\n\nMore.\n\n### Edge & cases\n", null);

        var headings = WikiReading.Headings(html);

        Assert.Equal(["Why", "The first step", "Edge & cases"], headings.Select(h => h.Text).ToArray());
        Assert.Equal([1, 2, 3], headings.Select(h => h.Level).ToArray());
        Assert.All(headings, h => Assert.Contains($"id=\"{h.Id}\"", html, StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_path_in_a_code_span_becomes_a_preview_link_and_nothing_else_does()
    {
        var html = new WikiMarkdownRenderer().ToHtml(
            "Read `src/Fravashi.Web/wwwroot/studio.css` and `./docs/v2.md`, not `dotnet build`, `a/../b.cs`, `notes.md` "
            + "or [`src/Inside/Link.cs`](https://example.com).\n\n```\nsrc/In/Block.cs\n```\n",
            null);

        var linked = WikiReading.LinkFilePaths(html);

        Assert.Contains(
            "<a class=\"wiki-file-ref\" href=\"/code/src/Fravashi.Web/wwwroot/studio.css\" data-path=\"src/Fravashi.Web/wwwroot/studio.css\"",
            linked, StringComparison.Ordinal);
        Assert.Contains("data-path=\"docs/v2.md\"", linked, StringComparison.Ordinal);
        Assert.Equal(2, linked.Split("wiki-file-ref").Length - 1);
        Assert.DoesNotContain("data-path=\"src/Inside/Link.cs\"", linked, StringComparison.Ordinal);
        Assert.DoesNotContain("data-path=\"src/In/Block.cs\"", linked, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/A.cs", "src/A.cs")]
    [InlineData("./docs/plans/x.md", "docs/plans/x.md")]
    [InlineData("README.md", null)]
    [InlineData("https://example.com/a.md", null)]
    [InlineData("../outside/a.md", null)]
    [InlineData("src/folder/", null)]
    [InlineData("/etc/passwd.txt", null)]
    public void A_path_is_a_workspace_file_path_or_nothing(string text, string? expected) =>
        Assert.Equal(expected, WikiReading.ReadPath(text));
}
