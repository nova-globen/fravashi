using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Application.Mirroring;
using Fravashi.Infrastructure.Tracker.Atlassian;
using Fravashi.Infrastructure.Wiki;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Wiki;

/// <summary>
/// The Confluence write adapter (backlog 83). The wire shape matters, but the thing most worth holding is
/// the CLASSIFICATION of a call that did not succeed, because the publisher sends a creation again on
/// "never left" and must not on "left with no answer".
/// </summary>
public sealed class AtlassianConfluencePageGatewayTests
{
    private const string Spaces = """{"results":[{"id":"9001","key":"TEAM","name":"Team","type":"collaboration"}]}""";

    private const string Page = """
        {"id":"555","title":"Rollout plan","spaceId":"9001","version":{"number":1},
         "body":{"storage":{"value":"<p>Hello</p>","representation":"storage"}},
         "_links":{"webui":"/spaces/TEAM/pages/555/Rollout+plan"}}
        """;

    private static (AtlassianConfluencePageGateway Gateway, Recorder Http) Build(
        Func<HttpRequestMessage, string?, HttpResponseMessage> respond, AtlassianConfig? config = null)
    {
        var recorder = new Recorder(respond);
        var options = Substitute.For<IOptionsMonitor<AtlassianConfig>>();
        options.CurrentValue.Returns(config ?? new AtlassianConfig
        {
            JiraKey = "token", JiraDomain = "acme.atlassian.net", JiraEmail = "me@acme.test",
        });
        return (new AtlassianConfluencePageGateway(new HttpClient(recorder), options), recorder);
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task A_creation_resolves_the_space_key_and_posts_the_storage_body_with_the_credential()
    {
        var (gateway, http) = Build((request, _) =>
            request.Method == HttpMethod.Get ? Answer(HttpStatusCode.OK, Spaces) : Answer(HttpStatusCode.OK, Page));

        var result = await gateway.CreateAsync("TEAM", "Rollout plan", "<p>Hello</p>");

        result.IsOk.Should().BeTrue(result.Reason);
        result.Value!.Id.Should().Be("555");
        result.Value.Version.Should().Be(1);
        result.Value.WebUrl.Should().Be("https://acme.atlassian.net/wiki/spaces/TEAM/pages/555/Rollout+plan");

        http.Requests[0].Url.Should().Be("https://acme.atlassian.net/wiki/api/v2/spaces?keys=TEAM&limit=1");
        var post = http.Requests[1];
        post.Method.Should().Be(HttpMethod.Post);
        post.Url.Should().Be("https://acme.atlassian.net/wiki/api/v2/pages");
        post.Authorization.Should().Be("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("me@acme.test:token")));

        using var sent = JsonDocument.Parse(post.Body!);
        sent.RootElement.GetProperty("spaceId").GetString().Should().Be("9001");
        sent.RootElement.GetProperty("title").GetString().Should().Be("Rollout plan");
        sent.RootElement.GetProperty("body").GetProperty("representation").GetString().Should().Be("storage");
        sent.RootElement.GetProperty("body").GetProperty("value").GetString().Should().Be("<p>Hello</p>");
    }

    [Fact]
    public async Task An_update_claims_the_version_it_was_told_to_become()
    {
        var (gateway, http) = Build((_, _) => Answer(HttpStatusCode.OK, Page.Replace("\"number\":1", "\"number\":4")));

        var result = await gateway.UpdateAsync("555", "Rollout plan", "<p>Again</p>", 4);

        result.Value!.Version.Should().Be(4);
        var put = http.Requests.Single();
        put.Method.Should().Be(HttpMethod.Put);
        put.Url.Should().Be("https://acme.atlassian.net/wiki/api/v2/pages/555");
        using var sent = JsonDocument.Parse(put.Body!);
        sent.RootElement.GetProperty("version").GetProperty("number").GetInt32().Should().Be(4);
        sent.RootElement.GetProperty("id").GetString().Should().Be("555");
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, ProviderOutcome.Conflict)]
    [InlineData(HttpStatusCode.NotFound, ProviderOutcome.Gone)]
    [InlineData(HttpStatusCode.BadRequest, ProviderOutcome.Refused)]
    [InlineData(HttpStatusCode.Unauthorized, ProviderOutcome.Refused)]
    [InlineData(HttpStatusCode.Forbidden, ProviderOutcome.Refused)]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderOutcome.Unreachable)]
    [InlineData(HttpStatusCode.InternalServerError, ProviderOutcome.Indeterminate)]
    [InlineData(HttpStatusCode.BadGateway, ProviderOutcome.Indeterminate)]
    public async Task An_answer_that_is_not_success_is_classified_by_what_it_says_about_the_write(
        HttpStatusCode status, ProviderOutcome expected)
    {
        var (gateway, _) = Build((_, _) => Answer(status, """{"errors":[{"title":"A page with this title already exists"}]}"""));

        var result = await gateway.UpdateAsync("555", "T", "<p/>", 2);

        result.Outcome.Should().Be(expected);
        result.Reason.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, ProviderOutcome.Unreachable)]
    [InlineData(HttpRequestError.ConnectionError, ProviderOutcome.Unreachable)]
    [InlineData(HttpRequestError.SecureConnectionError, ProviderOutcome.Unreachable)]
    [InlineData(HttpRequestError.ResponseEnded, ProviderOutcome.Indeterminate)]
    [InlineData(HttpRequestError.Unknown, ProviderOutcome.Indeterminate)]
    public async Task A_transport_failure_is_never_left_only_when_no_request_could_have_been_accepted(
        HttpRequestError error, ProviderOutcome expected)
    {
        var (gateway, _) = Build((_, _) => throw new HttpRequestException(error, "boom"));

        var result = await gateway.UpdateAsync("555", "T", "<p/>", 2);

        result.Outcome.Should().Be(expected);
    }

    [Fact]
    public async Task The_clients_own_timeout_is_left_with_no_answer_and_never_unreachable()
    {
        var (gateway, _) = Build((_, _) => throw new TaskCanceledException("timeout", new TimeoutException()));

        var result = await gateway.UpdateAsync("555", "T", "<p/>", 2);

        result.Outcome.Should().Be(ProviderOutcome.Indeterminate);
    }

    [Fact]
    public async Task A_success_nobody_can_read_is_not_knowing_and_not_a_refusal()
    {
        var (gateway, _) = Build((_, _) => Answer(HttpStatusCode.OK, "<html>a proxy page</html>"));

        var result = await gateway.UpdateAsync("555", "T", "<p/>", 2);

        result.Outcome.Should().Be(ProviderOutcome.Indeterminate);
    }

    [Fact]
    public async Task No_page_with_the_title_is_a_real_answer_and_a_near_match_is_not_the_page()
    {
        var list = """{"results":[{"id":"7","title":"Rollout plan (old)","version":{"number":3}}]}""";
        var (gateway, http) = Build((request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/spaces", StringComparison.Ordinal)
                ? Answer(HttpStatusCode.OK, Spaces)
                : Answer(HttpStatusCode.OK, list));

        var result = await gateway.FindByTitleAsync("TEAM", "Rollout plan");

        result.IsOk.Should().BeTrue();
        result.Value.Should().BeNull();
        http.Requests[1].Url.Should().Contain("space-id=9001").And.Contain("title=Rollout%20plan");
    }

    [Fact]
    public async Task A_page_list_that_is_missing_is_never_read_as_no_such_page()
    {
        var (gateway, _) = Build((request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/spaces", StringComparison.Ordinal)
                ? Answer(HttpStatusCode.OK, Spaces)
                : Answer(HttpStatusCode.NotFound, "{}"));

        var result = await gateway.FindByTitleAsync("TEAM", "Rollout plan");

        // Ok(null) is what lets a lost creation be sent again, so nothing but the site's own "none" may say it.
        result.IsOk.Should().BeFalse();
    }

    /// <summary>
    /// Measured against the real site on 2026-09-22, not read off Atlassian's contract: after the owner
    /// deleted the published page in Confluence's own UI, asking for it by id answered 200, with an EMPTY
    /// body and <c>"status":"trashed"</c>. The studio said "the page on the site holds this version",
    /// because only a 404 was read as gone and the version number still matched what the last publish
    /// wrote. The body below is that answer with the account identifiers taken out.
    /// </summary>
    [Fact]
    public async Task A_page_the_site_has_trashed_is_gone_although_it_answers_two_hundred()
    {
        const string Trashed = """
            {"parentType":null,"parentId":null,"createdAt":"2026-09-21T18:34:00.258Z","position":null,
             "id":"21069825","version":{"number":7,"message":"Published from Fravashi","minorEdit":false},
             "spaceId":"4030468","body":{},"status":"trashed","title":"Add Goodbye method to Greeter",
             "_links":{"webui":"/spaces/~owner/pages/21069825/Add+Goodbye+method+to+Greeter"}}
            """;

        var (gateway, _) = Build((_, _) => Answer(HttpStatusCode.OK, Trashed));

        var result = await gateway.GetAsync("21069825");

        result.Outcome.Should().Be(ProviderOutcome.Gone);
        result.Reason.Should().Contain("trashed");
    }

    /// <summary>
    /// The other side of the rule, so it stays narrow. Only the two statuses that mean the site no longer
    /// holds the page are gone. An archived page is still there, and saying otherwise offers "Publish as a
    /// new page", which is how a duplicate page arrives in somebody's wiki. A status the site does not send
    /// at all is read as current for the same reason.
    /// </summary>
    [Theory]
    [InlineData("current", ProviderOutcome.Ok)]
    [InlineData("archived", ProviderOutcome.Ok)]
    [InlineData("draft", ProviderOutcome.Ok)]
    [InlineData("trashed", ProviderOutcome.Gone)]
    [InlineData("deleted", ProviderOutcome.Gone)]
    public async Task Only_a_status_that_means_the_site_no_longer_holds_the_page_is_gone(
        string status, ProviderOutcome expected)
    {
        var (gateway, _) = Build((_, _) => Answer(
            HttpStatusCode.OK, Page.Replace("""{"id":"555",""", $$"""{"id":"555","status":"{{status}}",""")));

        var result = await gateway.GetAsync("555");

        result.Outcome.Should().Be(expected);
    }

    [Fact]
    public async Task An_answer_that_says_nothing_about_status_is_read_as_the_current_page()
    {
        var (gateway, _) = Build((_, _) => Answer(HttpStatusCode.OK, Page));

        var result = await gateway.GetAsync("555");

        result.IsOk.Should().BeTrue(result.Reason);
        result.Value!.Version.Should().Be(1);
    }

    [Fact]
    public async Task A_space_the_credential_cannot_see_refuses_before_anything_is_written()
    {
        var (gateway, http) = Build((_, _) => Answer(HttpStatusCode.OK, """{"results":[]}"""));

        var result = await gateway.CreateAsync("NOPE", "T", "<p/>");

        result.Outcome.Should().Be(ProviderOutcome.Refused);
        http.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task With_no_connection_configured_nothing_is_sent()
    {
        var (gateway, http) = Build((_, _) => Answer(HttpStatusCode.OK, Page), new AtlassianConfig());

        gateway.IsConfigured.Should().BeFalse();
        var result = await gateway.UpdateAsync("555", "T", "<p/>", 2);

        result.Outcome.Should().Be(ProviderOutcome.Unreachable);
        http.Requests.Should().BeEmpty();
    }

    internal sealed record Seen(HttpMethod Method, string Url, string? Authorization, string? Body);

    internal sealed class Recorder(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Seen(request.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), body));
            return respond(request, body);
        }
    }
}
