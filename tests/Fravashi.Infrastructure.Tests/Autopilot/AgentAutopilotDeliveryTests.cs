using System.Net;
using System.Net.Http;
using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.AgentSync;
using Fravashi.Application.Autopilot;
using Fravashi.Application.Git;
using Fravashi.Application.Onboarding;
using Fravashi.Application.Onboarding.Auth;
using Fravashi.Application.Persistence;
using Fravashi.Application.Projects;
using Fravashi.Application.Sessions;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Autopilot;
using Fravashi.Domain.Git;
using Fravashi.Domain.Insights;
using Fravashi.Domain.Projects;
using Fravashi.Domain.PullRequests;
using Fravashi.Domain.Repository;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;
using Fravashi.Infrastructure.Autopilot;
using Fravashi.Infrastructure.Ci;
using Fravashi.Infrastructure.Git;
using Fravashi.Infrastructure.Sessions;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Autopilot;

/// <summary>
/// The real <see cref="IAutopilotDelivery"/> (#301): push, pull request, CI, scenario, evidence and the
/// In Review transition under branch + pull request; a ref move under commit to main. Every provider call
/// is faked (a <see cref="StubHttpMessageHandler"/>-backed client behind a fake git and tracker); nothing
/// here touches a real forge.
/// </summary>
public sealed class AgentAutopilotDeliveryTests
{
    private static readonly ProjectId Project = new("proj-1");
    private static readonly WorkItemId Item1 = new("item-1");
    private const string Origin = "https://github.com/acme/widget.git";
    private const string AttemptBranch = "fravashi/item-1";
    private const string BaseBranch = "main";
    private const string Sha = "1111111111111111111111111111111111111111";

    private readonly AgentApprovalBroker _broker = new(TimeSpan.FromSeconds(5));
    private readonly AutopilotBuilderSessions _sessions;
    private readonly FakeSessionStore _sessionStore = new();
    private readonly FakeBranches _branches = new();
    private readonly FakeProjectDelivery _deliveryMode = new(AutopilotDeliveryMode.BranchAndPullRequest);
    private readonly FakeTracker _tracker;
    private readonly FakeTrackerResolver _trackers;
    private readonly FakeSync _sync = new();
    private readonly AgentSessionId _session = AgentSessionId.New();

    public AgentAutopilotDeliveryTests()
    {
        _sessions = new AutopilotBuilderSessions(_broker);
        _tracker = new FakeTracker(Project, ItemWithScenario());
        _trackers = new FakeTrackerResolver(_tracker);
        _sessionStore.Save(_session, "w1");
        _sessions.Bind(Item1, _session);
        _branches.Record(_session, new SessionBranch(AttemptBranch, BaseBranch, DateTimeOffset.UtcNow));
    }

    private static AutopilotDeliveryContext Context(MissingScenarioPolicy policy = MissingScenarioPolicy.Write) =>
        new(Project, policy);

    private static WorkItem ItemWithScenario() => WorkItem.Restore(new WorkItemState
    {
        Id = Item1,
        Project = Project,
        Number = 1,
        Level = WorkItemLevel.Task,
        Title = "A title",
        Description = "A description.",
        Status = WorkItemStatus.InProgress,
        Scenarios = [new TestScenario("s1", "The scenario", [new ScenarioStep("st1", "run it", "exits 0", Command: "true")])],
    }).Value;

    private AgentAutopilotDelivery Delivery(
        FakeGit git, FakeRemote remote, FakeRunner runner, HttpMessageHandler handler, GitHostAccess access, IAgentCommandRunner? commands = null) =>
        new(
            _sessions,
            new SessionGit(_sessionStore, new FakeWorkspaceGit(new WorkspaceGitServices("w1", runner, git, ThrowingTree.Instance, remote))),
            _branches,
            _deliveryMode,
            new FakeHttpClientFactory(handler),
            () => access,
            new ForgeRuns(new HttpClient(handler), () => access),
            _trackers,
            _sync,
            new ScenarioRunner(_trackers, commands ?? new FakeCommandRunner(_ => new AgentCommandResult(true, 0, string.Empty, string.Empty, false))));

    private static GitHostAccess ConfiguredAccess(string token = "tok-123") =>
        new(
            new FakeHosts(new GitHostRecord("github.com", OnboardingProviders.GitHub, "ref-1", DateTimeOffset.UtcNow)),
            new FakeSecrets(("ref-1", CredentialEnvelope.Serialize(new StoredCredential(token, null, null, "pat")))),
            new FakeConnections(),
            new FakeSettings());

    private static GitHostAccess NoTokenAccess() =>
        new(
            new FakeHosts(null),
            new FakeSecrets(),
            new FakeConnections(),
            new FakeSettings());

    // ---- DeliverAsync, branch + pull request ---------------------------------------------------------

