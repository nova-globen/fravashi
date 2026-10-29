using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Backlog 108 at the gate every tool call passes through. The value here is made up; nothing in this file
/// is, or resembles, a real credential.
/// </summary>
public sealed class AgentApprovalCredentialTests
{
    private const string Value = "not-a-real-key-0123456789abcdef";
    private const string Copy = "/workspaces/w1";

    private static readonly AgentSessionId Session = AgentSessionId.New();

    private static AgentApprovalBroker Broker(Func<IReadOnlyCollection<AgentHeldCredential>>? held = null) =>
        new(heldCredentials: held ?? (() => [new AgentHeldCredential("PROVIDER_API_KEY", Value)]));

    [Theory]
    [InlineData("Write", "content")]
    [InlineData("Bash", "command")]
    [InlineData("Grep", "pattern")]
    public async Task A_call_that_carries_the_value_is_refused_without_asking_and_the_value_is_in_nothing_it_says(
        string tool, string field)
    {
        var broker = Broker();
        broker.RegisterSession(Session, Copy);
        var asked = false;
        AutomaticToolRefusal? refusal = null;
        broker.Requested += _ => asked = true;
        broker.Refused += r => refusal = r;

        var decision = await broker.RequestAsync(
            Session, tool, "toolu_1", new Dictionary<string, string> { [field] = $"x {Value} y", ["file_path"] = Copy + "/a.txt" });

        decision.Approved.Should().BeFalse();
        asked.Should().BeFalse("there is no answer a person could give that makes this call right");
        decision.Reason.Should().Contain("PROVIDER_API_KEY").And.NotContain(Value);

        // Raised, never returned in silence (backlog 117), and the record that is kept does not keep the value.
        refusal.Should().NotBeNull();
        refusal!.Detail.Should().NotContain(Value);
        refusal.Reason.Should().NotContain(Value);
    }

