using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Application.Mirroring;
using Fravashi.Application.Onboarding;
using Fravashi.Application.Persistence;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Tests.Wiki;
using Fravashi.Infrastructure.Tracker.AzureDevOps;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Tracker;

/// <summary>
/// Backlog 143: Azure DevOps Boards behind the provider port, over recorded HTTP. What is held is what item 47 held for
/// Jira: a creation carries its stamp and can be found by it, a stale write is refused rather than applied, a move is to
/// a state the item's type has, and an answer that cannot be read is never taken for "no such item".
/// </summary>
public sealed class AzureDevOpsBoardsGatewayTests
{
    private const string Org = "https://dev.azure.com/acme";

    private const string Types = """{"count":3,"value":[{"name":"Epic"},{"name":"Issue"},{"name":"Task"}]}""";

    private const string States = """
        {"value":[{"name":"To Do","category":"Proposed"},{"name":"Doing","category":"InProgress"},
                  {"name":"Done","category":"Completed"}]}
        """;

    private static string Item(int id, int rev, string state = "To Do", string? description = null) => JsonSerializer.Serialize(new
    {
        id,
        rev,
        fields = new Dictionary<string, object?>
        {
            ["System.Title"] = "Resume a conversation in place",
            ["System.Description"] = description,
            ["System.State"] = state,
            ["System.WorkItemType"] = "Task",
            ["Microsoft.VSTS.Common.Priority"] = 2,
            ["System.AssignedTo"] = new { displayName = "Ada" },
            ["System.Parent"] = 7,
        },
    });

    private static (AzureDevOpsBoardsGateway Gateway, AtlassianConfluencePageGatewayTests.Recorder Http) Build(
        Func<HttpRequestMessage, string?, HttpResponseMessage> respond, bool configured = true)
    {
        var connections = Substitute.For<IProviderConnectionStore>();
        connections.Get("board").Returns(new ProviderConnection("board", ProviderKind.Board, Org + "/", "board:token", "Verified", DateTimeOffset.UtcNow));
        var settings = Substitute.For<IConfigurationStore>();
        settings.Get("Onboarding:Board:Provider").Returns(configured ? OnboardingProviders.AzureDevOps : OnboardingProviders.Jira);
        settings.Get("Onboarding:Board:Project").Returns("Studio Project");
        var secrets = Substitute.For<ISecretStore>();
        secrets.Get("board:token").Returns("my-pat");
        var recorder = new AtlassianConfluencePageGatewayTests.Recorder(respond);
        return (new AzureDevOpsBoardsGateway(new HttpClient(recorder), new AzureDevOpsBoardConnection(connections, settings, secrets)), recorder);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Answer(HttpRequestMessage request, string item)
    {
        var url = request.RequestUri!.AbsoluteUri;
        return url.Contains("/workitemtypes/Task/states", StringComparison.Ordinal) ? Json(States)
            : url.Contains("/workitemtypes?", StringComparison.Ordinal) ? Json(Types)
            : Json(item);
    }

    [Fact]
    public async Task A_creation_is_a_json_patch_of_a_task_carrying_its_stamp_and_reads_back_what_was_made()
    {
        var (gateway, http) = Build((request, _) => Answer(request, Item(41, 1)));
        var payload = JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            [nameof(WorkItem.Title)] = "Resume a conversation in place",
            [nameof(WorkItem.Description)] = "First <line>\nsecond line\n\nNext & last.",
            [nameof(WorkItem.Priority)] = StableNames.ToName(WorkItemPriority.High),
            [nameof(WorkItem.Parent)] = "7",
        });

        var created = await gateway.CreateAsync(payload, "op-123");

        created.IsOk.Should().BeTrue(created.Reason);
        var post = http.Requests.Single(r => r.Method == HttpMethod.Post);
        post.Url.Should().Be($"{Org}/Studio%20Project/_apis/wit/workitems/$Task?api-version=7.1");
        post.Authorization.Should().Be("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":my-pat")));
        post.Body.Should().Contain("\"path\":\"/fields/System.Title\"").And.Contain("\"value\":\"fravashi-op-op-123\"")
            .And.Contain("/fields/Microsoft.VSTS.Common.Priority\",\"value\":2")
            .And.Contain("System.LinkTypes.Hierarchy-Reverse").And.Contain($"{Org}/_apis/wit/workItems/7");
        var description = JsonDocument.Parse(post.Body!).RootElement.EnumerateArray()
            .Single(op => op.GetProperty("path").GetString() == "/fields/System.Description").GetProperty("value").GetString();
        description.Should().Be("<div>First &lt;line&gt;<br>second line</div><div>Next &amp; last.</div>");
        created.Value!.Key.Should().Be("41");
        created.Value.ProviderVersion.Should().Be("1");
        created.Value.NativeStatus.Should().Be("To Do");
        created.Value.NativeCategory.Should().Be("new");
        created.Value.Fields[nameof(WorkItem.Priority)].Should().Be(StableNames.ToName(WorkItemPriority.High));
        created.Value.Fields[nameof(WorkItem.Assignee)].Should().Be("Ada");
        created.Value.Fields[nameof(WorkItem.Parent)].Should().Be("7");
    }

