using AwesomeAssertions;
using Fravashi.Domain.AgentSessions;
using Xunit;

namespace Fravashi.Domain.Tests.AgentSessions;

/// <summary>
/// Gate G1 (ADR 0020, backlog 44): no change to the WORKSPACE without a work item. The distinction that
/// carries the whole design is workspace against graph, because a graph write is how an unlinked session
/// clears the gate, and a gate that blocked that would leave the agent with nothing it could do but ask.
/// </summary>
public class AgentWorkGateTests
{
    [Theory]
    [InlineData("Read")]
    [InlineData("Glob")]
    [InlineData("Grep")]
    public void Read_only_work_is_never_gated_linked_or_not(string tool)
    {
        AgentWorkGate.Check(tool, readOnly: true, hasWork: false).Should().Be(WorkGateVerdict.NotAMutation);
        AgentWorkGate.Check(tool, readOnly: true, hasWork: true).Should().Be(WorkGateVerdict.NotAMutation);
    }

    [Theory]
    [InlineData("Edit")]
    [InlineData("Write")]
    [InlineData("Bash")]
    [InlineData("NotebookEdit")]
    public void A_workspace_change_in_an_unlinked_session_is_refused(string tool)
    {
        var verdict = AgentWorkGate.Check(tool, readOnly: false, hasWork: false);

        verdict.Should().Be(WorkGateVerdict.Unlinked);
        verdict.Refuses().Should().BeTrue();
    }

    [Theory]
    [InlineData("Edit")]
    [InlineData("Write")]
    [InlineData("Bash")]
    public void Linking_an_item_unblocks_the_same_call(string tool)
    {
        var verdict = AgentWorkGate.Check(tool, readOnly: false, hasWork: true);

        verdict.Should().Be(WorkGateVerdict.Linked);
        verdict.Refuses().Should().BeFalse();
    }

    [Theory]
    [InlineData("mcp__fravashi__work_item_create")]
    [InlineData("mcp__fravashi__session_work_link")]
    [InlineData("mcp__fravashi__work_item_transition")]
    public void A_graph_change_passes_the_gate_because_it_is_how_the_gate_is_cleared(string tool)
    {
        // ADR 0020: the gates govern workspace mutations, never graph mutations. An agent that cannot write
        // to the graph cannot create the work item that would let it work, and the refusal becomes a wall.
        var verdict = AgentWorkGate.Check(tool, readOnly: false, hasWork: false);

        verdict.Should().Be(WorkGateVerdict.GraphMutation);
        verdict.Refuses().Should().BeFalse();
    }

