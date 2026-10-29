using System.Net;
using System.Text;
using Fravashi.Application.Onboarding;
using Fravashi.Infrastructure.Onboarding;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Onboarding;

/// <summary>
/// Unit tests for the Azure DevOps board probe's create → modify → status-change → delete round-trip. Every
/// HTTP call is answered by an in-process <see cref="RouteHandler"/> whose responses mirror the shapes observed
/// against a live organization (JSON-Patch create with <c>application/json-patch+json</c>, a work item that
/// starts in <c>New</c>, states read from the type, a delete that moves the item to the recycle bin): <b>no test
/// here contacts a live Azure DevOps instance</b>.
/// </summary>
public class AzureDevOpsBoardProbeTests
{
    private const string Org = "https://dev.azure.com/contoso";

    private sealed record Recorded(HttpMethod Method, Uri Uri, string? Body, string? ContentType);

    private sealed class RouteHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<Recorded> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add(new Recorded(request.Method, request.RequestUri!, body, request.Content?.Headers.ContentType?.MediaType));
            return responder(request, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static BoardAccessProbe ProbeWith(RouteHandler handler) => new(new HttpClient(handler));

    private static BoardAccessRequest Request(string? token = "pat") =>
        new(OnboardingProviders.AzureDevOps, Org, "Proj", null, token);

    // Happy responder for read + query + resolve type + states + create + modify/status. Delete behaviour is
    // supplied per test by intercepting DELETE before delegating here.
    private static HttpResponseMessage RespondHappy(HttpRequestMessage r, string? body)
    {
        var path = r.RequestUri!.AbsolutePath;

        if (r.Method == HttpMethod.Get && path.Contains("/_apis/projects/", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK, """{"id":"p1","name":"Proj"}""");
        }

        if (r.Method == HttpMethod.Post && path.EndsWith("/_apis/wit/wiql", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK, """{"workItems":[]}""");
        }

        if (r.Method == HttpMethod.Get && path.EndsWith("/_apis/wit/workitemtypes", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK, """{"value":[{"name":"Bug","isDisabled":false},{"name":"Task","isDisabled":false},{"name":"User Story","isDisabled":false}]}""");
        }

        if (r.Method == HttpMethod.Get && path.Contains("/workitemtypes/Task/states", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK, """{"value":[{"name":"New","category":"Proposed"},{"name":"Active","category":"InProgress"},{"name":"Closed","category":"Completed"},{"name":"Removed","category":"Removed"}]}""");
        }

        if (r.Method == HttpMethod.Post && path.Contains("/_apis/wit/workitems/$", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK, """{"id":42,"fields":{"System.State":"New"}}""");
        }

        if (r.Method == HttpMethod.Patch && path.EndsWith("/_apis/wit/workitems/42", StringComparison.Ordinal))
        {
            return Json(HttpStatusCode.OK, """{"id":42,"fields":{"System.State":"Active"}}""");
        }

        return new HttpResponseMessage(HttpStatusCode.NotImplemented);
    }

    [Fact]
    public async Task Full_round_trip_reports_read_query_write_modify_and_status_change_all_verified()
    {
        var handler = new RouteHandler((r, body) =>
            r.Method == HttpMethod.Delete
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : RespondHappy(r, body));

        var result = await ProbeWith(handler).ProbeAsync(Request());

        result.Checks.Single(c => c.Capability == "Read").Outcome.Should().Be(AccessProbeOutcome.Passed);
        result.Checks.Single(c => c.Capability == "Query").Outcome.Should().Be(AccessProbeOutcome.Passed);
        var write = result.Checks.Single(c => c.Capability == "Write");
        write.Outcome.Should().Be(AccessProbeOutcome.Passed);
        write.Detail.Should().Contain("#42").And.Contain("Task").And.Contain("recycle bin");
        result.Checks.Single(c => c.Capability == "Modify").Outcome.Should().Be(AccessProbeOutcome.Passed);
        var status = result.Checks.Single(c => c.Capability == "Status change");
        status.Outcome.Should().Be(AccessProbeOutcome.Passed);
        status.Detail.Should().Contain("New").And.Contain("Active");

        // The create used a JSON-Patch body with the correct media type and targeted the resolved Task type.
        var create = handler.Calls.Single(c => c.Method == HttpMethod.Post && c.Uri.AbsolutePath.Contains("/workitems/$", StringComparison.Ordinal));
        create.ContentType.Should().Be("application/json-patch+json");
        create.Uri.AbsolutePath.Should().EndWith("/workitems/$Task");
        create.Body.Should().Contain("System.Title").And.Contain("op");
        // A status-change PATCH set System.State; cleanup deleted the probe work item.
        handler.Calls.Should().Contain(c => c.Method == HttpMethod.Patch && c.Body!.Contains("System.State", StringComparison.Ordinal));
        handler.Calls.Should().Contain(c => c.Method == HttpMethod.Delete && c.Uri.AbsolutePath.EndsWith("/_apis/wit/workitems/42", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_token_leaves_write_modify_and_status_honestly_not_verified()
    {
        var handler = new RouteHandler((r, body) => RespondHappy(r, body));

        var result = await ProbeWith(handler).ProbeAsync(Request(token: null));

        // Read/query still ran (the responder answers them even without auth).
        result.Checks.Single(c => c.Capability == "Read").Outcome.Should().Be(AccessProbeOutcome.Passed);
        foreach (var cap in new[] { "Write", "Modify", "Status change" })
        {
            result.Checks.Single(c => c.Capability == cap).Outcome.Should().Be(AccessProbeOutcome.NotVerifiable);
            result.Checks.Single(c => c.Capability == cap).Detail.Should().Contain("personal access token");
        }

        // No create or delete ever hit the wire.
        handler.Calls.Should().NotContain(c => c.Method == HttpMethod.Post && c.Uri.AbsolutePath.Contains("/workitems/$", StringComparison.Ordinal));
        handler.Calls.Should().NotContain(c => c.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Permission_denied_on_create_surfaces_azure_devops_message_and_leaves_nothing_behind()
    {
        var handler = new RouteHandler((r, body) =>
            r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.Contains("/workitems/$", StringComparison.Ordinal)
                ? Json(HttpStatusCode.Forbidden, """{"message":"TF401027: You need the Contribute permission to perform this action."}""")
                : RespondHappy(r, body));

        var result = await ProbeWith(handler).ProbeAsync(Request());

        var write = result.Checks.Single(c => c.Capability == "Write");
        write.Outcome.Should().Be(AccessProbeOutcome.Failed);
        write.Detail.Should().Contain("403").And.Contain("TF401027").And.Contain("Contribute permission");
        result.Checks.Single(c => c.Capability == "Modify").Detail.Should().Contain("could not be created");
        // Nothing created → nothing deleted.
        handler.Calls.Should().NotContain(c => c.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Create_ok_but_delete_fails_reports_the_leftover_work_item_with_its_id_and_url()
    {
        var handler = new RouteHandler((r, body) =>
            r.Method == HttpMethod.Delete
                ? new HttpResponseMessage(HttpStatusCode.Forbidden) // lacks "Delete work items" - item left behind
                : RespondHappy(r, body));

        var result = await ProbeWith(handler).ProbeAsync(Request());

        // Write/modify/status still proven honestly.
        result.Checks.Single(c => c.Capability == "Write").Outcome.Should().Be(AccessProbeOutcome.Passed);
        result.Checks.Single(c => c.Capability == "Modify").Outcome.Should().Be(AccessProbeOutcome.Passed);
        result.Checks.Single(c => c.Capability == "Status change").Outcome.Should().Be(AccessProbeOutcome.Passed);
        // The leftover is named (id + edit URL) on the results so the user can clean up.
        result.Checks.Single(c => c.Capability == "Write").Detail
            .Should().Contain("left behind").And.Contain("#42").And.Contain("/Proj/_workitems/edit/42");
        result.Checks.Single(c => c.Capability == "Status change").Detail.Should().Contain("Please delete it manually");
    }

    [Fact]
    public async Task No_other_reachable_state_reports_status_change_not_verifiable_but_write_and_modify_pass()
    {
        var handler = new RouteHandler((r, body) =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (r.Method == HttpMethod.Get && path.Contains("/workitemtypes/Task/states", StringComparison.Ordinal))
            {
                // The type exposes only the state the item was created in.
                return Json(HttpStatusCode.OK, """{"value":[{"name":"New","category":"Proposed"}]}""");
            }

            if (r.Method == HttpMethod.Delete)
            {
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            return RespondHappy(r, body);
        });

        var result = await ProbeWith(handler).ProbeAsync(Request());

        result.Checks.Single(c => c.Capability == "Write").Outcome.Should().Be(AccessProbeOutcome.Passed);
        result.Checks.Single(c => c.Capability == "Modify").Outcome.Should().Be(AccessProbeOutcome.Passed);
        var status = result.Checks.Single(c => c.Capability == "Status change");
        status.Outcome.Should().Be(AccessProbeOutcome.NotVerifiable);
        status.Detail.Should().Contain("No other state is reachable");
        // No System.State PATCH fired; the item was still cleaned up.
        handler.Calls.Should().NotContain(c => c.Method == HttpMethod.Patch && c.Body!.Contains("System.State", StringComparison.Ordinal));
        handler.Calls.Should().Contain(c => c.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Read_failure_skips_the_write_round_trip_entirely()
    {
        var handler = new RouteHandler((r, body) =>
            r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.Contains("/_apis/projects/", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : RespondHappy(r, body));

        var result = await ProbeWith(handler).ProbeAsync(Request());

        result.Checks.Single(c => c.Capability == "Read").Outcome.Should().Be(AccessProbeOutcome.Failed);
        result.Checks.Single(c => c.Capability == "Write").Outcome.Should().Be(AccessProbeOutcome.NotVerifiable);
        result.Checks.Single(c => c.Capability == "Write").Detail.Should().Contain("read access failed");
        // No type resolution, no create, no delete - the round-trip never started.
        handler.Calls.Should().NotContain(c => c.Method == HttpMethod.Post && c.Uri.AbsolutePath.Contains("/workitems/$", StringComparison.Ordinal));
        handler.Calls.Should().NotContain(c => c.Method == HttpMethod.Delete);
    }
}
