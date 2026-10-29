using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Tests.Persistence;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Backlog 44, through the real store and the real approval broker: a workspace change in a session that
/// names no work item is denied before anyone is asked, linking one unblocks exactly that call, reading is
/// never gated, graph writes are never gated, and every way of failing denies.
/// </summary>
public sealed class SessionWorkGateTests : IDisposable
{
    private readonly SqliteTestContext _store = new();
    private readonly AgentSessionId _session = AgentSessionId.New();
    private readonly SqliteSessionWorkItems _work;
    private readonly SqliteSessionPlans _plans;
    private readonly AgentApprovalBroker _broker;

    public SessionWorkGateTests()
    {
        _work = new SqliteSessionWorkItems(_store.Factory, TimeProvider.System);
        _plans = new SqliteSessionPlans(_store.Factory, TimeProvider.System);
        _broker = new AgentApprovalBroker(TimeSpan.FromSeconds(2), work: _work, plans: _plans);
        _broker.RegisterSession(_session, "/workspaces/a");
    }

    /// <summary>Binds the session to work AND a plan, which is what both gates together require.</summary>
    private void Bind()
    {
        _work.Link(_session, new WorkItemId("item-1"), ProjectId.Default, "user");
        _plans.Link(_session, "plan-1", "user");
    }

    public void Dispose()
    {
        _broker.Dispose();
        _store.Dispose();
    }

    private static AgentToolInput Input(string name, string value) =>
        AgentToolInput.FromStrings(new Dictionary<string, string> { [name] = value });

    // ---- the store --------------------------------------------------------------------------

    [Fact]
    public void A_link_survives_and_reads_back_with_who_made_it()
    {
        var item = new WorkItemId("item-1");

        _work.HasWork(_session).Should().BeFalse();
        _work.Link(_session, item, ProjectId.Default, "user");

        _work.HasWork(_session).Should().BeTrue();
        var linked = _work.For(_session).Should().ContainSingle().Subject;
        linked.Item.Should().Be(item);
        linked.Project.Should().Be(ProjectId.Default);
        linked.LinkedBy.Should().Be("user");
    }

    [Fact]
    public void Linking_twice_is_one_link_and_unlinking_what_is_not_linked_is_not_an_error()
    {
        var item = new WorkItemId("item-1");

        _work.Link(_session, item, ProjectId.Default, "user");
        _work.Link(_session, item, ProjectId.Default, "session:abc");

        _work.For(_session).Should().ContainSingle()
            .Which.LinkedBy.Should().Be("user", "the first link stands rather than being rewritten");

        _work.Unlink(_session, new WorkItemId("item-never-linked"));
        _work.For(_session).Should().ContainSingle();

        _work.Unlink(_session, item);
        _work.HasWork(_session).Should().BeFalse();
    }

    [Fact]
    public void One_sessions_links_are_not_another_sessions()
    {
        var other = AgentSessionId.New();
        _work.Link(_session, new WorkItemId("item-1"), ProjectId.Default, "user");

        _work.HasWork(other).Should().BeFalse();
        _work.For(other).Should().BeEmpty();
    }

    [Fact]
    public void A_store_that_cannot_be_read_answers_no_work_rather_than_opening_the_gate()
    {
        // Every failure mode denies (backlog 44's acceptance). A database with no such table is the cheapest
        // honest way to make the read fail, and the answer must be false, never true and never an exception.
        var empty = new SqliteConnectionFactory(new SqliteStoreOptions(
            Path.Combine(Path.GetTempPath(), "fravashi-tests", Guid.NewGuid().ToString("N") + ".db"),
            null,
            Pooling: false));

        var broken = new SqliteSessionWorkItems(empty, TimeProvider.System);

        broken.HasWork(_session).Should().BeFalse();
        broken.For(_session).Should().BeEmpty();
    }

    // ---- the gate ---------------------------------------------------------------------------

