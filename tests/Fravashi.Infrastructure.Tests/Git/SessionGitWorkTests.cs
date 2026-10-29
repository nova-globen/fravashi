using System.Diagnostics;
using AwesomeAssertions;
using Fravashi.Application.AgentSync;
using Fravashi.Application.Git;
using Fravashi.Application.Persistence;
using Fravashi.Application.Sessions;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Git;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Autopilot;
using Fravashi.Infrastructure.Git;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Repository;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Tests.Persistence;
using Fravashi.Infrastructure.WorkItems;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Git;

/// <summary>
/// Backlog 86, over a REAL git repository in a temp folder: an attempt runs on its own branch, the files a
/// session was approved to change are the ones committed and nothing else is swept in, and a session that
/// changed nothing produces no commit. A fake git would prove the calls were made; only a real one proves
/// what ends up in the history, which is the thing this item is about.
/// </summary>
public sealed class SessionGitWorkTests : IDisposable
{
    private readonly string _repo =
        Path.Combine(Path.GetTempPath(), "fravashi-attempt-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly SqliteTestContext _store = new();
    private readonly AgentSessionId _session = AgentSessionId.New();
    private readonly SqliteSessionChangedFiles _changed;
    private readonly SqliteSessionBranches _branches;
    private readonly SqliteSessionWorkItems _work;
    private readonly LocalTrackerResolver _trackers;
    private readonly SessionGitWork _git;

    public SessionGitWorkTests()
    {
        Directory.CreateDirectory(_repo);
        Git("init", "-b", "main");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_repo, "seed.txt"), "seed\n");
        Git("add", "-A");
        Git("commit", "-m", "Initial commit");

        _changed = new SqliteSessionChangedFiles(_store.Factory, TimeProvider.System);
        _branches = new SqliteSessionBranches(_store.Factory);
        _work = new SqliteSessionWorkItems(_store.Factory, TimeProvider.System);
        _trackers = new LocalTrackerResolver(_store.Factory, TimeProvider.System);

        // Backlog 150: git is reached for the SESSION'S workspace. The session record names workspace "w1",
        // and the workspace git seam answers for "w1" with a runner over the temp repository, which is the same
        // shape the product builds (a bridge runner pinned to the id) with the host folder swapped for a local
        // one. A scope per operation was the previous shape, and it followed a circuit's window that a tool
        // call never has.
        var sessions = new SqliteSessionStore(_store.Factory);
        sessions.Save(new SessionRecord(
            _session, "w1", AgentTool.Claude, "t", null, AgentSessionState.Running, DateTimeOffset.UtcNow, null));

