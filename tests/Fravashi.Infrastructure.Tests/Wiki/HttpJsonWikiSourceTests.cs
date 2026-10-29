using System.Net;
using Fravashi.Infrastructure.Wiki;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Wiki;

public sealed class HttpJsonWikiSourceTests
{
    [Fact]
    public async Task FetchAll_ReadsPagesFromTheConfiguredBaseUrl()
    {
        const string body = """
            [
              { "space": "DOCS", "title": "Home", "slug": "home", "body": "hello", "format": "markdown", "version": "v1" },
              { "space": "DOCS", "title": "Guide", "slug": "guide", "body": "guide body", "format": "markdown", "version": "v2" }
            ]
            """;
        var handler = new StubHttpMessageHandler(req =>
        {
            req.RequestUri!.ToString().Should().Be("https://fake-wiki.local/pages");
            return (HttpStatusCode.OK, body);
        });
        var source = new HttpJsonWikiSource(new HttpClient(handler), "https://fake-wiki.local");

        var pages = await source.FetchAllAsync(progress: null);

        pages.Should().HaveCount(2);
        pages[0].Space.Should().Be("DOCS");
        pages[0].Slug.Should().Be("home");
        pages[0].Body.Should().Be("hello");
        pages[1].SourceRef.Should().Be("v2");
    }

    [Fact]
    public async Task FetchAll_TrailingSlashInBaseUrl_IsNormalized()
    {
        var handler = new StubHttpMessageHandler(req =>
        {
            req.RequestUri!.ToString().Should().Be("https://fake-wiki.local/pages");
            return (HttpStatusCode.OK,
                """[ { "space": "S", "title": "T", "slug": "t", "body": "b", "format": "markdown", "version": "v1" } ]""");
        });
        var source = new HttpJsonWikiSource(new HttpClient(handler), "https://fake-wiki.local/");

        (await source.FetchAllAsync(null)).Should().ContainSingle();
    }
}
