using System.Net;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Fravashi.Application.Wiki;
using Fravashi.Domain.Wiki;
using Fravashi.Infrastructure.Rendering;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Rendering;

/// <summary>
/// Backlog 286: a link or image in rendered markdown carried any scheme it was given, so
/// <c>[x](javascript:...)</c> ran script in the studio window on one click. Every pipeline (the Code page's
/// preview, the transcript's prose, the wiki) is checked against the same payloads, because the fix is one
/// policy and a pipeline that forgets to use it is exactly the regression to catch.
/// </summary>
public partial class LinkUrlPolicyTests
{
    public static TheoryData<string, string> Refused => new()
    {
        { "[click](javascript:alert(document.domain))", "javascript:" },
        { "[x](JaVaScRiPt:alert(1))", "javascript:" },
        { "[x](java&#9;script:alert(1))", "javascript:" },
        { "[x](java&#10;script:alert(1))", "javascript:" },
        { "<javascript:alert(1)>", "javascript:" },
        { "[ref][x]\n\n[x]: javascript:alert(1)", "javascript:" },
        { "[x](vbscript:msgbox)", "vbscript:" },
        { "[x](data:text/html,<script>alert(1)</script>)", "data:" },
        { "[x](file:///etc/passwd)", "file:" },
        { "[x](fravashi-custom:open)", "fravashi-custom:" },
        { "![img](javascript:alert(1))", "javascript:" },
        { "![img](data:image/svg+xml,%3Csvg%20onload=alert(1)%3E)", "data:" },
    };

    public static TheoryData<string, string> Kept => new()
    {
        { "[x](https://example.com/a?b=c)", "https://example.com/a?b=c" },
        { "[x](http://example.com)", "http://example.com" },
        { "[x](mailto:someone@example.com)", "mailto:someone@example.com" },
        { "[x](#anchor)", "#anchor" },
        { "[x](docs/readme.md)", "docs/readme.md" },
        { "[x](/workitems/12)", "/workitems/12" },
        { "<https://example.com>", "https://example.com" },
        { "![i](images/a.png)", "images/a.png" },
    };

    private static readonly MarkdownPreviewRenderer Preview = new();
    private static readonly WikiMarkdownRenderer Wiki = new();

    private static string PreviewHtml(string markdown) => Preview.ToHtml(markdown);

    private static string TranscriptHtml(string markdown) =>
        string.Concat(Preview.ToSegments(markdown).Select(s => s.Html));

    private static string WikiHtml(string markdown) => Wiki.ToHtml(markdown, WikiReferenceMap.Empty);

    public static TheoryData<string> Pipelines => new() { "preview", "transcript", "wiki" };

    private static string Render(string pipeline, string markdown) => pipeline switch
    {
        "preview" => PreviewHtml(markdown),
        "transcript" => TranscriptHtml(markdown),
        "wiki" => WikiHtml(markdown),
        _ => throw new ArgumentOutOfRangeException(nameof(pipeline)),
    };

    [GeneratedRegex("\\s(?:href|src)=\"([^\"]*)\"")]
    private static partial Regex UrlAttribute();

    /// <summary>
    /// Every href and src in the HTML, as the browser would read its scheme: entities decoded, tab and
    /// newline removed, the ends trimmed, lower case. Worked out here independently of the policy under test.
    /// </summary>
    private static IEnumerable<string> UrlsIn(string html) =>
        UrlAttribute().Matches(html).Select(m =>
            WebUtility.HtmlDecode(m.Groups[1].Value)
                .Replace("\t", string.Empty, StringComparison.Ordinal)
                .Replace("\n", string.Empty, StringComparison.Ordinal)
                .Replace("\r", string.Empty, StringComparison.Ordinal)
                .Trim()
                .ToLowerInvariant());

    public static IEnumerable<object[]> RefusedInEveryPipeline() =>
        from pipeline in new[] { "preview", "transcript", "wiki" }
        from row in Refused
        select new object[] { pipeline, row[0], row[1] };

    public static IEnumerable<object[]> KeptInEveryPipeline() =>
        from pipeline in new[] { "preview", "transcript", "wiki" }
        from row in Kept
        select new object[] { pipeline, row[0], row[1] };

    [Theory]
    [MemberData(nameof(RefusedInEveryPipeline))]
    public void A_refused_scheme_reaches_no_href_or_src(string pipeline, string markdown, string scheme)
    {
        var html = Render(pipeline, markdown);

        UrlsIn(html).Should().NotContain(u => u.StartsWith(scheme, StringComparison.Ordinal), html);
        html.Should().NotContain("<a ", "a refused link keeps its text and loses its anchor");
        html.Should().NotContain("<img", "a refused image loses its src, and with it the element");
    }

