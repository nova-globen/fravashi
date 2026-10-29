using AwesomeAssertions;
using Fravashi.Domain.AgentSessions;
using Xunit;

namespace Fravashi.Domain.Tests.AgentSessions;

/// <summary>
/// Backlog 88: a role is a configurable thing with its own instructions and its own capability set, and
/// the vision's open question is answered here. The mapping from a tool call to the capability it needs
/// is default-deny, which is the part that decides whether a role means anything.
/// </summary>
public class AgentRoleTests
{
    [Fact]
    public void The_four_roles_the_vision_names_all_exist_and_so_does_the_unrestricted_one()
    {
        AgentRoles.All.Select(r => r.Id).Should().Contain(
            ["interactive", "analyst", "designer", "implementer", "verifier"]);

        AgentRoles.Find(AgentRoles.DefaultId).Should().Be(AgentRoles.Interactive);
        AgentRoles.Find("nothing-like-a-role").Should().BeNull();
    }

    [Fact]
    public void Every_role_says_what_it_is_for_and_can_read()
    {
        foreach (var role in AgentRoles.All)
        {
            role.Name.Should().NotBeNullOrWhiteSpace(role.Id);
            role.Instructions.Should().NotBeNullOrWhiteSpace(role.Id);
            role.Holds(RoleCapabilities.Read).Should().BeTrue("{0} has to be able to read", role.Id);
        }
    }

    [Fact]
    public void The_roles_that_decide_what_to_build_cannot_build_it()
    {
        // The whole point of a role-scoped capability set: an analyst that could edit files would be an
        // implementer that also grooms, and the loop would have no seam in it.
        foreach (var role in new[] { AgentRoles.Analyst, AgentRoles.Designer })
        {
            role.Holds(RoleCapabilities.WriteGraph).Should().BeTrue(role.Id);
            role.Holds(RoleCapabilities.ChangeWorkspace).Should().BeFalse(role.Id);
            role.Holds(RoleCapabilities.RunCommands).Should().BeFalse(role.Id);
        }
    }

    [Fact]
    public void The_verifier_may_run_things_and_may_not_change_them()
    {
        // A verifier that fixes what it finds is no longer checking anything.
        AgentRoles.Verifier.Holds(RoleCapabilities.RunCommands).Should().BeTrue();
        AgentRoles.Verifier.Holds(RoleCapabilities.ChangeWorkspace).Should().BeFalse();
    }

    [Fact]
    public void The_implementer_holds_everything_it_needs_to_build()
    {
        AgentRoles.Implementer.Holds(
            RoleCapabilities.Read | RoleCapabilities.WriteGraph
            | RoleCapabilities.ChangeWorkspace | RoleCapabilities.RunCommands)
            .Should().BeTrue();
    }

    // ---- what a call needs -----------------------------------------------------------------------

    [Theory]
    [InlineData("Read", true, false, RoleCapabilities.Read)]
    [InlineData("Grep", true, false, RoleCapabilities.Read)]
    [InlineData("mcp__fravashi__work_item_create", false, true, RoleCapabilities.WriteGraph)]
    [InlineData("Bash", false, false, RoleCapabilities.RunCommands)]
    [InlineData("Edit", false, false, RoleCapabilities.ChangeWorkspace)]
    [InlineData("Write", false, false, RoleCapabilities.ChangeWorkspace)]
    public void A_call_needs_the_capability_that_matches_what_it_does(
        string tool, bool readOnly, bool studio, RoleCapabilities needed) =>
        AgentRoleGate.Needs(tool, readOnly, studio).Should().Be(needed);

    [Theory]
    [InlineData("SomethingNobodyHasHeardOf")]
    [InlineData("bash")]
    [InlineData("BASH")]
    public void A_tool_the_studio_does_not_recognise_counts_as_changing_the_workspace(string tool) =>
        // Default-deny: a name that is unfamiliar, or a case variant of a known one, gets the stricter
        // answer rather than the looser one.
        AgentRoleGate.Needs(tool, readOnly: false, isStudioTool: false)
            .Should().Be(RoleCapabilities.ChangeWorkspace);

    // ---- the gate ---------------------------------------------------------------------------------

    [Fact]
    public void A_session_with_no_role_is_unrestricted_which_is_not_the_same_as_holding_nothing()
    {
        AgentRoleGate.Check(null, "Edit", readOnly: false, isStudioTool: false).Should().BeNull();

        var holdsNothing = new AgentRole("empty", "Empty", RoleCapabilities.None, "Nothing.");
        AgentRoleGate.Check(holdsNothing, "Edit", readOnly: false, isStudioTool: false).Should().NotBeNull();
    }

    [Fact]
    public void A_verifier_asked_to_edit_is_told_whose_job_that_is()
    {
        var refusal = AgentRoleGate.Check(
            AgentRoles.Verifier, "Edit", readOnly: false, isStudioTool: false);

        refusal.Should().NotBeNull();
        refusal.Should().Contain("Reviewer / Verifier").And.Contain("change files in the workspace");
        refusal.Should().Contain("not an error", "the role is a statement, not a fault to work around");
    }

    [Fact]
    public void An_analyst_may_write_the_graph_and_not_run_a_command()
    {
        AgentRoleGate.Check(
            AgentRoles.Analyst, "mcp__fravashi__work_item_create", readOnly: false, isStudioTool: true)
            .Should().BeNull();

        AgentRoleGate.Check(AgentRoles.Analyst, "Bash", readOnly: false, isStudioTool: false)
            .Should().NotBeNull();
    }

    [Fact]
    public void Every_role_can_read_whatever_the_call_is()
    {
        foreach (var role in AgentRoles.All)
        {
            AgentRoleGate.Check(role, "Read", readOnly: true, isStudioTool: false).Should().BeNull(role.Id);
        }
    }
}