    [Fact]
    public async Task Pushing_then_creating_a_pull_request_carries_the_title_target_and_evidence()
    {
        _tracker.Evidence.Add(new EvidenceRecord(
            "ev-1", EvidenceKind.Review, EvidenceResult.Pass, "Commit review: passed.", "commit", null, null, null, null, DateTimeOffset.UtcNow));
        var git = new FakeGit(Origin, AttemptBranch);
        var remote = new FakeRemote();
        string? capturedBody = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return (HttpStatusCode.OK, "[]"); // no open pull request yet
            }

            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return (HttpStatusCode.Created, """{"number":1,"head":{"ref":"fravashi/item-1"},"base":{"ref":"main"},"html_url":"https://github.com/acme/widget/pull/1"}""");
        });

        var result = await Delivery(git, remote, new FakeRunner(), handler, ConfiguredAccess())
            .DeliverAsync(Context(), Item1, Sha);

        result.Delivered.Should().BeTrue();
        result.PullRequestUrl.Should().Be("https://github.com/acme/widget/pull/1");
        remote.PushCalls.Should().Be(1);
        capturedBody.Should().NotBeNull();
        using var created = JsonDocument.Parse(capturedBody!);
        created.RootElement.GetProperty("title").GetString().Should().Be("#1 A title");
        created.RootElement.GetProperty("head").GetString().Should().Be(AttemptBranch);
        created.RootElement.GetProperty("base").GetString().Should().Be(BaseBranch);
        var body = created.RootElement.GetProperty("body").GetString();
        body.Should().Contain("A description.").And.Contain("Commit review: passed.");
        _tracker.Evidence.Should().Contain(e => e.Kind == EvidenceKind.Other && e.ArtefactRef == "https://github.com/acme/widget/pull/1");
    }

    [Fact]
    public async Task A_second_delivery_finds_the_existing_open_pull_request_rather_than_opening_another()
    {
        var git = new FakeGit(Origin, AttemptBranch);
        var remote = new FakeRemote();
        var createCalls = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return (HttpStatusCode.OK,
                    """[{"number":1,"head":{"ref":"fravashi/item-1"},"base":{"ref":"main"},"html_url":"https://github.com/acme/widget/pull/1"}]""");
            }

            createCalls++;
            return (HttpStatusCode.Created, """{"number":2,"head":{"ref":"fravashi/item-1"},"base":{"ref":"main"}}""");
        });

        var result = await Delivery(git, remote, new FakeRunner(), handler, ConfiguredAccess())
            .DeliverAsync(Context(), Item1, Sha);

        result.Delivered.Should().BeTrue();
        result.PullRequestUrl.Should().Be("https://github.com/acme/widget/pull/1");
        createCalls.Should().Be(0, "a pull request that already exists for the branch is found, not opened twice");
    }

    [Fact]
    public async Task A_new_pull_request_after_the_first_closed_records_its_own_evidence_rather_than_replaying_the_first()
    {
        // Simulates a later run of the same item: the first pull request (pull/1) already has its evidence
        // recorded, it is no longer open, and a fresh one (pull/2) is opened for it.
        _tracker.Evidence.Add(new EvidenceRecord(
            "ev-1", EvidenceKind.Other, EvidenceResult.Pass, "Pull request opened: .../pull/1", "delivery",
            "https://github.com/acme/widget/pull/1", null, null, null, DateTimeOffset.UtcNow));
        var git = new FakeGit(Origin, AttemptBranch);
        var handler = new StubHttpMessageHandler(request => request.Method == HttpMethod.Get
            ? (HttpStatusCode.OK, "[]") // pull/1 is no longer open
            : (HttpStatusCode.Created, """{"number":2,"head":{"ref":"fravashi/item-1"},"base":{"ref":"main"},"html_url":"https://github.com/acme/widget/pull/2"}"""));

        var result = await Delivery(git, new FakeRemote(), new FakeRunner(), handler, ConfiguredAccess())
            .DeliverAsync(Context(), Item1, Sha);

        result.PullRequestUrl.Should().Be("https://github.com/acme/widget/pull/2");
        _tracker.Evidence.Should().Contain(e => e.ArtefactRef == "https://github.com/acme/widget/pull/2");
    }

    [Fact]
    public async Task A_push_whose_branch_does_not_match_the_recorded_attempt_branch_is_refused()
    {
        var git = new FakeGit(Origin, "some-other-branch");
        var remote = new FakeRemote();
        var handler = new StubHttpMessageHandler(_ => (HttpStatusCode.OK, "[]"));

        var result = await Delivery(git, remote, new FakeRunner(), handler, ConfiguredAccess())
            .DeliverAsync(Context(), Item1, Sha);

        result.Delivered.Should().BeFalse();
        result.Problem.Should().Contain("some-other-branch");
        remote.PushCalls.Should().Be(0, "nothing is pushed when the checked-out branch is not the attempt branch");
    }

    [Fact]
    public async Task A_push_whose_outcome_is_unknown_is_settled_by_asking_origin_rather_than_resent()
    {
        var git = new FakeGit(Origin, AttemptBranch);
        var pending = new GitPendingPush("refs/heads/" + AttemptBranch, Sha);
        var remote = new FakeRemote { Push = _ => Task.FromResult(new GitSyncOutcome(false) { Failure = GitSyncFailure.OutcomeUnknown, Pending = pending }) };
        remote.Compare = p => new GitSyncOutcome(true);
        var handler = new StubHttpMessageHandler(request =>
            request.Method == HttpMethod.Get ? (HttpStatusCode.OK, "[]") : (HttpStatusCode.Created, """{"number":1,"head":{"ref":"fravashi/item-1"},"base":{"ref":"main"}}"""));

        var result = await Delivery(git, remote, new FakeRunner(), handler, ConfiguredAccess())
            .DeliverAsync(Context(), Item1, Sha);

        result.Delivered.Should().BeTrue();
        remote.LastCompared.Should().Be(pending);
    }

    [Fact]
    public async Task No_git_host_token_is_reported_as_a_problem_rather_than_a_silent_pull_request()
    {
        var git = new FakeGit(Origin, AttemptBranch);
        var remote = new FakeRemote();
        var handler = new StubHttpMessageHandler(_ => (HttpStatusCode.OK, "[]"));

        var result = await Delivery(git, remote, new FakeRunner(), handler, NoTokenAccess())
            .DeliverAsync(Context(), Item1, Sha);

        result.Delivered.Should().BeFalse();
        result.PullRequestUrl.Should().BeNull();
        result.Problem.Should().Contain("token");
    }

    // ---- DeliverAsync, commit to main -----------------------------------------------------------------

    [Fact]
    public async Task A_clean_fast_forward_moves_the_base_branchs_ref_without_ever_checking_anything_out()
    {
        _deliveryMode.Mode = AutopilotDeliveryMode.CommitToMain;
        var git = new FakeGit(Origin, AttemptBranch) { MergeBaseAnswer = "base-head" };
        var runner = new FakeRunner(args => args[0] switch
        {
            // #308: checked first, for ANOTHER worktree having the base branch out; empty means none does.
            "worktree" => new GitCommandResult(0, string.Empty, string.Empty),
            "rev-parse" => new GitCommandResult(0, "base-head\n", string.Empty),
            "update-ref" => new GitCommandResult(0, string.Empty, string.Empty),
            _ => throw new InvalidOperationException("unexpected git call: " + string.Join(' ', args)),
        });

        var result = await Delivery(git, new FakeRemote(), runner, new StubHttpMessageHandler(_ => (HttpStatusCode.OK, "{}")), ConfiguredAccess())
            .DeliverAsync(Context(), Item1, Sha);

        result.Delivered.Should().BeTrue();
        result.PullRequestUrl.Should().BeNull();
        runner.Calls.Should().Contain(c => c[0] == "update-ref" && c[1] == "refs/heads/main" && c[2] == Sha && c[3] == "base-head");
        git.CheckoutCalls.Should().Be(0, "commit to main never checks anything out");
    }

    /// <summary>
    /// #308 host note point 6: under a parallel run this IS what the second of two parallel commit-to-main
    /// deliveries sees. The first one's own update-ref already moved the base branch's head past what this
    /// delivery's own merge-base read expects, so it parks for a person rather than rebasing it silently.
    /// </summary>
    [Fact]
    public async Task A_diverged_base_branch_refuses_the_ref_move_and_leaves_it_for_a_person()
    {
        _deliveryMode.Mode = AutopilotDeliveryMode.CommitToMain;
        var git = new FakeGit(Origin, AttemptBranch) { MergeBaseAnswer = "a-different-commit" };
        var runner = new FakeRunner(args => args[0] switch
        {
            "worktree" => new GitCommandResult(0, string.Empty, string.Empty),
            "rev-parse" => new GitCommandResult(0, "base-head\n", string.Empty),
            _ => throw new InvalidOperationException("update-ref must not run after a failed fast-forward check"),
        });

        var result = await Delivery(git, new FakeRemote(), runner, new StubHttpMessageHandler(_ => (HttpStatusCode.OK, "{}")), ConfiguredAccess())
            .DeliverAsync(Context(), Item1, Sha);

        result.Delivered.Should().BeFalse();
        result.Problem.Should().Contain("main");
    }

    /// <summary>
    /// #308 host note point 6: the resolving workspace's OWN current branch is not the base branch (the first
    /// check, already proven above), but a DIFFERENT worktree of the same repository has it checked out, e.g.
    /// the person's own folder, idle between runs. Never moved behind that working tree's back.
    /// </summary>
    [Fact]
    public async Task Commit_to_main_refuses_when_the_base_branch_is_checked_out_in_another_worktree()
    {
        _deliveryMode.Mode = AutopilotDeliveryMode.CommitToMain;
        var git = new FakeGit(Origin, AttemptBranch);
        var runner = new FakeRunner(args => args[0] switch
        {
            "worktree" => new GitCommandResult(
                0,
                "worktree /repos/widget\nbranch refs/heads/main\n\n"
                    + "worktree /repos/widget.fravashi-worktrees/2\nbranch refs/heads/fravashi/worktree/item-2\n",
                string.Empty),
            _ => throw new InvalidOperationException(
                "no other git call should run once another worktree has the base branch out: " + string.Join(' ', args)),
        });

        var result = await Delivery(git, new FakeRemote(), runner, new StubHttpMessageHandler(_ => (HttpStatusCode.OK, "{}")), ConfiguredAccess())
            .DeliverAsync(Context(), Item1, Sha);

        result.Delivered.Should().BeFalse();
        result.Problem.Should().Contain("main").And.Contain("/repos/widget");
    }

    [Fact]
    public async Task The_base_branch_checked_out_refuses_before_any_ref_move()
    {
        _deliveryMode.Mode = AutopilotDeliveryMode.CommitToMain;
        var git = new FakeGit(Origin, BaseBranch);
        var runner = new FakeRunner(_ => throw new InvalidOperationException("no git call should run"));

        var result = await Delivery(git, new FakeRemote(), runner, new StubHttpMessageHandler(_ => (HttpStatusCode.OK, "{}")), ConfiguredAccess())
            .DeliverAsync(Context(), Item1, Sha);

        result.Delivered.Should().BeFalse();
        runner.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// #315, the live check's run over a REAL repository: item 1 is built on its own attempt branch, delivered to
    /// main, and the folder is left on that branch. Item 2's attempt is started through the real
    /// <see cref="SessionGitWork"/> with the run's base (the builder sets it), so it is made from main and records
    /// main, and its commit-to-main delivery moves main, never item 1's branch, which is what v0.9.82 did.
    /// </summary>
    [Fact]
    public async Task The_second_item_of_a_sequential_run_delivers_to_main_and_never_moves_the_first_items_branch()
    {
        _deliveryMode.Mode = AutopilotDeliveryMode.CommitToMain;
        var repo = Path.Combine(Path.GetTempPath(), "fravashi-sequential-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repo);
        string Git(params string[] args) => Tests.Git.SessionGitWorkTests.RunGit(repo, args).Trim();
        try
        {
            Git("init", "-b", "main");
            Git("config", "user.email", "test@example.com");
            Git("config", "user.name", "Test");
            File.WriteAllText(Path.Combine(repo, "seed.txt"), "seed\n");
            Git("add", "-A");
            Git("commit", "-m", "Initial commit");

            var sessionGit = new SessionGit(_sessionStore, Tests.Git.SessionGitWorkTests.WorkspaceGitOver(repo, "w1"));
            var attemptBases = new InMemorySessionAttemptBases();
            var noWork = Substitute.For<ISessionWorkItems>();
            noWork.For(Arg.Any<AgentSessionId>()).Returns(Array.Empty<SessionWorkItem>());
            var attempts = new SessionGitWork(
                sessionGit, Substitute.For<ISessionChangedFiles>(), _branches, noWork, _trackers, TimeProvider.System,
                attemptBases: attemptBases);
            var delivery = new AgentAutopilotDelivery(
                _sessions, sessionGit, _branches, _deliveryMode,
                new FakeHttpClientFactory(new StubHttpMessageHandler(_ => (HttpStatusCode.OK, "{}"))),
                () => ConfiguredAccess(),
                new ForgeRuns(new HttpClient(new StubHttpMessageHandler(_ => (HttpStatusCode.OK, "{}"))), () => ConfiguredAccess()),
                _trackers, _sync,
                new ScenarioRunner(_trackers, new FakeCommandRunner(_ => new AgentCommandResult(true, 0, string.Empty, string.Empty, false))));

            // Item 1: its attempt, its commit, its delivery. The folder stays on its attempt branch afterwards.
            var firstSession = AgentSessionId.New();
            _sessionStore.Save(firstSession, "w1");
            _sessions.Bind(Item1, firstSession);
            attemptBases.Set(firstSession, "main");
            var firstBranch = (await attempts.StartAsync(firstSession)).Branch!;
            File.WriteAllText(Path.Combine(repo, "one.txt"), "item one\n");
            Git("add", "-A");
            Git("commit", "-m", "item one");
            var firstSha = Git("rev-parse", "HEAD");
            (await delivery.DeliverAsync(Context(), Item1, firstSha)).Delivered.Should().BeTrue();
            Git("rev-parse", "main").Should().Be(firstSha);
            Git("rev-parse", "--abbrev-ref", "HEAD").Should().Be(firstBranch);

            // Item 2, started while the folder is still on item 1's branch.
            var item2 = new WorkItemId("item-2");
            var secondSession = AgentSessionId.New();
            _sessionStore.Save(secondSession, "w1");
            _sessions.Bind(item2, secondSession);
            attemptBases.Set(secondSession, "main");
            var secondBranch = (await attempts.StartAsync(secondSession)).Branch!;
            _branches.For(secondSession)!.BaseBranch.Should().Be("main");
            Git("rev-parse", "HEAD").Should().Be(firstSha, "item 2 is made from main, which item 1 already moved");
            File.WriteAllText(Path.Combine(repo, "two.txt"), "item two\n");
            Git("add", "-A");
            Git("commit", "-m", "item two");
            var secondSha = Git("rev-parse", "HEAD");

            var result = await delivery.DeliverAsync(Context(), item2, secondSha);

            result.Delivered.Should().BeTrue(result.Problem);
            Git("rev-parse", "main").Should().Be(secondSha, "main receives item 2");
            Git("rev-parse", firstBranch).Should().Be(firstSha, "item 1's branch is never moved");
            secondBranch.Should().NotBe(firstBranch);
        }
        finally
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(repo, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(repo, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // ---- VerifyAsync, branch + pull request -------------------------------------------------------------

    [Fact]
    public async Task Ci_still_running_is_pending_rather_than_a_failure()
    {
        var git = new FakeGit(Origin, AttemptBranch);
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath.Contains("/actions/runs")
            ? (HttpStatusCode.OK, """{"workflow_runs":[{"id":1,"status":"in_progress","head_sha":"1111111111111111111111111111111111111111"}]}""")
            : (HttpStatusCode.OK, "[]"));

        var result = await Delivery(git, new FakeRemote(), new FakeRunner(), handler, ConfiguredAccess())
            .VerifyAsync(Context(), Item1, Sha);

        result.Passed.Should().BeFalse();
        result.NeedsPerson.Should().BeFalse();
        result.Pending.Should().BeTrue();
    }

    [Fact]
    public async Task A_run_github_reports_as_requested_is_pending_not_red()
    {
        var git = new FakeGit(Origin, AttemptBranch);
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath.Contains("/actions/runs")
            ? (HttpStatusCode.OK, """{"workflow_runs":[{"id":1,"status":"requested","head_sha":"1111111111111111111111111111111111111111","name":"build"}]}""")
            : (HttpStatusCode.OK, "[]"));

        var result = await Delivery(git, new FakeRemote(), new FakeRunner(), handler, ConfiguredAccess())
            .VerifyAsync(Context(), Item1, Sha);

        result.Pending.Should().BeTrue();
    }

    [Theory]
    [InlineData("skipped")]
    [InlineData("neutral")]
    public async Task A_settled_run_with_a_non_failure_conclusion_is_not_read_as_red(string conclusion)
    {
        _tracker.Item = WorkItem.Restore(_tracker.Item.ToState() with { Scenarios = [] }).Value;
        var git = new FakeGit(Origin, AttemptBranch);
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath.Contains("/actions/runs")
            ? (HttpStatusCode.OK, $$"""{"workflow_runs":[{"id":1,"conclusion":"{{conclusion}}","head_sha":"1111111111111111111111111111111111111111","name":"docs"}]}""")
            : (HttpStatusCode.OK, "[]"));

        var result = await Delivery(git, new FakeRemote(), new FakeRunner(), handler, ConfiguredAccess())
            .VerifyAsync(Context(MissingScenarioPolicy.Optional), Item1, Sha);

        result.Pending.Should().BeFalse();
        result.Problem.Should().BeNull($"'{conclusion}' is a documented non-failure conclusion, not a red run");
        result.Passed.Should().BeTrue();
    }

    [Theory]
    [InlineData("partiallySucceeded")]
    [InlineData("action_required")]
    [InlineData("")]
    public async Task A_settled_run_that_is_not_an_explicit_pass_word_is_red_not_green(string conclusion)
    {
        var git = new FakeGit(Origin, AttemptBranch);
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath.Contains("/actions/runs")
            ? (HttpStatusCode.OK, $$"""{"workflow_runs":[{"id":1,"conclusion":"{{conclusion}}","head_sha":"1111111111111111111111111111111111111111","name":"build"}]}""")
            : (HttpStatusCode.OK, "[]"));

        var result = await Delivery(git, new FakeRemote(), new FakeRunner(), handler, ConfiguredAccess())
            .VerifyAsync(Context(), Item1, Sha);

        result.Passed.Should().BeFalse($"'{conclusion}' is not a documented pass word, so it must not default to green");
        result.Pending.Should().BeFalse();
        _tracker.Item.Status.Should().Be(WorkItemStatus.InProgress, "nothing is transitioned for a settled run this build does not recognise as a pass");
        _tracker.Evidence.Should().NotContain(e => e.Kind == EvidenceKind.Build);
    }

    [Fact]
    public async Task No_contained_copy_for_the_scenario_needs_a_person_rather_than_being_read_as_a_failed_scenario()
    {
        var git = new FakeGit(Origin, AttemptBranch);
        var handler = GreenCiHandler();
        var commandRan = false;
        var commands = new FakeCommandRunner(_ => { commandRan = true; return new AgentCommandResult(true, 0, string.Empty, string.Empty, false); });
        var delivery = new AgentAutopilotDelivery(
            _sessions,
            new SessionGit(_sessionStore, new FakeWorkspaceGit(new WorkspaceGitServices("w1", new FakeRunner(), git, ThrowingTree.Instance, new FakeRemote()))),
            _branches,
            _deliveryMode,
            new FakeHttpClientFactory(handler),
            () => ConfiguredAccess(),
            new ForgeRuns(new HttpClient(handler), () => ConfiguredAccess()),
            _trackers,
            new FakeSync(copyPath: null),
            new ScenarioRunner(_trackers, commands));

        var result = await delivery.VerifyAsync(Context(), Item1, Sha);

        result.NeedsPerson.Should().BeTrue();
        result.Passed.Should().BeFalse();
        commandRan.Should().BeFalse("no contained copy means nothing can run, which is not the same as the scenario failing");
    }

    [Fact]
    public async Task Zero_ci_runs_for_the_sha_is_also_pending()
    {
        var git = new FakeGit(Origin, AttemptBranch);
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath.Contains("/actions/runs")
            ? (HttpStatusCode.OK, """{"workflow_runs":[]}""")
            : (HttpStatusCode.OK, "[]"));

        var result = await Delivery(git, new FakeRemote(), new FakeRunner(), handler, ConfiguredAccess())
            .VerifyAsync(Context(), Item1, Sha);

        result.Pending.Should().BeTrue();
    }

    [Fact]
    public async Task Red_ci_is_reported_and_the_scenario_is_never_attempted()
    {
        var git = new FakeGit(Origin, AttemptBranch);
        var scenarioRan = false;
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath.Contains("/actions/runs")
            ? (HttpStatusCode.OK, """{"workflow_runs":[{"id":1,"conclusion":"success","head_sha":"1111111111111111111111111111111111111111","name":"build"},{"id":2,"conclusion":"failure","head_sha":"1111111111111111111111111111111111111111","name":"test"}]}""")
            : (HttpStatusCode.OK, "[]"));
        var commands = new FakeCommandRunner(_ => { scenarioRan = true; return new AgentCommandResult(true, 0, string.Empty, string.Empty, false); });

        var result = await Delivery(git, new FakeRemote(), new FakeRunner(), handler, ConfiguredAccess(), commands)
            .VerifyAsync(Context(), Item1, Sha);

        result.Passed.Should().BeFalse();
        result.Pending.Should().BeFalse();
        result.NeedsPerson.Should().BeFalse();
        result.Problem.Should().Contain("test").And.Contain("failure");
        scenarioRan.Should().BeFalse();
    }

    [Fact]
    public async Task Green_ci_with_no_scenario_and_the_optional_policy_passes_and_moves_to_in_review()
    {
        _tracker.Item = WorkItem.Restore(_tracker.Item.ToState() with { Scenarios = [] }).Value;
        var git = new FakeGit(Origin, AttemptBranch);
        var handler = GreenCiHandler();

        var result = await Delivery(git, new FakeRemote(), new FakeRunner(), handler, ConfiguredAccess())
            .VerifyAsync(Context(MissingScenarioPolicy.Optional), Item1, Sha);

        result.Passed.Should().BeTrue();
        _tracker.Item.Status.Should().Be(WorkItemStatus.InReview);
    }

    [Fact]
    public async Task Green_ci_with_no_scenario_and_the_write_policy_needs_a_person()
    {
        _tracker.Item = WorkItem.Restore(_tracker.Item.ToState() with { Scenarios = [] }).Value;
        var git = new FakeGit(Origin, AttemptBranch);
        var handler = GreenCiHandler();

        var result = await Delivery(git, new FakeRemote(), new FakeRunner(), handler, ConfiguredAccess())
            .VerifyAsync(Context(MissingScenarioPolicy.Write), Item1, Sha);

        result.Passed.Should().BeFalse();
        result.NeedsPerson.Should().BeTrue();
        _tracker.Item.Status.Should().Be(WorkItemStatus.InProgress, "nothing is transitioned when verification does not pass");
    }

    [Fact]
    public async Task Green_ci_with_a_passing_scenario_transitions_the_item_to_in_review()
    {
        var git = new FakeGit(Origin, AttemptBranch);
        var handler = GreenCiHandler();

        var result = await Delivery(git, new FakeRemote(), new FakeRunner(), handler, ConfiguredAccess())
            .VerifyAsync(Context(), Item1, Sha);

        result.Passed.Should().BeTrue();
        result.Problem.Should().BeNull();
        _tracker.Item.Status.Should().Be(WorkItemStatus.InReview);
        _tracker.Evidence.Should().Contain(e => e.Kind == EvidenceKind.Build && e.Result == EvidenceResult.Pass);
    }

    [Fact]
    public async Task A_scenario_already_at_in_review_still_reports_passed()
    {
        _tracker.Item = WorkItem.Restore(_tracker.Item.ToState() with { Status = WorkItemStatus.InReview }).Value;
        var git = new FakeGit(Origin, AttemptBranch);
        var handler = GreenCiHandler();

        var result = await Delivery(git, new FakeRemote(), new FakeRunner(), handler, ConfiguredAccess())
            .VerifyAsync(Context(), Item1, Sha);

        result.Passed.Should().BeTrue("transitioning to the status the item is already at is a documented no-op success");
    }

    [Fact]
    public async Task A_failing_scenario_is_a_problem_and_nothing_is_transitioned()
    {
        var git = new FakeGit(Origin, AttemptBranch);
        var handler = GreenCiHandler();
        var commands = new FakeCommandRunner(_ => new AgentCommandResult(true, 1, string.Empty, "it broke", false));

        var result = await Delivery(git, new FakeRemote(), new FakeRunner(), handler, ConfiguredAccess(), commands)
            .VerifyAsync(Context(), Item1, Sha);

        result.Passed.Should().BeFalse();
        result.Pending.Should().BeFalse();
        result.NeedsPerson.Should().BeFalse();
        _tracker.Item.Status.Should().Be(WorkItemStatus.InProgress);
    }

    [Fact]
    public async Task A_scenario_already_running_elsewhere_is_pending()
    {
        var git = new FakeGit(Origin, AttemptBranch);
        var handler = GreenCiHandler();
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var commands = new FakeCommandRunner(async _ => { started.TrySetResult(); await gate.Task; return new AgentCommandResult(true, 0, string.Empty, string.Empty, false); });
        var scenarioRunner = new ScenarioRunner(_trackers, commands);
        var delivery = new AgentAutopilotDelivery(
            _sessions,
            new SessionGit(_sessionStore, new FakeWorkspaceGit(new WorkspaceGitServices("w1", new FakeRunner(), git, ThrowingTree.Instance, new FakeRemote()))),
            _branches,
            _deliveryMode,
            new FakeHttpClientFactory(handler),
            () => ConfiguredAccess(),
            new ForgeRuns(new HttpClient(handler), () => ConfiguredAccess()),
            _trackers,
            _sync,
            scenarioRunner);

        var first = delivery.VerifyAsync(Context(), Item1, Sha);
        await started.Task; // the first attempt is registered as in flight the moment its own step starts

        var second = await delivery.VerifyAsync(Context(), Item1, Sha);

        second.Pending.Should().BeTrue();
        gate.SetResult();
        await first;
        _tracker.Evidence.Count(e => e.Kind == EvidenceKind.Build).Should().Be(
            1, "a re-check of the same commit replays the CI-green evidence rather than writing it again");
    }

    [Fact]
    public async Task A_stale_in_review_operation_id_for_a_different_commit_does_not_stop_this_commit_moving_the_item()
    {
        // Simulates an EARLIER run of this item that reached In Review for a different commit (the item was
        // since reopened to In Progress): if the transition's operation id named only the item, this would
        // replay that old write and report Passed while leaving the item stuck In Progress.
        _tracker.MarkOperationIdUsed($"autopilot-verify-done-{Item1.Value}-{new string('2', 40)}");
        var git = new FakeGit(Origin, AttemptBranch);
        var handler = GreenCiHandler();

        var result = await Delivery(git, new FakeRemote(), new FakeRunner(), handler, ConfiguredAccess())
            .VerifyAsync(Context(), Item1, Sha);

        result.Passed.Should().BeTrue();
        _tracker.Item.Status.Should().Be(WorkItemStatus.InReview, "this commit's own operation id must not collide with an earlier commit's");
    }

    // ---- VerifyAsync, commit to main -------------------------------------------------------------------

    [Fact]
    public async Task Commit_to_main_always_needs_a_person_naming_the_base_branch_with_nothing_read()
    {
        _deliveryMode.Mode = AutopilotDeliveryMode.CommitToMain;
        var git = new FakeGit(Origin, AttemptBranch);
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("no CI or scenario call is expected under commit to main"));

        var result = await Delivery(git, new FakeRemote(), new FakeRunner(), handler, ConfiguredAccess())
            .VerifyAsync(Context(), Item1, Sha);

        result.Passed.Should().BeFalse();
        result.NeedsPerson.Should().BeTrue();
        result.Problem.Should().Contain(BaseBranch);
        _tracker.Item.Status.Should().Be(WorkItemStatus.InProgress);
    }

    private static StubHttpMessageHandler GreenCiHandler() => new(request => request.RequestUri!.AbsolutePath.Contains("/actions/runs")
        ? (HttpStatusCode.OK, """{"workflow_runs":[{"id":1,"conclusion":"success","head_sha":"1111111111111111111111111111111111111111","name":"build"}]}""")
        : (HttpStatusCode.OK, "[]"));

    // ---- fakes -----------------------------------------------------------------------------------------

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private sealed class FakeWorkspaceGit(WorkspaceGitServices services) : IWorkspaceGit
    {
        public WorkspaceGitServices? For(string workspaceId) => workspaceId == services.WorkspaceId ? services : null;
    }

    private sealed class FakeSessionStore : ISessionStore
    {
        private readonly Dictionary<AgentSessionId, SessionRecord> _records = [];

        public void Save(AgentSessionId id, string workspaceId) =>
            _records[id] = new SessionRecord(id, workspaceId, AgentTool.Claude, "title", null, AgentSessionState.Running, DateTimeOffset.UtcNow, null);

        public void Save(SessionRecord record) => _records[record.Id] = record;

        public SessionRecord? Find(AgentSessionId id) => _records.TryGetValue(id, out var r) ? r : null;

        public IReadOnlyList<SessionRecord> ForWorkspace(string workspaceId, int max = 50) => [];

        public IReadOnlyList<SessionRecord> Recent(int max = 10) => [];

        public void Append(AgentSessionId id, IEnumerable<AgentEvent> events)
        {
        }

        public IReadOnlyList<AgentEvent> Transcript(AgentSessionId id) => [];

        public void Delete(AgentSessionId id) => _records.Remove(id);

        public SessionUsageTotals ReadUsageTotals(DateTimeOffset dayStartUtc, DateTimeOffset monthStartUtc) =>
            new(new UsageTotal(0m, 0, 0), new UsageTotal(0m, 0, 0), new UsageTotal(0m, 0, 0));

        public IReadOnlyList<SessionDay> ReadSessionDays(DateOnly fromDayUtc, DateOnly toDayUtc) => [];

        public IReadOnlyList<DayCount> ReadTurnDays(DateOnly fromDayUtc, DateOnly toDayUtc) => [];

        public DurationStats ReadSessionDurations(DateTimeOffset fromUtc) => new(0, TimeSpan.Zero, TimeSpan.Zero);
    }

    private sealed class FakeBranches : ISessionBranches
    {
        private readonly Dictionary<AgentSessionId, SessionBranch> _branches = [];

        public SessionBranch? For(AgentSessionId session) => _branches.TryGetValue(session, out var b) ? b : null;

        public void Record(AgentSessionId session, SessionBranch branch) => _branches[session] = branch;

        public void Forget(AgentSessionId session) => _branches.Remove(session);
    }

    private sealed class FakeProjectDelivery(AutopilotDeliveryMode mode) : Application.Projects.IProjectDelivery
    {
        public AutopilotDeliveryMode Mode { get; set; } = mode;

        public AutopilotDeliveryMode For(ProjectId project) => Mode;

        public void Set(ProjectId project, AutopilotDeliveryMode mode) => Mode = mode;
    }

    private sealed class FakeSync(string? copyPath = "/workspaces/w1") : IWorkspaceAgentSync
    {
        public void EnsureStarted(string workspaceId)
        {
        }

        public string? CopyPathFor(string workspaceId) => copyPath;

        public object? StatusFor(string workspaceId) => null;
    }

    private sealed class FakeCommandRunner(Func<AgentCommand, Task<AgentCommandResult>> responder) : IAgentCommandRunner
    {
        public FakeCommandRunner(Func<AgentCommand, AgentCommandResult> responder)
            : this(command => Task.FromResult(responder(command)))
        {
        }

        public Task<AgentCommandResult> RunAsync(AgentCommand command, CancellationToken cancellationToken = default) =>
            responder(command);
    }

    private sealed class FakeRunner(Func<IReadOnlyList<string>, GitCommandResult>? responder = null) : IGitCommandRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public bool IsAvailable() => true;

        public Task<GitCommandResult> RunAsync(
            IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
        {
            Calls.Add(args);
            return Task.FromResult(responder?.Invoke(args) ?? new GitCommandResult(0, string.Empty, string.Empty));
        }
    }

    private sealed class FakeGit(string? remoteUrl, string currentBranch) : IGitService
    {
        public int CheckoutCalls { get; private set; }

        public string? MergeBaseAnswer { get; set; }

        public bool IsAvailable => true;

        public Task<string> GetCurrentBranchAsync(CancellationToken cancellationToken = default) => Task.FromResult(currentBranch);

        public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task CheckoutAsync(string branch, CancellationToken cancellationToken = default)
        {
            CheckoutCalls++;
            throw new InvalidOperationException("the person's own checkout must never be switched");
        }

        public Task CreateBranchAsync(string name, string baseBranch, bool checkout, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RepositoryNode>> ListTreeAsync(string reference, string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RepositoryFile?> ReadFileAsync(string reference, string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<GitCommit>> GetCommitsAsync(string reference, int max = 50, string? path = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GitCommitDetail?> GetCommitAsync(string sha, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string> GetDiffAsync(string sha, string? path = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<GitFileChange>> GetDiffFilesAsync(string baseRef, string headRef, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string?> GetMergeBaseAsync(string first, string second, CancellationToken cancellationToken = default) =>
            Task.FromResult(MergeBaseAnswer);

        public Task<IReadOnlyList<GitCommit>> GetFileHistoryAsync(string path, string reference, int max = 50, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string?> GetRemoteUrlAsync(CancellationToken cancellationToken = default) => Task.FromResult(remoteUrl);

        public Task<IReadOnlyList<GitRemoteBranch>> GetRemoteBranchesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CheckoutTrackingAsync(GitRemoteBranch branch, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<GitGraphCommit>> GetGraphAsync(int max = 150, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GitLineBlame?> GetLineBlameAsync(string path, int line, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeRemote : IGitRemoteService
    {
        public Func<CancellationToken, Task<GitSyncOutcome>> Push { get; set; } = _ => Task.FromResult(new GitSyncOutcome(true));

        public Func<GitPendingPush, GitSyncOutcome> Compare { get; set; } = _ => new GitSyncOutcome(true);

        public int PushCalls { get; private set; }

        public GitPendingPush? LastCompared { get; private set; }

        public Task<IReadOnlyList<GitRemote>> GetRemotesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GitSyncOutcome> FetchAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GitSyncOutcome> PullAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GitSyncOutcome> PushAsync(CancellationToken cancellationToken = default)
        {
            PushCalls++;
            return Push(cancellationToken);
        }

        public Task<GitSyncOutcome> PushTagAsync(string tag, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GitSyncOutcome> ComparePushAsync(GitPendingPush pending, CancellationToken cancellationToken = default)
        {
            LastCompared = pending;
            return Task.FromResult(Compare(pending));
        }
    }

    private sealed class ThrowingTree : IGitWorkingTreeService
    {
        public static readonly ThrowingTree Instance = new();

        public Task<GitWorkingStatus> GetStatusAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task StageAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task UnstageAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GitCommitOutcome> CommitAsync(string message, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task MarkExecutableAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(string Original, string Modified)> GetWorkingDiffAsync(string path, bool staged, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GitCommitOutcome> StashAsync(string message, IReadOnlyList<string>? paths = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<GitStash>> GetStashesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GitCommitOutcome> StashPopAsync(int index, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GitCommitOutcome> StashApplyAsync(int index, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeHosts(GitHostRecord? record) : IGitHostStore
    {
        public IReadOnlyList<GitHostRecord> GetAll() => record is null ? [] : [record];

        public GitHostRecord? Get(string host) => record is not null && string.Equals(record.Host, host, StringComparison.OrdinalIgnoreCase) ? record : null;

        public void Upsert(GitHostRecord value)
        {
        }

        public void Remove(string host)
        {
        }
    }

    private sealed class FakeSecrets(params (string Reference, string Plaintext)[] secrets) : ISecretStore
    {
        public void Set(string reference, string plaintext)
        {
        }

        public string? Get(string reference) => secrets.FirstOrDefault(s => s.Reference == reference).Plaintext;

        public void Remove(string reference)
        {
        }
    }

    private sealed class FakeConnections : IProviderConnectionStore
    {
        public IReadOnlyList<ProviderConnection> GetAll() => [];

        public IReadOnlyList<ProviderConnection> GetByKind(ProviderKind kind) => [];

        public ProviderConnection? Get(string id) => null;

        public void Upsert(ProviderConnection connection)
        {
        }

        public void Remove(string id)
        {
        }
    }

    private sealed class FakeSettings : IConfigurationStore
    {
        public string? Get(string key) => null;

        public IReadOnlyDictionary<string, string?> GetAll() => new Dictionary<string, string?>();

        public void Set(string key, string? value)
        {
        }

        public void SetMany(IReadOnlyDictionary<string, string?> values)
        {
        }

        public void Remove(string key)
        {
        }
    }

    private sealed class FakeTracker : ITracker
    {
        private readonly List<string> _seenOperationIds = [];
        private readonly Dictionary<string, EvidenceRecord> _evidenceByOperationId = [];

        public FakeTracker(ProjectId project, WorkItem item)
        {
            Project = project;
            Item = item;
        }

        /// <summary>Simulates an operation id a PRIOR call already recorded, for a replay test.</summary>
        public void MarkOperationIdUsed(string operationId) => _seenOperationIds.Add(operationId);

        public WorkItem Item { get; set; }

        public List<EvidenceRecord> Evidence { get; } = [];

        public ProjectId Project { get; }

        public string ProviderName => "local";

        public TrackerCapabilities Capabilities => TrackerCapabilities.ReadOnly;

        public Task<TrackerResult<WorkItem>> CreateAsync(NewWorkItem item, MutationContext context, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default) =>
            Task.FromResult<WorkItem?>(Item.Id == id ? Item : null);

        public Task<WorkItem?> GetByNumberAsync(int number, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<WorkItemPage> QueryAsync(WorkItemQuery query, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<WorkItemEvent>> HistoryAsync(WorkItemId id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<WorkItemLink>> LinksAsync(WorkItemId id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TrackerResult<WorkItem>> UpdateAsync(WorkItemId id, WorkItemPatch patch, long expectedVersion, MutationContext context, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TrackerResult<WorkItem>> TransitionAsync(
            WorkItemId id, WorkItemStatus to, WorkItemResolution? resolution, WorkItemId? after, WorkItemId? before,
            long expectedVersion, MutationContext context, CancellationToken ct = default)
        {
            if (context.OperationId is { Length: > 0 } operationId)
            {
                if (_seenOperationIds.Contains(operationId))
                {
                    return Task.FromResult(TrackerResult<WorkItem>.Ok(Item, replayed: true));
                }

                _seenOperationIds.Add(operationId);
            }

            if (Item.Status == to)
            {
                return Task.FromResult(TrackerResult<WorkItem>.Ok(Item));
            }

            if (expectedVersion != Item.Version)
            {
                return Task.FromResult(TrackerResult<WorkItem>.Conflict(Item.Version));
            }

            Item = WorkItem.Restore(Item.ToState() with { Status = to, Version = Item.Version + 1 }).Value;
            return Task.FromResult(TrackerResult<WorkItem>.Ok(Item));
        }

        public Task<TrackerResult<WorkItem>> MoveAsync(WorkItemId id, WorkItemId? after, WorkItemId? before, long expectedVersion, MutationContext context, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TrackerResult<WorkItemLink>> LinkAsync(WorkItemId from, WorkItemLinkType type, WorkItemId to, MutationContext context, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TrackerResult<bool>> UnlinkAsync(WorkItemId from, WorkItemLinkType type, WorkItemId to, MutationContext context, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TrackerResult<EvidenceRecord>> RecordEvidenceAsync(WorkItemId id, NewEvidence evidence, MutationContext context, CancellationToken ct = default)
        {
            if (context.OperationId is { Length: > 0 } operationId && _evidenceByOperationId.TryGetValue(operationId, out var already))
            {
                // A replay returns the evidence that operation id already wrote, never a second copy.
                return Task.FromResult(TrackerResult<EvidenceRecord>.Ok(already, replayed: true));
            }

            var record = new EvidenceRecord(
                Guid.NewGuid().ToString(), evidence.Kind, evidence.Result, evidence.Summary, evidence.Gate,
                evidence.ArtefactRef, evidence.Criterion, evidence.Scenario, null, DateTimeOffset.UtcNow, context.Actor);
            Evidence.Add(record);
            if (context.OperationId is { Length: > 0 } newOperationId)
            {
                _evidenceByOperationId[newOperationId] = record;
            }

            Item = WorkItem.Restore(Item.ToState() with { Version = Item.Version + 1 }).Value;
            return Task.FromResult(TrackerResult<EvidenceRecord>.Ok(record));
        }

        public Task<IReadOnlyList<EvidenceRecord>> EvidenceAsync(WorkItemId id, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EvidenceRecord>>(Evidence);

        public Task<StartabilityReport> WhatCanStartNowAsync(StartableQuery query, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeTrackerResolver(ITracker tracker) : ITrackerResolver
    {
        public ITracker For(ProjectId project) => tracker;
    }
}