    [Theory]
    [MemberData(nameof(KeptInEveryPipeline))]
    public void An_allowed_target_still_renders_as_a_link(string pipeline, string markdown, string url)
    {
        var html = Render(pipeline, markdown);

        UrlsIn(html).Should().Contain(url.ToLowerInvariant(), html);
    }

    [Theory]
    [MemberData(nameof(Pipelines))]
    public void A_refused_link_keeps_its_text(string pipeline)
    {
        var html = Render(pipeline, "Before [Open the report](javascript:void(0)) after.");

        html.Should().Contain("Before Open the report after.");
    }

    [Theory]
    [MemberData(nameof(Pipelines))]
    public void A_generic_attribute_cannot_add_a_handler_a_url_or_a_style(string pipeline)
    {
        var html = Render(
            pipeline,
            "[x](https://example.com){onclick=\"alert(1)\" href=\"javascript:alert(2)\" style=\"position:fixed\" title=\"kept\"}\n\n" +
            "# A heading {onmouseover=\"alert(3)\" .note}");

        html.Should().NotContainAny("onclick", "onmouseover", "javascript:", "style=");
        html.Should().Contain("href=\"https://example.com\"").And.Contain("title=\"kept\"").And.Contain("class=\"note\"");
    }

    [Theory]
    [InlineData(WikiReferenceKind.WorkItem, 432, "/workitems/432")]
    [InlineData(WikiReferenceKind.PullRequest, 6243, "/git/pr/6243")]
    [InlineData(WikiReferenceKind.WikiItem, 7354, "/wiki/7354")]
    public void The_wikis_own_references_still_render_as_links(WikiReferenceKind kind, int number, string route)
    {
        var reference = new WikiReference(kind, number);
        var map = new WikiReferenceMap().Add(reference, "A title");

        var html = Wiki.ToHtml("See " + reference.Text + " and [x](javascript:alert(1)).", map);

        html.Should().Contain($"href=\"{route}\"").And.Contain(WikiMarkdownRenderer.ResolvedClass);
        UrlsIn(html).Should().NotContain(u => u.StartsWith("javascript:", StringComparison.Ordinal));
    }

    [Fact]
    public void The_transcripts_copy_control_still_works_beside_a_refused_link()
    {
        var segments = Preview.ToSegments(
            "Run [this](javascript:alert(1)):\n\n```sh\nmake test\n```\n\n1. then:\n\n   ```sh\n   ls\n   ```\n");

        segments.Should().Contain(s => s.IsCode && s.Source == "make test");
        var prose = string.Concat(segments.Where(s => !s.IsCode).Select(s => s.Html));
        prose.Should().Contain("data-testid=\"copy-code\"").And.Contain("data-copy-source=\"ls\"");
        UrlsIn(prose).Should().NotContain(u => u.StartsWith("javascript:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("javascript:alert(1)", "javascript")]
    [InlineData("JAVASCRIPT:alert(1)", "javascript")]
    [InlineData("  \u0001javascript:alert(1)", "javascript")]
    [InlineData("java\tscr\nipt:alert(1)", "javascript")]
    [InlineData("vbscript:x", "vbscript")]
    [InlineData("c:\\windows", "c")]
    [InlineData("a+b-c.d:x", "a+b-c.d")]
    [InlineData("https://example.com", "https")]
    [InlineData("#anchor", null)]
    [InlineData("/a:b", null)]
    [InlineData("./javascript:x", null)]
    [InlineData("foo bar:baz", null)]
    [InlineData("1abc:x", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_scheme_is_read_the_way_a_browser_reads_it(string? url, string? scheme) =>
        LinkUrlPolicy.SchemeOf(url).Should().Be(scheme);

    [Theory]
    [InlineData("https://x", true)]
    [InlineData("HTTP://x", true)]
    [InlineData("mailto:a@b.c", true)]
    [InlineData("#a", true)]
    [InlineData("relative/path", true)]
    [InlineData("", true)]
    [InlineData("javascript:x", false)]
    [InlineData("data:text/html,x", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("ftp://x", false)]
    [InlineData("custom-scheme:x", false)]
    public void Only_http_https_mailto_a_fragment_and_a_relative_url_are_allowed(string url, bool allowed) =>
        LinkUrlPolicy.IsAllowed(url).Should().Be(allowed);
}
