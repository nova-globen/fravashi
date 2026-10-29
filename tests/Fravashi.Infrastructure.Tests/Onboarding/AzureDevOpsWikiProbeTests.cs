using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Fravashi.Application.Onboarding;
using Fravashi.Infrastructure.Onboarding;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Onboarding;

/// <summary>
/// Unit tests for the Azure DevOps wiki probe's create → edit → delete round-trip. Every HTTP call is answered
/// by an in-process <see cref="RouteHandler"/> whose responses mirror the live shapes (a wiki list, a create
/// PUT that returns the page version as an <c>ETag</c> header, an edit PUT gated by <c>If-Match</c>, and a
/// delete): <b>no test here contacts a live Azure DevOps instance</b>. The no-wiki path is the honest
/// NotVerifiable case, not a failure.
/// </summary>
public class AzureDevOpsWikiProbeTests
{
    private const string Base = "https://dev.azure.com/contoso/Proj";

    private sealed class FakeGit : GitLsRemoteRunner
    {
        public override Task<LsRemoteResult> LsRemoteAsync(string authenticatedUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LsRemoteResult(true, 0, string.Empty));
    }

    private sealed record Recorded(HttpMethod Method, Uri Uri, string? Body, string? IfMatch);

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<Recorded> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var ifMatch = request.Headers.TryGetValues("If-Match", out var v) ? string.Join(",", v) : null;
            Calls.Add(new Recorded(request.Method, request.RequestUri!, body, ifMatch));
            return responder(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage CreatedWithETag(string etag)
    {
        var response = Json(HttpStatusCode.Created, """{"id":1,"path":"/Fravashi-access-probe"}""");
        response.Headers.ETag = new EntityTagHeaderValue($"\"{etag}\"");
        return response;
    }

    private static WikiAccessProbe ProbeWith(RouteHandler handler) => new(new HttpClient(handler), new FakeGit());

    private static WikiAccessRequest Request() =>
        new(OnboardingProviders.AzureDevOpsWiki, Base, null, "pat");

    private static HttpResponseMessage RespondCommon(HttpRequestMessage r)
    {
        var path = r.RequestUri!.AbsolutePath;
        if (r.Method == HttpMethod.Get && path.EndsWith("/_apis/wiki/wikis", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK, """{"value":[{"id":"w1","name":"Proj.wiki"}]}""");
        }

        if (r.Method == HttpMethod.Put && path.Contains("/wikis/w1/pages", StringComparison.Ordinal))
        {
            // A create has no If-Match; an edit does. The create returns the ETag the edit must echo.
            return r.Headers.Contains("If-Match")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : CreatedWithETag("v1etag");
        }

        return new HttpResponseMessage(HttpStatusCode.NotImplemented);
    }

    [Fact]
    public async Task Full_round_trip_reports_read_write_delete_all_verified_and_edits_with_if_match()
    {
        var handler = new RouteHandler(r =>
            r.Method == HttpMethod.Delete ? new HttpResponseMessage(HttpStatusCode.OK) : RespondCommon(r));

        var result = await ProbeWith(handler).ProbeAsync(Request());

        result.Checks.Single(c => c.Capability == "Read").Outcome.Should().Be(AccessProbeOutcome.Passed);
        var write = result.Checks.Single(c => c.Capability == "Write");
        write.Outcome.Should().Be(AccessProbeOutcome.Passed);
        write.Detail.Should().Contain("If-Match");
        result.Checks.Single(c => c.Capability == "Delete").Outcome.Should().Be(AccessProbeOutcome.Passed);

        // Exactly two PUTs: the create (no If-Match) and the edit (If-Match carrying the create's ETag).
        var puts = handler.Calls.Where(c => c.Method == HttpMethod.Put).ToList();
        puts.Should().HaveCount(2);
        puts[0].IfMatch.Should().BeNull();
        puts[1].IfMatch.Should().Contain("v1etag");
        handler.Calls.Should().Contain(c => c.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task No_wiki_reports_write_and_delete_not_verifiable_not_failed()
    {
        var handler = new RouteHandler(r =>
            r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/_apis/wiki/wikis", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, """{"value":[]}""")
                : RespondCommon(r));

        var result = await ProbeWith(handler).ProbeAsync(Request());

        result.Checks.Single(c => c.Capability == "Read").Outcome.Should().Be(AccessProbeOutcome.Passed);
        var write = result.Checks.Single(c => c.Capability == "Write");
        write.Outcome.Should().Be(AccessProbeOutcome.NotVerifiable);
        write.Detail.Should().Contain("No wiki exists");
        result.Checks.Single(c => c.Capability == "Delete").Outcome.Should().Be(AccessProbeOutcome.NotVerifiable);
        // No page was ever written.
        handler.Calls.Should().NotContain(c => c.Method == HttpMethod.Put || c.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Permission_denied_on_create_reports_write_failed_and_delete_not_attempted()
    {
        var handler = new RouteHandler(r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (r.Method == HttpMethod.Put && path.Contains("/pages", StringComparison.Ordinal) && !r.Headers.Contains("If-Match"))
            {
                return Json(HttpStatusCode.Forbidden, """{"message":"TF401027: You need the GenericContribute permission."}""");
            }

            return RespondCommon(r);
        });

        var result = await ProbeWith(handler).ProbeAsync(Request());

        var write = result.Checks.Single(c => c.Capability == "Write");
        write.Outcome.Should().Be(AccessProbeOutcome.Failed);
        write.Detail.Should().Contain("403").And.Contain("TF401027");
        var delete = result.Checks.Single(c => c.Capability == "Delete");
        delete.Outcome.Should().Be(AccessProbeOutcome.NotVerifiable);
        delete.Detail.Should().Contain("could not be created");
        // Nothing created → no delete.
        handler.Calls.Should().NotContain(c => c.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Create_ok_but_delete_fails_reports_the_leftover_page_with_its_path_and_url()
    {
        var handler = new RouteHandler(r =>
            r.Method == HttpMethod.Delete ? new HttpResponseMessage(HttpStatusCode.Forbidden) : RespondCommon(r));

        var result = await ProbeWith(handler).ProbeAsync(Request());

        result.Checks.Single(c => c.Capability == "Write").Outcome.Should().Be(AccessProbeOutcome.Passed);
        var delete = result.Checks.Single(c => c.Capability == "Delete");
        delete.Outcome.Should().Be(AccessProbeOutcome.Failed);
        delete.Detail.Should().Contain("left behind")
            .And.Contain("Fravashi-access-probe")
            .And.Contain("_wiki/wikis/Proj.wiki");
    }

    [Fact]
    public async Task Read_failure_skips_the_write_round_trip_entirely()
    {
        var handler = new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await ProbeWith(handler).ProbeAsync(Request());

        result.Checks.Single(c => c.Capability == "Read").Outcome.Should().Be(AccessProbeOutcome.Failed);
        result.Checks.Single(c => c.Capability == "Write").Outcome.Should().Be(AccessProbeOutcome.NotVerifiable);
        result.Checks.Single(c => c.Capability == "Write").Detail.Should().Contain("read access failed");
        // Only the wiki-list GET was made; the round-trip never started.
        handler.Calls.Should().OnlyContain(c => c.Method == HttpMethod.Get);
    }
}
