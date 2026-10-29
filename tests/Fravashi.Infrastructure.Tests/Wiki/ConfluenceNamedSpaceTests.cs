using System.Net;
using AwesomeAssertions;
using Fravashi.Infrastructure.Tracker.Atlassian;
using Fravashi.Infrastructure.Wiki;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Wiki;

/// <summary>
/// Backlog 12, the second cause. The site here behaves as the owner's was MEASURED to on 2026-09-21: listing
/// the spaces answers 200 and leaves the personal space out, and asking for it by key finds it. A reader that
/// looks for a named space in the listing resolves nothing and reads no pages, with no error anywhere.
/// </summary>
public sealed class ConfluenceNamedSpaceTests
{
    [Fact]
    public async Task A_space_the_listing_leaves_out_is_still_read_when_it_is_asked_for_by_key()
    {
        var asked = new List<string>();
        var reader = Reader(request =>
        {
            var url = request.RequestUri!.PathAndQuery;
            asked.Add(url);
            if (url.Contains("/spaces?keys=~owner", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, """{"results":[{"id":"9001","key":"~owner","name":"Owner","type":"personal"}]}""");
            }

            if (url.Contains("/spaces?", StringComparison.Ordinal))
            {
                return (HttpStatusCode.OK, """{"results":[{"id":"1","key":"TEAM","name":"Team","type":"global"}]}""");
            }

            return url.Contains("/pages?space-id=9001", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, """{"results":[{"id":"21069825","title":"Add Goodbye method to Greeter"}]}""")
                : (HttpStatusCode.NotFound, "{}");
        });

        var pages = await reader.ListPagesAsync("~owner");

        pages.Select(p => p.Id).Should().Equal("21069825");
        asked.Should().Contain(u => u.Contains("keys=~owner", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_space_nobody_has_is_no_pages_and_not_a_failure()
    {
        var reader = Reader(_ => (HttpStatusCode.OK, """{"results":[]}"""));

        (await reader.ListPagesAsync("GONE")).Should().BeEmpty();
    }

    private static AtlassianConfluenceReader Reader(Func<HttpRequestMessage, (HttpStatusCode, string)> site)
    {
        var options = Substitute.For<IOptionsMonitor<AtlassianConfig>>();
        options.CurrentValue.Returns(new AtlassianConfig
        {
            JiraKey = "token",
            JiraDomain = "acme.atlassian.net",
            JiraEmail = "docs@acme.com",
        });
        return new AtlassianConfluenceReader(new HttpClient(new StubHttpMessageHandler(site)), options);
    }
}