    [Fact]
    public async Task A_workspace_change_in_an_unlinked_session_is_denied_without_asking_anyone()
    {
        AutomaticToolRefusal? raised = null;
        _broker.Refused += r => raised = r;
        var asked = false;
        _broker.Requested += _ => asked = true;

        var decision = await _broker.RequestAsync(
            _session, "Write", "toolu_1", Input("file_path", "/workspaces/a/src/thing.cs"));

        decision.Approved.Should().BeFalse();
        decision.Reason.Should().Be(AgentWorkGate.UnlinkedSentence);
        asked.Should().BeFalse("nobody is asked about a call the gate already refused");
        raised.Should().NotBeNull("a refusal nobody sees is the thing backlog 117 was about");
        raised!.ToolName.Should().Be("Write");
    }

    [Fact]
    public async Task Linking_an_item_and_a_plan_unblocks_the_same_call()
    {
        Bind();

        PendingToolApproval? pending = null;
        _broker.Requested += p => pending = p;

        var call = _broker.RequestAsync(_session, "Write", "toolu_2", Input("file_path", "/workspaces/a/x.cs"));

        // It now reaches a person instead of being refused, which is the whole of "linking unblocks it".
        await WaitUntil(() => pending is not null);
        _broker.Answer(pending!.RequestId, approved: true, "Go ahead.");

        (await call).Approved.Should().BeTrue();
    }

    [Fact]
    public async Task A_bash_command_that_only_reads_is_sent_back_to_read_grep_and_glob_without_asking_anyone()
    {
        // Backlog 166, in both self-hosting trials: an agent read through Bash, which asked a person each time, or
        // met this gate's "write a plan first", which is the wrong answer to someone who is only looking.
        AutomaticToolRefusal? raised = null;
        _broker.Refused += r => raised = r;
        var asked = false;
        _broker.Requested += _ => asked = true;

        var decision = await _broker.RequestAsync(
            _session, "Bash", "toolu_r", Input("command", "sed -n '60,95p' src/Fravashi.Desktop/Program.cs"));

        decision.Approved.Should().BeFalse();
        decision.Reason.Should().Be(AgentReadRedirect.Sentence);
        asked.Should().BeFalse();
        raised.Should().NotBeNull("it is raised like every automatic refusal, so it shows in the transcript");
    }

    [Theory]
    [InlineData("pwd")]
    [InlineData("pwd -P")]
    public async Task A_bare_pwd_before_a_plan_exists_is_not_refused_by_the_work_gate(string command)
    {
        // Work item #340: a bare pwd changes nothing, so it should ask a person exactly like any other Bash
        // call once it is contained, rather than being refused by the work gate before a plan exists.
        var asked = false;
        _broker.Requested += _ => asked = true;
        PendingToolApproval? pending = null;
        _broker.Requested += p => pending = p;

        var call = _broker.RequestAsync(_session, "Bash", "toolu_pwd", Input("command", command));

        await WaitUntil(() => pending is not null);
        asked.Should().BeTrue("a bare pwd still asks a person; only the work gate's refusal lifts");
        _broker.Answer(pending!.RequestId, approved: true, "Go ahead.");

        var decision = await call;
        decision.Approved.Should().BeTrue();
        decision.Reason.Should().NotBe(AgentWorkGate.UnlinkedSentence);
    }

    [Theory]
    [InlineData("pwd && touch x")]
    [InlineData("pwd > f")]
    public async Task A_pwd_combined_with_a_change_is_still_refused_by_the_work_gate(string command)
    {
        AutomaticToolRefusal? raised = null;
        _broker.Refused += r => raised = r;
        var asked = false;
        _broker.Requested += _ => asked = true;

        var decision = await _broker.RequestAsync(_session, "Bash", "toolu_pwd_combined", Input("command", command));

        decision.Approved.Should().BeFalse();
        decision.Reason.Should().Be(AgentWorkGate.UnlinkedSentence);
        asked.Should().BeFalse();
        raised.Should().NotBeNull();
    }

