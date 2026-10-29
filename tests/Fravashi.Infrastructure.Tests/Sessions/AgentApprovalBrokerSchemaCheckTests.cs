using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Sessions.Tools;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Follow-up filed 2026-10-22 (approval card with no arguments): an agent called the deferred studio tool
/// work_item_update before loading its schema with ToolSearch, so the call carried <c>{}</c>, and a person was asked to
/// approve a call the tool itself would refuse. A studio tool call its own declared schema could not accept is now
/// refused before anyone is asked, counted as refused without asking; everything else is decided exactly as before.
/// </summary>
public class AgentApprovalBrokerSchemaCheckTests
{
    private const string Update = AgentWorkGate.StudioToolPrefix + WorkItemUpdateTool.ToolName;

    private static readonly AgentSessionId Session = AgentSessionId.New();

    private static readonly string UpdateSchema =
        new WorkItemUpdateTool(Substitute.For<ITrackerResolver>(), Substitute.For<IWorkItemChanges>()).InputSchemaJson;

    private static AgentApprovalBroker Broker(
        Func<string, string?> schema, ISessionAttention? attention = null, TimeSpan? timeout = null)
    {
        var broker = new AgentApprovalBroker(
            timeout ?? TimeSpan.FromMinutes(5), attention: attention, studioToolSchema: schema);
        broker.RegisterSession(Session, "/workspaces/w1");
        return broker;
    }

    private static string? Schemas(string name) => name == WorkItemUpdateTool.ToolName ? UpdateSchema : null;

    /// <summary>Arguments as the endpoint builds them: a JSON string is text, anything else is its raw JSON.</summary>
    private static AgentToolInput Json(params (string Name, string Raw, bool Text)[] fields) =>
        AgentToolInput.Create(
            fields.ToDictionary(f => f.Name, f => f.Raw, StringComparer.Ordinal),
            fields.Where(f => f.Text).Select(f => f.Name));

    [Fact]
    public async Task An_update_with_no_arguments_is_refused_up_front_naming_the_missing_fields_and_nobody_is_asked()
    {
        var attention = Substitute.For<ISessionAttention>();
        var broker = Broker(Schemas, attention);
        var asked = false;
        AutomaticToolRefusal? refused = null;
        broker.Requested += _ => asked = true;
        broker.Refused += r => refused = r;

        var decision = await broker.RequestAsync(Session, Update, "toolu_1", AgentToolInput.Empty);

        decision.Approved.Should().BeFalse();
        decision.DecidedBy.Should().Be(ToolApprovalDecidedBy.Studio);
        decision.Reason.Should().Contain("missing required number, version");
        decision.Reason.Should().Contain("no arguments at all");
        decision.Reason.Should().Contain("ToolSearch");
        decision.Reason.Should().Contain("select:" + Update);
        decision.Reason.Length.Should().BeLessThanOrEqualTo(500, "a deny reason past 500 characters is cut on its way to the agent");
        asked.Should().BeFalse();
        broker.PendingFor(Session).Should().BeEmpty();
        refused.Should().NotBeNull();
        refused!.ToolName.Should().Be(Update);
        attention.Received(1).Record(Arg.Is<AttentionEntry>(e => e.Outcome == AttentionOutcome.Refused));
    }

    [Fact]
    public async Task A_required_field_of_the_wrong_kind_is_refused_naming_it()
    {
        var broker = Broker(Schemas);
        var asked = false;
        broker.Requested += _ => asked = true;

        var decision = await broker.RequestAsync(
            Session, Update, "toolu_1", Json(("number", "162", true), ("version", "3", false)));

        decision.Approved.Should().BeFalse();
        decision.Reason.Should().Contain("wrong kind for number (expected integer, got string)");
        decision.Reason.Should().NotContain("missing required");
        asked.Should().BeFalse();
    }

    [Fact]
    public async Task A_fractional_number_for_an_integer_is_the_wrong_kind()
    {
        var decision = await Broker(Schemas).RequestAsync(
            Session, Update, "toolu_1", Json(("number", "1.5", false), ("version", "3", false)));

        decision.Approved.Should().BeFalse();
        decision.Reason.Should().Contain("wrong kind for number");
    }

