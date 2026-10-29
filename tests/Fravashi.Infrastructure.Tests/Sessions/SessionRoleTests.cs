using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Sessions.Tools;
using Fravashi.Infrastructure.Tests.Persistence;
using Fravashi.Infrastructure.Wiki;
using Fravashi.Infrastructure.WorkItems;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Backlog 88, through the real broker and the real tool server: a session runs under a role, and the role
/// is enforced where the studio can actually enforce it rather than where it would merely be requested.
/// </summary>
public sealed class SessionRoleTests : IDisposable
{
    private const string Version = "2025-11-25";

    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "fravashi-roles-" + Guid.NewGuid().ToString("N"));

    private readonly SqliteTestContext _store = new();
    private readonly AgentApprovalBroker _broker;
    private readonly AgentSessionId _session = AgentSessionId.New();
    private readonly SqliteSessionRoles _roles;
    private readonly SqliteSessionWorkItems _work;
    private readonly SqliteSessionPlans _plans;
    private readonly LocalTrackerResolver _trackers;
    private readonly SqliteWikiLibrary _wiki;
    private readonly string _secret;

    public SessionRoleTests()
    {
        _roles = new SqliteSessionRoles(_store.Factory);
        _work = new SqliteSessionWorkItems(_store.Factory, TimeProvider.System);
        _plans = new SqliteSessionPlans(_store.Factory, TimeProvider.System);
        _trackers = new LocalTrackerResolver(_store.Factory, TimeProvider.System);
        _wiki = new SqliteWikiLibrary(_store.Factory, TimeProvider.System, Fravashi.Domain.Projects.ProjectId.Default);
        _broker = new AgentApprovalBroker(TimeSpan.FromSeconds(2), roles: _roles);
        _secret = _broker.RegisterSession(_session, "/workspaces/a");
    }

    public void Dispose()
    {
        _broker.Dispose();
        _store.Dispose();
        try
        {
            if (Directory.Exists(_dataDirectory))
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private static AgentToolInput Input(string name, string value) =>
        AgentToolInput.FromStrings(new Dictionary<string, string> { [name] = value });

    // ---- the store ------------------------------------------------------------------------------

    [Fact]
    public void A_session_runs_unrestricted_until_a_role_is_chosen_and_the_choice_survives()
    {
        _roles.For(_session).Should().BeNull();

        _roles.Assign(_session, AgentRoles.Verifier.Id).Should().BeTrue();
        _roles.For(_session).Should().Be(AgentRoles.Verifier);

        // Choosing again replaces rather than adding: a conversation does one kind of work.
        _roles.Assign(_session, AgentRoles.Analyst.Id).Should().BeTrue();
        _roles.For(_session).Should().Be(AgentRoles.Analyst);

        _roles.Clear(_session);
        _roles.For(_session).Should().BeNull();
    }

    [Fact]
    public void A_role_nobody_has_heard_of_is_refused_rather_than_stored()
    {
        _roles.Assign(_session, "dictator").Should().BeFalse();
        _roles.For(_session).Should().BeNull();
    }

    [Fact]
    public void An_unreadable_store_answers_with_the_most_restrictive_role_rather_than_with_none()
    {
        // The opposite direction from the other session stores, and deliberately: here "we could not tell"
        // must not read as "unrestricted".
        var broken = new SqliteSessionRoles(new BrokenFactory());

        var role = broken.For(_session);

        role.Should().NotBeNull();
        role!.Holds(RoleCapabilities.ChangeWorkspace).Should().BeFalse();
        role.Holds(RoleCapabilities.RunCommands).Should().BeFalse();
    }

    // ---- enforcement in the approval path ---------------------------------------------------------

    [Fact]
    public async Task A_verifier_asked_to_edit_a_file_is_refused_without_anyone_being_asked()
    {
        _roles.Assign(_session, AgentRoles.Verifier.Id);
        var asked = false;
        _broker.Requested += _ => asked = true;

        var decision = await _broker.RequestAsync(
            _session, "Edit", "toolu_1", Input("file_path", "/workspaces/a/src/x.cs"));

        decision.Approved.Should().BeFalse();
        decision.Reason.Should().Contain("Reviewer / Verifier");
        asked.Should().BeFalse();
    }

    [Fact]
    public async Task An_analyst_cannot_run_a_command()
    {
        _roles.Assign(_session, AgentRoles.Analyst.Id);

        var decision = await _broker.RequestAsync(
            _session, "Bash", "toolu_2", Input("command", "rm -rf ."));

        decision.Approved.Should().BeFalse();
        decision.Reason.Should().Contain("run commands");
    }

    [Fact]
    public async Task Every_role_may_still_read_inside_the_copy_unattended()
    {
        foreach (var role in AgentRoles.All)
        {
            _roles.Assign(_session, role.Id);

            var decision = await _broker.RequestAsync(
                _session, "Read", "toolu_r_" + role.Id, Input("file_path", "/workspaces/a/README.md"));

            decision.Approved.Should().BeTrue(role.Id);
        }
    }

    [Fact]
    public async Task An_implementer_reaches_the_person_rather_than_being_refused_by_its_role()
    {
        _roles.Assign(_session, AgentRoles.Implementer.Id);

        PendingToolApproval? pending = null;
        _broker.Requested += p => pending = p;

        var call = _broker.RequestAsync(_session, "Edit", "toolu_3", Input("file_path", "/workspaces/a/x.cs"));

        for (var i = 0; i < 200 && pending is null; i++)
        {
            await Task.Delay(10);
        }

        pending.Should().NotBeNull();
        _broker.Answer(pending!.RequestId, approved: true, "Go ahead.");
        (await call).Approved.Should().BeTrue();
    }

    [Fact]
    public async Task A_session_with_no_role_is_not_refused_by_this_gate()
    {
        PendingToolApproval? pending = null;
        _broker.Requested += p => pending = p;

        var call = _broker.RequestAsync(_session, "Edit", "toolu_4", Input("file_path", "/workspaces/a/x.cs"));

        for (var i = 0; i < 200 && pending is null; i++)
        {
            await Task.Delay(10);
        }

        pending.Should().NotBeNull("an unrestricted session is what every session was before roles existed");
        _broker.Answer(pending!.RequestId, approved: true, "Yes.");
        (await call).Approved.Should().BeTrue();
    }

    // ---- enforcement in the studio's own tool server -----------------------------------------------

    [Fact]
    public async Task An_unattended_role_sees_a_role_scoped_tool_set_rather_than_the_full_one()
    {
        // Item 88's acceptance in as many words. The analyst may write the graph; a role that may not
        // would not be shown the tools that do, and could not call them by name either.
        _roles.Assign(_session, AgentRoles.Analyst.Id);
        var server = Server();

        var listed = await ListAsync(server);
        listed.Should().Contain(WorkItemCreateTool.ToolName).And.Contain(WorkItemFindTool.ToolName);

        var readerOnly = new AgentRole("reader", "Reader", RoleCapabilities.Read, "Reads only.");
        var readerServer = Server(new FixedRoles(readerOnly));

        var readerTools = await ListAsync(readerServer);
        readerTools.Should().Contain(WorkItemFindTool.ToolName);
        readerTools.Should().NotContain(WorkItemCreateTool.ToolName);
    }

    [Fact]
    public async Task A_role_that_may_not_write_the_graph_cannot_call_a_writing_tool_by_name()
    {
        var readerOnly = new AgentRole("reader", "Reader", RoleCapabilities.Read, "Reads only.");
        var server = Server(new FixedRoles(readerOnly));

        var result = await CallAsync(
            server, WorkItemCreateTool.ToolName, """{"title":"Sneaked in","description":"Why.","level":"task"}""");

        result.Should().Contain("not change it");
        (await _trackers.For(ProjectId.Default).QueryAsync(new WorkItemQuery())).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task The_governance_read_hands_the_agent_its_own_instructions()
    {
        // The channel the studio can prove: passing them as a system prompt would mean changing the CLI's
        // command line, which is a pinned contract.
        _roles.Assign(_session, AgentRoles.Verifier.Id);
        var server = Server();

        var text = await CallAsync(server, SessionGovernanceReadTool.ToolName, """{}""");

        using var json = JsonDocument.Parse(text);
        var role = json.RootElement.GetProperty("data").GetProperty("role");
        role.GetProperty("id").GetString().Should().Be(AgentRoles.Verifier.Id);
        role.GetProperty("instructions").GetString().Should().Be(AgentRoles.Verifier.Instructions);
        role.GetProperty("mayChangeFiles").GetBoolean().Should().BeFalse();
        role.GetProperty("mayRunCommands").GetBoolean().Should().BeTrue();
    }

    // ---- helpers --------------------------------------------------------------------------------

    private StudioToolServer Server(ISessionRoles? roles = null) =>
        new(
            _broker,
            [
                new WorkItemFindTool(_trackers, new Application.WorkItems.WorkItemChanges()),
                new WorkItemCreateTool(_trackers, new Application.WorkItems.WorkItemChanges()),
                new SessionGovernanceReadTool(new OneWiki(_wiki), _trackers, _work, _plans, roles ?? _roles),
            ],
            new FileToolCallLedger(_dataDirectory),
            "9.8.7",
            StudioToolServer.DefaultMaxResultBytes,
            grants: new RoleStudioToolGrants(roles ?? _roles));

    private async Task<IReadOnlyList<string>> ListAsync(StudioToolServer server)
    {
        var response = await server.HandleAsync(new StudioToolHttpRequest(
            "POST", _secret, Version, null, """{"method":"tools/list","jsonrpc":"2.0","id":1}"""));

        using var json = JsonDocument.Parse(response.Json!);
        return
        [
            .. json.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
                .Select(t => t.GetProperty("name").GetString()!),
        ];
    }

    private async Task<string> CallAsync(StudioToolServer server, string tool, string arguments)
    {
        var body =
            $"{{\"method\":\"tools/call\",\"params\":{{\"name\":\"{tool}\",\"arguments\":{arguments},"
            + $"\"_meta\":{{\"claudecode/toolUseId\":\"toolu_{Guid.NewGuid():N}\"}}}},\"jsonrpc\":\"2.0\",\"id\":2}}";

        var response = await server.HandleAsync(new StudioToolHttpRequest("POST", _secret, Version, null, body));
        using var json = JsonDocument.Parse(response.Json!);

        return json.RootElement.TryGetProperty("error", out var error)
            ? error.GetProperty("message").GetString()!
            : json.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    /// <summary>A role store that always answers with the same role, for the cases a shipped one cannot make.</summary>
    private sealed class FixedRoles(AgentRole role) : ISessionRoles
    {
        public event Action<AgentSessionId>? Changed;

        public IReadOnlyList<AgentRole> Catalogue => [role];

        public AgentRole? For(AgentSessionId session) => role;

        public bool Assign(AgentSessionId session, string roleId)
        {
            Changed?.Invoke(session);
            return false;
        }

        public void Clear(AgentSessionId session) => Changed?.Invoke(session);

        public void Forget(AgentSessionId session)
        {
        }
    }

    /// <summary>A factory whose connections open onto nothing, so every read throws.</summary>
    private sealed class BrokenFactory : Fravashi.Infrastructure.Persistence.ISqliteConnectionFactory
    {
        public Microsoft.Data.Sqlite.SqliteConnection Open() =>
            throw new InvalidOperationException("This store is not available.");
    }
}