    [Theory]
    [InlineData("git status")]
    [InlineData("git init")]
    [InlineData("cd workspace && git commit -m \"done\"")]
    public async Task A_bash_command_that_runs_git_is_refused_without_asking_anyone(string command)
    {
        // Work item #323: the contained copy has no .git by design, so git in Bash cannot see or change the
        // real repository, and git init there would make a commit look real and then lose it.
        AutomaticToolRefusal? raised = null;
        _broker.Refused += r => raised = r;
        var asked = false;
        _broker.Requested += _ => asked = true;

        var decision = await _broker.RequestAsync(_session, "Bash", "toolu_git", Input("command", command));

        decision.Approved.Should().BeFalse();
        decision.Reason.Should().Be(AgentGitInBash.Sentence);
        decision.Reason.Should().Contain("git_commit");
        asked.Should().BeFalse();
        raised.Should().NotBeNull("it is raised like every automatic refusal, so it shows in the transcript");
    }

    [Fact]
    public async Task An_ordinary_dotnet_build_is_not_affected_by_the_git_in_bash_rule()
    {
        Bind();
        PendingToolApproval? pending = null;
        _broker.Requested += p => pending = p;

        var call = _broker.RequestAsync(
            _session, "Bash", "toolu_build", Input("command", "dotnet build Fravashi.sln -p:EnableWindowsTargeting=true"));

        await WaitUntil(() => pending is not null);
        _broker.Answer(pending!.RequestId, approved: true, "Go ahead.");

        (await call).Approved.Should().BeTrue();
    }

    [Fact]
    public async Task A_session_registered_with_no_root_is_not_refused_by_the_git_in_bash_rule()
    {
        // The Claude runner (this version's one runtime, CLAUDE.md) registers a root before any call reaches
        // this gate, so this path is not a real production scenario; it exists for a session registered with
        // none (today only a test like this one), where "this copy has no .git" is a claim the studio cannot
        // back, so the rule stays quiet and the call falls through to ask a person like any other call this
        // gate cannot explain.
        using var noCopy = new AgentApprovalBroker(TimeSpan.FromSeconds(2));
        var session = AgentSessionId.New();
        noCopy.RegisterSession(session);

        PendingToolApproval? pending = null;
        noCopy.Requested += p => pending = p;

        var call = noCopy.RequestAsync(session, "Bash", "toolu_nocopy", Input("command", "git status"));

        await WaitUntil(() => pending is not null);
        noCopy.Answer(pending!.RequestId, approved: true, "Go ahead.");

        (await call).Approved.Should().BeTrue();
    }

    [Fact]
    public async Task A_devcontainer_write_is_refused_with_the_dockerfile_alternative_without_asking_anyone()
    {
        // Backlog 271: the pinned CLI refuses this write after any approval, so asking a person bought only a loop.
        // Bound, so it is not the work gate answering.
        Bind();
        AutomaticToolRefusal? raised = null;
        _broker.Refused += r => raised = r;
        var asked = false;
        _broker.Requested += _ => asked = true;

        var decision = await _broker.RequestAsync(
            _session, "Write", "toolu_dc", Input("file_path", "/workspaces/a/.devcontainer/devcontainer.json"));

        decision.Approved.Should().BeFalse();
        decision.Reason.Should().Be(AgentCliProtectedWrite.Sentence + AgentCliProtectedWrite.DevcontainerAlternative);
        asked.Should().BeFalse();
        raised.Should().NotBeNull("it is raised like every automatic refusal, so it shows in the transcript");
    }

    [Fact]
    public async Task Another_location_the_cli_protects_is_refused_without_asking_anyone_and_without_the_dockerfile_hint()
    {
        Bind();
        var asked = false;
        _broker.Requested += _ => asked = true;

        var decision = await _broker.RequestAsync(
            _session, "Write", "toolu_vs", Input("file_path", "/workspaces/a/.vscode/settings.json"));

        decision.Approved.Should().BeFalse();
        decision.Reason.Should().Be(AgentCliProtectedWrite.Sentence + AgentCliProtectedWrite.OtherwiseAskAPerson);
        asked.Should().BeFalse();
    }