        _git = new SessionGitWork(
            new SessionGit(sessions, WorkspaceGitOver(_repo, "w1")),
            _changed,
            _branches,
            _work,
            _trackers,
            TimeProvider.System);
    }

    /// <summary>The workspace git seam answering for one id with a local runner over a repository folder.</summary>
    internal static WorkspaceGit WorkspaceGitOver(string repository, string workspaceId) =>
        WorkspaceGitOver(
            repository, workspaceId, new LocalGitCommandRunner(Options.Create(new RepositoryReaderOptions { Root = repository })));

    /// <summary>The same seam with a runner of the caller's (backlog 266: one over a bridge with no git).</summary>
    internal static WorkspaceGit WorkspaceGitOver(
        string repository, string workspaceId, Fravashi.Application.Git.IGitCommandRunner runner)
    {
        var options = Options.Create(new RepositoryReaderOptions { Root = repository });
        return new WorkspaceGit(
            id => id == workspaceId ? runner : null,
            options,
            new RepositoryWriteGate(),
            new ConfigurationBuilder().Build(),
            new GitHostAccess(
                Substitute.For<IGitHostStore>(),
                Substitute.For<ISecretStore>(),
                Substitute.For<IProviderConnectionStore>(),
                Substitute.For<IConfigurationStore>()));
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            foreach (var file in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_repo, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task An_attempt_runs_on_its_own_branch_named_after_the_work()
    {
        var item = await ItemAsync("Wire the approval prompt");
        _work.Link(_session, item.Id, ProjectId.Default, "user");

        var started = await _git.StartAsync(_session);

        started.Branch.Should().StartWith(SessionGitWork.BranchPrefix + "item-" + item.Number);
        Git("rev-parse", "--abbrev-ref", "HEAD").Trim().Should().Be(started.Branch);

        // And it is remembered, so a restart does not lose which branch the attempt is on.
        _branches.For(_session)!.Branch.Should().Be(started.Branch);
        _branches.For(_session)!.BaseBranch.Should().Be("main");
    }

    [Fact]
    public async Task A_session_with_no_work_still_gets_a_branch_of_its_own()
    {
        var started = await _git.StartAsync(_session);

        started.Branch.Should().StartWith(SessionGitWork.BranchPrefix + "session-");
    }

    [Fact]
    public async Task Starting_twice_is_one_branch()
    {
        var first = await _git.StartAsync(_session);
        var second = await _git.StartAsync(_session);

        second.Branch.Should().Be(first.Branch);
    }

    /// <summary>
    /// #308 decision 3: a worktree's own branch IS the attempt branch, so <c>AgentAutopilotBuilder</c>
    /// pre-records it (<c>ISessionBranches.Record</c>) before the agent's own <c>attempt_branch</c> call ever
    /// reaches <c>StartAsync</c>. Proven here by pre-recording a branch no git command in this repository
    /// ever made: StartAsync must return it unchanged, never branch or check out anything to "confirm" it,
    /// which this real repository's own checked-out branch staying exactly where it started proves directly.
    /// </summary>
    [Fact]
    public async Task A_pre_recorded_branch_is_returned_without_branching_or_checking_out_anything()
    {
        var before = Git("rev-parse", "--abbrev-ref", "HEAD").Trim();
        _branches.Record(_session, new SessionBranch("fravashi/worktree/item-1", "main", DateTimeOffset.UtcNow));

        var started = await _git.StartAsync(_session);

        started.Branch.Should().Be("fravashi/worktree/item-1");
        Git("rev-parse", "--abbrev-ref", "HEAD").Trim().Should().Be(
            before, "StartAsync must not branch or check out anything once a branch is already recorded");
    }

    /// <summary>
    /// #315: an autopilot item's attempt is made from its run's base branch even while the folder is still on
    /// the previous item's attempt branch, and THAT base is what is recorded for delivery to read.
    /// </summary>
    [Fact]
    public async Task An_attempt_base_set_for_the_session_is_branched_from_and_recorded_whatever_is_checked_out()
    {
        Git("checkout", "-b", "fravashi/item-1-previous");
        File.WriteAllText(Path.Combine(_repo, "previous.txt"), "the previous item's work\n");
        Git("add", "-A");
        Git("commit", "-m", "previous item");
        var mainHead = Git("rev-parse", "main").Trim();

        var attemptBases = new InMemorySessionAttemptBases();
        attemptBases.Set(_session, "main");
        var sessions = new SqliteSessionStore(_store.Factory);
        var git = new SessionGitWork(
            new SessionGit(sessions, WorkspaceGitOver(_repo, "w1")),
            _changed, _branches, _work, _trackers, TimeProvider.System, attemptBases: attemptBases);

        var started = await git.StartAsync(_session);

        Git("rev-parse", "--abbrev-ref", "HEAD").Trim().Should().Be(started.Branch);
        Git("rev-parse", "HEAD").Trim().Should().Be(mainHead, "the attempt is made from main, not the previous item's branch");
        _branches.For(_session)!.BaseBranch.Should().Be("main");
    }

    [Fact]
    public async Task Only_the_files_the_session_was_approved_to_change_are_committed()
    {
        await _git.StartAsync(_session);

        // Two files change on disk; the session was only ever approved for one of them.
        File.WriteAllText(Path.Combine(_repo, "mine.txt"), "changed by the session\n");
        File.WriteAllText(Path.Combine(_repo, "theirs.txt"), "somebody else was editing this\n");
        _changed.Record(_session, "mine.txt");

        var result = await _git.CommitAsync(_session, "fix the thing");

        result.Sha.Should().NotBeNullOrWhiteSpace(result.Error);
        result.Committed.Should().Be(1);

        Files(result.Sha!).Should().Equal("mine.txt");
        Git("status", "--porcelain").Should().Contain("theirs.txt", "the other file is still uncommitted");
    }

    /// <summary>
    /// Backlog 332: the worktree #308 made for its own item belongs to the ITEM, never to a person, so a
    /// resumed item's fresh conversation still commits a file an earlier, stopped conversation left there,
    /// never only what THIS conversation itself was approved to change.
    /// </summary>
    [Fact]
    public async Task A_worktree_owning_sessions_default_commit_covers_everything_uncommitted_in_it()
    {
        var broker = Substitute.For<IAgentApprovalBroker>();
        var autopilotSessions = new AutopilotBuilderSessions(broker);
        var item = new WorkItemId("item-1");
        autopilotSessions.Bind(item, _session);
        autopilotSessions.BindWorktree(item, "repo-w1", "w1", 1);
        var git = new SessionGitWork(
            new SessionGit(new SqliteSessionStore(_store.Factory), WorkspaceGitOver(_repo, "w1")),
            _changed, _branches, _work, _trackers, TimeProvider.System, autopilotSessions: autopilotSessions);

        // This conversation's own write, approved as usual...
        File.WriteAllText(Path.Combine(_repo, "mine.txt"), "written by this conversation\n");
        _changed.Record(_session, "mine.txt");
        // ...and the earlier, stopped conversation's own leftover, never approved for this session.
        File.WriteAllText(Path.Combine(_repo, "leftover.txt"), "written by a stopped earlier attempt\n");

        var result = await git.CommitAsync(_session, "finish the item");

        result.Sha.Should().NotBeNullOrWhiteSpace(result.Error);
        Files(result.Sha!).Should().Contain(["mine.txt", "leftover.txt"]);
    }

    /// <summary>
    /// The honest fallback: a worktree-owning session whose worktree happens to be clean (the ordinary case,
    /// since a worktree is clean right after it is made) has nothing worktree-specific to say, so this reads
    /// exactly as the ordinary approved-changes accounting, same as a session that owns no worktree at all.
    /// </summary>
    [Fact]
    public async Task A_worktree_owning_session_with_a_clean_tree_falls_back_to_its_own_approved_changes()
    {
        var broker = Substitute.For<IAgentApprovalBroker>();
        var autopilotSessions = new AutopilotBuilderSessions(broker);
        var item = new WorkItemId("item-1");
        autopilotSessions.Bind(item, _session);
        autopilotSessions.BindWorktree(item, "repo-w1", "w1", 1);
        var git = new SessionGitWork(
            new SessionGit(new SqliteSessionStore(_store.Factory), WorkspaceGitOver(_repo, "w1")),
            _changed, _branches, _work, _trackers, TimeProvider.System, autopilotSessions: autopilotSessions);

        var result = await git.DefaultPathsAsync(_session);

        result.NotSettled.Should().BeFalse();
        result.Error.Should().BeNull();
        result.Paths.Should().BeEmpty(
            "a clean worktree has nothing worktree-specific to say, and this session was approved for nothing either");
    }

    /// <summary>
    /// Backlog 332 (commit review, third pass): a worktree-owning session whose own settle genuinely times
    /// out must never read as "nothing to commit", which is exactly #332's own symptom under a slow bridge
    /// instead of a stopped conversation. This session has no approved changes of its own (the resumed-item
    /// shape), so the old silent fallback to <c>Paths(session)</c> would have answered empty and
    /// <c>CommitAsync</c> would have reported <c>Nothing</c>.
    /// </summary>
    [Fact]
    public async Task A_worktree_owning_sessions_commit_reports_not_settled_rather_than_nothing_when_its_settle_times_out()
    {
        var broker = Substitute.For<IAgentApprovalBroker>();
        var autopilotSessions = new AutopilotBuilderSessions(broker);
        var item = new WorkItemId("item-1");
        autopilotSessions.Bind(item, _session);
        autopilotSessions.BindWorktree(item, "repo-w1", "w1", 1);

        var gitService = Substitute.For<IGitService>();
        gitService.IsAvailable.Returns(true);
        var tree = Substitute.For<IGitWorkingTreeService>();
        var services = new WorkspaceGitServices(
            "w1", Substitute.For<IGitCommandRunner>(), gitService, tree, Substitute.For<IGitRemoteService>());
        var sync = Substitute.For<IWorkspaceAgentSync>();
        sync.SettleAsync("w1", Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<bool?>(false));

        var git = new SessionGitWork(
            new SessionGit(new SqliteSessionStore(_store.Factory), new FakeWorkspaceGit(services), sync),
            _changed, _branches, _work, _trackers, TimeProvider.System, autopilotSessions: autopilotSessions);

        var result = await git.CommitAsync(_session, "finish the item");

        result.NotSettled.Should().BeTrue();
        result.Nothing.Should().BeFalse("a settle that genuinely timed out is not the same as nothing to commit");
        _ = tree.DidNotReceive().GetStatusAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The counterpart: a status read that fails is reported as an error, never silently read as nothing to
    /// commit either. <see cref="IGitWorkingTreeService.GetStatusAsync"/> never actually throws on a failed
    /// git call: the real implementation answers <see cref="GitWorkingStatus.Empty"/> (an empty
    /// <see cref="GitWorkingStatus.Branch"/>) on a non-zero exit or an unavailable runner, so that is the
    /// shape this fakes, not a thrown exception.
    /// </summary>
    [Fact]
    public async Task A_worktree_owning_sessions_commit_reports_an_error_rather_than_nothing_when_its_status_cannot_be_read()
    {
        var broker = Substitute.For<IAgentApprovalBroker>();
        var autopilotSessions = new AutopilotBuilderSessions(broker);
        var item = new WorkItemId("item-1");
        autopilotSessions.Bind(item, _session);
        autopilotSessions.BindWorktree(item, "repo-w1", "w1", 1);

        var gitService = Substitute.For<IGitService>();
        gitService.IsAvailable.Returns(true);
        var tree = Substitute.For<IGitWorkingTreeService>();
        tree.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(GitWorkingStatus.Empty));
        var services = new WorkspaceGitServices(
            "w1", Substitute.For<IGitCommandRunner>(), gitService, tree, Substitute.For<IGitRemoteService>());

        var git = new SessionGitWork(
            new SessionGit(new SqliteSessionStore(_store.Factory), new FakeWorkspaceGit(services)),
            _changed, _branches, _work, _trackers, TimeProvider.System, autopilotSessions: autopilotSessions);

        var result = await git.CommitAsync(_session, "finish the item");

        result.Error.Should().Contain("could not be read");
        result.Nothing.Should().BeFalse("a status read failure is not the same as nothing to commit");
    }

    /// <summary>
    /// Backlog 332 (commit review, fourth pass): an unreachable or unavailable repository must answer an
    /// error too, never fall through silently to the ordinary approved-changes accounting with no flag set.
    /// </summary>
    [Fact]
    public async Task A_worktree_owning_sessions_commit_reports_an_error_when_its_repository_cannot_be_reached()
    {
        var broker = Substitute.For<IAgentApprovalBroker>();
        var autopilotSessions = new AutopilotBuilderSessions(broker);
        var item = new WorkItemId("item-1");
        autopilotSessions.Bind(item, _session);
        autopilotSessions.BindWorktree(item, "repo-w1", "w1", 1);

        var gitService = Substitute.For<IGitService>();
        gitService.IsAvailable.Returns(false);
        var tree = Substitute.For<IGitWorkingTreeService>();
        var services = new WorkspaceGitServices(
            "w1", Substitute.For<IGitCommandRunner>(), gitService, tree, Substitute.For<IGitRemoteService>());

        var git = new SessionGitWork(
            new SessionGit(new SqliteSessionStore(_store.Factory), new FakeWorkspaceGit(services)),
            _changed, _branches, _work, _trackers, TimeProvider.System, autopilotSessions: autopilotSessions);

        var result = await git.CommitAsync(_session, "finish the item");

        result.Error.Should().NotBeNullOrWhiteSpace();
        result.Nothing.Should().BeFalse("an unreachable repository is not the same as nothing to commit");
        _ = tree.DidNotReceive().GetStatusAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Backlog 332 (commit review, fourth pass): the bridge's own transport failure
    /// (<see cref="HttpRequestException"/>, which <c>BridgeClient</c> throws on a non-success answer) must be
    /// caught and reported as an error too, never escape <c>CommitAsync</c> as an unhandled exception.
    /// </summary>
    [Fact]
    public async Task A_worktree_owning_sessions_commit_reports_an_error_rather_than_crashing_on_a_bridge_transport_failure()
    {
        var broker = Substitute.For<IAgentApprovalBroker>();
        var autopilotSessions = new AutopilotBuilderSessions(broker);
        var item = new WorkItemId("item-1");
        autopilotSessions.Bind(item, _session);
        autopilotSessions.BindWorktree(item, "repo-w1", "w1", 1);

        var gitService = Substitute.For<IGitService>();
        gitService.IsAvailable.Returns(true);
        var tree = Substitute.For<IGitWorkingTreeService>();
        tree.GetStatusAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<GitWorkingStatus>(new HttpRequestException("the bridge refused the request")));
        var services = new WorkspaceGitServices(
            "w1", Substitute.For<IGitCommandRunner>(), gitService, tree, Substitute.For<IGitRemoteService>());

        var git = new SessionGitWork(
            new SessionGit(new SqliteSessionStore(_store.Factory), new FakeWorkspaceGit(services)),
            _changed, _branches, _work, _trackers, TimeProvider.System, autopilotSessions: autopilotSessions);

        var result = await git.CommitAsync(_session, "finish the item");

        result.Error.Should().Contain("the bridge refused the request");
        result.Nothing.Should().BeFalse("a bridge transport failure is not the same as nothing to commit");
    }

    /// <summary>Backlog 332: the worktree's own git seams, answered for one fixed workspace id.</summary>
    private sealed class FakeWorkspaceGit(WorkspaceGitServices services) : IWorkspaceGit
    {
        public WorkspaceGitServices? For(string workspaceId) => workspaceId == services.WorkspaceId ? services : null;
    }

    [Fact]
    public async Task The_message_names_the_work_and_says_an_agent_session_produced_it()
    {
        var item = await ItemAsync("Wire the approval prompt");
        _work.Link(_session, item.Id, ProjectId.Default, "user");
        await _git.StartAsync(_session);
        File.WriteAllText(Path.Combine(_repo, "mine.txt"), "changed\n");
        _changed.Record(_session, "mine.txt");

        var result = await _git.CommitAsync(_session, "wire the prompt");

        var message = Git("log", "-1", "--format=%B", result.Sha!);
        message.Should().Contain("wire the prompt");
        message.Should().Contain("#" + item.Number).And.Contain("Wire the approval prompt");
        message.Should().Contain("Fravashi agent session");

        // The AUTHOR stays the host identity: that is who is accountable for what lands in the repository.
        Git("log", "-1", "--format=%an", result.Sha!).Trim().Should().Be("Test");
    }

    /// <summary>
    /// #318, the commit seen on v0.9.84: an agent passed a copy of the trailer as its whole message. That, an
    /// empty message and one whose first line is empty are each refused in words, and nothing is committed.
    /// </summary>
    [Theory]
    [InlineData("Work items:\n- #24 Add a changelog\n\nProduced by a Fravashi agent session (703debf8).", "trailer")]
    [InlineData("Produced by a Fravashi agent session (703debf8).", "trailer")]
    [InlineData("\nfix the thing", "no subject line")]
    [InlineData("   ", "empty")]
    public async Task A_message_with_no_subject_of_its_own_is_refused_in_words_and_nothing_is_committed(
        string summary, string named)
    {
        var item = await ItemAsync("Add a changelog");
        _work.Link(_session, item.Id, ProjectId.Default, "user");
        await _git.StartAsync(_session);
        File.WriteAllText(Path.Combine(_repo, "mine.txt"), "changed\n");
        _changed.Record(_session, "mine.txt");
        var head = Git("rev-parse", "HEAD").Trim();

        var result = await _git.CommitAsync(_session, summary);

        result.Refused.Should().BeTrue();
        result.Sha.Should().BeNull();
        result.Error.Should().Contain(named).And.Contain("subject").And.StartWith("Nothing was committed");
        Git("rev-parse", "HEAD").Trim().Should().Be(head);
        Git("diff", "--cached", "--name-only").Trim().Should().BeEmpty("a refused message stages nothing");
    }

    [Fact]
    public async Task A_message_that_already_carries_the_trailer_gets_it_once_naming_this_session()
    {
        var item = await ItemAsync("Add a changelog");
        _work.Link(_session, item.Id, ProjectId.Default, "user");
        await _git.StartAsync(_session);
        File.WriteAllText(Path.Combine(_repo, "mine.txt"), "changed\n");
        _changed.Record(_session, "mine.txt");

        // The trailer copied from ANOTHER session's commit, under a real subject and body.
        var result = await _git.CommitAsync(
            _session,
            "docs: add a changelog\n\nWhy it exists.\n\nWork items:\n- #" + item.Number
                + " Add a changelog\n\nProduced by a Fravashi agent session (0badc0de).");

        result.Sha.Should().NotBeNullOrWhiteSpace(result.Error);
        var message = Git("log", "-1", "--format=%B", result.Sha!);
        Git("log", "-1", "--format=%s", result.Sha!).Trim().Should().Be("docs: add a changelog");
        message.Should().Contain("Why it exists.");
        Occurrences(message, "Work items:").Should().Be(1);
        Occurrences(message, "Produced by a Fravashi agent session").Should().Be(1);
        message.Should().Contain(_session.Value.ToString("N")[..8]).And.NotContain("0badc0de");
    }

    [Fact]
    public async Task An_ordinary_message_is_committed_exactly_as_before()
    {
        var item = await ItemAsync("Wire the approval prompt");
        _work.Link(_session, item.Id, ProjectId.Default, "user");
        await _git.StartAsync(_session);
        File.WriteAllText(Path.Combine(_repo, "mine.txt"), "changed\n");
        _changed.Record(_session, "mine.txt");

        var result = await _git.CommitAsync(_session, "feat(x): wire the prompt\n\nA body line.\n- a bullet");

        Git("log", "-1", "--format=%B", result.Sha!).TrimEnd().Should().Be(
            "feat(x): wire the prompt\n\nA body line.\n- a bullet\n\nWork items:\n- #" + item.Number
            + " Wire the approval prompt\n\nProduced by a Fravashi agent session ("
            + _session.Value.ToString("N")[..8] + ").");
    }

    [Fact]
    public async Task A_session_that_changed_nothing_produces_no_commit_and_that_is_not_an_error()
    {
        await _git.StartAsync(_session);

        var result = await _git.CommitAsync(_session, "nothing happened");

        result.Nothing.Should().BeTrue();
        result.Sha.Should().BeNull();
        result.Error.Should().BeNull("changing nothing is a success, not a failure");
    }

    [Fact]
    public async Task A_file_approved_and_then_left_alone_produces_no_commit_either()
    {
        await _git.StartAsync(_session);

        // Approved, and then not actually changed: there is nothing to record.
        _changed.Record(_session, "seed.txt");

        var result = await _git.CommitAsync(_session, "nothing in the end");

        result.Nothing.Should().BeTrue();
        result.Sha.Should().BeNull();
    }

    [Fact]
    public async Task A_deleted_file_the_session_was_approved_for_is_committed_as_a_deletion()
    {
        await _git.StartAsync(_session);
        File.Delete(Path.Combine(_repo, "seed.txt"));
        _changed.Record(_session, "seed.txt");

        var result = await _git.CommitAsync(_session, "remove the seed");

        result.Sha.Should().NotBeNullOrWhiteSpace(result.Error);
        Files(result.Sha!).Should().Equal("seed.txt");
    }

    [Fact]
    public async Task The_commit_lands_on_the_attempts_branch_and_leaves_the_base_alone()
    {
        var started = await _git.StartAsync(_session);
        File.WriteAllText(Path.Combine(_repo, "mine.txt"), "changed\n");
        _changed.Record(_session, "mine.txt");
        await _git.CommitAsync(_session, "on the branch");

        Git("log", "-1", "--format=%s", started.Branch!).Trim().Should().Be("on the branch");
        Git("log", "-1", "--format=%s", "main").Trim().Should().Be("Initial commit");
    }

    // ---- helpers ------------------------------------------------------------------------------

    private async Task<WorkItem> ItemAsync(string title)
    {
        var created = await _trackers.For(ProjectId.Default).CreateAsync(
            new NewWorkItem { Level = WorkItemLevel.Task, Title = title, Description = "Why it exists." },
            MutationContext.User);
        created.Outcome.Should().Be(TrackerOutcome.Ok);
        return created.Value;
    }

    private static int Occurrences(string text, string part) =>
        (text.Length - text.Replace(part, string.Empty, StringComparison.Ordinal).Length) / part.Length;

    private IReadOnlyList<string> Files(string sha) =>
        [.. Git("show", "--name-only", "--format=", sha)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private string Git(params string[] args) => RunGit(_repo, args);

    /// <summary>Runs git in <paramref name="repository"/> and returns its output, throwing when it fails.</summary>
    internal static string RunGit(string repository, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;

        // Both pipes at once: draining one to the end before starting the other deadlocks a child that
        // fills the buffer it is not being read from (the same fault backlog 99 records for search).
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        var output = stdout.GetAwaiter().GetResult();
        var error = stderr.GetAwaiter().GetResult();

        return process.ExitCode == 0
            ? output
            : throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
    }
}
