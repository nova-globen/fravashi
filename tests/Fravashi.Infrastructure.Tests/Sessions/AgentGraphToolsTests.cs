using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Sessions.Tools;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Backlog 77: which studio tool calls run without asking. The list is exact names in the Domain, and the
/// tools live in Infrastructure, so the tests that matter are the ones that hold the two together: a name on
/// the list that no tool has is dead, and a name filed under "reads" that belongs to a tool that WRITES would
/// be the list lying about itself.
/// </summary>
public sealed class AgentGraphToolsTests
{
    private const string Prefix = AgentWorkGate.StudioToolPrefix;
    private const string Copy = "/workspaces/w1";

    private static readonly Dictionary<string, string> Nothing = [];

    private static readonly AgentSessionId Session = AgentSessionId.New();

    /// <summary>Every studio tool by bare name, with whether it changes anything, read from the tool classes.</summary>
    private static IReadOnlyDictionary<string, StudioToolAccess> RealTools()
    {
        var tools = typeof(WikiFindTool).Assembly.GetTypes()
            .Where(t => typeof(IStudioTool).IsAssignableFrom(t) && t is { IsAbstract: false, IsClass: true })
            .ToList();
        tools.Should().HaveCountGreaterThan(15, "the reflection has to be finding the real tools");

        var byName = new Dictionary<string, StudioToolAccess>(StringComparer.Ordinal);
        foreach (var type in tools)
        {
            var name = (string)type.GetField("ToolName")!.GetRawConstantValue()!;
            var access = (StudioToolAccess)type.GetProperty(nameof(IStudioTool.Access))!
                .GetValue(System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type))!;
            byName[name] = access;
        }

