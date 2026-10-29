using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Fravashi.Application.Mirroring;
using Fravashi.Application.Network;
using Fravashi.Domain.Mirroring;
using Fravashi.Domain.Projects;
using Fravashi.Infrastructure.Mirroring;
using Fravashi.Infrastructure.Tests.WorkItems;
using Fravashi.Infrastructure.Tracker.Atlassian;
using Fravashi.Infrastructure.WorkItems;
using Fravashi.Testing.Network;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Mirroring;

/// <summary>
/// Backlog 232 (plan @58): the board's write path through #228's scripted network, end to end. The real Jira adapter,
/// the real outbox drain and SQLite stores, and a fake Jira SITE behind the scripted handler that holds every creation
/// whose body ARRIVED, answered or not. That is what makes a duplicate countable: the site's creations are the
/// journal's arrived <c>POST /issue</c> calls, so "exactly one issue" is a fact about the wire and not a call count.
/// <para>
/// Nothing here drains by itself. Every send is a "Sync now", as in the product, because syncing writes to somebody's
/// tracker; the tests press it again as a person would after the network comes back.
/// </para>
/// </summary>
public sealed partial class ProviderSyncUnderNetworkFaultTests : IDisposable
{
    private readonly LocalBoardFixture _board = new();
    private readonly SqliteMirrorStore _mirror;
    private readonly SqliteOutboxStore _outbox;
    private readonly FakeNetworkClock _clock = new();
    private readonly ProviderOutages _outages;

    public ProviderSyncUnderNetworkFaultTests()
    {
        _mirror = new SqliteMirrorStore(_board.Factory);
        _outbox = new SqliteOutboxStore(_board.Factory);
        _outages = new ProviderOutages(_clock);
    }

    public void Dispose() => _board.Dispose();

