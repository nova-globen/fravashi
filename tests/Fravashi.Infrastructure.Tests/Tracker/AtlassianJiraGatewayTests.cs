using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Application.Mirroring;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Tests.Wiki;
using Fravashi.Infrastructure.Tracker.Atlassian;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Tracker;

/// <summary>
/// The Jira adapter for <see cref="IProviderGateway"/> (backlog 47, first increment), against a simulated site.
/// As with the Confluence adapter, the wire shape matters less than two things the outbox stands on: how a call
/// that did not succeed is CLASSIFIED, and that a creation can be found again by the operation id stamped on it,
/// which is the only thing that tells a crashed-but-accepted creation from one that never happened.
/// </summary>
public sealed class AtlassianJiraGatewayTests
{
    private const string IssueWithCategory = """
        {"id":"10042","key":"DD-7","fields":{
          "summary":"With a category","description":null,
          "status":{"name":"In Progress","statusCategory":{"key":"indeterminate","name":"In Progress"}},
          "updated":"2026-09-22T01:02:03.000+0000"}}
        """;

    private const string Issue = """
        {"id":"10042","key":"DD-7","fields":{
          "summary":"Resume a conversation in place",
          "description":{"type":"doc","version":1,"content":[
            {"type":"paragraph","content":[{"type":"text","text":"First paragraph."}]},
            {"type":"paragraph","content":[{"type":"text","text":"Second, "},{"type":"text","text":"in two runs."}]}]},
          "issuetype":{"name":"Story"},"priority":{"name":"High"},
          "assignee":{"displayName":"Ada Lovelace","emailAddress":"ada@acme.test"},
          "status":{"name":"In Progress"},"parent":{"key":"DD-1"},
          "labels":["fravashi-op-op-123"],"updated":"2026-09-22T01:02:03.000+0000"}}
        """;

    private static (AtlassianJiraGateway Gateway, AtlassianConfluencePageGatewayTests.Recorder Http) Build(
        Func<HttpRequestMessage, string?, HttpResponseMessage> respond, AtlassianConfig? config = null)
    {
        var recorder = new AtlassianConfluencePageGatewayTests.Recorder(respond);
        var options = Substitute.For<IOptionsMonitor<AtlassianConfig>>();
        options.CurrentValue.Returns(config ?? new AtlassianConfig
        {
            JiraKey = "token", JiraDomain = "acme.atlassian.net", JiraEmail = "me@acme.test", JiraProject = "DD",
        });
        return (new AtlassianJiraGateway(new HttpClient(recorder), options), recorder);
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, string body = "") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Fields(params (string Name, string? Value)[] fields) =>
        JsonSerializer.Serialize(fields.ToDictionary(f => f.Name, f => f.Value));

    // ---- reading ----------------------------------------------------------------------------------------

    [Fact]
    public async Task An_item_is_read_in_the_boards_own_field_names_with_the_sites_status_kept_verbatim()
    {
        var (gateway, http) = Build((_, _) => Answer(HttpStatusCode.OK, Issue));

        var result = await gateway.GetAsync("DD-7");

        result.IsOk.Should().BeTrue(result.Reason);
        var item = result.Value!;
        item.Key.Should().Be("DD-7");
        item.ProviderVersion.Should().Be("2026-09-22T01:02:03.000+0000", "Jira has no etag; its updated stamp is the version token, kept verbatim");
        item.NativeStatus.Should().Be("In Progress");
        item.Fields[nameof(WorkItem.Title)].Should().Be("Resume a conversation in place");
        item.Fields[nameof(WorkItem.Description)].Should().Be("First paragraph.\n\nSecond, in two runs.");
        item.Fields[nameof(WorkItem.Priority)].Should().Be("high");
        item.Fields[nameof(WorkItem.Assignee)].Should().Be("Ada Lovelace");
        item.Fields[nameof(WorkItem.Parent)].Should().Be("DD-1");

        http.Requests[0].Url.Should().StartWith("https://acme.atlassian.net/rest/api/3/issue/DD-7?fields=");
        http.Requests[0].Authorization.Should().StartWith("Basic ");
    }

    [Fact]
    public async Task An_item_the_site_no_longer_has_is_gone_which_is_what_a_tombstone_is_made_from()
    {
        var (gateway, _) = Build((_, _) => Answer(HttpStatusCode.NotFound, """{"errorMessages":["Issue does not exist"]}"""));

        (await gateway.GetAsync("DD-404")).Outcome.Should().Be(ProviderOutcome.Gone);
    }

    // ---- creating, and finding a creation again ---------------------------------------------------------

