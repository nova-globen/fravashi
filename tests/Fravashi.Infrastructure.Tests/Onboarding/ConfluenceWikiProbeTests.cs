using System.Net;
using System.Text;
using Fravashi.Application.Onboarding;
using Fravashi.Infrastructure.Onboarding;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Onboarding;

/// <summary>
/// Unit tests for the Confluence write/delete round-trip probe. Every HTTP call is answered by an in-process
/// <see cref="RouteHandler"/>: <b>no test here contacts a live Atlassian instance</b>. The scenarios cover the
/// full round-trip (create + update + delete), the honest no-space-key path, permission-denied on create, a
/// create-ok/delete-fails partial that must report the leftover page, and space-not-found.
/// </summary>
public class ConfluenceWikiProbeTests
{
    private const string Site = "https://acme.atlassian.net";

    private sealed class FakeGit : GitLsRemoteRunner
    {
        public override Task<LsRemoteResult> LsRemoteAsync(string authenticatedUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LsRemoteResult(true, 0, string.Empty));
    }

    private sealed record Recorded(HttpMethod Method, Uri Uri, string? Body);

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<Recorded> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add(new Recorded(request.Method, request.RequestUri!, body));
            return responder(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static WikiAccessProbe ProbeWith(RouteHandler handler) =>
        new(new HttpClient(handler), new FakeGit());

    private static WikiAccessRequest Request(string? spaceKey) =>
        new(OnboardingProviders.Confluence, Site, "me@acme.com", "token", spaceKey);

    // A responder for the happy resolve + create path; the delete behaviour is supplied per test.
    private static HttpResponseMessage RespondCommon(HttpRequestMessage r)
    {
        var path = r.RequestUri!.AbsolutePath;
        var query = r.RequestUri!.Query;
        if (r.Method == HttpMethod.Get && path.EndsWith("/spaces", StringComparison.Ordinal) && query.Contains("limit=1") && !query.Contains("keys", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK, """{"results":[{"id":"1","key":"DOCS"}]}""");
        }

        if (r.Method == HttpMethod.Get && path.EndsWith("/spaces", StringComparison.Ordinal) && query.Contains("keys", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK, """{"results":[{"id":"555","key":"DOCS"}]}""");
        }

        if (r.Method == HttpMethod.Post && path.EndsWith("/pages", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK, """{"id":"9001","title":"probe","version":{"number":1},"_links":{"webui":"/spaces/DOCS/pages/9001/probe","base":"https://acme.atlassian.net/wiki"}}""");
        }

        if (r.Method == HttpMethod.Put && path.Contains("/pages/9001", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK, """{"id":"9001","version":{"number":2}}""");
        }

        return new HttpResponseMessage(HttpStatusCode.NotImplemented);
    }

    [Fact]
    public async Task Full_round_trip_reports_read_write_delete_all_verified()
    {
        var handler = new RouteHandler(r =>
        {
            if (r.Method == HttpMethod.Delete && r.RequestUri!.Query.Contains("purge", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden); // no space-admin - purge declined, trash is the honest outcome
            }

            if (r.Method == HttpMethod.Delete)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return RespondCommon(r);
        });

        var result = await ProbeWith(handler).ProbeAsync(Request("DOCS"));

        result.Checks.Should().Contain(c => c.Capability == "Read" && c.Outcome == AccessProbeOutcome.Passed);
        var write = result.Checks.Single(c => c.Capability == "Write");
        write.Outcome.Should().Be(AccessProbeOutcome.Passed);
        write.Detail.Should().Contain("version bumped to 2");
        var delete = result.Checks.Single(c => c.Capability == "Delete");
        delete.Outcome.Should().Be(AccessProbeOutcome.Passed);
        delete.Detail.Should().Contain("trash");
        delete.Mechanism.Should().Contain("/wiki/api/v2/pages"); // the v2 path succeeded - no fallback needed
        // The create body carried the resolved spaceId and a storage-format body; the update bumped the version.
        var post = handler.Calls.Single(c => c.Method == HttpMethod.Post);
        post.Body.Should().Contain("\"spaceId\":\"555\"").And.Contain("\"representation\":\"storage\"");
        var put = handler.Calls.Single(c => c.Method == HttpMethod.Put);
        put.Body.Should().Contain("\"number\":2");
    }

    [Fact]
    public async Task Delete_falls_back_to_the_v1_content_api_when_the_v2_delete_returns_500()
    {
        // Regression for the live 500: DELETE /wiki/api/v2/pages/{id} intermittently 500s on Confluence Cloud
        // (seen in personal spaces). The probe must recover via the v1 content API and report the fallback.
        var handler = new RouteHandler(r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (r.Method == HttpMethod.Delete && path.Contains("/wiki/api/v2/pages/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError); // the buggy v2 delete
            }

            if (r.Method == HttpMethod.Delete && path.Contains("/wiki/rest/api/content/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent); // v1 fallback works
            }

            return RespondCommon(r);
        });

        var result = await ProbeWith(handler).ProbeAsync(Request("DOCS"));

        var delete = result.Checks.Single(c => c.Capability == "Delete");
        delete.Outcome.Should().Be(AccessProbeOutcome.Passed);
        delete.Mechanism.Should().Contain("v1 fallback");
        delete.Detail.Should().Contain("v2 delete failed").And.Contain("500").And.Contain("v1 content API");
        // Both the failed v2 delete and the successful v1 delete hit the wire.
        handler.Calls.Should().Contain(c => c.Method == HttpMethod.Delete && c.Uri.AbsolutePath.Contains("/wiki/api/v2/pages/", StringComparison.Ordinal));
        handler.Calls.Should().Contain(c => c.Method == HttpMethod.Delete && c.Uri.AbsolutePath.Contains("/wiki/rest/api/content/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delete_reports_the_leftover_page_when_both_v2_and_the_v1_fallback_fail()
    {
        // Every delete mechanism fails → the page is honestly reported as left behind.
        var handler = new RouteHandler(r =>
            r.Method == HttpMethod.Delete
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : RespondCommon(r));

        var result = await ProbeWith(handler).ProbeAsync(Request("DOCS"));

        result.Checks.Single(c => c.Capability == "Write").Outcome.Should().Be(AccessProbeOutcome.Passed);
        var delete = result.Checks.Single(c => c.Capability == "Delete");
        delete.Outcome.Should().Be(AccessProbeOutcome.Failed);
        delete.Detail.Should().Contain("v2 delete failed")
            .And.Contain("v1 content-API fallback failed")
            .And.Contain("left behind")
            .And.Contain("acme.atlassian.net/wiki/spaces/DOCS/pages/9001");
    }

    [Fact]
    public async Task No_space_key_leaves_write_and_delete_honestly_not_verified()
    {
        var handler = new RouteHandler(RespondCommon);

        var result = await ProbeWith(handler).ProbeAsync(Request(spaceKey: null));

        result.Checks.Should().Contain(c => c.Capability == "Read" && c.Outcome == AccessProbeOutcome.Passed);
        result.Checks.Single(c => c.Capability == "Write").Outcome.Should().Be(AccessProbeOutcome.NotVerifiable);
        result.Checks.Single(c => c.Capability == "Write").Detail.Should().Contain("provide a space key");
        result.Checks.Single(c => c.Capability == "Delete").Outcome.Should().Be(AccessProbeOutcome.NotVerifiable);
        // No write ever hit the wire.
        handler.Calls.Should().NotContain(c => c.Method == HttpMethod.Post || c.Method == HttpMethod.Put || c.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Permission_denied_on_create_reports_write_failed_and_delete_not_attempted()
    {
        var handler = new RouteHandler(r =>
            r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/pages", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : RespondCommon(r));

        var result = await ProbeWith(handler).ProbeAsync(Request("DOCS"));

        var write = result.Checks.Single(c => c.Capability == "Write");
        write.Outcome.Should().Be(AccessProbeOutcome.Failed);
        write.Detail.Should().Contain("permission").And.Contain("403");
        var delete = result.Checks.Single(c => c.Capability == "Delete");
        delete.Outcome.Should().Be(AccessProbeOutcome.NotVerifiable);
        delete.Detail.Should().Contain("could not be created");
        // Nothing was left behind: no page was created, so no delete was attempted.
        handler.Calls.Should().NotContain(c => c.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Create_ok_but_delete_fails_reports_the_leftover_page_with_its_title_and_url()
    {
        var handler = new RouteHandler(r =>
            r.Method == HttpMethod.Delete
                ? new HttpResponseMessage(HttpStatusCode.Forbidden) // trash delete refused → page left behind
                : RespondCommon(r));

        var result = await ProbeWith(handler).ProbeAsync(Request("DOCS"));

        // Create + update proved write.
        result.Checks.Single(c => c.Capability == "Write").Outcome.Should().Be(AccessProbeOutcome.Passed);
        var delete = result.Checks.Single(c => c.Capability == "Delete");
        delete.Outcome.Should().Be(AccessProbeOutcome.Failed);
        delete.Detail.Should().Contain("left behind")
            .And.Contain("Fravashi access probe")
            .And.Contain("acme.atlassian.net/wiki/spaces/DOCS/pages/9001");
    }

    [Fact]
    public async Task Space_not_found_reports_write_failed_and_never_creates_a_page()
    {
        var handler = new RouteHandler(r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            var query = r.RequestUri!.Query;
            if (r.Method == HttpMethod.Get && path.EndsWith("/spaces", StringComparison.Ordinal) && query.Contains("keys", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, """{"results":[]}"""); // no space with that key
            }

            return RespondCommon(r);
        });

        var result = await ProbeWith(handler).ProbeAsync(Request("NOPE"));

        var write = result.Checks.Single(c => c.Capability == "Write");
        write.Outcome.Should().Be(AccessProbeOutcome.Failed);
        write.Detail.Should().Contain("was not found");
        result.Checks.Single(c => c.Capability == "Delete").Outcome.Should().Be(AccessProbeOutcome.NotVerifiable);
        handler.Calls.Should().NotContain(c => c.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task Read_failure_skips_the_write_round_trip_entirely()
    {
        var handler = new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var result = await ProbeWith(handler).ProbeAsync(Request("DOCS"));

        result.Checks.Single(c => c.Capability == "Read").Outcome.Should().Be(AccessProbeOutcome.Failed);
        result.Checks.Single(c => c.Capability == "Write").Outcome.Should().Be(AccessProbeOutcome.NotVerifiable);
        result.Checks.Single(c => c.Capability == "Write").Detail.Should().Contain("read access failed");
        // Only the read call was made; the round-trip never started.
        handler.Calls.Should().OnlyContain(c => c.Method == HttpMethod.Get);
    }
}