    [Fact]
    public async Task A_command_that_names_the_variable_asks_and_the_alarm_is_the_first_thing_on_the_prompt()
    {
        var broker = Broker();
        broker.RegisterSession(Session, Copy);
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        var pending = broker.RequestAsync(
            Session, "Bash", "toolu_1", new Dictionary<string, string> { ["command"] = "echo $PROVIDER_API_KEY > notes.txt" });

        pending.IsCompleted.Should().BeFalse("naming a variable is not refused: the studio cannot know what a shell does with it");
        seen.Should().NotBeNull();
        var first = seen!.Effects![0];
        first.Weight.Should().Be(AgentEffectWeight.Alarm);
        first.Notice.Should().Contain("PROVIDER_API_KEY").And.NotContain(Value);

        broker.Answer(seen.RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
    }

    /// <summary>
    /// Backlog 241, through the exact name a live call carries: a graph-text tool only stores its text, so the
    /// card has no whole-environment line, while the same text in a Bash command keeps it.
    /// </summary>
    [Theory]
    [InlineData("work_item_update", "description")]
    [InlineData("wiki_write", "body")]
    public async Task A_graph_text_tool_mentioning_printenv_carries_no_whole_environment_line(string bareTool, string field)
    {
        var broker = Broker();
        broker.RegisterSession(Session, Copy);
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        var pending = broker.RequestAsync(
            Session,
            AgentWorkGate.StudioToolPrefix + bareTool,
            "toolu_1",
            new Dictionary<string, string> { ["number"] = "241", ["version"] = "1", [field] = "Bare printenv alarms." });

        seen.Should().NotBeNull();
        (seen!.Effects ?? []).Should().NotContain(e => e.Notice.Contains("whole environment"));

        broker.Answer(seen.RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
    }

    [Fact]
    public async Task A_bash_command_reading_the_whole_environment_still_carries_the_line()
    {
        var broker = Broker();
        broker.RegisterSession(Session, Copy);
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        var pending = broker.RequestAsync(
            Session, "Bash", "toolu_1", new Dictionary<string, string> { ["command"] = "printenv > dump.txt" });

        seen.Should().NotBeNull();
        seen!.Effects.Should().Contain(e => e.Notice.Contains("whole environment"));

        broker.Answer(seen.RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
    }

    [Fact]
    public async Task A_bash_command_printing_one_variable_carries_no_credential_line()
    {
        var broker = Broker();
        broker.RegisterSession(Session, Copy);
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        var pending = broker.RequestAsync(
            Session, "Bash", "toolu_1",
            new Dictionary<string, string> { ["command"] = "dotnet test ; printenv PLAYWRIGHT_BROWSERS_PATH" });

        seen.Should().NotBeNull();
        (seen!.Effects ?? []).Should().NotContain(e => e.Notice.Contains("credential the agent is running with"));

        broker.Answer(seen.RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
    }

    /// <summary>
    /// Backlog 314, through the gate: the card #313's host saw. A plain build whose Bash description ended in "env"
    /// carried "This reads the whole environment"; the description is a label and is not read by either alarm.
    /// </summary>
    [Fact]
    public async Task A_plain_build_whose_bash_description_ends_in_env_carries_no_credential_line()
    {
        var broker = Broker();
        broker.RegisterSession(Session, Copy);
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        var pending = broker.RequestAsync(
            Session, "Bash", "toolu_1",
            new Dictionary<string, string>
            {
                ["command"] = "dotnet build tests/Fravashi.Web.Tests && dotnet test tests/Fravashi.Web.Tests --no-build 2>&1 | tail -40",
                ["description"] = "Build and run full AutopilotCardTests suite without live browser env",
            });

        seen.Should().NotBeNull();
        (seen!.Effects ?? []).Should().NotContain(e => e.Notice.Contains("credential the agent is running with"));

        broker.Answer(seen.RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
    }

    /// <summary>Backlog 314 scoped the alarms, never the refusal: the value in a Bash description is still refused.</summary>
    [Fact]
    public async Task A_bash_description_carrying_the_value_is_still_refused()
    {
        var broker = Broker();
        broker.RegisterSession(Session, Copy);
        var asked = false;
        broker.Requested += _ => asked = true;

        var decision = await broker.RequestAsync(
            Session, "Bash", "toolu_1",
            new Dictionary<string, string> { ["command"] = "dotnet build", ["description"] = $"Build with {Value}" });

        decision.Approved.Should().BeFalse();
        asked.Should().BeFalse();
        decision.Reason.Should().Contain("PROVIDER_API_KEY").And.NotContain(Value);
    }

    /// <summary>Backlog 241 moved an alarm, never a refusal: a graph-text tool carrying the value is refused too.</summary>
    [Theory]
    [InlineData("work_item_update", "description")]
    [InlineData("wiki_write", "body")]
    public async Task A_graph_text_tool_carrying_the_value_is_still_refused(string bareTool, string field)
    {
        var broker = Broker();
        broker.RegisterSession(Session, Copy);
        var asked = false;
        broker.Requested += _ => asked = true;

        var decision = await broker.RequestAsync(
            Session,
            AgentWorkGate.StudioToolPrefix + bareTool,
            "toolu_1",
            new Dictionary<string, string> { ["number"] = "241", ["version"] = "1", [field] = $"the key is {Value}" });

        decision.Approved.Should().BeFalse();
        asked.Should().BeFalse();
        decision.Reason.Should().Contain("PROVIDER_API_KEY").And.NotContain(Value);
    }

    [Fact]
    public async Task A_denied_call_issued_again_asks_again_and_is_never_let_through_on_the_second_try()
    {
        // The first clause of backlog 108. What the owner saw on 2026-08-13 read as "denied, then it ran":
        // the first call was abandoned and mislabelled, and the second was approved by hand. This holds the
        // property that reading suggested was missing: a denial buys the same call nothing.
        var broker = Broker();
        broker.RegisterSession(Session, Copy);
        var prompts = new List<PendingToolApproval>();
        broker.Requested += prompts.Add;
        var call = new Dictionary<string, string> { ["command"] = "cp a.txt b.txt" };

        var first = broker.RequestAsync(Session, "Bash", "toolu_1", call);
        broker.Answer(prompts[0].RequestId, approved: false, "no");
        (await first).Approved.Should().BeFalse();

        var second = broker.RequestAsync(Session, "Bash", "toolu_2", call);

        second.IsCompleted.Should().BeFalse("an identical call after a denial has to wait for a person exactly as the first did");
        prompts.Should().HaveCount(2);
        broker.Answer(prompts[1].RequestId, approved: false, "still no");
        (await second).Approved.Should().BeFalse();
    }

    [Fact]
    public async Task A_credential_source_that_throws_does_not_take_the_gate_down_with_it()
    {
        var broker = Broker(() => throw new InvalidOperationException("the secret store is locked"));
        broker.RegisterSession(Session, Copy);
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        // A command that asks. It was "ls" until backlog 166, which sends a read-only command back to Read, Grep and
        // Glob before anyone is asked; the point here is the credential source, so the call has to reach a person.
        var pending = broker.RequestAsync(Session, "Bash", "toolu_1", new Dictionary<string, string> { ["command"] = "dotnet build" });

        seen.Should().NotBeNull("the call still asks, as every change does");
        broker.Answer(seen!.RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
    }
}
