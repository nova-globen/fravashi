using System.Net;
using System.Text;
using Fravashi.Application.Onboarding;
using Fravashi.Infrastructure.Onboarding;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Onboarding;

/// <summary>
/// Unit tests for the Jira board probe: the enhanced-JQL query fix (the legacy endpoint's 410 must not
/// recur) and the user-consented create → modify → transition → delete round-trip. Every HTTP call is
/// answered by an in-process <see cref="RouteHandler"/>: <b>no test here contacts a live Atlassian instance</b>.
/// </summary>
public class JiraBoardProbeTests
{
    private const string Site = "https://acme.atlassian.net";

    private sealed record Recorded(HttpMethod Method, Uri Uri, string? Body);

    private sealed class RouteHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<Recorded> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add(new Recorded(request.Method, request.RequestUri!, body));
            return responder(request, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static BoardAccessProbe ProbeWith(RouteHandler handler) => new(new HttpClient(handler));

    private static BoardAccessRequest Request(string? projectKey) =>
        new(OnboardingProviders.Jira, Site, projectKey, "me@acme.com", "token");

    // The happy-path responder for read + query + resolve + create + modify + transitions. Delete behaviour is
    // supplied per test by intercepting DELETE before delegating here.
    private static HttpResponseMessage RespondHappy(HttpRequestMessage r, string? body)
    {
        var path = r.RequestUri!.AbsolutePath;

        if (r.Method == HttpMethod.Get && path.EndsWith("/rest/api/3/myself", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK, """{"accountId":"abc"}""");
        }

        if (r.Method == HttpMethod.Post && path.EndsWith("/rest/api/3/search/jql", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK, """{"issues":[],"isLast":true}""");
        }

        if (r.Method == HttpMethod.Get && path.Contains("/rest/api/3/project/", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK, """{"id":"1","key":"ENG","issueTypes":[{"id":"9","name":"Subtask","subtask":true},{"id":"10001","name":"Task","subtask":false}]}""");
        }

        if (r.Method == HttpMethod.Post && path.EndsWith("/rest/api/3/issue", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.Created, """{"id":"20001","key":"ENG-42"}""");
        }

        if (r.Method == HttpMethod.Put && path.Contains("/rest/api/3/issue/ENG-42", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        if (r.Method == HttpMethod.Get && path.EndsWith("/rest/api/3/issue/ENG-42/transitions", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK, """{"transitions":[{"id":"11","name":"In Progress"},{"id":"21","name":"Done"}]}""");
        }

        if (r.Method == HttpMethod.Post && path.EndsWith("/rest/api/3/issue/ENG-42/transitions", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        return new HttpResponseMessage(HttpStatusCode.NotImplemented);
    }

    [Fact]
    public async Task Query_uses_the_enhanced_jql_endpoint_and_never_the_retired_search_endpoint()
    {
        // Regression for the 410: the legacy /rest/api/3/search must never be called; if it is, fail it 410.
        var handler = new RouteHandler((r, body) =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path.EndsWith("/rest/api/3/search", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Gone); // the retired endpoint
            }

            return RespondHappy(r, body);
        });

        var result = await ProbeWith(handler).ProbeAsync(Request(projectKey: null));

        var query = result.Checks.Single(c => c.Capability == "Query");
        query.Outcome.Should().Be(AccessProbeOutcome.Passed);
        query.Mechanism.Should().Be("Jira POST /rest/api/3/search/jql");
        handler.Calls.Should().NotContain(c => c.Uri.AbsolutePath.EndsWith("/rest/api/3/search", StringComparison.Ordinal));
        // The enhanced endpoint's semantics: fields must be requested explicitly (none by default).
        handler.Calls.Single(c => c.Uri.AbsolutePath.EndsWith("/search/jql", StringComparison.Ordinal)).Body.Should().Contain("\"fields\"");
    }

    [Fact]
    public async Task Query_sends_a_bounded_jql_restriction_so_the_enhanced_endpoint_does_not_reject_it()
    {
        // Regression for the live 400: the enhanced endpoint rejects UNBOUNDED JQL ("order by created DESC"
        // alone) with "Unbounded JQL queries are not allowed here". The probe must send a search restriction.
        var handler = new RouteHandler((r, body) =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (r.Method == HttpMethod.Post && path.EndsWith("/rest/api/3/search/jql", StringComparison.Ordinal))
            {
                // Mirror the live rule: reject any JQL that lacks a bounding restriction (here, a `created` bound).
                return body is not null && body.Contains("created >=", StringComparison.Ordinal)
                    ? Json(HttpStatusCode.OK, """{"issues":[],"isLast":true}""")
                    : Json(HttpStatusCode.BadRequest, """{"errorMessages":["Unbounded JQL queries are not allowed here. Please add a search restriction to your query."],"errors":{}}""");
            }

            return RespondHappy(r, body);
        });

        var result = await ProbeWith(handler).ProbeAsync(Request(projectKey: null));

        result.Checks.Single(c => c.Capability == "Query").Outcome.Should().Be(AccessProbeOutcome.Passed);
        var jqlCall = handler.Calls.Single(c => c.Uri.AbsolutePath.EndsWith("/search/jql", StringComparison.Ordinal));
        jqlCall.Body.Should().Contain("created >=");
    }

    [Fact]
    public async Task Query_failure_surfaces_jiras_own_error_message_not_just_a_status_code()
    {
        // If Jira still rejects the query, the check must show Jira's errorMessages, not a bare "returned 400".
        var handler = new RouteHandler((r, body) =>
            r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/rest/api/3/search/jql", StringComparison.Ordinal)
                ? Json(HttpStatusCode.BadRequest, """{"errorMessages":["Unbounded JQL queries are not allowed here. Please add a search restriction to your query."],"errors":{}}""")
                : RespondHappy(r, body));

        var result = await ProbeWith(handler).ProbeAsync(Request(projectKey: null));

        var query = result.Checks.Single(c => c.Capability == "Query");
        query.Outcome.Should().Be(AccessProbeOutcome.Failed);
        query.Detail.Should().Contain("400").And.Contain("Unbounded JQL queries are not allowed here");
    }

    [Fact]
    public async Task Full_round_trip_reports_write_modify_and_status_change_all_verified()
    {
        var handler = new RouteHandler((r, body) =>
            r.Method == HttpMethod.Delete
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : RespondHappy(r, body));

        var result = await ProbeWith(handler).ProbeAsync(Request("ENG"));

        result.Checks.Single(c => c.Capability == "Read").Outcome.Should().Be(AccessProbeOutcome.Passed);
        result.Checks.Single(c => c.Capability == "Query").Outcome.Should().Be(AccessProbeOutcome.Passed);
        var write = result.Checks.Single(c => c.Capability == "Write");
        write.Outcome.Should().Be(AccessProbeOutcome.Passed);
        write.Detail.Should().Contain("ENG-42").And.Contain("Task");
        result.Checks.Single(c => c.Capability == "Modify").Outcome.Should().Be(AccessProbeOutcome.Passed);
        var status = result.Checks.Single(c => c.Capability == "Status change");
        status.Outcome.Should().Be(AccessProbeOutcome.Passed);
        status.Detail.Should().Contain("In Progress");

        // The create body carried the project key and the resolved non-subtask issue type; a transition POST fired.
        var create = handler.Calls.Single(c => c.Method == HttpMethod.Post && c.Uri.AbsolutePath.EndsWith("/rest/api/3/issue", StringComparison.Ordinal));
        create.Body.Should().Contain("\"key\":\"ENG\"").And.Contain("\"id\":\"10001\"");
        handler.Calls.Should().Contain(c => c.Method == HttpMethod.Post && c.Uri.AbsolutePath.EndsWith("/transitions", StringComparison.Ordinal));
        // Cleanup deleted the probe issue.
        handler.Calls.Should().Contain(c => c.Method == HttpMethod.Delete && c.Uri.AbsolutePath.EndsWith("/rest/api/3/issue/ENG-42", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_project_key_leaves_write_modify_and_status_honestly_not_verified()
    {
        var handler = new RouteHandler(RespondHappy);

        var result = await ProbeWith(handler).ProbeAsync(Request(projectKey: null));

        result.Checks.Single(c => c.Capability == "Read").Outcome.Should().Be(AccessProbeOutcome.Passed);
        result.Checks.Single(c => c.Capability == "Query").Outcome.Should().Be(AccessProbeOutcome.Passed);
        foreach (var cap in new[] { "Write", "Modify", "Status change" })
        {
            var check = result.Checks.Single(c => c.Capability == cap);
            check.Outcome.Should().Be(AccessProbeOutcome.NotVerifiable);
            check.Detail.Should().Contain("provide a project key");
        }

        // No write ever hit the wire.
        handler.Calls.Should().NotContain(c => c.Method == HttpMethod.Post && c.Uri.AbsolutePath.EndsWith("/rest/api/3/issue", StringComparison.Ordinal));
        handler.Calls.Should().NotContain(c => c.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Permission_denied_on_create_reports_write_failed_and_nothing_left_behind()
    {
        var handler = new RouteHandler((r, body) =>
            r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/rest/api/3/issue", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : RespondHappy(r, body));

        var result = await ProbeWith(handler).ProbeAsync(Request("ENG"));

        var write = result.Checks.Single(c => c.Capability == "Write");
        write.Outcome.Should().Be(AccessProbeOutcome.Failed);
        write.Detail.Should().Contain("permission").And.Contain("403");
        result.Checks.Single(c => c.Capability == "Modify").Detail.Should().Contain("could not be created");
        result.Checks.Single(c => c.Capability == "Status change").Detail.Should().Contain("could not be created");
        // Nothing created → nothing deleted.
        handler.Calls.Should().NotContain(c => c.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Bad_request_on_create_surfaces_the_field_errors_jira_returns()
    {
        var handler = new RouteHandler((r, body) =>
            r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/rest/api/3/issue", StringComparison.Ordinal)
                ? Json(HttpStatusCode.BadRequest, """{"errorMessages":[],"errors":{"summary":"Field 'summary' is required."}}""")
                : RespondHappy(r, body));

        var result = await ProbeWith(handler).ProbeAsync(Request("ENG"));

        var write = result.Checks.Single(c => c.Capability == "Write");
        write.Outcome.Should().Be(AccessProbeOutcome.Failed);
        write.Detail.Should().Contain("400").And.Contain("summary").And.Contain("required");
    }

    [Fact]
    public async Task Create_ok_but_delete_fails_reports_the_leftover_issue_with_its_key_and_url()
    {
        var handler = new RouteHandler((r, body) =>
            r.Method == HttpMethod.Delete
                ? new HttpResponseMessage(HttpStatusCode.Forbidden) // lacks "Delete issues" - issue left behind
                : RespondHappy(r, body));

        var result = await ProbeWith(handler).ProbeAsync(Request("ENG"));

        // Write/modify/status still proven honestly.
        result.Checks.Single(c => c.Capability == "Write").Outcome.Should().Be(AccessProbeOutcome.Passed);
        result.Checks.Single(c => c.Capability == "Modify").Outcome.Should().Be(AccessProbeOutcome.Passed);
        result.Checks.Single(c => c.Capability == "Status change").Outcome.Should().Be(AccessProbeOutcome.Passed);
        // The leftover is named (key + browse URL) on the results so the user can clean up.
        result.Checks.Single(c => c.Capability == "Write").Detail
            .Should().Contain("left behind").And.Contain("ENG-42").And.Contain("https://acme.atlassian.net/browse/ENG-42");
        result.Checks.Single(c => c.Capability == "Status change").Detail.Should().Contain("Please delete it manually");
    }

    [Fact]
    public async Task No_transitions_reports_status_change_not_verifiable_but_write_and_modify_pass()
    {
        var handler = new RouteHandler((r, body) =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (r.Method == HttpMethod.Get && path.EndsWith("/transitions", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, """{"transitions":[]}"""); // workflow offers none from the initial status
            }

            if (r.Method == HttpMethod.Delete)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return RespondHappy(r, body);
        });

        var result = await ProbeWith(handler).ProbeAsync(Request("ENG"));

        result.Checks.Single(c => c.Capability == "Write").Outcome.Should().Be(AccessProbeOutcome.Passed);
        result.Checks.Single(c => c.Capability == "Modify").Outcome.Should().Be(AccessProbeOutcome.Passed);
        var status = result.Checks.Single(c => c.Capability == "Status change");
        status.Outcome.Should().Be(AccessProbeOutcome.NotVerifiable);
        status.Detail.Should().Contain("No transition is available").And.Contain("workflow");
        // No transition POST fired; the issue was still cleaned up.
        handler.Calls.Should().NotContain(c => c.Method == HttpMethod.Post && c.Uri.AbsolutePath.EndsWith("/transitions", StringComparison.Ordinal));
        handler.Calls.Should().Contain(c => c.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Project_not_found_reports_write_failed_and_never_creates_an_issue()
    {
        var handler = new RouteHandler((r, body) =>
            r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.Contains("/rest/api/3/project/", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : RespondHappy(r, body));

        var result = await ProbeWith(handler).ProbeAsync(Request("NOPE"));

        var write = result.Checks.Single(c => c.Capability == "Write");
        write.Outcome.Should().Be(AccessProbeOutcome.Failed);
        write.Detail.Should().Contain("was not found").And.Contain("404");
        result.Checks.Single(c => c.Capability == "Modify").Outcome.Should().Be(AccessProbeOutcome.NotVerifiable);
        handler.Calls.Should().NotContain(c => c.Method == HttpMethod.Post && c.Uri.AbsolutePath.EndsWith("/rest/api/3/issue", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Read_failure_skips_the_write_round_trip_entirely()
    {
        var handler = new RouteHandler((r, body) =>
            r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.EndsWith("/rest/api/3/myself", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : RespondHappy(r, body));

        var result = await ProbeWith(handler).ProbeAsync(Request("ENG"));

        result.Checks.Single(c => c.Capability == "Read").Outcome.Should().Be(AccessProbeOutcome.Failed);
        result.Checks.Single(c => c.Capability == "Write").Outcome.Should().Be(AccessProbeOutcome.NotVerifiable);
        result.Checks.Single(c => c.Capability == "Write").Detail.Should().Contain("read access failed");
        // No project GET, no create, no delete - the round-trip never started.
        handler.Calls.Should().NotContain(c => c.Method == HttpMethod.Post && c.Uri.AbsolutePath.EndsWith("/rest/api/3/issue", StringComparison.Ordinal));
        handler.Calls.Should().NotContain(c => c.Method == HttpMethod.Delete);
    }

    // The Azure DevOps board round-trip now has its own suite (AzureDevOpsBoardProbeTests) - it is no longer
    // "not yet implemented", so the former placeholder assertion has moved there.
}
