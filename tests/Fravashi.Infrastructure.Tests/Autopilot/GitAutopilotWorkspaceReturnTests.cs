using AwesomeAssertions;
using Fravashi.Application.AgentSync;
using Fravashi.Application.Autopilot;
using Fravashi.Application.Git;
using Fravashi.Domain.Git;
using Fravashi.Domain.Projects;
using Fravashi.Infrastructure.Autopilot;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Autopilot;

/// <summary>
/// Backlog 310: one class behind both <see cref="IAutopilotWorkspaceStatus"/> (the dispatch's dirty check,
/// which resolves a project's workspace) and <see cref="IAutopilotWorkspaceReturn"/> (returning the workspace
/// once a run ends, which takes the EXACT workspace id the dispatch resolved and recorded, never re-resolving
/// by project). Never forces a checkout over work.
/// </summary>
public sealed class GitAutopilotWorkspaceReturnTests
{
    private static readonly ProjectId Project = new("proj-1");

    private readonly IGitService _git = Substitute.For<IGitService>();
    private readonly IGitWorkingTreeService _tree = Substitute.For<IGitWorkingTreeService>();
    private readonly IWorkspaceAgentSync _sync = Substitute.For<IWorkspaceAgentSync>();
    private readonly WorkspaceGitServices _services;
    private readonly FakeWorkspaceGit _workspaceGit;

    public GitAutopilotWorkspaceReturnTests()
    {
        _git.IsAvailable.Returns(true);
        // No sync session for this workspace is the honest default for these tests (a dev host, or a test):
        // SettleAsync answers null, and ReturnAsync proceeds straight to the tree read, exactly as
        // SessionGitWork.CommitAsync treats the same answer.
        _sync.SettleAsync(Arg.Any<string>(), Arg.Any<TimeSpan>()).Returns(Task.FromResult<bool?>(null));
        _services = new WorkspaceGitServices(
            "w1", Substitute.For<IGitCommandRunner>(), _git, _tree, Substitute.For<IGitRemoteService>());
        _workspaceGit = new FakeWorkspaceGit(_services);
    }

    private GitAutopilotWorkspaceReturn Return(Func<ProjectId, string?>? workspaceIdFor = null) =>
        new(workspaceIdFor ?? (_ => "w1"), _workspaceGit, _sync);

    // ---- IAutopilotWorkspaceStatus (the dispatch's own dirty check, by project) ----------------------------

    [Fact]
    public async Task Check_reads_the_branch_and_sums_the_dirty_entries()
    {
        _git.GetCurrentBranchAsync().Returns(Task.FromResult("feature/x"));
        _tree.GetStatusAsync().Returns(Task.FromResult(new GitWorkingStatus(
            "feature/x", null, 0, 0,
            StagedEntries: [new GitWorkingEntry("a.txt", GitEntryState.Modified, Staged: true)],
            UnstagedEntries: [new GitWorkingEntry("b.txt", GitEntryState.Modified, Staged: false)])));

        var state = await Return().CheckAsync(Project);

        state.Branch.Should().Be("feature/x");
        state.DirtyCount.Should().Be(2);
        state.Reachable.Should().BeTrue();
        state.WorkspaceId.Should().Be("w1");
    }

    [Fact]
    public async Task Check_on_a_workspace_the_lookup_cannot_resolve_is_unreachable()
    {
        var state = await Return(_ => null).CheckAsync(Project);

        state.Reachable.Should().BeFalse();
        state.Branch.Should().BeEmpty();
        state.DirtyCount.Should().Be(0);
        state.WorkspaceId.Should().BeNull();
    }

    /// <summary>
    /// Commit review #310: neither AutopilotDispatcher nor the dispatch form catches anything from this call,
    /// so a bridge fault reading the branch must answer Reachable: false, the same honest refusal an
    /// unresolvable workspace already gets, rather than crash the dispatch's click handler.
    /// </summary>
    [Fact]
    public async Task Check_when_the_branch_read_throws_is_unreachable_rather_than_crash()
    {
        _git.GetCurrentBranchAsync().Returns(Task.FromException<string>(new InvalidOperationException("bridge reset")));

        var state = await Return().CheckAsync(Project);

        state.Reachable.Should().BeFalse();
        state.Branch.Should().BeEmpty();
        state.DirtyCount.Should().Be(0);
    }

    /// <summary>
    /// Commit review #310 (third pass): GitWorkingTreeService.GetStatusAsync answers GitWorkingStatus.Empty
    /// (an empty Branch, among other things) when the runner is unavailable or git status itself exited
    /// nonzero, never when a real read simply found nothing to report (a real read always carries its own
    /// branch.head line). Reading that as a dirty count of 0 would let a dispatch proceed on a count this
    /// seam never actually measured.
    /// </summary>
    [Fact]
    public async Task Check_when_the_status_read_fails_is_unreachable_rather_than_a_clean_zero()
    {
        _git.GetCurrentBranchAsync().Returns(Task.FromResult("feature/x"));
        _tree.GetStatusAsync().Returns(Task.FromResult(GitWorkingStatus.Empty));

        var state = await Return().CheckAsync(Project);

        state.Reachable.Should().BeFalse();
        state.DirtyCount.Should().Be(0);
    }

