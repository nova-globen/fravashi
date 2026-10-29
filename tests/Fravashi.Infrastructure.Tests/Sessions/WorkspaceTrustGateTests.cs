using AwesomeAssertions;
using Fravashi.Application.Persistence;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Tests.Persistence;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// ADR 0028 (backlog 152): a folder whose person chose "trust edits" lets an edit that provably lands inside the
/// contained copy run without asking, and NOTHING else changes: a command asks, a change outside the copy asks,
/// a hidden path asks, the Ask level asks, the work gate still refuses, and every trusted call is raised so the
/// transcript shows it. The store, too: an unknown level reads as Ask.
/// </summary>
public sealed class WorkspaceTrustGateTests : IDisposable
{
    private const string Workspace = "w1";

    private static readonly AgentSessionId Session = AgentSessionId.New();

    private readonly SqliteTestContext _store = new();
    private readonly SqliteWorkspaceTrust _trust;

    // A REAL folder, because the destination the budget and the trust branch key on is the canonical path
    // relative to the root, and a root that does not exist canonicalises differently on each platform.
    private readonly string _copy = Path.Combine(
        Path.GetTempPath(), "fravashi-trust-" + Guid.NewGuid().ToString("N")[..8]);

    // Backlog 202: a REAL folder standing in for this session's own agent memory, registered the same
    // way AgentApprovalBroker.RegisterSession's memoryRoot parameter is in production.
    private readonly string _memory = Path.Combine(
        Path.GetTempPath(), "fravashi-trust-memory-" + Guid.NewGuid().ToString("N")[..8]);

    private string Copy => _copy.Replace('\\', '/');

    private string Memory => _memory.Replace('\\', '/');

    public WorkspaceTrustGateTests()
    {
        Directory.CreateDirectory(Path.Combine(_copy, "src"));
        Directory.CreateDirectory(_memory);
        _trust = new SqliteWorkspaceTrust(_store.Factory);
    }