    [Theory]
    [InlineData("One line")]
    [InlineData("First <tag> & \"quotes\"\nsecond line\n\nA second paragraph.")]
    [InlineData("")]
    public void A_description_reads_back_as_exactly_the_text_that_was_sent(string text)
    {
        // A reconcile compares what it sent with what it reads; a lossy round trip would look like an edit on the site.
        AzureDevOpsBoardsGateway.ToText(AzureDevOpsBoardsGateway.ToHtml(text)).Should().Be(text);
    }

    [Fact]
    public void A_description_the_site_stored_with_a_space_ending_each_block_still_reads_back_as_sent()
    {
        // Measured against a real Azure DevOps organisation, 2026-09-24: every block came back ending in a space.
        const string sent = "**Type** Feature | **Status** Done\n\n**Description.** The implementation.\nSecond line.";
        var stored = AzureDevOpsBoardsGateway.ToHtml(sent).Replace("</div>", " </div>", StringComparison.Ordinal);

        AzureDevOpsBoardsGateway.ToText(stored).Should().Be(sent);
        AzureDevOpsBoardsGateway.ToText("<div>a&nbsp;</div><div>b</div>").Should().Be("a\n\nb");
    }

    [Fact]
    public async Task A_change_carries_a_test_of_the_revision_and_a_stale_one_is_a_conflict()
    {
        var (gateway, http) = Build((request, _) => request.Method == HttpMethod.Patch
            ? Json("""{"message":"TF26071: This work item has been changed by someone else since you opened it."}""", HttpStatusCode.PreconditionFailed)
            : Answer(request, Item(41, 5)));

        var result = await gateway.UpdateAsync(
            "41", JsonSerializer.Serialize(new Dictionary<string, string?> { [nameof(WorkItem.Title)] = "New title" }), "4", "op-9");

        result.Outcome.Should().Be(ProviderOutcome.Conflict);
        http.Requests.Single(r => r.Method == HttpMethod.Patch).Body.Should()
            .StartWith("[{\"op\":\"test\",\"path\":\"/rev\",\"value\":4}");
    }

    [Fact]
    public async Task A_move_is_to_a_state_the_items_type_has_and_any_other_is_refused_naming_them()
    {
        var (gateway, http) = Build((request, _) => Answer(request, Item(41, 2, request.Method == HttpMethod.Patch ? "Doing" : "To Do")));

        var moved = await gateway.TransitionAsync("41", """{"NativeStatus":"doing"}""", "2", "op-1");
        var refused = await gateway.TransitionAsync("41", """{"NativeStatus":"Shipped"}""", "2", "op-2");

        moved.IsOk.Should().BeTrue(moved.Reason);
        moved.Value!.NativeStatus.Should().Be("Doing");
        moved.Value.NativeCategory.Should().Be("indeterminate");
        http.Requests.Single(r => r.Method == HttpMethod.Patch).Body.Should().Contain("\"path\":\"/fields/System.State\",\"value\":\"Doing\"");
        refused.Outcome.Should().Be(ProviderOutcome.Refused);
        refused.Reason.Should().Contain("To Do, Doing, Done");
    }

    [Fact]
    public async Task A_lost_creation_is_found_by_its_stamp_and_an_empty_answer_is_a_real_no()
    {
        var found = Build((request, body) => request.Method == HttpMethod.Post && body!.Contains("fravashi-op-op-7", StringComparison.Ordinal)
            ? Json("""{"workItems":[{"id":41}]}""")
            : Answer(request, Item(41, 1)));
        var none = Build((request, _) => Json("""{"workItems":[]}"""));

        (await found.Gateway.FindByOperationIdAsync("op-7")).Value!.Key.Should().Be("41");
        var nothing = await none.Gateway.FindByOperationIdAsync("op-8");
        nothing.IsOk.Should().BeTrue();
        nothing.Value.Should().BeNull();
    }

