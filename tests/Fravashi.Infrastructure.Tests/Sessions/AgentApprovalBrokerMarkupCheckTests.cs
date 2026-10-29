using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Sessions;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Work item #356: seen 2026-10-23, a conversation wrote leaked tool-call markup into a work item's description and
/// retried 19 times, each an approval. A studio tool call whose string argument ends in that markup is refused up front,
/// in words, counted as refused without asking; it only ever turns an ask into a refusal.
/// </summary>
public class AgentApprovalBrokerMarkupCheckTests
{
    private const string Update = AgentWorkGate.StudioToolPrefix + "work_item_update";

    private static readonly AgentSessionId Session = AgentSessionId.New();

    private static AgentApprovalBroker Broker(ISessionAttention? attention = null)
    {
        var broker = new AgentApprovalBroker(TimeSpan.FromMinutes(5), attention: attention);
        broker.RegisterSession(Session, "/workspaces/w1");
        return broker;
    }

    private static AgentToolInput Json(params (string Name, string Raw, bool Text)[] fields) =>
        AgentToolInput.Create(
            fields.ToDictionary(f => f.Name, f => f.Raw, StringComparer.Ordinal),
            fields.Where(f => f.Text).Select(f => f.Name));

    [Theory]
    [InlineData("The fix.\n</parameter>")]
    [InlineData("The fix.</invoke>")]
    [InlineData("The fix.\n</parameter>\n</invoke>\n  ")]
    public async Task A_description_ending_in_leaked_markup_is_refused_in_words_and_nobody_is_asked(string description)
    {
        var attention = Substitute.For<ISessionAttention>();
        var broker = Broker(attention);
        var asked = false;
        AutomaticToolRefusal? refused = null;
        broker.Requested += _ => asked = true;
        broker.Refused += r => refused = r;

        var decision = await broker.RequestAsync(
            Session, Update, "toolu_1", Json(("number", "23", false), ("description", description, true)));

        decision.Approved.Should().BeFalse();
        decision.DecidedBy.Should().Be(ToolApprovalDecidedBy.Studio);
        decision.Reason.Should().Contain("description");
        decision.Reason.Should().Contain("Remove that trailing markup");
        decision.Reason.Length.Should().BeLessThanOrEqualTo(500);
        asked.Should().BeFalse();
        broker.PendingFor(Session).Should().BeEmpty();
        refused.Should().NotBeNull();
        attention.Received(1).Record(Arg.Is<AttentionEntry>(e => e.Outcome == AttentionOutcome.Refused));
    }

    [Fact]
    public async Task Markup_at_the_end_of_a_criterion_inside_a_json_array_is_refused_too()
    {
        var decision = await Broker().RequestAsync(
            Session, Update, "toolu_1",
            Json(("number", "23", false), ("acceptanceCriteria", "[\"first\",\"second</parameter>\"]", false)));

        decision.Approved.Should().BeFalse();
        decision.Reason.Should().Contain("acceptanceCriteria");
    }

    [Fact]
    public async Task Markup_in_the_middle_of_a_value_still_asks_a_person()
    {
        var broker = Broker();
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        var pending = broker.RequestAsync(
            Session, Update, "toolu_1",
            Json(("number", "23", false), ("description", "Explains why </parameter> leaked, then says more.", true)));

        pending.IsCompleted.Should().BeFalse("only a value that ENDS in the markup is refused");
        seen.Should().NotBeNull();
        broker.Answer(seen!.RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
    }

    [Fact]
    public void A_built_in_tool_is_never_looked_at()
    {
        StudioToolMarkupCheck.Check("Write", Json(("content", "<x>\n</parameter>", true))).Should().BeNull();
        StudioToolMarkupCheck.Check("mcp__other__tool", Json(("text", "a</invoke>", true))).Should().BeNull();
    }

    [Theory]
    [InlineData("done </parameter>", true)]
    [InlineData("done</invoke>\r\n", true)]
    [InlineData("done </function_calls>", true)]
    [InlineData("done </param>", false)]
    [InlineData("done", false)]
    [InlineData("", false)]
    public void Ends_in_markup_reads_only_the_trailing_closing_tags(string text, bool expected) =>
        StudioToolMarkupCheck.EndsInMarkup(text).Should().Be(expected);
}