    [Theory]
    [InlineData("mcp__fravashi_evil__work_item_create")]
    [InlineData("MCP__FRAVASHI__work_item_create")]
    [InlineData("x_mcp__fravashi__work_item_create")]
    [InlineData("mcp__other__write_file")]
    public void A_name_that_merely_looks_like_a_studio_tool_does_not_pass(string tool)
    {
        // The prefix is matched ordinally and from the start, so another server's tool, a case variant, and
        // a name with the prefix buried inside it are all ordinary mutations.
        AgentWorkGate.Check(tool, readOnly: false, hasWork: false).Should().Be(WorkGateVerdict.Unlinked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SomeToolNobodyHasHeardOf")]
    public void An_unrecognised_tool_is_treated_as_a_change(string? tool)
    {
        // Fail closed: the short explicit list is of things that cannot change anything, and a name that is
        // not on it is not evidence of safety.
        AgentWorkGate.Check(tool, readOnly: false, hasWork: false).Should().Be(WorkGateVerdict.Unlinked);
        AgentWorkGate.Check(tool, readOnly: false, hasWork: true).Should().Be(WorkGateVerdict.Linked);
    }

    [Fact]
    public void A_session_with_work_and_no_plan_is_refused_by_the_second_gate()
    {
        var verdict = AgentWorkGate.Check("Edit", readOnly: false, hasWork: true, hasPlan: false);

        verdict.Should().Be(WorkGateVerdict.NoPlan);
        verdict.Refuses().Should().BeTrue();
        verdict.SentenceFor().Should().Be(AgentWorkGate.NoPlanSentence);
    }

    [Fact]
    public void A_session_with_neither_is_told_to_describe_the_work_first()
    {
        // Order matters: a plan written before anyone has said what it is for is a plan for nothing.
        var verdict = AgentWorkGate.Check("Edit", readOnly: false, hasWork: false, hasPlan: false);

        verdict.Should().Be(WorkGateVerdict.Unlinked);
        verdict.SentenceFor().Should().Be(AgentWorkGate.UnlinkedSentence);
    }

    [Fact]
    public void A_session_with_both_may_go_on_to_be_approved()
    {
        var verdict = AgentWorkGate.Check("Edit", readOnly: false, hasWork: true, hasPlan: true);

        verdict.Should().Be(WorkGateVerdict.Linked);
        verdict.Refuses().Should().BeFalse();
        verdict.SentenceFor().Should().BeNull();
    }

    [Fact]
    public void Neither_gate_touches_a_read_or_a_graph_write_even_with_no_plan()
    {
        AgentWorkGate.Check("Read", readOnly: true, hasWork: false, hasPlan: false)
            .Should().Be(WorkGateVerdict.NotAMutation);
        AgentWorkGate.Check(
            AgentWorkGate.StudioToolPrefix + "wiki_write", readOnly: false, hasWork: true, hasPlan: false)
            .Should().Be(WorkGateVerdict.GraphMutation);
    }

    [Fact]
    public void The_second_refusal_names_the_steps_in_the_order_they_have_to_happen()
    {
        var sentence = AgentWorkGate.NoPlanSentence;

        sentence.Should().Contain("wiki_write").And.Contain("wiki_link").And.Contain("session_plan_link");
        sentence.IndexOf("wiki_write", StringComparison.Ordinal)
            .Should().BeLessThan(sentence.IndexOf("session_plan_link", StringComparison.Ordinal));
        sentence.Should().Contain("This is not an error");
    }

    [Fact]
    public void The_refusal_tells_the_agent_what_to_do_rather_than_apologising()
    {
        // Backlog 44's acceptance in as many words: the agent responds by helping rather than apologising.
        AgentWorkGate.UnlinkedSentence.Should().Contain("work_item_create")
            .And.Contain("Help the user")
            .And.Contain("nothing to apologise for");
    }

    // ---- IsBarePwd (work item #340) -------------------------------------------------------

    [Theory]
    [InlineData("pwd")]
    [InlineData("pwd -P")]
    [InlineData("pwd -L")]
    [InlineData("  pwd  ")]
    public void A_bare_pwd_is_treated_as_reading_nothing(string command) =>
        AgentWorkGate.IsBarePwd("Bash", command).Should().BeTrue();

    [Theory]
    [InlineData("pwd && touch x")]
    [InlineData("pwd; rm y")]
    [InlineData("pwd | cat")]
    [InlineData("pwd > f")]
    [InlineData("pwd < f")]
    [InlineData("pwd $(touch x)")]
    [InlineData("pwd `touch x`")]
    [InlineData("echo $(pwd)")]
    [InlineData("cd /tmp && pwd")]
    [InlineData("pwdx")] // a different command that merely starts with "pwd"
    public void Pwd_combined_with_anything_else_is_not_bare(string command) =>
        AgentWorkGate.IsBarePwd("Bash", command).Should().BeFalse();

    [Fact]
    public void Only_bash_can_be_a_bare_pwd() =>
        AgentWorkGate.IsBarePwd("Read", "pwd").Should().BeFalse();

    [Fact]
    public void A_bare_pwd_before_a_plan_exists_is_not_refused_by_the_gate()
    {
        var verdict = AgentWorkGate.Check(
            "Bash", readOnly: AgentWorkGate.IsBarePwd("Bash", "pwd"), hasWork: false);

        verdict.Should().Be(WorkGateVerdict.NotAMutation);
        verdict.Refuses().Should().BeFalse();
    }

    [Fact]
    public void Pwd_combined_with_a_change_is_still_refused_before_a_plan_exists()
    {
        var verdict = AgentWorkGate.Check(
            "Bash", readOnly: AgentWorkGate.IsBarePwd("Bash", "pwd && touch x"), hasWork: false);

        verdict.Should().Be(WorkGateVerdict.Unlinked);
        verdict.Refuses().Should().BeTrue();
    }
}