        return byName;
    }

    [Theory]
    [InlineData("wiki_find")]
    [InlineData("wiki_read")]
    [InlineData("mockup_check")]
    [InlineData("mockup_schema")]
    [InlineData("prototype_check")]
    [InlineData("work_items_find")]
    [InlineData("work_item_read")]
    [InlineData("work_items_startable")]
    [InlineData("session_work_read")]
    [InlineData("session_governance_read")]
    [InlineData("session_attention")]
    [InlineData("studio_version")]
    public void A_read_of_the_graph_runs_unattended_and_the_tool_it_names_really_is_read_only(string tool)
    {
        AgentGraphTools.RunsUnattended(Prefix + tool, Nothing).Should().NotBeNull();
        RealTools()[tool].Should().Be(StudioToolAccess.ReadOnly);
    }

    [Theory]
    [InlineData("work_item_create")]
    [InlineData("work_item_link")]
    [InlineData("wiki_link")]
    [InlineData("session_work_link")]
    [InlineData("session_plan_link")]
    public void What_a_session_creates_or_links_to_clear_a_gate_runs_unattended_and_names_a_real_tool(string tool)
    {
        AgentGraphTools.RunsUnattended(Prefix + tool, Nothing).Should().NotBeNull();
        RealTools().Should().ContainKey(tool);
    }

    [Theory]
    [InlineData("git_status")]
    [InlineData("git_log")]
    [InlineData("git_show")]
    [InlineData("git_diff")]
    [InlineData("git_blame")]
    [InlineData("ci_runs")]
    [InlineData("ci_run_log")]
    public void A_read_of_the_host_repository_or_its_ci_runs_unattended_and_the_tool_it_names_really_is_read_only(string tool)
    {
        // Backlog 150: history, blame and diffs run through the bridge with a fixed argument vector and change
        // nothing on the host; asking for each would make history unusable without making anything safer.
        // Backlog 153: CI runs and logs are read from the git host with the studio's token, which stays in the studio.
        AgentGraphTools.RunsUnattended(Prefix + tool, Nothing).Should().NotBeNull();
        RealTools()[tool].Should().Be(StudioToolAccess.ReadOnly);
    }

    [Theory]
    [InlineData("git_commit", "it writes to the host repository")]
    [InlineData("git_push", "it writes to a remote with the host's credentials")]
    [InlineData("git_tag", "it writes to the host repository")]
    [InlineData("attempt_branch", "it touches the host repository")]
    [InlineData("attempt_commit", "it touches the host repository")]
    [InlineData("work_item_scenario_run", "it executes a command")]
    [InlineData("design_approve", "approving a design is a person's act")]
    [InlineData("quick_change", "it is built around one confirmed step")]
    [InlineData("work_item_update", "it changes something that already exists")]
    [InlineData("work_item_transition", "it changes something that already exists")]
    [InlineData(
        "work_item_evidence",
        "evidence is what lets an item pass the Done gate, so a person reads each claim before it is recorded")]
    [InlineData("prototype_adjust", "it changes a prototype that already exists")]
    [InlineData(
        "work_item_scenario_write",
        "it replaces the item's whole scenario list, and its command field is executed later with no "
        + "command text of its own on that later call to show again")]
    public void What_reaches_outside_the_graph_or_changes_what_exists_still_asks(string tool, string why)
    {
        RealTools().Should().ContainKey(tool, "this test is only worth something while the tool exists");
        AgentGraphTools.RunsUnattended(Prefix + tool, Nothing).Should().BeNull(why);
    }

    [Fact]
    public void Writing_a_document_is_unattended_only_when_it_creates_one()
    {
        AgentGraphTools.RunsUnattended(Prefix + "wiki_write", new Dictionary<string, string> { ["title"] = "Plan" })
            .Should().NotBeNull();

        // The wiki keeps no history, so rewriting a document somebody wrote is never done unseen.
        AgentGraphTools.RunsUnattended(
                Prefix + "wiki_write", new Dictionary<string, string> { ["number"] = "12", ["version"] = "3" })
            .Should().BeNull();
    }

    [Fact]
    public void Rendering_a_prototype_is_unattended_only_when_it_creates_one()
    {
        // Backlog 215, the wiki_write rule: a number names an existing prototype whose pin the call would move.
        RealTools().Should().ContainKey("prototype_render");
        AgentGraphTools.RunsUnattended(Prefix + "prototype_render", new Dictionary<string, string> { ["design"] = "3" })
            .Should().NotBeNull();
        AgentGraphTools.RunsUnattended(
                Prefix + "prototype_render", new Dictionary<string, string> { ["number"] = "12", ["version"] = "3" })
            .Should().BeNull();
    }

    [Theory]
    [InlineData("wiki_find")]
    [InlineData("mcp__other__wiki_find")]
    [InlineData("mcp__fravashi__WIKI_FIND")]
    [InlineData("mcp__fravashi__wiki_find_all")]
    [InlineData("")]
    public void A_name_that_is_only_close_to_one_on_the_list_asks(string name) =>
        AgentGraphTools.RunsUnattended(name, Nothing).Should().BeNull();

    [Fact]
    public void Every_studio_tool_is_either_on_the_list_or_named_here_as_one_that_asks()
    {
        // A tool added tomorrow asks by default, which is the safe direction. This test makes that a decision
        // somebody takes rather than a thing nobody noticed: a new tool turns it red until it is filed.
        string[] asks =
        [
            "attempt_branch", "attempt_commit", "work_item_scenario_run", "work_item_scenario_write",
            "design_approve", "quick_change",
            "work_item_update", "work_item_transition", "wiki_write",
            // Follow-up filed 2026-10-22 (record evidence against a criterion): evidence clears the Done gate.
            "work_item_evidence", "git_commit", "git_push", "git_tag",
            "browser_open", "browser_fill",
            // Backlog 157: replacing the product is a person's decision every time.
            "studio_upgrade",
            // Backlog 215: an adjustment changes an existing prototype, and its card lists each operation.
            "prototype_adjust",
            // Work item #359: the studio assistant's two own tools always ask, and attaching an image changes an item.
            "session_start_building", "autopilot_dispatch", "work_item_attach_image",
        ];

        var unfiled = RealTools().Keys
            .Where(tool => AgentGraphTools.RunsUnattended(Prefix + tool, Nothing) is null && !asks.Contains(tool))
            .ToList();

        unfiled.Should().BeEmpty();
    }

    /// <summary>
    /// Backlog 191: the host-alias alarm's exemption list is a different, narrower list than this one (an
    /// exempt tool can still ask for other reasons), but every name on it has to be this same real form and a
    /// real tool, or the exemption would be guarding a name nobody sends.
    /// </summary>
    [Fact]
    public void Every_host_alias_exempt_tool_is_a_real_studio_tool_by_its_prefixed_name()
    {
        var realTools = RealTools();
        foreach (var tool in AgentHostAlias.ExemptTools)
        {
            tool.Should().StartWith(Prefix);
            realTools.Should().ContainKey(tool[Prefix.Length..], $"{tool} has to name a real tool");
        }

        AgentHostAlias.ExemptTools.Should().NotContain(
            Prefix + "work_item_scenario_run", "it executes a command, so it stays on the alarm's side");
    }

    [Fact]
    public async Task At_the_gate_a_graph_creation_is_allowed_without_a_prompt_and_a_change_to_a_document_is_not()
    {
        using var broker = new AgentApprovalBroker();
        broker.RegisterSession(Session, Copy);
        var asked = new List<PendingToolApproval>();
        broker.Requested += asked.Add;

        var created = await broker.RequestAsync(
            Session, Prefix + "work_item_create", "toolu_1", new Dictionary<string, string> { ["title"] = "Add Goodbye" });

        created.Approved.Should().BeTrue();
        asked.Should().BeEmpty();

        var change = broker.RequestAsync(
            Session, Prefix + "wiki_write", "toolu_2", new Dictionary<string, string> { ["number"] = "4", ["version"] = "2" });

        change.IsCompleted.Should().BeFalse("rewriting an existing document waits for a person");
        asked.Should().ContainSingle();
        broker.Answer(asked[0].RequestId, approved: false, "no");
        (await change).Approved.Should().BeFalse();
    }
}