    // ---- IAutopilotWorkspaceReturn (returning the workspace once a run ends, by its recorded id) -----------

    [Fact]
    public async Task Already_on_the_base_branch_is_a_no_op_success()
    {
        _git.GetCurrentBranchAsync().Returns(Task.FromResult("main"));

        var result = await Return().ReturnAsync("w1", "main");

        result.Returned.Should().BeTrue();
        result.Note.Should().BeNull();
        await _git.DidNotReceive().CheckoutAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task A_dirty_tree_refuses_with_the_exact_sentence_and_never_checks_out()
    {
        _git.GetCurrentBranchAsync().Returns(Task.FromResult("fravashi/item-1-abc"));
        _tree.GetStatusAsync().Returns(Task.FromResult(new GitWorkingStatus(
            "fravashi/item-1-abc", null, 0, 0,
            StagedEntries: [new GitWorkingEntry("a.txt", GitEntryState.Modified, Staged: true)],
            UnstagedEntries: [])));

        var result = await Return().ReturnAsync("w1", "main");

        result.Returned.Should().BeFalse();
        result.Note.Should().Be("left on fravashi/item-1-abc, uncommitted changes");
        await _git.DidNotReceive().CheckoutAsync(Arg.Any<string>());
    }

    /// <summary>
    /// Commit review #310 (third pass): a failed status read must never be read as "nothing staged or
    /// unstaged", or a checkout would land over whatever work is really there with this seam never having
    /// actually looked (the same GitWorkingStatus.Empty ambiguity CheckAsync guards against).
    /// </summary>
    [Fact]
    public async Task A_status_read_that_fails_refuses_and_never_checks_out()
    {
        _git.GetCurrentBranchAsync().Returns(Task.FromResult("fravashi/item-1-abc"));
        _tree.GetStatusAsync().Returns(Task.FromResult(GitWorkingStatus.Empty));

        var result = await Return().ReturnAsync("w1", "main");

        result.Returned.Should().BeFalse();
        result.Note.Should().Be("not returned to main: the working tree could not be read");
        await _git.DidNotReceive().CheckoutAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task A_clean_tree_on_a_different_branch_checks_out_the_base_branch()
    {
        _git.GetCurrentBranchAsync().Returns(Task.FromResult("fravashi/item-1-abc"));
        _tree.GetStatusAsync().Returns(Task.FromResult(GitWorkingStatus.Empty with { Branch = "fravashi/item-1-abc" }));

        var result = await Return().ReturnAsync("w1", "main");

        result.Returned.Should().BeTrue();
        result.Note.Should().BeNull();
        await _git.Received(1).CheckoutAsync("main");
    }

    /// <summary>
    /// Commit review #310: a status read taken before the agent's own edits all reached the host folder could
    /// see a clean tree, check out the base branch, and then have the write-back land on it. A settle that
    /// answers false (edits still on their way) must refuse before ever reading the tree or checking out.
    /// </summary>
    [Fact]
    public async Task A_settle_that_answers_false_never_calls_checkout_and_carries_its_own_note()
    {
        _git.GetCurrentBranchAsync().Returns(Task.FromResult("fravashi/item-1-abc"));
        _sync.SettleAsync("w1", Arg.Any<TimeSpan>()).Returns(Task.FromResult<bool?>(false));

        var result = await Return().ReturnAsync("w1", "main");

        result.Returned.Should().BeFalse();
        result.Note.Should().Be("left on fravashi/item-1-abc, edits still reaching the folder");
        await _tree.DidNotReceive().GetStatusAsync();
        await _git.DidNotReceive().CheckoutAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task An_unresolvable_workspace_id_answers_not_returned_naming_that()
    {
        var result = await Return().ReturnAsync("unknown-workspace", "main");

        result.Returned.Should().BeFalse();
        result.Note.Should().Be("not returned to main: the workspace could not be reached");
        await _git.DidNotReceive().CheckoutAsync(Arg.Any<string>());
    }

    /// <summary>
    /// Commit review #310 (third pass): the project-to-workspace lookup answers "the most recently opened
    /// workspace", which can change while a run is open (a person opens a different folder bound to the same
    /// project). ReturnAsync must act on the id it was GIVEN, never ask the lookup again, or it would check
    /// out a folder the run never touched onto the branch the dispatched folder was really on.
    /// </summary>
    [Fact]
    public async Task Return_never_consults_the_project_lookup_and_acts_only_on_the_given_workspace_id()
    {
        var otherGit = Substitute.For<IGitService>();
        otherGit.IsAvailable.Returns(true);
        var otherServices = new WorkspaceGitServices(
            "w2", Substitute.For<IGitCommandRunner>(), otherGit, Substitute.For<IGitWorkingTreeService>(),
            Substitute.For<IGitRemoteService>());
        var multiWorkspace = new FakeWorkspaceGit(_services, otherServices);
        _git.GetCurrentBranchAsync().Returns(Task.FromResult("fravashi/item-1-abc"));
        _tree.GetStatusAsync().Returns(Task.FromResult(GitWorkingStatus.Empty with { Branch = "fravashi/item-1-abc" }));

        // The lookup now answers a DIFFERENT workspace than the one dispatched, standing in for a person
        // opening another folder bound to the same project while the run is open.
        var instance = new GitAutopilotWorkspaceReturn(_ => "w2", multiWorkspace, _sync);

        var result = await instance.ReturnAsync("w1", "main");

        result.Returned.Should().BeTrue();
        await _git.Received(1).CheckoutAsync("main");
        await otherGit.DidNotReceive().CheckoutAsync(Arg.Any<string>());
        await otherGit.DidNotReceive().GetCurrentBranchAsync();
    }

    // ---- IAutopilotItemStart (putting the folder back on the run's base branch between items, #315) ----------

    [Fact]
    public async Task Prepare_on_the_base_branch_and_clean_is_ready_without_a_checkout()
    {
        _git.GetCurrentBranchAsync().Returns(Task.FromResult("main"));
        _tree.GetStatusAsync().Returns(Task.FromResult(GitWorkingStatus.Empty with { Branch = "main" }));

        var refusal = await Return().PrepareAsync("w1", "main");

        refusal.Should().BeNull();
        await _git.DidNotReceive().CheckoutAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task Prepare_on_the_previous_items_branch_and_clean_checks_out_the_base_branch()
    {
        _git.GetCurrentBranchAsync().Returns(Task.FromResult("fravashi/item-1-abc"));
        _tree.GetStatusAsync().Returns(Task.FromResult(GitWorkingStatus.Empty with { Branch = "fravashi/item-1-abc" }));

        var refusal = await Return().PrepareAsync("w1", "main");

        refusal.Should().BeNull();
        await _git.Received(1).CheckoutAsync("main");
    }

    [Fact]
    public async Task Prepare_on_a_dirty_previous_items_branch_refuses_in_words_and_never_checks_out()
    {
        _git.GetCurrentBranchAsync().Returns(Task.FromResult("fravashi/item-1-abc"));
        _tree.GetStatusAsync().Returns(Task.FromResult(new GitWorkingStatus(
            "fravashi/item-1-abc", null, 0, 0,
            StagedEntries: [],
            UnstagedEntries: [new GitWorkingEntry("leftover.txt", GitEntryState.Modified, Staged: false)])));

        var refusal = await Return().PrepareAsync("w1", "main");

        refusal.Should().Be("left on fravashi/item-1-abc, uncommitted changes");
        await _git.DidNotReceive().CheckoutAsync(Arg.Any<string>());
    }

    /// <summary>
    /// Unlike the run-end return, a folder already on the base branch is still read: between items, uncommitted
    /// work there is leftovers too, and the next item must not start on top of it.
    /// </summary>
    [Fact]
    public async Task Prepare_on_a_dirty_base_branch_refuses_too()
    {
        _git.GetCurrentBranchAsync().Returns(Task.FromResult("main"));
        _tree.GetStatusAsync().Returns(Task.FromResult(new GitWorkingStatus(
            "main", null, 0, 0,
            StagedEntries: [new GitWorkingEntry("leftover.txt", GitEntryState.Modified, Staged: true)],
            UnstagedEntries: [])));

        var refusal = await Return().PrepareAsync("w1", "main");

        refusal.Should().Be("left on main, uncommitted changes");
        await _git.DidNotReceive().CheckoutAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task Prepare_while_edits_are_still_reaching_the_folder_refuses_before_reading_the_tree()
    {
        _git.GetCurrentBranchAsync().Returns(Task.FromResult("fravashi/item-1-abc"));
        _sync.SettleAsync("w1", Arg.Any<TimeSpan>()).Returns(Task.FromResult<bool?>(false));

        var refusal = await Return().PrepareAsync("w1", "main");

        refusal.Should().Be("left on fravashi/item-1-abc, edits still reaching the folder");
        await _tree.DidNotReceive().GetStatusAsync();
        await _git.DidNotReceive().CheckoutAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task Prepare_on_an_unresolvable_workspace_refuses_naming_that()
    {
        var refusal = await Return().PrepareAsync("unknown-workspace", "main");

        refusal.Should().Be("not returned to main: the workspace could not be reached");
    }

    private sealed class FakeWorkspaceGit(params WorkspaceGitServices[] services) : IWorkspaceGit
    {
        public WorkspaceGitServices? For(string workspaceId) =>
            services.FirstOrDefault(s => s.WorkspaceId == workspaceId);
    }
}