    public void Dispose()
    {
        _store.Dispose();
        foreach (var path in new[] { _copy, _memory })
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static Dictionary<string, string> Writing(string path) => new()
    {
        ["file_path"] = path,
        ["content"] = "hello",
    };

    private AgentApprovalBroker Broker(ISessionGovernance? governance = null, IWorkspaceStore? workspaceStore = null) =>
        new(TimeSpan.FromMinutes(5), governance: governance, trust: _trust, workspaceStore: workspaceStore);

    // ---- the store --------------------------------------------------------------------------

    [Fact]
    public void A_folder_starts_at_ask_and_keeps_the_level_a_person_chose()
    {
        _trust.For(Workspace).Should().Be(WorkspaceTrust.Ask);

        var changed = new List<string>();
        _trust.Changed += changed.Add;

        _trust.Set(Workspace, WorkspaceTrust.Edits);
        _trust.For(Workspace).Should().Be(WorkspaceTrust.Edits);
        new SqliteWorkspaceTrust(_store.Factory).For(Workspace).Should().Be(WorkspaceTrust.Edits, "it is durable");

        _trust.Set(Workspace, WorkspaceTrust.Ask);
        _trust.For(Workspace).Should().Be(WorkspaceTrust.Ask);
        changed.Should().Equal(Workspace, Workspace);

        _trust.For("another").Should().Be(WorkspaceTrust.Ask, "a level is per folder");
        _trust.For("").Should().Be(WorkspaceTrust.Ask);
    }

    // ---- the gate ----------------------------------------------------------------------------

    [Fact]
    public async Task With_edits_trusted_an_edit_inside_the_copy_runs_without_asking_is_recorded_and_counts_against_the_budget()
    {
        var governance = Substitute.For<ISessionGovernance>();
        var broker = Broker(governance);
        broker.RegisterSession(Session, Copy, workspaceId: Workspace);
        _trust.Set(Workspace, WorkspaceTrust.Edits);
        var asked = new List<PendingToolApproval>();
        var trusted = new List<TrustedToolCall>();
        broker.Requested += asked.Add;
        broker.Trusted += trusted.Add;

        var decision = await broker.RequestAsync(Session, "Edit", "toolu_1", Writing(Copy + "/src/Program.cs"));

        decision.Approved.Should().BeTrue();
        decision.Reason.Should().Be(AgentApprovalBroker.TrustedEditReason);
        decision.DecidedBy.Should().Be(ToolApprovalDecidedBy.Studio, "nobody was asked, so nobody is credited");
        asked.Should().BeEmpty("the point is that nobody was asked");

        // Raised, never silent: the transcript row is built from this.
        trusted.Should().ContainSingle();
        trusted[0].ToolName.Should().Be("Edit");
        trusted[0].Detail.Should().Contain("Program.cs");
        trusted[0].ToolUseId.Should().Be("toolu_1");

        // Still a change against the budget, recorded here because no answer is coming to record it after.
        governance.Received(1).RecordApprovedChange(Session, "src/Program.cs");
    }

    /// <summary>
    /// Backlog 202: the memory root AgentReadScope was given is scoped to the READ-ONLY auto-allow
    /// alone. Resolve() is called again for the trust branch's own destination/target WITHOUT the
    /// memory root, on purpose: a write under this session's own memory is not the bounded exception
    /// ADR 0028 describes, and feeding the memory root into that second call would have quietly let a
    /// "trust edits" workspace auto-approve a write there too.
    /// </summary>
    [Fact]
    public async Task With_edits_trusted_and_memory_linked_an_edit_inside_memory_still_asks()
    {
        var broker = Broker();
        broker.RegisterSession(Session, Copy, workspaceId: Workspace, memoryRoot: Memory, memoryTarget: Memory);
        _trust.Set(Workspace, WorkspaceTrust.Edits);
        var trusted = new List<TrustedToolCall>();
        broker.Trusted += trusted.Add;

        var pending = broker.RequestAsync(Session, "Write", "toolu_1", Writing(Memory + "/note.md"));

        broker.PendingFor(Session).Should().ContainSingle(
            "a write under this session's own memory must still ask, exactly like any other change outside the copy");
        trusted.Should().BeEmpty();
        broker.Answer(broker.PendingFor(Session)[0].RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
    }

    [Theory]
    [InlineData("Bash", "a command is not an edit, whatever it writes")]
    [InlineData("Agent", "a subagent is not an edit")]
    [InlineData("WebFetch", "the network is not an edit")]
    public async Task With_edits_trusted_anything_that_is_not_an_editing_tool_still_asks(string tool, string why)
    {
        var broker = Broker();
        broker.RegisterSession(Session, Copy, workspaceId: Workspace);
        _trust.Set(Workspace, WorkspaceTrust.Edits);
        var trusted = new List<TrustedToolCall>();
        broker.Trusted += trusted.Add;

        var pending = broker.RequestAsync(Session, tool, "toolu_1", new Dictionary<string, string>
        {
            ["command"] = "echo hi > " + Copy + "/notes.md",
            ["file_path"] = Copy + "/notes.md",
        });

        broker.PendingFor(Session).Should().ContainSingle(why);
        trusted.Should().BeEmpty();
        broker.Answer(broker.PendingFor(Session)[0].RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
    }

    [Theory]
    [InlineData("/../other/notes.md", "it lands beside the copy")]
    [InlineData("/src/../../../keys/x", "it climbs out")]
    public async Task With_edits_trusted_an_edit_that_leaves_the_copy_still_asks(string suffix, string why)
    {
        var broker = Broker();
        broker.RegisterSession(Session, Copy, workspaceId: Workspace);
        _trust.Set(Workspace, WorkspaceTrust.Edits);
        var trusted = new List<TrustedToolCall>();
        broker.Trusted += trusted.Add;

        var pending = broker.RequestAsync(Session, "Write", "toolu_1", Writing(Copy + suffix));

        broker.PendingFor(Session).Should().ContainSingle(why);
        trusted.Should().BeEmpty();
        broker.Answer(broker.PendingFor(Session)[0].RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
    }

    [Theory]
    [InlineData("/.githooks/pre-commit")]
    [InlineData("/.forgejo/workflows/ci.yml")]
    [InlineData("/.editorconfig")]
    [InlineData("/src/.env")]
    public async Task With_edits_trusted_a_hidden_path_inside_the_copy_still_asks(string suffix)
    {
        var path = Copy + suffix;
        // Hooks run on the host at the next commit, workflows run in CI at the next push, and a dotfile configures
        // some tool: a person who trusted edits to the code did not say they trusted those. (.claude used to be a case
        // here; since backlog 271 a write there is refused before trust is consulted, as the next test holds.)
        var broker = Broker();
        broker.RegisterSession(Session, Copy, workspaceId: Workspace);
        _trust.Set(Workspace, WorkspaceTrust.Edits);
        var trusted = new List<TrustedToolCall>();
        broker.Trusted += trusted.Add;

        var pending = broker.RequestAsync(Session, "Write", "toolu_1", Writing(path));

        broker.PendingFor(Session).Should().ContainSingle();
        broker.PendingFor(Session)[0].Target!.Containment.Should().Be(AgentTargetContainment.Inside, "it IS inside; it asks for what it is, not where");
        trusted.Should().BeEmpty();
        broker.Answer(broker.PendingFor(Session)[0].RequestId, approved: true, "ok");
        (await pending).Approved.Should().BeTrue();
    }

    [Fact]
    public async Task With_edits_trusted_a_location_the_cli_protects_is_refused_and_never_trusted()
    {
        // Backlog 271: the agent CLI refuses this write after any approval, so trust is never consulted for it.
        var broker = Broker();
        broker.RegisterSession(Session, Copy, workspaceId: Workspace);
        _trust.Set(Workspace, WorkspaceTrust.Edits);
        var trusted = new List<TrustedToolCall>();
        broker.Trusted += trusted.Add;

        var decision = await broker.RequestAsync(Session, "Write", "toolu_1", Writing(Copy + "/.claude/settings.json"));

        decision.Approved.Should().BeFalse();
        decision.Reason.Should().StartWith(AgentCliProtectedWrite.Sentence);
        broker.PendingFor(Session).Should().BeEmpty();
        trusted.Should().BeEmpty();
    }

    [Fact]
    public async Task At_the_ask_level_the_same_edit_asks_exactly_as_before()
    {
        var broker = Broker();
        broker.RegisterSession(Session, Copy, workspaceId: Workspace);
        var trusted = new List<TrustedToolCall>();
        broker.Trusted += trusted.Add;

        var pending = broker.RequestAsync(Session, "Edit", "toolu_1", Writing(Copy + "/src/Program.cs"));

        broker.PendingFor(Session).Should().ContainSingle();
        trusted.Should().BeEmpty();
        broker.Answer(broker.PendingFor(Session)[0].RequestId, approved: true, "ok");
        (await pending).Approved.Should().BeTrue();
    }

    [Fact]
    public async Task A_change_of_level_applies_to_the_next_call_in_both_directions()
    {
        var broker = Broker();
        broker.RegisterSession(Session, Copy, workspaceId: Workspace);

        _trust.Set(Workspace, WorkspaceTrust.Edits);
        (await broker.RequestAsync(Session, "Edit", "toolu_1", Writing(Copy + "/a.cs"))).Approved.Should().BeTrue();

        _trust.Set(Workspace, WorkspaceTrust.Ask);
        var pending = broker.RequestAsync(Session, "Edit", "toolu_2", Writing(Copy + "/a.cs"));
        broker.PendingFor(Session).Should().ContainSingle("the level is read live, so a change of mind is immediate");
        broker.Answer(broker.PendingFor(Session)[0].RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
    }

    [Fact]
    public async Task A_session_registered_without_its_workspace_has_no_level_and_asks()
    {
        var broker = Broker();
        broker.RegisterSession(Session, Copy);
        _trust.Set(Workspace, WorkspaceTrust.Edits);

        var pending = broker.RequestAsync(Session, "Edit", "toolu_1", Writing(Copy + "/a.cs"));

        broker.PendingFor(Session).Should().ContainSingle("no workspace was named, so no folder's trust applies");
        broker.Answer(broker.PendingFor(Session)[0].RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
    }

    [Fact]
    public async Task With_edits_trusted_the_work_gate_still_refuses_an_edit_with_no_work_item()
    {
        // The trust branch sits AFTER the gates: trusting edits is not a way around ADR 0020.
        var work = new SqliteSessionWorkItems(_store.Factory, TimeProvider.System);
        var broker = new AgentApprovalBroker(TimeSpan.FromMinutes(5), work: work, trust: _trust);
        broker.RegisterSession(Session, Copy, workspaceId: Workspace);
        _trust.Set(Workspace, WorkspaceTrust.Edits);
        var refused = new List<AutomaticToolRefusal>();
        var trusted = new List<TrustedToolCall>();
        broker.Refused += refused.Add;
        broker.Trusted += trusted.Add;

        var decision = await broker.RequestAsync(Session, "Edit", "toolu_1", Writing(Copy + "/a.cs"));

        decision.Approved.Should().BeFalse();
        refused.Should().ContainSingle();
        trusted.Should().BeEmpty();
        broker.PendingFor(Session).Should().BeEmpty();
    }

    // ---- backlog 312: a worktree session's trust is its repository folder's ----------------------------------

    private const string RepositoryWorkspace = "repo-1";
    private const string WorktreeWorkspace = "wt-1";
    private const string RepositoryRoot = "/repos/widget";
    private const string WorktreeRoot = "/repos/widget.fravashi-worktrees/1";

    [Fact]
    public async Task A_worktree_whose_repository_trusts_edits_lets_an_edit_run_unasked()
    {
        var workspaces = new SqliteWorkspaceStore(_store.Factory);
        workspaces.Touch(RepositoryWorkspace, "widget", RepositoryRoot);
        workspaces.Touch(WorktreeWorkspace, "widget-item-1", WorktreeRoot);
        _trust.Set(RepositoryWorkspace, WorkspaceTrust.Edits);

        var broker = Broker(workspaceStore: workspaces);
        broker.RegisterSession(Session, Copy, workspaceId: WorktreeWorkspace);
        var trusted = new List<TrustedToolCall>();
        broker.Trusted += trusted.Add;

        var decision = await broker.RequestAsync(Session, "Edit", "toolu_1", Writing(Copy + "/src/Program.cs"));

        decision.Approved.Should().BeTrue();
        trusted.Should().ContainSingle("the worktree's OWN id was never set to Edits; its repository's was");
        broker.PendingFor(Session).Should().BeEmpty();
    }

    [Fact]
    public async Task A_worktree_whose_repository_does_not_trust_edits_asks()
    {
        var workspaces = new SqliteWorkspaceStore(_store.Factory);
        workspaces.Touch(RepositoryWorkspace, "widget", RepositoryRoot);
        workspaces.Touch(WorktreeWorkspace, "widget-item-1", WorktreeRoot);
        // The repository's level is left at Ask (the default): never set to Edits.

        var broker = Broker(workspaceStore: workspaces);
        broker.RegisterSession(Session, Copy, workspaceId: WorktreeWorkspace);
        var trusted = new List<TrustedToolCall>();
        broker.Trusted += trusted.Add;

        var pending = broker.RequestAsync(Session, "Edit", "toolu_1", Writing(Copy + "/src/Program.cs"));

        broker.PendingFor(Session).Should().ContainSingle();
        trusted.Should().BeEmpty();
        broker.Answer(broker.PendingFor(Session)[0].RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
    }

    [Fact]
    public async Task Lowering_the_repositorys_trust_after_the_worktree_was_made_makes_its_next_edit_ask()
    {
        var workspaces = new SqliteWorkspaceStore(_store.Factory);
        workspaces.Touch(RepositoryWorkspace, "widget", RepositoryRoot);
        workspaces.Touch(WorktreeWorkspace, "widget-item-1", WorktreeRoot);
        _trust.Set(RepositoryWorkspace, WorkspaceTrust.Edits);

        var broker = Broker(workspaceStore: workspaces);
        broker.RegisterSession(Session, Copy, workspaceId: WorktreeWorkspace);

        (await broker.RequestAsync(Session, "Edit", "toolu_1", Writing(Copy + "/a.cs"))).Approved.Should().BeTrue();

        _trust.Set(RepositoryWorkspace, WorkspaceTrust.Ask);
        var pending = broker.RequestAsync(Session, "Edit", "toolu_2", Writing(Copy + "/a.cs"));
        broker.PendingFor(Session).Should().ContainSingle("the level is read live, so a change to the folder reaches its worktree immediately");
        broker.Answer(broker.PendingFor(Session)[0].RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
    }

    [Fact]
    public async Task A_worktree_whose_repository_has_no_record_asks_the_stricter_answer()
    {
        var workspaces = new SqliteWorkspaceStore(_store.Factory);
        // Only the worktree itself is registered: the repository folder it was made from (backlog 312's
        // own fail-closed case) was never opened through the welcome page, so there is nothing to read a
        // trust level from.
        workspaces.Touch(WorktreeWorkspace, "widget-item-1", WorktreeRoot);
        _trust.Set(RepositoryWorkspace, WorkspaceTrust.Edits);

        var broker = Broker(workspaceStore: workspaces);
        broker.RegisterSession(Session, Copy, workspaceId: WorktreeWorkspace);
        var trusted = new List<TrustedToolCall>();
        broker.Trusted += trusted.Add;

        var pending = broker.RequestAsync(Session, "Edit", "toolu_1", Writing(Copy + "/a.cs"));

        broker.PendingFor(Session).Should().ContainSingle("the repository has no record, so the strict answer (Ask) applies");
        trusted.Should().BeEmpty();
        broker.Answer(broker.PendingFor(Session)[0].RequestId, approved: false, "no");
        (await pending).Approved.Should().BeFalse();
    }

    [Fact]
    public async Task An_ordinary_workspaces_trust_is_unaffected_by_the_workspace_store_being_wired()
    {
        var workspaces = new SqliteWorkspaceStore(_store.Factory);
        workspaces.Touch(Workspace, "widget", RepositoryRoot);
        _trust.Set(Workspace, WorkspaceTrust.Edits);

        var broker = Broker(workspaceStore: workspaces);
        broker.RegisterSession(Session, Copy, workspaceId: Workspace);
        var trusted = new List<TrustedToolCall>();
        broker.Trusted += trusted.Add;

        var decision = await broker.RequestAsync(Session, "Edit", "toolu_1", Writing(Copy + "/a.cs"));

        decision.Approved.Should().BeTrue();
        trusted.Should().ContainSingle();
    }

    [Fact]
    public void A_trusted_call_round_trips_through_the_transcript_store_as_its_own_kind()
    {
        var e = new ToolCallTrusted(DateTimeOffset.UtcNow, "Edit", "/workspaces/w1/a.cs", AgentApprovalBroker.TrustedEditReason);

        AgentEventJson.KindOf(e).Should().Be("tool_trusted");
        var back = AgentEventJson.Deserialize(AgentEventJson.KindOf(e)!, AgentEventJson.Serialize(e));

        back.Should().BeOfType<ToolCallTrusted>().Which.Should().BeEquivalentTo(e);
    }
}