    [Fact]
    public async Task A_creation_reset_after_its_body_arrived_is_settled_by_the_lookup_and_never_made_twice()
    {
        var site = new FakeJiraSite(new NetworkScript(_clock,
            new NetworkPhase(NetworkBehaviour.ResetAfterWrite, Calls: 1),
            new NetworkPhase(NetworkBehaviour.Healthy)));
        var item = await QueueCreationAsync(site, "Sent as the VPN reconnected");

        await Sync(site).SyncNowAsync(ProjectId.Default);

        site.Creations.Should().Be(1, "the body arrived and the site made the issue; only the answer was lost");
        (await _outbox.ForItemAsync(ProjectId.Default, item)).Single().State.Should().Be(OutboxState.Unresolved,
            "a call that left with no answer is identical to a crash: looked up, never requeued");
        _outages.For("Jira").Should().BeNull("a lone reset after write proves nothing about an outage");

        await Sync(site).SyncNowAsync(ProjectId.Default);

        site.Creations.Should().Be(1, "the lookup found the issue by its stamp, so nothing was sent again");
        site.Handler.Journal.Should().Contain(c => c.Uri!.AbsolutePath.EndsWith("/search/jql", StringComparison.Ordinal),
            "the creation was settled by asking the site for its stamp");
        (await _mirror.GetAsync(ProjectId.Default, item))!.Key.Should().Be("DD-1");
        (await OpenCreationsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_creation_refused_before_sending_is_requeued_named_as_an_outage_and_later_lands_once()
    {
        var site = new FakeJiraSite(new NetworkScript(_clock,
            new NetworkPhase(NetworkBehaviour.Refuses, Calls: 1),
            new NetworkPhase(NetworkBehaviour.Healthy)));
        var item = await QueueCreationAsync(site, "Sent while the site refused");

        var first = await Sync(site).SyncNowAsync(ProjectId.Default);

        first.Happened.Should().BeFalse();
        first.Sentence.Should().Be(
            "Jira unreachable since 12:00 (connection refused); 1 change(s) waiting; nothing lost. What this page shows is "
            + "this board's own copy, and Sync now sends what waits once Jira answers.");
        (await Sync(site).OutageAsync(ProjectId.Default)).Should().Be(first.Sentence);
        site.Creations.Should().Be(0);
        (await _outbox.ForItemAsync(ProjectId.Default, item)).Single().State.Should().Be(OutboxState.Pending,
            "a call that provably never left is safe to send again");

        _clock.Advance(TimeSpan.FromMinutes(2));
        var second = await Sync(site).SyncNowAsync(ProjectId.Default);

        second.Happened.Should().BeTrue(second.Sentence);
        site.Creations.Should().Be(1);
        (await _mirror.GetAsync(ProjectId.Default, item))!.Key.Should().Be("DD-1");
        (await Sync(site).OutageAsync(ProjectId.Default)).Should().BeNull("Jira answered, so the line is gone");
    }

    [Theory]
    [InlineData("F1", true)]
    [InlineData("F2", true)]
    [InlineData("F3", false)]
    [InlineData("F4", true)]
    public async Task Through_each_fault_the_queue_is_kept_and_drains_once_when_the_provider_answers(string fault, bool namesAnOutage)
    {
        var script = fault switch
        {
            "F1" => NetworkScript.F1(_clock),
            "F2" => NetworkScript.F2(_clock),
            "F3" => NetworkScript.F3(_clock),
            _ => NetworkScript.F4(_clock),
        };
        var site = new FakeJiraSite(script);
        var item = await QueueCreationAsync(site, $"Sent through {fault}");
        var sawOutage = false;

        // A person pressing Sync now every ten seconds until it lands, for at most ten minutes.
        for (var press = 0; press < 60 && await _mirror.GetAsync(ProjectId.Default, item) is null; press++)
        {
            await Sync(site).SyncNowAsync(ProjectId.Default);
            sawOutage |= await Sync(site).OutageAsync(ProjectId.Default) is not null;

            site.Creations.Should().BeLessThanOrEqualTo(1, $"press {press}: a creation is never made twice");
            if (await _mirror.GetAsync(ProjectId.Default, item) is null)
            {
                (await OpenCreationsAsync()).Should().ContainSingle($"press {press}: the creation waits, it is not lost");
            }

            _clock.Advance(TimeSpan.FromSeconds(10));
        }

        site.Creations.Should().Be(1);
        (await _mirror.GetAsync(ProjectId.Default, item))!.Key.Should().Be("DD-1");
        (await OpenCreationsAsync()).Should().BeEmpty();
        sawOutage.Should().Be(namesAnOutage, "an outage is named only from a call that provably never left");
        (await Sync(site).OutageAsync(ProjectId.Default)).Should().BeNull("the provider answered in the end");
    }

    private async Task<Fravashi.Domain.WorkItems.WorkItemId> QueueCreationAsync(FakeJiraSite site, string title)
    {
        var item = await _board.CreateAsync(LocalBoardFixture.NewTask(title));
        var queued = await Sync(site).SendAsync(ProjectId.Default, item.Id);
        queued.Happened.Should().BeTrue(queued.Sentence);
        return item.Id;
    }

    private async Task<IReadOnlyList<OutboxEntry>> OpenCreationsAsync() =>
        [.. (await _outbox.OpenAsync(ProjectId.Default)).Where(e => e.Kind == OutboxKind.Create)];

    private ProviderSync Sync(FakeJiraSite site)
    {
        var options = Substitute.For<IOptionsMonitor<AtlassianConfig>>();
        options.CurrentValue.Returns(new AtlassianConfig
        {
            JiraKey = "token", JiraDomain = "acme.atlassian.net", JiraEmail = "me@acme.test", JiraProject = "DD",
        });
        var gateway = new AtlassianJiraGateway(new HttpClient(site.Handler), options);
        var gateways = Substitute.For<IProviderGateways>();
        gateways.For(Arg.Any<ProjectId>()).Returns(gateway);

        return new ProviderSync(
            gateways,
            new LocalTrackerResolver(_board.Factory, _board.Clock),
            _outbox,
            _mirror,
            new OutboxDrain(_outbox, _mirror, _clock),
            new ReconcilePass(_mirror, _outbox, _clock),
            _clock,
            outages: _outages);
    }

    /// <summary>
    /// A Jira site as the wire sees it: it holds every creation whose body arrived, in order, keyed DD-1, DD-2 and so on;
    /// it answers a stamp search from those; and it answers a read of an issue it holds.
    /// </summary>
    private sealed partial class FakeJiraSite
    {
        public FakeJiraSite(NetworkScript script) => Handler = new ScriptedHttpHandler(script, Answer);

        public ScriptedHttpHandler Handler { get; }

        /// <summary>How many issues the site made: creations whose body arrived, answered or not.</summary>
        public int Creations => Held().Count;

        private List<JsonObject> Held() =>
        [
            .. Handler.Journal
                .Where(c => c.Arrived && c.Method == HttpMethod.Post && c.Uri!.AbsolutePath.EndsWith("/issue", StringComparison.Ordinal))
                .Select((c, i) =>
                {
                    var fields = JsonNode.Parse(c.BodyArrived!)!["fields"]!.AsObject();
                    return new JsonObject
                    {
                        ["id"] = $"{10000 + i + 1}",
                        ["key"] = $"DD-{i + 1}",
                        ["fields"] = new JsonObject
                        {
                            ["summary"] = fields["summary"]?.DeepClone(),
                            ["description"] = fields["description"]?.DeepClone(),
                            ["labels"] = fields["labels"]?.DeepClone(),
                            ["status"] = new JsonObject
                            {
                                ["name"] = "To Do",
                                ["statusCategory"] = new JsonObject { ["key"] = "new", ["name"] = "To Do" },
                            },
                            ["updated"] = "2026-10-07T12:00:00.000+0000",
                        },
                    };
                }),
        ];

        private HttpResponseMessage Answer(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            var held = Held();

            if (request.Method == HttpMethod.Post && path.EndsWith("/issue", StringComparison.Ordinal))
            {
                // This call is already in the journal, so it is the last one held.
                var made = held[^1];
                return Json(new JsonObject { ["id"] = made["id"]!.DeepClone(), ["key"] = made["key"]!.DeepClone() });
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/search/jql", StringComparison.Ordinal))
            {
                var jql = JsonNode.Parse(Handler.Journal[^1].BodyArrived!)!["jql"]!.GetValue<string>();
                var stamp = Stamp().Match(jql);
                var issues = held.Where(issue => !stamp.Success
                    || issue["fields"]!["labels"]!.AsArray().Any(l => l!.GetValue<string>() == stamp.Groups[1].Value));
                return Json(new JsonObject
                {
                    ["issues"] = new JsonArray([.. issues.Select(i => i.DeepClone())]),
                    ["isLast"] = true,
                });
            }

            if (request.Method == HttpMethod.Get && path.Contains("/issue/", StringComparison.Ordinal) && !path.EndsWith("/transitions", StringComparison.Ordinal))
            {
                var key = path[(path.LastIndexOf('/') + 1)..];
                return held.FirstOrDefault(i => i["key"]!.GetValue<string>() == key) is { } issue
                    ? Json(issue)
                    : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
            }

            return Json(new JsonObject { ["transitions"] = new JsonArray() });
        }

        private static HttpResponseMessage Json(JsonNode body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

        [GeneratedRegex("labels = \"([^\"]+)\"")]
        private static partial Regex Stamp();
    }
}