    [Fact]
    public async Task A_creation_is_stamped_with_its_operation_id_so_it_can_be_found_after_a_crash()
    {
        var (gateway, http) = Build((request, _) => request.Method == HttpMethod.Post
            ? Answer(HttpStatusCode.Created, """{"id":"10042","key":"DD-7"}""")
            : Answer(HttpStatusCode.OK, Issue));

        var result = await gateway.CreateAsync(
            Fields((nameof(WorkItem.Title), "Resume a conversation in place"), (nameof(WorkItem.Description), "First paragraph.\n\nSecond.")),
            "op 123/x");

        result.IsOk.Should().BeTrue(result.Reason);
        result.Value!.Key.Should().Be("DD-7");

        var post = http.Requests[0];
        post.Url.Should().Be("https://acme.atlassian.net/rest/api/3/issue");
        using var sent = JsonDocument.Parse(post.Body!);
        var fields = sent.RootElement.GetProperty("fields");
        fields.GetProperty("project").GetProperty("key").GetString().Should().Be("DD");
        fields.GetProperty("summary").GetString().Should().Be("Resume a conversation in place");
        fields.GetProperty("labels")[0].GetString().Should().Be("fravashi-op-op-123-x", "a label cannot hold a space or a slash");
        fields.GetProperty("description").GetProperty("content").GetArrayLength().Should().Be(2, "one paragraph per block");
    }

    [Fact]
    public async Task A_creation_that_landed_with_no_answer_is_found_by_its_stamp()
    {
        var (gateway, http) = Build((_, _) => Answer(HttpStatusCode.OK, $$"""{"issues":[{{Issue}}]}"""));

        var found = await gateway.FindByOperationIdAsync("op-123");

        found.IsOk.Should().BeTrue(found.Reason);
        found.Value!.Key.Should().Be("DD-7");
        using var sent = JsonDocument.Parse(http.Requests[0].Body!);
        sent.RootElement.GetProperty("jql").GetString()
            .Should().Be($"project = \"DD\" AND labels in (\"fravashi-op-op-123\", \"{Fravashi.Domain.FormerNames.TrackerStampPrefix}op-123\")",
                "scoped to the project, so another project's stamp is never adopted, and a creation the studio left in flight before "
                + "the internal rename is found by its former stamp (the bridge release)");
        http.Requests[0].Url.Should().Be("https://acme.atlassian.net/rest/api/3/search/jql");
    }

    [Fact]
    public async Task No_item_carrying_the_stamp_is_a_real_answer_and_means_the_write_never_landed()
    {
        var (gateway, _) = Build((_, _) => Answer(HttpStatusCode.OK, """{"issues":[]}"""));

        var found = await gateway.FindByOperationIdAsync("op-never");

        found.IsOk.Should().BeTrue("Ok(null) is the answer that lets the outbox send the creation again");
        found.Value.Should().BeNull();
    }

    [Fact]
    public async Task A_search_that_could_not_be_read_is_not_taken_for_no_such_item()
    {
        // Saying "not there" on an unreadable answer is what would make the outbox create a second issue.
        var (gateway, _) = Build((_, _) => Answer(HttpStatusCode.OK, "<html>a proxy's page</html>"));

        (await gateway.FindByOperationIdAsync("op-123")).Outcome.Should().Be(ProviderOutcome.Indeterminate);
    }

    // ---- changing ---------------------------------------------------------------------------------------

    [Fact]
    public async Task An_update_sends_only_the_fields_it_was_given_and_answers_with_the_item_as_it_now_is()
    {
        var (gateway, http) = Build((request, _) => request.Method == HttpMethod.Put
            ? Answer(HttpStatusCode.NoContent)
            : Answer(HttpStatusCode.OK, Issue));

        var result = await gateway.UpdateAsync(
            "DD-7", Fields((nameof(WorkItem.Title), "A new title")), "2026-09-22T01:02:03.000+0000", "op-9");

        result.IsOk.Should().BeTrue(result.Reason);
        var put = http.Requests.Single(r => r.Method == HttpMethod.Put);
        using var sent = JsonDocument.Parse(put.Body!);
        sent.RootElement.GetProperty("fields").EnumerateObject().Select(p => p.Name).Should().Equal("summary");
    }

    [Fact]
    public async Task An_update_against_an_item_that_moved_on_the_site_is_a_conflict_and_nothing_is_sent()
    {
        var (gateway, http) = Build((_, _) => Answer(HttpStatusCode.OK, Issue));

        var result = await gateway.UpdateAsync("DD-7", Fields((nameof(WorkItem.Title), "Mine")), "an-older-stamp", "op-9");

        result.Outcome.Should().Be(ProviderOutcome.Conflict);
        http.Requests.Should().NotContain(r => r.Method == HttpMethod.Put, "the site's change must not be overwritten");
    }