    [Fact]
    public async Task A_creation_left_in_flight_before_the_rename_is_found_by_its_former_stamp()
    {
        // The bridge release: the studio stamped its creations with the former prefix before the internal rename, and a
        // creation that left with no answer then must be found, never made twice.
        var former = Fravashi.Domain.FormerNames.TrackerStampPrefix + "op-9";
        var (gateway, http) = Build((request, body) => request.Method == HttpMethod.Post && body!.Contains(former, StringComparison.Ordinal)
            ? Json("""{"workItems":[{"id":42}]}""")
            : Answer(request, Item(42, 1)));

        (await gateway.FindByOperationIdAsync("op-9")).Value!.Key.Should().Be("42");
        http.Requests.First(r => r.Method == HttpMethod.Post).Body.Should().Contain("fravashi-op-op-9").And.Contain(former);
    }

    [Fact]
    public async Task A_search_answer_that_cannot_be_read_is_not_taken_for_no_such_item()
    {
        var (gateway, _) = Build((_, _) => Json("""{"unexpected":true}"""));

        var result = await gateway.FindByOperationIdAsync("op-7");

        result.Outcome.Should().Be(ProviderOutcome.Indeterminate);
    }

    [Fact]
    public async Task A_credential_answered_with_the_sign_in_page_is_refused_not_taken_for_a_yes()
    {
        var (gateway, _) = Build((_, _) => new HttpResponseMessage(HttpStatusCode.NonAuthoritativeInformation)
        {
            Content = new StringContent("<html>Sign in</html>", Encoding.UTF8, "text/html"),
        });

        var result = await gateway.GetAsync("41");

        result.Outcome.Should().Be(ProviderOutcome.Refused);
        result.Reason.Should().Contain("did not accept the board's credential");
    }

    [Fact]
    public async Task With_no_azure_devops_board_configured_nothing_is_sent()
    {
        var (gateway, http) = Build((_, _) => Json("{}"), configured: false);

        gateway.IsConfigured.Should().BeFalse();
        (await gateway.GetAsync("41")).Outcome.Should().Be(ProviderOutcome.Unreachable);
        http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_project_larger_than_the_batch_read_limit_is_paged_through_its_ids_with_wiql_asked_once()
    {
        // backlog 182: WIQL names every id the project has, ONCE; the batch read (its own 200-id ceiling,
        // unrelated to WIQL's) is what is actually paged, over however many calls that takes.
        var ids = Enumerable.Range(1, 250).ToArray();
        var wiqlCalls = 0;
        var batches = new List<string>();

        var (gateway, _) = Build((request, _) =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("_apis/wit/wiql", StringComparison.Ordinal))
            {
                wiqlCalls++;
                return Json(JsonSerializer.Serialize(new { workItems = ids.Select(id => new { id }) }));
            }

            if (url.Contains("/workitemtypes/Task/states", StringComparison.Ordinal))
            {
                return Json(States);
            }

            var requestedIds = request.RequestUri!.Query.Split("ids=")[1].Split('&')[0];
            batches.Add(requestedIds);
            var value = string.Join(",", requestedIds.Split(',').Select(id => Item(int.Parse(id, System.Globalization.CultureInfo.InvariantCulture), 1)));
            return Json($"{{\"value\":[{value}]}}");
        });

        var firstPage = await gateway.ListAsync(null);
        firstPage.IsOk.Should().BeTrue(firstPage.Reason);
        firstPage.Value!.Items.Should().HaveCount(200, "the batch read's own limit, not WIQL's");
        firstPage.Value.Cursor.Should().NotBeNull("50 ids are still waiting to be read");

        var secondPage = await gateway.ListAsync(firstPage.Value.Cursor);
        secondPage.IsOk.Should().BeTrue(secondPage.Reason);
        secondPage.Value!.Items.Should().HaveCount(50);
        secondPage.Value.Cursor.Should().BeNull("every id WIQL named has now been read");

        wiqlCalls.Should().Be(1, "the second page's ids rode in its own cursor; WIQL was not asked again");
        batches.Should().HaveCount(2);
        batches[0].Split(',').Should().HaveCount(200);
        batches[1].Split(',').Should().HaveCount(50);
    }
}
