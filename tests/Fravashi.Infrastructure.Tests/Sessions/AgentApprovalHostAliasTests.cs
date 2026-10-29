using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Sessions;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>Backlog 187 at the gate every tool call passes through: wslc's host alias is named loudly on the prompt.</summary>
public sealed class AgentApprovalHostAliasTests
{
    private const string Copy = "/workspaces/w1";

    private static readonly AgentSessionId Session = AgentSessionId.New();

    [Theory]
    [InlineData("Bash", "command", "curl -s http://host.wslc.internal:5432/")]
    [InlineData("WebFetch", "url", "http://169.254.73.254:3000/")]
    [InlineData("Agent", "prompt", "See what HOST.WSLC.INTERNAL:5037 answers.")]
    public async Task A_call_naming_the_alias_asks_with_a_loud_line_saying_it_reaches_this_pc(
        string tool, string field, string argument)
    {
        var broker = new AgentApprovalBroker();
        broker.RegisterSession(Session, Copy);
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        var pending = broker.RequestAsync(Session, tool, "toolu_1", new Dictionary<string, string> { [field] = argument });

        seen.Should().NotBeNull();
        var first = seen!.Effects![0];
        first.Weight.Should().Be(AgentEffectWeight.Alarm);
        first.Field.Should().Be(field);
        first.Notice.Should().Contain("SERVICE ON THIS PC");

        broker.Answer(seen.RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
    }

    [Fact]
    public async Task An_ordinary_url_gets_no_such_line()
    {
        var broker = new AgentApprovalBroker();
        broker.RegisterSession(Session, Copy);
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        var pending = broker.RequestAsync(
            Session, "WebFetch", "toolu_1", new Dictionary<string, string> { ["url"] = "https://learn.microsoft.com/dotnet/" });

        seen.Should().NotBeNull();
        (seen!.Effects ?? []).Should().NotContain(e => e.Notice.Contains("SERVICE ON THIS PC"));

        broker.Answer(seen.RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
    }

    /// <summary>
    /// Backlog 191, through the exact path a live call takes: the CLI sends the studio's own tools as
    /// <c>mcp__fravashi__&lt;name&gt;</c> (<see cref="AgentWorkGate.StudioToolPrefix"/>), and
    /// <see cref="AgentApprovalBroker.RequestAsync"/> is what has to read the alarm off for that exact
    /// string. A test that exempted the bare name instead would pass while the real alarm still fired.
    /// </summary>
    [Theory]
    [InlineData("work_item_update", "description")]
    [InlineData("wiki_write", "body")]
    public async Task A_studio_graph_text_tool_update_naming_the_alias_carries_no_alarm(string bareTool, string field)
    {
        var broker = new AgentApprovalBroker();
        broker.RegisterSession(Session, Copy);
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        var pending = broker.RequestAsync(
            Session,
            AgentWorkGate.StudioToolPrefix + bareTool,
            "toolu_1",
            new Dictionary<string, string>
            {
                ["number"] = "190",
                ["version"] = "1",
                [field] = $"The defect is about {AgentHostAlias.Name}.",
            });

        seen.Should().NotBeNull();
        (seen!.Effects ?? []).Should().NotContain(e => e.Notice.Contains("SERVICE ON THIS PC"));

        broker.Answer(seen.RequestId, approved: true, "yes");
        (await pending).Approved.Should().BeTrue();
    }

    /// <summary>
    /// Backlog 191 (host review correction): work_item_scenario_write is NOT exempt, because its step
    /// command is authored text that work_item_scenario_run later EXECUTES, and that run's own arguments
    /// (just a number and a scenario id) never show the command again. The write's own approval card is the
    /// only place a person ever sees it, so it has to keep the alarm. The "scenarios" argument is not a
    /// string at the top level, so it reaches the broker the same way the real hook payload would deliver it
    /// (<see cref="Fravashi.Infrastructure.Sessions.AgentApprovalEndpoint"/> renders a non-string JSON value
    /// as its raw text), with the command embedded in that text.
    /// </summary>
    [Fact]
    public async Task A_scenario_write_whose_step_command_names_the_alias_still_carries_the_alarm()
    {
        var broker = new AgentApprovalBroker();
        broker.RegisterSession(Session, Copy);
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        var pending = broker.RequestAsync(
            Session,
            AgentWorkGate.StudioToolPrefix + "work_item_scenario_write",
            "toolu_1",
            new Dictionary<string, string>
            {
                ["number"] = "190",
                ["version"] = "1",
                ["scenarios"] =
                    "[{\"title\":\"check\",\"steps\":[{\"action\":\"probe\",\"expected\":\"answers\","
                    + $"\"command\":\"curl http://{AgentHostAlias.Name}:5432/\"}}]}}]",
            });

        seen.Should().NotBeNull();
        seen!.Effects.Should().Contain(e => e.Notice.Contains("SERVICE ON THIS PC"));

        broker.Answer(seen.RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
    }

    /// <summary>Everything else keeps the alarm, the studio's own browser_open (#187) included.</summary>
    [Theory]
    [InlineData("Bash", "command")]
    [InlineData("WebFetch", "url")]
    public async Task Bash_and_webfetch_still_alarm_on_the_alias(string tool, string field)
    {
        var broker = new AgentApprovalBroker();
        broker.RegisterSession(Session, Copy);
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        var pending = broker.RequestAsync(
            Session, tool, "toolu_1", new Dictionary<string, string> { [field] = $"touches {AgentHostAlias.Name}" });

        seen.Should().NotBeNull();
        seen!.Effects.Should().Contain(e => e.Notice.Contains("SERVICE ON THIS PC"));

        broker.Answer(seen.RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
    }

    [Fact]
    public async Task The_studios_own_browser_open_still_alarms_on_the_alias()
    {
        var broker = new AgentApprovalBroker();
        broker.RegisterSession(Session, Copy);
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        var pending = broker.RequestAsync(
            Session,
            AgentWorkGate.StudioToolPrefix + "browser_open",
            "toolu_1",
            new Dictionary<string, string> { ["url"] = $"http://{AgentHostAlias.Name}:5432/" });

        seen.Should().NotBeNull();
        seen!.Effects.Should().Contain(e => e.Notice.Contains("SERVICE ON THIS PC"));

        broker.Answer(seen.RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
    }

    [Fact]
    public async Task An_unknown_mcp_tool_naming_the_alias_still_alarms()
    {
        var broker = new AgentApprovalBroker();
        broker.RegisterSession(Session, Copy);
        PendingToolApproval? seen = null;
        broker.Requested += p => seen = p;

        var pending = broker.RequestAsync(
            Session,
            "mcp__other__something",
            "toolu_1",
            new Dictionary<string, string> { ["argument"] = $"reaches {AgentHostAlias.Name}" });

        seen.Should().NotBeNull();
        seen!.Effects.Should().Contain(e => e.Notice.Contains("SERVICE ON THIS PC"));

        broker.Answer(seen.RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
    }
}