    [Fact]
    public async Task A_transition_picks_the_sites_own_transition_to_the_named_status()
    {
        const string transitions = """{"transitions":[{"id":"11","name":"Start","to":{"name":"In Progress"}},{"id":"31","name":"Finish","to":{"name":"Done"}}]}""";
        var (gateway, http) = Build((request, _) => request.Method == HttpMethod.Post
            ? Answer(HttpStatusCode.NoContent)
            : request.RequestUri!.AbsolutePath.EndsWith("/transitions", StringComparison.Ordinal)
                ? Answer(HttpStatusCode.OK, transitions)
                : Answer(HttpStatusCode.OK, Issue));

        var result = await gateway.TransitionAsync("DD-7", """{"NativeStatus":"done"}""", null, "op-10");

        result.IsOk.Should().BeTrue(result.Reason);
        http.Requests.Single(r => r.Method == HttpMethod.Post).Body.Should().Contain("\"31\"");
    }

    [Fact]
    public async Task A_status_the_sites_workflow_does_not_lead_to_from_here_is_refused_with_what_it_does_offer()
    {
        const string transitions = """{"transitions":[{"id":"11","name":"Start","to":{"name":"In Progress"}}]}""";
        var (gateway, http) = Build((request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/transitions", StringComparison.Ordinal)
                ? Answer(HttpStatusCode.OK, transitions)
                : Answer(HttpStatusCode.OK, Issue));

        var result = await gateway.TransitionAsync("DD-7", """{"NativeStatus":"Done"}""", null, "op-10");

        result.Outcome.Should().Be(ProviderOutcome.Refused);
        result.Reason.Should().Contain("Done").And.Contain("In Progress");
        http.Requests.Should().NotContain(r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task The_states_the_site_offers_for_an_issue_can_be_asked_for_on_their_own()
    {
        // Backlog 48: the same lookup a transition already made, asked as its own question, so a person can
        // be offered the states that really exist and are really reachable rather than names we guessed at.
        const string transitions = """
            {"transitions":[{"id":"11","name":"Start","to":{"name":"In Progress"}},
            {"id":"31","name":"Finish","to":{"name":"Done"}},
            {"id":"41","name":"Close","to":{"name":"Done"}}]}
            """;
        var (gateway, http) = Build((request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/transitions", StringComparison.Ordinal)
                ? Answer(HttpStatusCode.OK, transitions)
                : Answer(HttpStatusCode.OK, Issue));

        var offered = await gateway.OfferedTransitionsAsync("DD-7");

        offered.IsOk.Should().BeTrue();
        offered.Value.Should().Equal("In Progress", "Done");
        http.Requests.Should().NotContain(r => r.Method == HttpMethod.Post, "asking changes nothing");
        gateway.Capabilities.CanOfferTransitions.Should().BeTrue();
    }

    [Fact]
    public async Task A_site_that_offers_no_transition_answers_an_empty_list_rather_than_a_refusal()
    {
        // An empty workflow and an adapter that cannot answer are different things, and the caller acts
        // differently on each: one means the item cannot move there, the other means we do not know.
        var (gateway, _) = Build((request, _) =>
            request.RequestUri!.AbsolutePath.EndsWith("/transitions", StringComparison.Ordinal)
                ? Answer(HttpStatusCode.OK, """{"transitions":[]}""")
                : Answer(HttpStatusCode.OK, Issue));

        var offered = await gateway.OfferedTransitionsAsync("DD-7");

        offered.IsOk.Should().BeTrue();
        offered.Value.Should().BeEmpty();
    }

    // ---- how a call that did not succeed is classified ---------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ProviderOutcome.Refused)]
    [InlineData(HttpStatusCode.Forbidden, ProviderOutcome.Refused)]
    [InlineData(HttpStatusCode.BadRequest, ProviderOutcome.Refused)]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderOutcome.Unreachable)]
    [InlineData(HttpStatusCode.InternalServerError, ProviderOutcome.Indeterminate)]
    [InlineData(HttpStatusCode.BadGateway, ProviderOutcome.Indeterminate)]
    public async Task A_creation_the_site_did_not_accept_is_classified_by_what_is_known_about_it(
        HttpStatusCode status, ProviderOutcome expected)
    {
        var (gateway, _) = Build((_, _) => Answer(status, """{"errorMessages":["no"]}"""));

        (await gateway.CreateAsync(Fields((nameof(WorkItem.Title), "x")), "op-1")).Outcome.Should().Be(expected);
    }

    [Fact]
    public async Task A_call_that_never_left_is_unreachable_and_one_that_left_with_no_answer_is_not()
    {
        var (neverLeft, _) = Build((_, _) => throw new HttpRequestException(HttpRequestError.NameResolutionError));
        var (noAnswer, _) = Build((_, _) => throw new HttpRequestException(HttpRequestError.ResponseEnded));

        (await neverLeft.CreateAsync(Fields((nameof(WorkItem.Title), "x")), "op-1")).Outcome.Should().Be(ProviderOutcome.Unreachable);
        (await noAnswer.CreateAsync(Fields((nameof(WorkItem.Title), "x")), "op-1")).Outcome.Should().Be(ProviderOutcome.Indeterminate);
    }

    [Fact]
    public async Task With_no_jira_project_configured_nothing_is_sent_at_all()
    {
        var (gateway, http) = Build((_, _) => Answer(HttpStatusCode.OK, Issue), new AtlassianConfig());

        (await gateway.GetAsync("DD-7")).Outcome.Should().Be(ProviderOutcome.Unreachable);
        http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Asking_what_the_project_holds_searches_it_newest_first_and_reads_each_issue()
    {
        var (gateway, http) = Build((_, _) => Answer(HttpStatusCode.OK, $$"""{"issues":[{{Issue}}]}"""));

        var held = await gateway.ListAsync(null);

        held.IsOk.Should().BeTrue(held.Reason);
        held.Value!.Items.Should().ContainSingle().Which.Key.Should().Be("DD-7");
        held.Value.Cursor.Should().BeNull("no isLast at all, as every answer recorded before backlog 182 reads, is the last page");
        using var sent = JsonDocument.Parse(http.Requests[0].Body!);
        sent.RootElement.GetProperty("jql").GetString().Should().Be("project = \"DD\" ORDER BY updated DESC");
        sent.RootElement.GetProperty("maxResults").GetInt32().Should().Be(100);
        sent.RootElement.TryGetProperty("nextPageToken", out _).Should().BeFalse("the first page asks for no continuation");
    }

    [Fact]
    public async Task A_project_larger_than_one_page_is_paged_with_the_sites_own_continuation_until_it_says_there_is_no_more()
    {
        const string first = """
            {"issues":[{"id":"1","key":"DD-1","fields":{"summary":"First page","updated":"2026-10-01T00:00:00.000+0000"}}],
             "isLast":false,"nextPageToken":"page-2-token"}
            """;
        const string second = """
            {"issues":[{"id":"2","key":"DD-2","fields":{"summary":"Second page","updated":"2026-10-01T00:00:00.000+0000"}}],
             "isLast":true}
            """;

        var calls = 0;
        var (gateway, http) = Build((_, _) => Answer(HttpStatusCode.OK, calls++ == 0 ? first : second));

        var firstPage = await gateway.ListAsync(null);
        firstPage.IsOk.Should().BeTrue(firstPage.Reason);
        firstPage.Value!.Items.Should().ContainSingle().Which.Key.Should().Be("DD-1");
        firstPage.Value.Cursor.Should().Be("page-2-token");

        var secondPage = await gateway.ListAsync(firstPage.Value.Cursor);
        secondPage.IsOk.Should().BeTrue(secondPage.Reason);
        secondPage.Value!.Items.Should().ContainSingle().Which.Key.Should().Be("DD-2");
        secondPage.Value.Cursor.Should().BeNull("isLast:true says there is no more, whatever token rides along");

        using var sentSecond = JsonDocument.Parse(http.Requests[1].Body!);
        sentSecond.RootElement.GetProperty("nextPageToken").GetString().Should().Be("page-2-token",
            "the previous page's own continuation, round-tripped unexamined");
    }

    [Fact]
    public async Task An_issues_own_status_category_comes_across_so_an_unmapped_status_can_still_be_placed()
    {
        var (gateway, _) = Build((_, _) => Answer(HttpStatusCode.OK, IssueWithCategory));

        var read = await gateway.GetAsync("DD-7");

        read.Value!.NativeStatus.Should().Be("In Progress");
        read.Value.NativeCategory.Should().Be("indeterminate", "the KEY, which is the same on every site, not the translated name");
    }

    [Fact]
    public void It_says_what_it_can_do_rather_than_inferring_it_from_a_credential()
    {
        var (gateway, _) = Build((_, _) => Answer(HttpStatusCode.OK, Issue), new AtlassianConfig());

        gateway.Capabilities.Should().Be(new ProviderCapabilities(
            CanWrite: true,
            CanSearchByOperationId: true,
            CanTombstone: true,
            CanList: true,
            CanOfferTransitions: true));
        gateway.Connection.Should().Be("Jira");
    }
}