    [Fact]
    public async Task A_valid_update_still_asks_a_person_exactly_as_before()
    {
        var broker = Broker(Schemas);
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        var pending = broker.RequestAsync(
            Session, Update, "toolu_1",
            Json(("number", "162", false), ("version", "3", false), ("title", "A title", true), ("acceptanceCriteria", "[\"a\"]", false)));

        pending.IsCompleted.Should().BeFalse("a valid change to an existing item asks, as it always did");
        seen.Should().NotBeNull();
        broker.Answer(seen!.RequestId, approved: true, ToolApprovalDecision.UserApprovedReason);
        (await pending).Approved.Should().BeTrue();
    }

    [Fact]
    public async Task A_valid_graph_read_still_runs_unattended()
    {
        const string readSchema = """{"type":"object","properties":{"number":{"type":"integer"}},"required":["number"]}""";
        var broker = Broker(name => name == "work_item_read" ? readSchema : null);

        var decision = await broker.RequestAsync(
            Session, AgentWorkGate.StudioToolPrefix + "work_item_read", "toolu_1", Json(("number", "162", false)));

        decision.Approved.Should().BeTrue();
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("""{"type":"object","properties":{"number":{"type":"whole-number"}}}""")]
    [InlineData("""{"type":"object","required":"number"}""")]
    public async Task A_schema_the_check_cannot_read_leaves_the_call_asking_as_before(string schema)
    {
        var broker = Broker(_ => schema);
        var asked = false;
        broker.Requested += _ => asked = true;

        var pending = broker.RequestAsync(Session, Update, "toolu_1", Json(("number", "\"162\"", false)));

        pending.IsCompleted.Should().BeFalse();
        asked.Should().BeTrue();
        broker.ReleaseSession(Session);
        (await pending).Approved.Should().BeFalse();
    }

    [Fact]
    public async Task A_schema_source_that_throws_leaves_the_call_asking_as_before()
    {
        var broker = Broker(_ => throw new InvalidOperationException("no tools"));
        var asked = false;
        broker.Requested += _ => asked = true;

        var pending = broker.RequestAsync(Session, Update, "toolu_1", AgentToolInput.Empty);

        pending.IsCompleted.Should().BeFalse();
        asked.Should().BeTrue();
        broker.ReleaseSession(Session);
        await pending;
    }

    [Fact]
    public async Task A_built_in_tool_is_never_checked_against_a_studio_schema()
    {
        // Out of scope by design: only mcp__fravashi__* names are looked up.
        var looked = new List<string>();
        var broker = Broker(name => { looked.Add(name); return UpdateSchema; });

        var pending = broker.RequestAsync(Session, "Bash", "toolu_1", AgentToolInput.Empty);

        pending.IsCompleted.Should().BeFalse();
        looked.Should().BeEmpty();
        broker.ReleaseSession(Session);
        await pending;
    }

    [Theory]
    [InlineData("""{"properties":{"a":{"type":["string","null"]}}}""", "null", false, false)]
    [InlineData("""{"properties":{"a":{"type":["string","null"]}}}""", "5", false, true)]
    [InlineData("""{"properties":{"a":{"type":"boolean"}}}""", "true", false, false)]
    [InlineData("""{"properties":{"a":{"type":"array"}}}""", "{}", false, true)]
    [InlineData("""{"properties":{"a":{"type":"number"}}}""", "2.5", false, false)]
    [InlineData("""{"properties":{"a":{"description":"no type"}}}""", "2.5", false, false)]
    [InlineData("""{"properties":{"a":{"type":"string"}}}""", "x", true, false)]
    public void The_check_reads_only_the_top_level_kind(string schema, string raw, bool text, bool refused)
    {
        var verdict = StudioToolArgumentCheck.Check(
            AgentWorkGate.StudioToolPrefix + "t", schema, Json(("a", raw, text)));

        (verdict is not null).Should().Be(refused);
    }
}