    [LinkFact]
    public async Task A_link_inside_the_copy_does_not_route_a_devcontainer_write_around_the_refusal()
    {
        // A real tree: <root>/.devcontainer and a link <root>/dc pointing at it. The path as written names no
        // devcontainer; only where it resolves does, which is the broker's destination.
        var root = Path.Combine(Path.GetTempPath(), "fravashi-tests", "dc271-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".devcontainer"));
        Directory.CreateSymbolicLink(Path.Combine(root, "dc"), Path.Combine(root, ".devcontainer"));
        try
        {
            var session = AgentSessionId.New();
            using var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(2));
            broker.RegisterSession(session, root);
            var asked = false;
            broker.Requested += _ => asked = true;

            var decision = await broker.RequestAsync(
                session, "Write", "toolu_link", Input("file_path", Path.Combine(root, "dc", "devcontainer.json")));

            decision.Approved.Should().BeFalse();
            decision.Reason.Should().Be(AgentCliProtectedWrite.Sentence + AgentCliProtectedWrite.DevcontainerAlternative);
            asked.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_skill_under_claude_is_refused_up_front_without_asking_anyone()
    {
        // Backlog 279: the carve-out for the binary's .claude exceptions is gone, measured to buy only a wasted
        // approval (the CLI refused the write after a person approved it anyway).
        Bind();
        var asked = false;
        _broker.Requested += _ => asked = true;

        var decision = await _broker.RequestAsync(
            _session, "Edit", "toolu_sk", Input("file_path", "/workspaces/a/.claude/skills/releasing-fravashi/SKILL.md"));

        decision.Approved.Should().BeFalse();
        decision.Reason.Should().Be(AgentCliProtectedWrite.Sentence + AgentCliProtectedWrite.OtherwiseAskAPerson);
        asked.Should().BeFalse();
    }

    [Fact]
    public async Task A_root_dockerfile_write_still_asks_a_person()
    {
        Bind();
        PendingToolApproval? pending = null;
        _broker.Requested += p => pending = p;

        var call = _broker.RequestAsync(_session, "Write", "toolu_df", Input("file_path", "/workspaces/a/Dockerfile"));

        await WaitUntil(() => pending is not null);
        _broker.Answer(pending!.RequestId, approved: true, "Go ahead.");
        (await call).Approved.Should().BeTrue();
    }

    [Fact]
    public async Task A_build_piped_into_tail_still_asks_a_person()
    {
        Bind();
        PendingToolApproval? pending = null;
        _broker.Requested += p => pending = p;

        var call = _broker.RequestAsync(
            _session, "Bash", "toolu_b", Input("command", "dotnet build Fravashi.sln 2>&1 | tail -15"));

        await WaitUntil(() => pending is not null);
        _broker.Answer(pending!.RequestId, approved: true, "Go ahead.");
        (await call).Approved.Should().BeTrue();
    }

    [Fact]
    public async Task A_contained_read_is_never_gated()
    {
        var decision = await _broker.RequestAsync(
            _session, "Read", "toolu_3", Input("file_path", "/workspaces/a/README.md"));

        decision.Approved.Should().BeTrue();
        decision.Reason.Should().NotBe(AgentWorkGate.UnlinkedSentence);
    }

    [Fact]
    public async Task An_uncontained_read_still_asks_rather_than_being_refused_by_this_gate()
    {
        // Read-only work is never blocked BY G1. It can still ask, because where the read lands is a
        // different gate, and conflating the two would make the refusal say the wrong thing.
        PendingToolApproval? pending = null;
        _broker.Requested += p => pending = p;

        var call = _broker.RequestAsync(_session, "Read", "toolu_4", Input("file_path", "/data/fravashi.db"));

        await WaitUntil(() => pending is not null);
        _broker.Answer(pending!.RequestId, approved: false, "No.");

        var decision = await call;
        decision.Reason.Should().NotBe(AgentWorkGate.UnlinkedSentence);
    }

    [Fact]
    public async Task Work_with_no_plan_is_refused_by_the_second_gate_with_its_own_sentence()
    {
        // Gate G2 (backlog 45): the work says what is wanted, and the plan says how it will be done.
        _work.Link(_session, new WorkItemId("item-1"), ProjectId.Default, "user");

        var decision = await _broker.RequestAsync(
            _session, "Write", "toolu_g2", Input("file_path", "/workspaces/a/x.cs"));

        decision.Approved.Should().BeFalse();
        decision.Reason.Should().Be(AgentWorkGate.NoPlanSentence);
    }

    [Fact]
    public async Task A_plan_with_no_work_is_still_refused_for_the_missing_work()
    {
        _plans.Link(_session, "plan-1", "user");

        var decision = await _broker.RequestAsync(
            _session, "Write", "toolu_g1", Input("file_path", "/workspaces/a/x.cs"));

        decision.Approved.Should().BeFalse();
        decision.Reason.Should().Be(AgentWorkGate.UnlinkedSentence);
    }

    [Fact]
    public async Task A_studio_with_a_board_and_no_wiki_keeps_the_first_gate_and_has_no_second()
    {
        // Refusing every change for a plan the studio could never hold would be the wrong answer.
        using var noWiki = new AgentApprovalBroker(TimeSpan.FromSeconds(2), work: _work);
        var session = AgentSessionId.New();
        noWiki.RegisterSession(session, "/workspaces/a");
        _work.Link(session, new WorkItemId("item-1"), ProjectId.Default, "user");

        PendingToolApproval? pending = null;
        noWiki.Requested += p => pending = p;

        var call = noWiki.RequestAsync(session, "Write", "toolu_nw", Input("file_path", "/workspaces/a/x.cs"));

        await WaitUntil(() => pending is not null);
        noWiki.Answer(pending!.RequestId, approved: true, "Yes.");

        (await call).Approved.Should().BeTrue();
    }

    [Fact]
    public async Task A_graph_change_is_never_gated_because_it_is_how_the_gate_is_cleared()
    {
        PendingToolApproval? pending = null;
        _broker.Requested += p => pending = p;

        // Since backlog 77 a CREATION in the graph is not even asked about: it is how a session with no
        // work item gets one, and asking made a one-line change cost six clicks.
        var created = await _broker.RequestAsync(
            _session,
            AgentWorkGate.StudioToolPrefix + "work_item_create",
            "toolu_5",
            AgentToolInput.FromStrings(new Dictionary<string, string> { ["title"] = "A new item" }));

        created.Approved.Should().BeTrue();
        created.Reason.Should().NotBe(AgentWorkGate.UnlinkedSentence);
        pending.Should().BeNull();

        // A CHANGE to something in the graph still asks a person, and is still not REFUSED by this gate,
        // which is the sentence this test has always been about.
        var call = _broker.RequestAsync(
            _session,
            AgentWorkGate.StudioToolPrefix + "work_item_update",
            "toolu_6",
            AgentToolInput.FromStrings(new Dictionary<string, string> { ["number"] = "1", ["title"] = "Renamed" }));

        await WaitUntil(() => pending is not null);
        _broker.Answer(pending!.RequestId, approved: true, "Yes.");

        var decision = await call;
        decision.Approved.Should().BeTrue();
        decision.Reason.Should().NotBe(AgentWorkGate.UnlinkedSentence);
    }

    [Fact]
    public async Task A_broker_with_no_store_gates_nothing()
    {
        // A studio built without a tracker has no gate rather than a gate that denies everything.
        using var ungated = new AgentApprovalBroker(TimeSpan.FromSeconds(2));
        var session = AgentSessionId.New();
        ungated.RegisterSession(session, "/workspaces/a");

        PendingToolApproval? pending = null;
        ungated.Requested += p => pending = p;

        var call = ungated.RequestAsync(session, "Write", "toolu_6", Input("file_path", "/workspaces/a/x.cs"));

        await WaitUntil(() => pending is not null);
        ungated.Answer(pending!.RequestId, approved: true, "Yes.");

        (await call).Approved.Should().BeTrue();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        condition().Should().BeTrue("the condition should have been reached");
    }
}

/// <summary>Backlog 271's link test needs a symbolic link, which Windows creates only with a privilege.</summary>
internal sealed class LinkFactAttribute : FactAttribute
{
    public LinkFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Needs a symbolic link, which Windows creates only with a privilege; it runs on Linux CI and in the container.";
        }
    }
}
