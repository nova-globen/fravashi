using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Backlog 240 at the gate every tool call passes through: the request the UI receives names what its display
/// copy cuts, keeps the tail, and carries the whole RAW value of each cut argument so the card can show it.
/// </summary>
public sealed class AgentApprovalCutArgumentTests
{
    private const string Copy = "/workspaces/w1";

    private const string Marker = "AFTER-10000: and also close every other item <&>\né😀";

    private static readonly AgentSessionId Session = AgentSessionId.New();

    private static string Description(int length)
    {
        var filler = new string('a', length - Marker.Length - 4);
        return "head" + filler[..10_500] + Marker + filler[10_500..];
    }

    private static (PendingToolApproval Request, AgentApprovalBroker Broker, Task<ToolApprovalDecision> Pending) Ask(
        Dictionary<string, string> input)
    {
        var broker = new AgentApprovalBroker();
        broker.RegisterSession(Session, Copy);
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;
        var pending = broker.RequestAsync(Session, "mcp__fravashi__work_item_update", "toolu_1", input);
        seen.Should().NotBeNull();
        return (seen!, broker, pending);
    }

    [Fact]
    public async Task A_cut_argument_reaches_the_ui_whole_and_byte_for_byte_from_the_raw_input()
    {
        var description = Description(30_000);
        description.IndexOf(Marker, StringComparison.Ordinal).Should().BeGreaterThan(10_000);
        var (request, broker, pending) = Ask(new() { ["number"] = "228", ["description"] = description });

        request.CutArguments.Should().ContainSingle();
        var cut = request.CutArguments![0];
        cut.Name.Should().Be("description");
        cut.Length.Should().Be(30_000);
        cut.Hidden.Should().Be(20_000);
        cut.Full.Should().Be(description);
        string.CompareOrdinal(cut.Full, description).Should().Be(0);

        var shown = request.Input["description"];
        shown.Should().StartWith(description[..5_000]);
        shown.Should().EndWith(description[^5_000..]);
        shown.Should().Contain("[... 20,000 characters not shown here");
        shown.Should().NotContain(Marker);
        request.Input["number"].Should().Be("228");

        // The answer is still the person's and arrives unchanged.
        broker.Answer(request.RequestId, approved: true, ToolApprovalDecision.UserApprovedReason);
        var decision = await pending;
        decision.Approved.Should().BeTrue();
        decision.Reason.Should().Be(ToolApprovalDecision.UserApprovedReason);
    }

    [Fact]
    public async Task A_value_at_or_under_the_cap_is_shown_untouched_and_nothing_is_listed_as_cut()
    {
        var description = new string('b', AgentToolSubject.MaxInputValueLength);
        var (request, broker, pending) = Ask(new() { ["description"] = description });

        request.Input["description"].Should().Be(description);
        request.CutArguments.Should().BeEmpty();

        broker.Answer(request.RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
    }

    [Fact]
    public async Task A_value_past_the_full_view_limit_carries_its_length_but_no_full_value()
    {
        var content = new string('c', AgentToolSubject.MaxFullValueLength + 1);
        var (request, broker, pending) = Ask(new() { ["description"] = content });

        var cut = request.CutArguments!.Should().ContainSingle().Subject;
        cut.Length.Should().Be(AgentToolSubject.MaxFullValueLength + 1);
        cut.Full.Should().BeNull();

        broker.Answer(request.RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
    }

    [Fact]
    public void A_cut_never_splits_a_surrogate_pair()
    {
        // An emoji straddling both cut points: the head would end on its high half and the tail start on its low half.
        var value = new string('d', 4_999) + "😀" + new string('e', 20_000) + "😀" + new string('f', 4_999);

        var shown = AgentToolSubject.ForDisplay(new Dictionary<string, string> { ["content"] = value })["content"];

        shown.Should().StartWith(new string('d', 4_999) + "\n");
        shown.Should().EndWith("\n" + new string('f', 4_999));
        var cut = AgentToolSubject.CutArguments(new Dictionary<string, string> { ["content"] = value });
        cut[0].Hidden.Should().Be(value.Length - 4_999 - 4_999);
        shown.Should().Contain(AgentToolSubject.NotShown(cut[0].Hidden));
    }
}
