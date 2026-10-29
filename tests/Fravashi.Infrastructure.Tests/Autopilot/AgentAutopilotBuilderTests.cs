using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Threading.Channels;
using AwesomeAssertions;
using Fravashi.Application.Autopilot;
using Fravashi.Application.Git;
using Fravashi.Application.Sessions;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Git;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;
using Fravashi.Infrastructure.AgentSync;
using Fravashi.Infrastructure.Autopilot;
using Fravashi.Infrastructure.Persistence;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Sessions.Tools;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Autopilot;

/// <summary>
/// The real <see cref="IAutopilotBuilder"/> (#309, ADR 0031 decision 11): an ORDINARY studio session, driven
/// through the real <see cref="LiveSessionRegistry"/> and <see cref="AgentApprovalBroker"/>, with a scripted
/// fake for the agent CLI's own protocol (the <c>DesignFromDrawingStarterTests</c> pattern).
/// </summary>
public sealed class AgentAutopilotBuilderTests : IDisposable
{
    private static readonly ProjectId Project = new("proj-1");
    private static readonly WorkItemId Item1 = new("item-1");
    private static readonly WorkItemId Item2 = new("item-2");
    private const string WorkspaceId = "w1";
    private const string CopyPath = "/workspaces/w1";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "fravashi-autobuilder-" + Guid.NewGuid().ToString("N"));

    private readonly SqliteSessionStore _store;
    private readonly AgentApprovalBroker _broker = new(TimeSpan.FromSeconds(5));
    private readonly ScriptedRunner _runner = new();
    private readonly LiveSessionRegistry _registry;
    private readonly FakeWork _work = new();
    private readonly FakePlans _plans = new();
    private readonly AutopilotBuilderSessions _sessions;
    private readonly AgentAutopilotBuilder _builder;

    public AgentAutopilotBuilderTests()
    {
        Directory.CreateDirectory(_directory);
        var factory = new SqliteConnectionFactory(
            new SqliteStoreOptions(Path.Combine(_directory, "fravashi.db"), Pooling: false));
        SqliteSchema.EnsureCreated(factory);
        _store = new SqliteSessionStore(factory);
        _runner.Broker = _broker;
        _registry = new LiveSessionRegistry(_runner, _store, _broker, new FakeSync(CopyPath));
        _sessions = new AutopilotBuilderSessions(_broker);
        _builder = new AgentAutopilotBuilder(
            _registry,
            _broker,
            _work,
            _plans,
            new FakeTrackerResolver(ItemWithPlan(), SecondItem()),
            new FakeSync(CopyPath),
            _sessions,
            workspaceIdFor: _ => WorkspaceId,
            delay: (_, ct) => Task.Delay(1, ct));
    }

    public void Dispose()
    {
        _sessions.Dispose();
        _registry.Dispose();
        _broker.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static WorkItem ItemWithPlan() => WorkItem.Restore(new WorkItemState
    {
        Id = Item1,
        Project = Project,
        Number = 1,
        Level = WorkItemLevel.Task,
        Title = "A title",
        Status = WorkItemStatus.Ready,
        Refs = [new ArtifactRef("ref-1", ArtifactRefRole.Plan, ArtifactRefKind.Wiki, "42")],
    }).Value;

    private static WorkItem SecondItem() => WorkItem.Restore(new WorkItemState
    {
        Id = Item2,
        Project = Project,
        Number = 2,
        Level = WorkItemLevel.Task,
        Title = "A second title",
        Status = WorkItemStatus.Ready,
    }).Value;

    private static readonly Guid Run1 = Guid.NewGuid();

    private static AutopilotBuilderContext Context(
        Guid? runId = null, int? concurrencyCapOverride = null, string? baseBranch = null, int effectiveConcurrencyCap = 1,
        string? repositoryWorkspaceId = null) =>
        new(
            Project, new Fravashi.Domain.Autopilot.AutopilotRole(AgentTool.Claude, "claude-opus-5-5"), runId ?? Run1,
            concurrencyCapOverride, baseBranch, effectiveConcurrencyCap, repositoryWorkspaceId ?? WorkspaceId);

    [Fact]
    public async Task A_fresh_session_is_started_titled_marked_autopilot_and_linked_to_the_item_and_its_plan()
    {
        var build = _builder.BuildAsync(Context(), Item1, "build the item");
        var sessionId = await WaitForBoundSessionAsync();
        Complete(sessionId);

        var result = await build;

        result.NeedsPerson.Should().BeFalse();
        _runner.Requests.Should().ContainSingle();

        // Requested 2026-10-22: an item that suggests nothing starts the building agent exactly as before.
        _runner.Requests[0].Model.Should().Be("claude-opus-5-5", "the dispatch's builder model");
        _runner.Requests[0].Effort.Should().BeNull("no effort flag at all");
        var session = _registry.Find(sessionId)!;
        session.Title.Should().Be("Autopilot: #1 A title");
        _broker.IsAutopilot(sessionId).Should().BeTrue();
        _work.Links.Should().ContainSingle(l => l.Session == sessionId && l.Item == Item1 && l.Project == Project);
        _plans.Links.Should().ContainSingle(l => l.Session == sessionId && l.WikiItemId == "42");
    }

    [Fact]
    public async Task An_items_suggested_model_and_effort_start_the_building_agent()
    {
        // Requested 2026-10-22 (model and effort per work item): the item's suggestion wins over the dispatch's
        // builder model, and its effort becomes the session's.
        var suggesting = WorkItem.Restore(ItemWithPlan().ToState() with
        {
            SuggestedModel = AgentModelAlias.Haiku,
            SuggestedEffort = AgentEffort.Low,
        }).Value;
        var builder = new AgentAutopilotBuilder(
            _registry,
            _broker,
            _work,
            _plans,
            new FakeTrackerResolver(suggesting),
            new FakeSync(CopyPath),
            _sessions,
            workspaceIdFor: _ => WorkspaceId,
            delay: (_, ct) => Task.Delay(1, ct));

        var build = builder.BuildAsync(Context(), Item1, "build the item");
        var sessionId = await WaitForBoundSessionAsync();
        Complete(sessionId);
        await build;

        _runner.Requests.Should().ContainSingle();
        _runner.Requests[0].Model.Should().Be("haiku");
        _runner.Requests[0].Effort.Should().Be(AgentEffort.Low);
        _registry.Find(sessionId)!.Effort.Should().Be(AgentEffort.Low);
    }

    private AgentAutopilotBuilder BuilderWithDefaults(WorkItem item, AgentDefault defaults) =>
        new(
            _registry,
            _broker,
            _work,
            _plans,
            new FakeTrackerResolver(item),
            new FakeSync(CopyPath),
            _sessions,
            workspaceIdFor: _ => WorkspaceId,
            delay: (_, ct) => Task.Delay(1, ct),
            builderDefaults: () => defaults);

    [Fact]
    public async Task The_autopilot_builders_default_effort_starts_an_item_that_suggests_nothing()
    {
        // Work item #357: Settings > Agents > Default models and effort, autopilot builder. The dispatch's own model is
        // the person's pick for the run (the dispatch form pre-fills it from the default), so it stays; the effort,
        // which a dispatch does not carry, comes from the default.
        var builder = BuilderWithDefaults(ItemWithPlan(), new AgentDefault("sonnet", AgentEffort.Medium));

        var build = builder.BuildAsync(Context(), Item1, "build the item");
        var sessionId = await WaitForBoundSessionAsync();
        Complete(sessionId);
        await build;

        _runner.Requests.Should().ContainSingle();
        _runner.Requests[0].Model.Should().Be("claude-opus-5-5", "the dispatch's builder model is the run's own pick");
        _runner.Requests[0].Effort.Should().Be(AgentEffort.Medium);
    }

    [Fact]
    public async Task A_dispatch_that_names_no_model_starts_on_the_autopilot_builders_default_model()
    {
        var builder = BuilderWithDefaults(ItemWithPlan(), new AgentDefault("sonnet", null));
        var context = Context() with { Role = new Fravashi.Domain.Autopilot.AutopilotRole(AgentTool.Claude, " ") };

        var build = builder.BuildAsync(context, Item1, "build the item");
        var sessionId = await WaitForBoundSessionAsync();
        Complete(sessionId);
        await build;

        _runner.Requests[0].Model.Should().Be("sonnet");
        _runner.Requests[0].Effort.Should().BeNull();
    }

    [Fact]
    public async Task An_items_suggestion_wins_over_the_autopilot_builders_default()
    {
        var suggesting = WorkItem.Restore(ItemWithPlan().ToState() with
        {
            SuggestedModel = AgentModelAlias.Haiku,
            SuggestedEffort = AgentEffort.Low,
        }).Value;
        var builder = BuilderWithDefaults(suggesting, new AgentDefault("sonnet", AgentEffort.Max));

        var build = builder.BuildAsync(Context(), Item1, "build the item");
        var sessionId = await WaitForBoundSessionAsync();
        Complete(sessionId);
        await build;

        _runner.Requests[0].Model.Should().Be("haiku");
        _runner.Requests[0].Effort.Should().Be(AgentEffort.Low);
    }

    [Fact]
    public void The_precedence_is_item_then_dispatch_or_default_then_nothing()
    {
        var plain = ItemWithPlan();
        var role = new Fravashi.Domain.Autopilot.AutopilotRole(AgentTool.Claude, "claude-opus-5-5");
        var blank = new Fravashi.Domain.Autopilot.AutopilotRole(AgentTool.Claude, string.Empty);

        AgentAutopilotBuilder.BuilderModel(plain, role).Should().Be("claude-opus-5-5");
        AgentAutopilotBuilder.BuilderModel(plain, blank, new AgentDefault("opus", null)).Should().Be("opus");
        AgentAutopilotBuilder.BuilderModel(plain, blank).Should().BeNull("nothing set passes no model flag");
        AgentAutopilotBuilder.BuilderEffort(plain).Should().BeNull();
        AgentAutopilotBuilder.BuilderEffort(plain, new AgentDefault(null, AgentEffort.High)).Should().Be(AgentEffort.High);
    }

    [Fact]
    public async Task A_second_call_for_the_same_item_sends_a_turn_on_the_same_session_rather_than_starting_a_new_one()
    {
        var first = _builder.BuildAsync(Context(), Item1, "first prompt");
        var sessionId = await WaitForBoundSessionAsync();
        Complete(sessionId);
        await first;

        var second = _builder.BuildAsync(Context(), Item1, "second prompt");
        await WaitUntilAsync(() => _runner.SentTurns.Any(t => t.Id == sessionId));
        Complete(sessionId);
        await second;

        _runner.Requests.Should().ContainSingle("only the first call ever starts a process");
        _runner.SentTurns.Should().ContainSingle(t => t.Id == sessionId && t.Text == "second prompt");
    }

    [Fact]
    public async Task A_run_advances_past_its_first_item_because_the_first_items_leftover_session_is_stopped_to_make_room()
    {
        // The first item's session stays alive after its own turn ends (an ordinary session, not killed by
        // closing one turn), so without ending it, the run's SECOND item could never start at all: the
        // workspace holds exactly one live session, and LiveSessionRegistry.Start refuses a second one.
        var firstBuild = _builder.BuildAsync(Context(), Item1, "first item's prompt");
        var firstSessionId = await WaitForBoundSessionAsync();
        Complete(firstSessionId);
        await firstBuild;

        _registry.Find(firstSessionId)!.IsRunning.Should().BeTrue("an ordinary session outlives the turn that just finished");

        var secondBuild = _builder.BuildAsync(Context(), Item2, "second item's prompt");
        await WaitUntilAsync(() => _sessions.SessionFor(Item2) is not null);
        var secondSessionId = _sessions.SessionFor(Item2)!.Value;
        Complete(secondSessionId);
        await secondBuild;

        secondSessionId.Should().NotBe(firstSessionId);
        _registry.Find(firstSessionId)!.IsRunning.Should().BeFalse("ending the first item's session is what made room for the second");
        _registry.RunningFor(WorkspaceId).Should().Be(_registry.Find(secondSessionId));
    }

    [Fact]
    public async Task A_successful_attempt_commit_is_read_as_the_commit_and_its_sha()
    {
        var build = _builder.BuildAsync(Context(), Item1, "build the item");
        var sessionId = await WaitForBoundSessionAsync();

        var channel = _runner.ChannelFor(sessionId);
        var now = DateTimeOffset.UtcNow;
        await channel.Writer.WriteAsync(new ToolCallCompleted(
            now, "tu1", StudioToolServer.CliToolName(AttemptCommitTool.ToolName), true,
            """{"changed":"committed","item":{"sha":"abc123","files":1,"note":null}}"""));
        await channel.Writer.WriteAsync(new TurnCompleted(now, "done", IsError: false, TerminalReason: null));

        var result = await build;

        result.CommitMade.Should().BeTrue();
        result.BranchHeadSha.Should().Be("abc123");
        result.NeedsPerson.Should().BeFalse();
    }

    [Fact]
    public async Task A_turn_with_neither_a_commit_nor_a_pending_call_reports_no_commit()
    {
        var build = _builder.BuildAsync(Context(), Item1, "build the item");
        var sessionId = await WaitForBoundSessionAsync();
        Complete(sessionId);

        var result = await build;

        result.CommitMade.Should().BeFalse();
        result.NeedsPerson.Should().BeFalse();
        result.Problem.Should().Contain("ended without committing anything");
    }

    [Fact]
    public async Task A_commit_the_reviewer_fails_is_reported_as_a_failed_review_not_a_plain_no_commit()
    {
        // The broker refuses a FAILED commit before attempt_commit's own tool ever runs (backlog 243), so
        // this needs a dedicated broker with a reviewer wired, unlike every other test here.
        var directory = Path.Combine(Path.GetTempPath(), "fravashi-autobuilder-failedreview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var factory = new SqliteConnectionFactory(new SqliteStoreOptions(Path.Combine(directory, "fravashi.db"), Pooling: false));
            SqliteSchema.EnsureCreated(factory);
            var store = new SqliteSessionStore(factory);
            var role = new CommitReviewerRole(true, AgentTool.Claude, "claude-sonnet-5");
            var failed = new CommitReview(
                CommitReviewOutcome.Failed, role,
                Findings: [new CommitReviewFinding("a.cs", 1, "a rule", "fix it", "a problem")]);
            using var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(5), reviewer: () => new FixedReviewer(failed));
            var runner = new ScriptedRunner { Broker = broker };
            using var registry = new LiveSessionRegistry(runner, store, broker, new FakeSync(CopyPath));
            using var sessions = new AutopilotBuilderSessions(broker);
            var builder = new AgentAutopilotBuilder(
                registry, broker, new FakeWork(), new FakePlans(), new FakeTrackerResolver(ItemWithPlan()),
                new FakeSync(CopyPath), sessions, _ => WorkspaceId, delay: (_, ct) => Task.Delay(1, ct));

            var build = builder.BuildAsync(Context(), Item1, "build the item");
            await WaitUntilAsync(() => sessions.SessionFor(Item1) is not null);
            var sessionId = sessions.SessionFor(Item1)!.Value;
            await WaitUntilAsync(() => runner.Requests.Count > 0); // RegisterSession runs before this, same call

            // The approval broker is given the call exactly as the CLI's hook would post it: the MCP-prefixed
            // name, never the tool's own bare one (measured against the real CLI, ClaudeCliContractTests).
            var decision = await broker.RequestAsync(
                sessionId, StudioToolServer.CliToolName(AttemptCommitTool.ToolName), "tu1",
                AgentToolInput.Create(new Dictionary<string, string> { ["summary"] = "x" }, ["summary"]));
            decision.Approved.Should().BeFalse("the reviewer failed this commit");

            runner.ChannelFor(sessionId).Writer.TryWrite(new TurnCompleted(DateTimeOffset.UtcNow, "done", IsError: false, TerminalReason: null));

            var result = await build;

            result.ReviewFailed.Should().BeTrue();
            result.CommitMade.Should().BeFalse();
            result.NeedsPerson.Should().BeFalse();
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// #321: the proof run's item 2 had three commit reviews FAIL in a row, on one building session, and the
    /// engine's own per-item counter (<see cref="AutopilotRunItem.FailedReviews"/>, read by
    /// <see cref="AutopilotDiscipline.CheckItemLimits"/> against ADR 0031 decision 6's ceiling) stayed at zero.
    /// Each failure must be reported as <see cref="AutopilotBuildResult.ReviewFailed"/> on its OWN
    /// <see cref="AgentAutopilotBuilder.BuildAsync"/> call, the same session reused every retry (the engine
    /// keeps the item in Building and tries again), never only the first.
    /// </summary>
    [Fact]
    public async Task Three_consecutive_failed_reviews_on_the_same_item_and_session_are_all_reported()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fravashi-autobuilder-threefailedreviews-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var factory = new SqliteConnectionFactory(new SqliteStoreOptions(Path.Combine(directory, "fravashi.db"), Pooling: false));
            SqliteSchema.EnsureCreated(factory);
            var store = new SqliteSessionStore(factory);
            var role = new CommitReviewerRole(true, AgentTool.Claude, "claude-sonnet-5");
            var failed = new CommitReview(
                CommitReviewOutcome.Failed, role,
                Findings: [new CommitReviewFinding("a.cs", 1, "a rule", "fix it", "a problem")]);
            using var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(5), reviewer: () => new FixedReviewer(failed));
            var runner = new ScriptedRunner { Broker = broker };
            using var registry = new LiveSessionRegistry(runner, store, broker, new FakeSync(CopyPath));
            using var sessions = new AutopilotBuilderSessions(broker);
            var builder = new AgentAutopilotBuilder(
                registry, broker, new FakeWork(), new FakePlans(), new FakeTrackerResolver(ItemWithPlan()),
                new FakeSync(CopyPath), sessions, _ => WorkspaceId, delay: (_, ct) => Task.Delay(1, ct));

            AgentSessionId? sessionId = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var build = builder.BuildAsync(Context(), Item1, "build the item, attempt " + attempt.ToString(CultureInfo.InvariantCulture));

                if (sessionId is null)
                {
                    await WaitUntilAsync(() => sessions.SessionFor(Item1) is not null);
                    sessionId = sessions.SessionFor(Item1)!.Value;
                    await WaitUntilAsync(() => runner.Requests.Count > 0);
                }
                else
                {
                    // A retry on the SAME item reuses the SAME session (the engine keeps it in Building and
                    // sends another turn), never a fresh one: the proof run's three failures were all on one
                    // building session.
                    sessions.SessionFor(Item1).Should().Be(sessionId, "a retry must reuse the item's own session");
                    await WaitUntilAsync(() => runner.SentTurns.Count(t => t.Id == sessionId!.Value) == attempt - 1);
                }

                var decision = await broker.RequestAsync(
                    sessionId!.Value, StudioToolServer.CliToolName(AttemptCommitTool.ToolName), "tu" + attempt.ToString(CultureInfo.InvariantCulture),
                    AgentToolInput.Create(new Dictionary<string, string> { ["summary"] = "x" }, ["summary"]));
                decision.Approved.Should().BeFalse("the reviewer failed this commit");

                runner.ChannelFor(sessionId.Value).Writer.TryWrite(
                    new TurnCompleted(DateTimeOffset.UtcNow, "done", IsError: false, TerminalReason: null));

                var result = await build;

                result.ReviewFailed.Should().BeTrue($"attempt {attempt}'s own failed review must be reported, not silently dropped");
                result.CommitMade.Should().BeFalse();
            }
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// The same scenario as <see cref="Three_consecutive_failed_reviews_on_the_same_item_and_session_are_all_reported"/>,
    /// but on a parallel run's own worktree (#308, effective cap above 1), which is the shape the proof run
    /// actually dispatched with (#321's own evidence names "Settings cap 2"). Written to rule #319 (the
    /// worktree bound to the wrong PROJECT) in or out as a contributing cause: this test's worktree is bound
    /// to the right project throughout (the fakes never name a wrong one), so if it still fails the same way,
    /// #319 cannot be the cause of #321.
    /// </summary>
    [Fact]
    public async Task Three_consecutive_failed_reviews_on_a_parallel_items_own_worktree_are_all_reported()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fravashi-autobuilder-threefailedreviews-worktree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var factory = new SqliteConnectionFactory(new SqliteStoreOptions(Path.Combine(directory, "fravashi.db"), Pooling: false));
            SqliteSchema.EnsureCreated(factory);
            var store = new SqliteSessionStore(factory);
            var role = new CommitReviewerRole(true, AgentTool.Claude, "claude-sonnet-5");
            var failed = new CommitReview(
                CommitReviewOutcome.Failed, role,
                Findings: [new CommitReviewFinding("a.cs", 1, "a rule", "fix it", "a problem")]);
            using var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(5), reviewer: () => new FixedReviewer(failed));
            var runner = new ScriptedRunner { Broker = broker };
            using var registry = new LiveSessionRegistry(runner, store, broker, new FakeSync(CopyPath));
            using var sessions = new AutopilotBuilderSessions(broker);
            var worktrees = new FakeWorktrees();
            var workspaceStore = new FakeWorkspaceStore(isSessionAlreadyRunning: id => registry.RunningFor(id) is not null);
            var branches = new FakeBranches();
            var builder = new AgentAutopilotBuilder(
                registry, broker, new FakeWork(), new FakePlans(), new FakeTrackerResolver(ItemWithPlan()),
                new FakeSync(CopyPath), sessions, _ => WorkspaceId,
                worktrees: worktrees, workspaceStore: workspaceStore, branches: branches,
                delay: (_, ct) => Task.Delay(1, ct));
            var context = Context(baseBranch: "main", effectiveConcurrencyCap: 2);

            AgentSessionId? sessionId = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var build = builder.BuildAsync(context, Item1, "build the item, attempt " + attempt.ToString(CultureInfo.InvariantCulture));

                if (sessionId is null)
                {
                    await WaitUntilAsync(() => sessions.SessionFor(Item1) is not null);
                    sessionId = sessions.SessionFor(Item1)!.Value;
                    await WaitUntilAsync(() => runner.Requests.Count > 0);
                }
                else
                {
                    sessions.SessionFor(Item1).Should().Be(sessionId, "a retry must reuse the item's own session");
                    await WaitUntilAsync(() => runner.SentTurns.Count(t => t.Id == sessionId!.Value) == attempt - 1);
                }

                var decision = await broker.RequestAsync(
                    sessionId!.Value, StudioToolServer.CliToolName(AttemptCommitTool.ToolName), "tu" + attempt.ToString(CultureInfo.InvariantCulture),
                    AgentToolInput.Create(new Dictionary<string, string> { ["summary"] = "x" }, ["summary"]));
                decision.Approved.Should().BeFalse("the reviewer failed this commit");

                runner.ChannelFor(sessionId.Value).Writer.TryWrite(
                    new TurnCompleted(DateTimeOffset.UtcNow, "done", IsError: false, TerminalReason: null));

                var result = await build;

                result.ReviewFailed.Should().BeTrue($"attempt {attempt}'s own failed review must be reported, not silently dropped");
                result.CommitMade.Should().BeFalse();
            }
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// #328: the worktree's copy answers a path from <c>CopyPathFor</c> the moment <c>EnsureStarted</c>
    /// registers it, before its first pull (and so its folder on disk) may have finished. The builder must
    /// wait for that pull to settle (<c>SlowSync</c>'s own readiness flag standing in for
    /// <c>WorkspaceSyncStatus.InitialSyncComplete</c>) before it ever starts the CLI, and start it once the
    /// copy is ready.
    /// </summary>
    [Fact]
    public async Task A_build_for_a_worktree_whose_copy_is_not_ready_waits_for_it_and_starts_once_it_is()
    {
        var sync = new SlowSync(CopyPath);
        var worktrees = new FakeWorktrees();
        var workspaceStore = new FakeWorkspaceStore();
        var branches = new FakeBranches();
        var builder = new AgentAutopilotBuilder(
            _registry, _broker, _work, _plans, new FakeTrackerResolver(ItemWithPlan(), SecondItem()),
            sync, _sessions, _ => WorkspaceId,
            worktrees: worktrees, workspaceStore: workspaceStore, branches: branches,
            delay: (_, ct) => Task.Delay(1, ct));
        var context = Context(baseBranch: "main", effectiveConcurrencyCap: 2);

        var build = builder.BuildAsync(context, Item1, "build the item");

        // The worktree is created and registered (both happen before the wait), but the copy is not ready,
        // so no CLI has been started while the builder polls for it.
        await WaitUntilAsync(() => worktrees.Created.Count > 0);
        await Task.Delay(100);
        _runner.Requests.Should().BeEmpty("the copy is not ready yet, so no CLI was started");

        var worktreeWorkspaceId = worktrees.Created.Single().RepositoryWorkspaceId + "-worktree-" + worktrees.Created.Single().Item.ToString(CultureInfo.InvariantCulture);
        sync.SetReady(worktreeWorkspaceId);

        var sessionId = await WaitForBoundSessionAsync();
        Complete(sessionId);
        var result = await build;

        result.Refused.Should().BeFalse();
        result.NeedsPerson.Should().BeFalse();
        _runner.Requests.Should().ContainSingle("the CLI started only once the copy became ready");
    }

    /// <summary>
    /// #328: a copy whose first pull never settles must not hang the item forever. Uses the same
    /// fast-advancing fake clock as <see cref="StopAsync_times_out_with_an_exception_when_the_process_never_reports_ending"/>
    /// so the bound is crossed within a handful of fast polls rather than a real two minutes.
    /// </summary>
    [Fact]
    public async Task A_copy_that_never_becomes_ready_fails_the_item_naming_the_copy_and_starts_no_cli()
    {
        var sync = new SlowSync(CopyPath);
        var start = DateTimeOffset.UtcNow;
        var clockCalls = 0;
        var builder = new AgentAutopilotBuilder(
            _registry, _broker, _work, _plans, new FakeTrackerResolver(ItemWithPlan(), SecondItem()),
            sync, _sessions, _ => WorkspaceId,
            clock: () => start.AddSeconds(++clockCalls * 10),
            delay: (_, ct) => Task.Delay(1, ct));

        var result = await builder.BuildAsync(Context(), Item1, "build the item");

        result.Refused.Should().BeTrue();
        result.CommitMade.Should().BeFalse();
        result.NeedsPerson.Should().BeFalse();
        result.Problem.Should().Contain(WorkspaceId);
        result.Problem.Should().Contain(CopyPath);
        _runner.Requests.Should().BeEmpty("the copy never became ready, so no CLI was ever started");
        _sessions.SessionFor(Item1).Should().BeNull("the item was refused before any session was bound");
    }

    /// <summary>
    /// #328: a copy whose start fails WHILE its path was already known (the instant between
    /// <c>WorkspaceAgentSync</c> recording the failure and removing the session, or a path read moments
    /// earlier in the same wait) must be reported at once by the wait loop itself, never waited out to the
    /// bound.
    /// </summary>
    [Fact]
    public async Task A_copy_whose_start_fails_fails_the_item_naming_the_copy_and_starts_no_cli()
    {
        var sync = new SlowSync(CopyPath);
        sync.Fail(WorkspaceId, "the bridge refused the pull");
        var builder = new AgentAutopilotBuilder(
            _registry, _broker, _work, _plans, new FakeTrackerResolver(ItemWithPlan(), SecondItem()),
            sync, _sessions, _ => WorkspaceId,
            delay: (_, ct) => Task.Delay(1, ct));

        var result = await builder.BuildAsync(Context(), Item1, "build the item");

        result.Refused.Should().BeTrue();
        result.Problem.Should().Contain(WorkspaceId);
        result.Problem.Should().Contain(CopyPath);
        result.Problem.Should().Contain("the bridge refused the pull");
        _runner.Requests.Should().BeEmpty("the copy's start had already failed, so no CLI was ever started");
        _sessions.SessionFor(Item1).Should().BeNull("the item was refused before any session was bound");
    }

    /// <summary>
    /// #328: a copy whose start failed on an EARLIER call (a retried item, whose worktree or shared workspace
    /// id is the same every time) leaves <c>CopyPathFor</c> answering null for good, the state the real
    /// <c>WorkspaceAgentSync</c> actually settles into once it removes the failed session: the branch that
    /// resolves the copy's path must read that as a refusal naming the reason, never the generic "has no
    /// contained copy yet" exception that would otherwise stop the whole run.
    /// </summary>
    [Fact]
    public async Task A_copy_whose_start_already_failed_before_its_path_was_ever_read_fails_the_item_and_starts_no_cli()
    {
        var sync = new SlowSync(CopyPath);
        sync.FailBeforeStart(WorkspaceId, "the bridge refused the pull");
        var builder = new AgentAutopilotBuilder(
            _registry, _broker, _work, _plans, new FakeTrackerResolver(ItemWithPlan(), SecondItem()),
            sync, _sessions, _ => WorkspaceId,
            delay: (_, ct) => Task.Delay(1, ct));

        var result = await builder.BuildAsync(Context(), Item1, "build the item");

        result.Refused.Should().BeTrue();
        result.Problem.Should().Contain(WorkspaceId);
        result.Problem.Should().Contain("the bridge refused the pull");
        _runner.Requests.Should().BeEmpty("the copy's start had already failed, so no CLI was ever started");
        _sessions.SessionFor(Item1).Should().BeNull("the item was refused before any session was bound");
    }

    /// <summary>#328: the ordinary case, unchanged: a copy that is already ready starts the session at once.</summary>
    [Fact]
    public async Task An_already_ready_copy_starts_the_building_session_at_once()
    {
        var build = _builder.BuildAsync(Context(), Item1, "build the item");
        var sessionId = await WaitForBoundSessionAsync();
        Complete(sessionId);

        var result = await build;

        result.Refused.Should().BeFalse();
        _runner.Requests.Should().ContainSingle();
    }

    /// <summary>
    /// #321 (commit review's second pass): a turn can fail review once and then succeed on a LATER
    /// <c>attempt_commit</c> within the SAME turn. The Reviewing stage's own read (<c>AutopilotCommitReviews
    /// .ReviewAsync</c>) only ever wants the ONE review for the commit that actually landed, so the earlier
    /// failure must be counted HERE, in the building result itself, or it is drained and discarded uncounted
    /// once the Reviewing stage reads the Passed review that follows it.
    /// </summary>
    [Fact]
    public async Task A_failed_review_followed_by_a_passed_one_in_the_same_turn_counts_the_failure_and_still_lets_the_commit_through()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fravashi-autobuilder-failedthenpassed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var factory = new SqliteConnectionFactory(new SqliteStoreOptions(Path.Combine(directory, "fravashi.db"), Pooling: false));
            SqliteSchema.EnsureCreated(factory);
            var store = new SqliteSessionStore(factory);
            var role = new CommitReviewerRole(true, AgentTool.Claude, "claude-sonnet-5");
            var failed = new CommitReview(
                CommitReviewOutcome.Failed, role,
                Findings: [new CommitReviewFinding("a.cs", 1, "a rule", "fix it", "a problem")]);
            var passed = new CommitReview(CommitReviewOutcome.Passed, role, Summary: "looks fine now");
            // A single shared instance: the broker's reviewer factory is invoked anew on every call, and a
            // fresh SequencedReviewer each time would restart its own script from the beginning.
            var sequencedReviewer = new SequencedReviewer(failed, passed);
            using var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(5), reviewer: () => sequencedReviewer);
            var runner = new ScriptedRunner { Broker = broker };
            using var registry = new LiveSessionRegistry(runner, store, broker, new FakeSync(CopyPath));
            using var sessions = new AutopilotBuilderSessions(broker);
            var builder = new AgentAutopilotBuilder(
                registry, broker, new FakeWork(), new FakePlans(), new FakeTrackerResolver(ItemWithPlan()),
                new FakeSync(CopyPath), sessions, _ => WorkspaceId, delay: (_, ct) => Task.Delay(1, ct));

            var build = builder.BuildAsync(Context(), Item1, "build the item");
            await WaitUntilAsync(() => sessions.SessionFor(Item1) is not null);
            var sessionId = sessions.SessionFor(Item1)!.Value;
            await WaitUntilAsync(() => runner.Requests.Count > 0);

            var firstDecision = await broker.RequestAsync(
                sessionId, StudioToolServer.CliToolName(AttemptCommitTool.ToolName), "tu1",
                AgentToolInput.Create(new Dictionary<string, string> { ["summary"] = "x" }, ["summary"]));
            firstDecision.Approved.Should().BeFalse("the first attempt failed review");

            var secondDecision = await broker.RequestAsync(
                sessionId, StudioToolServer.CliToolName(AttemptCommitTool.ToolName), "tu2",
                AgentToolInput.Create(new Dictionary<string, string> { ["summary"] = "y" }, ["summary"]));
            secondDecision.Approved.Should().BeTrue("the second attempt passed review, which the autopilot rule list clears unread");

            var now = DateTimeOffset.UtcNow;
            runner.ChannelFor(sessionId).Writer.TryWrite(new ToolCallCompleted(
                now, "tu2", StudioToolServer.CliToolName(AttemptCommitTool.ToolName), true,
                """{"changed":"committed","item":{"sha":"abc123","files":1,"note":null}}"""));
            runner.ChannelFor(sessionId).Writer.TryWrite(new TurnCompleted(now, "done", IsError: false, TerminalReason: null));

            var result = await build;

            result.CommitMade.Should().BeTrue();
            result.BranchHeadSha.Should().Be("abc123");
            result.FailedReviewCount.Should().Be(1, "the first attempt's failed review still owes its own count");

            // The Reviewing stage's own read, which only ever wants the one review for the commit that
            // landed: the earlier failure must not still be sitting here to be misread as THIS commit's own
            // verdict, and the review it finds must be the Passed one that actually let the commit through.
            var reviews = new AutopilotCommitReviews(sessions);
            var reviewed = await reviews.ReviewAsync(Item1);
            reviewed.Outcome.Should().Be(CommitReviewOutcome.Passed);
            sessions.TakeReviews(Item1).Should().BeEmpty();
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// #321 (fourth pass): a review's verdict is captured the moment the broker auto-refuses the commit
    /// (synchronously, before the hook even answers), independent of anything else happening in the same
    /// turn. If something else in that SAME turn is still pending a person's answer when
    /// <c>ReadResult</c> reads the turn, the "needs a person" result must still report the count NOW, while
    /// the item waits: ADR 0031 decision 6 says whichever limit fires first stops the item, and money and
    /// time already fire mid-wait, so a failed review captured before the wait began must count the same
    /// way, not only once the wait eventually settles (this is the shape the proof run's own evidence
    /// describes: a builder asking a person around the same time as the uncounted failures).
    /// </summary>
    [Fact]
    public async Task A_failed_review_captured_while_an_unrelated_call_is_still_pending_counts_immediately()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fravashi-autobuilder-reviewbesidepending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var factory = new SqliteConnectionFactory(new SqliteStoreOptions(Path.Combine(directory, "fravashi.db"), Pooling: false));
            SqliteSchema.EnsureCreated(factory);
            var store = new SqliteSessionStore(factory);
            var role = new CommitReviewerRole(true, AgentTool.Claude, "claude-sonnet-5");
            var failed = new CommitReview(
                CommitReviewOutcome.Failed, role,
                Findings: [new CommitReviewFinding("a.cs", 1, "a rule", "fix it", "a problem")]);
            using var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(5), reviewer: () => new FixedReviewer(failed));
            var runner = new ScriptedRunner { Broker = broker };
            using var registry = new LiveSessionRegistry(runner, store, broker, new FakeSync(CopyPath));
            using var sessions = new AutopilotBuilderSessions(broker);
            var builder = new AgentAutopilotBuilder(
                registry, broker, new FakeWork(), new FakePlans(), new FakeTrackerResolver(ItemWithPlan()),
                new FakeSync(CopyPath), sessions, _ => WorkspaceId, delay: (_, ct) => Task.Delay(1, ct));

            var build = builder.BuildAsync(Context(), Item1, "build the item");
            await WaitUntilAsync(() => sessions.SessionFor(Item1) is not null);
            var sessionId = sessions.SessionFor(Item1)!.Value;
            await WaitUntilAsync(() => runner.Requests.Count > 0);

            // This turn's attempt_commit, reviewed and refused synchronously, before anything else in the
            // turn happens: this is where the review is captured (OnReviewed).
            var commitDecision = await broker.RequestAsync(
                sessionId, StudioToolServer.CliToolName(AttemptCommitTool.ToolName), "tu-commit",
                AgentToolInput.Create(new Dictionary<string, string> { ["summary"] = "x" }, ["summary"]));
            commitDecision.Approved.Should().BeFalse("the reviewer failed this commit");

            // An ordinary Bash call the SAME turn then makes, still unanswered: nothing resolves it (no
            // rule match, no judge configured), so it parks and stays pending. WaitForSettledAsync (inside
            // the still-running build call above) exits the moment this becomes pending, so it must be
            // made to park AFTER the review above is already captured, or ReadResult could run before the
            // review exists at all and this test would be racing its own setup rather than the product.
            var bashRequested = broker.RequestAsync(
                sessionId, "Bash", "tu-bash",
                AgentToolInput.FromStrings(new Dictionary<string, string> { ["command"] = "echo hello" }));
            await WaitUntilAsync(() => broker.PendingFor(sessionId).Count > 0);

            // ReadResult runs with the Bash call still pending: the result is NeedsPerson, about the
            // unrelated Bash call, but the already-captured failed review is drained and reported on THIS
            // same result, not deferred until the wait eventually settles.
            var first = await build;
            first.NeedsPerson.Should().BeTrue();
            first.FailedReviewCount.Should().Be(1, "the review captured earlier in this turn counts now, while the item waits");

            // The Bash call is answered and the turn ends with no commit at all (the agent gave up after
            // the refusal): the review was already counted on the result above, so this one reports nothing
            // further (never double-counted).
            var bashRequestId = broker.PendingFor(sessionId).Single().RequestId;
            broker.Answer(bashRequestId, approved: true, "approved").Should().BeTrue();
            runner.ChannelFor(sessionId).Writer.TryWrite(
                new TurnCompleted(DateTimeOffset.UtcNow, "done", IsError: false, TerminalReason: null));

            var second = await builder.BuildAsync(Context(), Item1, "build the item");

            second.ReviewFailed.Should().BeFalse("the one review in this turn was already counted and reported above");
            second.CommitMade.Should().BeFalse();
            (await bashRequested).Approved.Should().BeTrue();
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// #321 (second pass): a review captured AFTER a turn is already reported as NeedsPerson (so nothing
    /// has drained it yet) must still count if the session then dies before anyone answers the pending
    /// call that made it wait, rather than the turn ever genuinely settling. The next retry binds a FRESH
    /// session to the item, so a review queue keyed by SESSION would strand the captured review under the
    /// dead session's id forever; keyed by item (this fix's own second correction), it survives the rebind.
    /// </summary>
    [Fact]
    public async Task A_failed_review_captured_after_a_pending_call_is_already_reported_survives_the_session_dying()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fravashi-autobuilder-reviewbesidedeadsession-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var factory = new SqliteConnectionFactory(new SqliteStoreOptions(Path.Combine(directory, "fravashi.db"), Pooling: false));
            SqliteSchema.EnsureCreated(factory);
            var store = new SqliteSessionStore(factory);
            var role = new CommitReviewerRole(true, AgentTool.Claude, "claude-sonnet-5");
            var failed = new CommitReview(
                CommitReviewOutcome.Failed, role,
                Findings: [new CommitReviewFinding("a.cs", 1, "a rule", "fix it", "a problem")]);
            using var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(5), reviewer: () => new FixedReviewer(failed));
            var runner = new ScriptedRunner { Broker = broker };
            using var registry = new LiveSessionRegistry(runner, store, broker, new FakeSync(CopyPath));
            using var sessions = new AutopilotBuilderSessions(broker);
            var builder = new AgentAutopilotBuilder(
                registry, broker, new FakeWork(), new FakePlans(), new FakeTrackerResolver(ItemWithPlan()),
                new FakeSync(CopyPath), sessions, _ => WorkspaceId, delay: (_, ct) => Task.Delay(1, ct));

            var firstBuild = builder.BuildAsync(Context(), Item1, "build the item");
            await WaitUntilAsync(() => sessions.SessionFor(Item1) is not null);
            var firstSessionId = sessions.SessionFor(Item1)!.Value;
            await WaitUntilAsync(() => runner.Requests.Count > 0);

            // An ordinary Bash call the same turn makes, left unanswered on purpose: this is read and
            // reported BEFORE the review below is ever captured, so nothing has drained the queue yet.
            _ = broker.RequestAsync(
                firstSessionId, "Bash", "tu-bash",
                AgentToolInput.FromStrings(new Dictionary<string, string> { ["command"] = "echo hello" }));
            await WaitUntilAsync(() => broker.PendingFor(firstSessionId).Count > 0);

            var first = await firstBuild;
            first.NeedsPerson.Should().BeTrue("the unrelated Bash call is still pending");
            first.FailedReviewCount.Should().Be(0, "no attempt_commit has happened yet");

            // The review happens AFTER that first read, so nothing has consumed it yet.
            var commitDecision = await broker.RequestAsync(
                firstSessionId, StudioToolServer.CliToolName(AttemptCommitTool.ToolName), "tu-commit",
                AgentToolInput.Create(new Dictionary<string, string> { ["summary"] = "x" }, ["summary"]));
            commitDecision.Approved.Should().BeFalse("the reviewer failed this commit");

            // The session dies with the Bash call STILL unanswered: no one ever resolves it, the turn never
            // genuinely settles, and the next retry has nothing to re-check (NOT a re-check: the session
            // itself is gone), so it starts a fresh one.
            runner.ChannelFor(firstSessionId).Writer.TryWrite(
                new SessionEnded(DateTimeOffset.UtcNow, AgentSessionState.Failed, "the process died"));
            await WaitUntilAsync(() => !registry.Find(firstSessionId)!.IsRunning);

            var secondBuild = builder.BuildAsync(Context(), Item1, "build the item");
            await WaitUntilAsync(() => sessions.SessionFor(Item1) is { } rebound && rebound != firstSessionId);
            var secondSessionId = sessions.SessionFor(Item1)!.Value;
            runner.ChannelFor(secondSessionId).Writer.TryWrite(
                new TurnCompleted(DateTimeOffset.UtcNow, "done", IsError: false, TerminalReason: null));

            var second = await secondBuild;

            second.ReviewFailed.Should().BeTrue(
                "the review captured on the dead session must still count once a fresh session is bound in its place");
            second.FailedReviewCount.Should().Be(1);
            sessions.TakeReviews(Item1).Should().BeEmpty();
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// #321: a review captured for a turn whose session then ends (the process dies, or is stopped by
    /// something other than a normal turn end) before the turn otherwise finishes must still count: the
    /// "session ended" branch of <c>ReadResult</c> used to build its result without ever reading the queue,
    /// so a Failed review captured just before the session died was orphaned under that dead session's id
    /// once a later retry bound a FRESH session to the item in its place.
    /// </summary>
    [Fact]
    public async Task A_failed_review_captured_just_before_the_session_ends_still_counts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fravashi-autobuilder-reviewbeforesessionends-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var factory = new SqliteConnectionFactory(new SqliteStoreOptions(Path.Combine(directory, "fravashi.db"), Pooling: false));
            SqliteSchema.EnsureCreated(factory);
            var store = new SqliteSessionStore(factory);
            var role = new CommitReviewerRole(true, AgentTool.Claude, "claude-sonnet-5");
            var failed = new CommitReview(
                CommitReviewOutcome.Failed, role,
                Findings: [new CommitReviewFinding("a.cs", 1, "a rule", "fix it", "a problem")]);
            using var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(5), reviewer: () => new FixedReviewer(failed));
            var runner = new ScriptedRunner { Broker = broker };
            using var registry = new LiveSessionRegistry(runner, store, broker, new FakeSync(CopyPath));
            using var sessions = new AutopilotBuilderSessions(broker);
            var builder = new AgentAutopilotBuilder(
                registry, broker, new FakeWork(), new FakePlans(), new FakeTrackerResolver(ItemWithPlan()),
                new FakeSync(CopyPath), sessions, _ => WorkspaceId, delay: (_, ct) => Task.Delay(1, ct));

            var build = builder.BuildAsync(Context(), Item1, "build the item");
            await WaitUntilAsync(() => sessions.SessionFor(Item1) is not null);
            var sessionId = sessions.SessionFor(Item1)!.Value;
            await WaitUntilAsync(() => runner.Requests.Count > 0);

            var decision = await broker.RequestAsync(
                sessionId, StudioToolServer.CliToolName(AttemptCommitTool.ToolName), "tu1",
                AgentToolInput.Create(new Dictionary<string, string> { ["summary"] = "x" }, ["summary"]));
            decision.Approved.Should().BeFalse("the reviewer failed this commit");

            // The process ends on its own (a crash, not a normal turn end): no TurnCompleted, just the
            // session reporting it is no longer running.
            runner.ChannelFor(sessionId).Writer.TryWrite(
                new SessionEnded(DateTimeOffset.UtcNow, AgentSessionState.Failed, "the process died"));

            var result = await build;

            result.ReviewFailed.Should().BeTrue("the review captured just before the session died must still count");
            result.FailedReviewCount.Should().Be(1);
            sessions.TakeReviews(Item1).Should().BeEmpty("drained and reported, nothing left orphaned under the dead session");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// #321: within ONE turn the building agent can retry <c>attempt_commit</c> more than once (each one its
    /// own review), before the turn ends with nothing committed. Two failed reviews in the same turn must
    /// count as two failed reviews, not one, or the proof run's "3 reviews: 3 fail" / "0 of 2" gap
    /// can repeat with as few as two
    /// retries inside a single turn: before the fix, <see cref="AutopilotBuilderSessions"/> kept only the
    /// LATEST review per session (<c>OnReviewed</c> overwrote the one before it), so the first of the two
    /// never reached <c>AgentAutopilotBuilder.NotCommitted</c> at all.
    /// </summary>
    [Fact]
    public async Task Two_failed_reviews_within_the_same_turn_both_count()
    {
        var directory = Path.Combine(Path.GetTempPath(), "fravashi-autobuilder-tworeviewsoneturn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var factory = new SqliteConnectionFactory(new SqliteStoreOptions(Path.Combine(directory, "fravashi.db"), Pooling: false));
            SqliteSchema.EnsureCreated(factory);
            var store = new SqliteSessionStore(factory);
            var role = new CommitReviewerRole(true, AgentTool.Claude, "claude-sonnet-5");
            var failed = new CommitReview(
                CommitReviewOutcome.Failed, role,
                Findings: [new CommitReviewFinding("a.cs", 1, "a rule", "fix it", "a problem")]);
            using var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(5), reviewer: () => new FixedReviewer(failed));
            var runner = new ScriptedRunner { Broker = broker };
            using var registry = new LiveSessionRegistry(runner, store, broker, new FakeSync(CopyPath));
            using var sessions = new AutopilotBuilderSessions(broker);
            var builder = new AgentAutopilotBuilder(
                registry, broker, new FakeWork(), new FakePlans(), new FakeTrackerResolver(ItemWithPlan()),
                new FakeSync(CopyPath), sessions, _ => WorkspaceId, delay: (_, ct) => Task.Delay(1, ct));

            var build = builder.BuildAsync(Context(), Item1, "build the item");
            await WaitUntilAsync(() => sessions.SessionFor(Item1) is not null);
            var sessionId = sessions.SessionFor(Item1)!.Value;
            await WaitUntilAsync(() => runner.Requests.Count > 0);

            // Two retries of attempt_commit, both reviewed and refused, inside the SAME turn (the agent never
            // ended its turn between them).
            var first = await broker.RequestAsync(
                sessionId, StudioToolServer.CliToolName(AttemptCommitTool.ToolName), "tu1",
                AgentToolInput.Create(new Dictionary<string, string> { ["summary"] = "x" }, ["summary"]));
            first.Approved.Should().BeFalse();
            var second = await broker.RequestAsync(
                sessionId, StudioToolServer.CliToolName(AttemptCommitTool.ToolName), "tu2",
                AgentToolInput.Create(new Dictionary<string, string> { ["summary"] = "y" }, ["summary"]));
            second.Approved.Should().BeFalse();

            runner.ChannelFor(sessionId).Writer.TryWrite(
                new TurnCompleted(DateTimeOffset.UtcNow, "done", IsError: false, TerminalReason: null));

            var result = await build;

            result.ReviewFailed.Should().BeTrue();
            result.FailedReviewCount.Should().Be(2, "both reviews this turn failed, so both must count toward the limit");
            sessions.TakeReviews(Item1).Should().BeEmpty("both were drained and reported, nothing left to answer for later");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task StopAsync_actually_ends_the_items_live_session()
    {
        var build = _builder.BuildAsync(Context(), Item1, "build the item");
        var sessionId = await WaitForBoundSessionAsync();
        Complete(sessionId);
        await build;

        await _builder.StopAsync(Item1);

        await WaitUntilAsync(() => !_registry.Find(sessionId)!.IsRunning);
        _registry.RunningFor(WorkspaceId).Should().BeNull();
    }

    [Fact]
    public async Task StopAsync_for_an_item_with_no_bound_session_does_nothing()
    {
        var act = () => _builder.StopAsync(new WorkItemId("item-never-built"));

        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Commit review #310 (fifth pass): StopAsync must wait for the process to actually end, not just ask
    /// the registry to kill it (that ask returns at once; killing a real process is not instant). A session
    /// that never reports back must time out and throw, the way the engine's own MaybeReturnWorkspaceAsync
    /// already expects (it turns the exception into a WorkspaceReturnNote rather than checking out over a
    /// session that could still be writing).
    /// </summary>
    [Fact]
    public async Task StopAsync_times_out_with_an_exception_when_the_process_never_reports_ending()
    {
        var stuckRunner = new StuckRunner { Broker = _broker };
        var registry = new LiveSessionRegistry(stuckRunner, _store, _broker, new FakeSync(CopyPath));
        var start = DateTimeOffset.UtcNow;
        var clockCalls = 0;
        var builder = new AgentAutopilotBuilder(
            registry,
            _broker,
            _work,
            _plans,
            new FakeTrackerResolver(ItemWithPlan(), SecondItem()),
            new FakeSync(CopyPath),
            _sessions,
            workspaceIdFor: _ => WorkspaceId,
            // Advances 10 simulated seconds on every read, so each wait's own deadline (however it was
            // established) is reliably crossed within a handful of fast, 1 ms-delayed polls, rather than
            // this test waiting out a real 30 seconds or plateauing on a fixed "past the deadline" value
            // that itself never advances again.
            clock: () => start.AddSeconds(++clockCalls * 10),
            delay: (_, ct) => Task.Delay(1, ct));

        var build = builder.BuildAsync(Context(), Item1, "build the item");
        var sessionId = await WaitForBoundSessionAsync(stuckRunner.IsRegistered);
        stuckRunner.ChannelFor(sessionId).Writer.TryWrite(
            new TurnCompleted(DateTimeOffset.UtcNow, "done", IsError: false, TerminalReason: null));
        await build;

        var act = () => builder.StopAsync(Item1);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*did not stop*");
        registry.Dispose();
    }

    [Fact]
    public async Task A_tool_call_that_parks_reports_needs_person_without_blocking()
    {
        var build = _builder.BuildAsync(Context(), Item1, "build the item");
        var sessionId = await WaitForBoundSessionAsync();

        // Nothing resolves this: an ordinary Bash command, no rule match, no judge configured, so it parks.
        _ = _broker.RequestAsync(
            sessionId, "Bash", "tu1",
            AgentToolInput.FromStrings(new Dictionary<string, string> { ["command"] = "echo hello" }));

        var result = await build;

        result.NeedsPerson.Should().BeTrue();
        result.CommitMade.Should().BeFalse();
        result.Problem.Should().Contain("Bash");
    }

    /// <summary>
    /// ADR 0031 decision 12 (#305): while the run waits on the person who has not answered that pending
    /// approval yet, a later BuildAsync call for the SAME item re-checks the session's own state rather than
    /// sending a second turn behind the one already blocked, which would otherwise run as a real, billed turn
    /// the instant the first one settles.
    /// </summary>
    [Fact]
    public async Task A_second_build_call_while_a_tool_call_is_still_pending_sends_nothing_and_still_reports_needs_person()
    {
        var build = _builder.BuildAsync(Context(), Item1, "build the item");
        var sessionId = await WaitForBoundSessionAsync();
        _ = _broker.RequestAsync(
            sessionId, "Bash", "tu1",
            AgentToolInput.FromStrings(new Dictionary<string, string> { ["command"] = "echo hello" }));
        var first = await build;
        first.NeedsPerson.Should().BeTrue();
        _runner.Requests.Should().ContainSingle("only the first call ever starts a process");

        var second = await _builder.BuildAsync(Context(), Item1, "build the item");

        second.NeedsPerson.Should().BeTrue();
        second.CostUsd.Should().Be(0m, "nothing new happened between the two calls");
        _runner.SentTurns.Should().BeEmpty("the pending approval is still open; this call must only read the session, never send to it");
    }

    /// <summary>
    /// #310's own ending stops a parked item's session when its run ends, but a stop that fails or times out
    /// is swallowed into a WorkspaceReturnNote rather than retried (a known, accepted gap), so the OLD
    /// session for this item can still be running when a LATER run resumes it. BuildAsync must never read a
    /// re-check from, or send a turn into, a session a DIFFERENT run bound: it starts a genuinely fresh one.
    /// </summary>
    [Fact]
    public async Task A_build_call_under_a_new_run_never_reuses_an_earlier_runs_leftover_session_for_the_same_item()
    {
        var firstRunBuild = _builder.BuildAsync(Context(Run1), Item1, "build the item");
        var firstSessionId = await WaitForBoundSessionAsync();
        _ = _broker.RequestAsync(
            firstSessionId, "Bash", "tu1",
            AgentToolInput.FromStrings(new Dictionary<string, string> { ["command"] = "echo hello" }));
        var firstResult = await firstRunBuild;
        firstResult.NeedsPerson.Should().BeTrue("the first run's own session is left parked, still alive");

        var secondRun = Guid.NewGuid();
        var secondBuild = _builder.BuildAsync(Context(secondRun), Item1, "build the item");
        await WaitUntilAsync(() => _sessions.RunFor(Item1) == secondRun);
        var secondSessionId = _sessions.SessionFor(Item1)!.Value;
        Complete(secondSessionId);
        await secondBuild;

        secondSessionId.Should().NotBe(firstSessionId, "the new run starts a genuinely fresh session, never the old run's leftover one");
        _runner.Requests.Should().HaveCount(2, "a SECOND process was started rather than sending into the first run's own session");
        _runner.SentTurns.Should().BeEmpty("neither call ever sends a follow-up turn: the first parks on its own first turn, the second starts fresh");
        _registry.Find(firstSessionId)!.IsRunning.Should().BeFalse("the first run's own leftover session was stopped to make room");
    }

    /// <summary>
    /// A Questions document ENDS the turn (TurnInFlight reads false), unlike a pending tool approval, so a
    /// re-check keyed on TurnInFlight alone could not tell "nobody has answered the question yet" apart from
    /// "safe to send a fresh turn". AutopilotBuilderSessions.TurnStart is what actually carries the distinction:
    /// it is recorded whenever a result comes back NeedsPerson, whatever the reason, and only cleared once one
    /// does not.
    /// </summary>
    [Fact]
    public async Task A_turn_that_ends_on_a_question_is_a_re_check_too_and_a_second_call_sends_nothing()
    {
        var build = _builder.BuildAsync(Context(), Item1, "build the item");
        var sessionId = await WaitForBoundSessionAsync();
        var channel = _runner.ChannelFor(sessionId);
        await channel.Writer.WriteAsync(new TurnCompleted(
            DateTimeOffset.UtcNow, "a question", IsError: false, TerminalReason: null,
            Document: new AgentTurnDocument("1", "questions", "which approach?", AgentTurnDocumentKind.Questions)));

        var first = await build;
        first.NeedsPerson.Should().BeTrue();
        first.Problem.Should().Contain("which approach?");

        var second = await _builder.BuildAsync(Context(), Item1, "build the item");

        second.NeedsPerson.Should().BeTrue();
        second.CostUsd.Should().Be(0m);
        _runner.SentTurns.Should().BeEmpty("nobody has answered the question yet; this call must only re-read, never send");
    }

    /// <summary>
    /// The acceptance line "an answer in time lets the item continue in the same session" (#305), proven on
    /// the real seam rather than a fake: the approval is answered BETWEEN two BuildAsync calls, the tool then
    /// runs and the turn completes with a commit, and the re-check call (which never sent anything) is what
    /// reads it.
    /// </summary>
    [Fact]
    public async Task An_approval_answered_between_two_build_calls_is_read_as_the_commit_on_the_re_check()
    {
        var build = _builder.BuildAsync(Context(), Item1, "build the item");
        var sessionId = await WaitForBoundSessionAsync();
        var requested = _broker.RequestAsync(
            sessionId, StudioToolServer.CliToolName(AttemptCommitTool.ToolName), "tu1",
            AgentToolInput.Create(new Dictionary<string, string> { ["summary"] = "x" }, ["summary"]));
        await WaitUntilAsync(() => _broker.PendingFor(sessionId).Count > 0);

        var first = await build;
        first.NeedsPerson.Should().BeTrue();

        // The person answers the pending approval; the tool then runs and the turn completes with a commit,
        // independent of this fake's own channel (a real CLI reports exactly this once the hook unblocks).
        var requestId = _broker.PendingFor(sessionId).Single().RequestId;
        _broker.Answer(requestId, approved: true, "approved").Should().BeTrue();
        var channel = _runner.ChannelFor(sessionId);
        var now = DateTimeOffset.UtcNow;
        await channel.Writer.WriteAsync(new ToolCallCompleted(
            now, "tu1", StudioToolServer.CliToolName(AttemptCommitTool.ToolName), true,
            """{"changed":"committed","item":{"sha":"abc123","files":1,"note":null}}"""));
        await channel.Writer.WriteAsync(new TurnCompleted(now, "done", IsError: false, TerminalReason: null));

        var second = await _builder.BuildAsync(Context(), Item1, "build the item");

        second.CommitMade.Should().BeTrue();
        second.BranchHeadSha.Should().Be("abc123");
        second.NeedsPerson.Should().BeFalse();
        _runner.SentTurns.Should().BeEmpty("the already-answered approval is what resolved this; this call never sent anything");
        (await requested).Approved.Should().BeTrue();
    }

    [Fact]
    public async Task BuildAsync_refuses_naming_the_other_sessions_title_rather_than_replacing_a_persons_own_conversation()
    {
        var personsSession = _registry.Start(new SessionStartRequest(
            WorkspaceId, CopyPath, "a person's own prompt", Title: "A person's own chat"));

        var act = () => _builder.BuildAsync(Context(), Item1, "build the item");

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("A person's own chat");
        _sessions.SessionFor(Item1).Should().BeNull();
        _work.Links.Should().BeEmpty();
        _registry.RunningFor(WorkspaceId)!.Id.Should().Be(personsSession.Id, "nothing here touched the running conversation");
    }

    /// <summary>
    /// ADR 0031 decision 16, #307/#308: a gate that refuses stops a FRESH start before it ever reaches the
    /// registry, but is not a failure of the item (#308 host note point 4): BuildAsync reports
    /// WaitingForCapacity instead of throwing, spending nothing, so the engine leaves the item exactly as it
    /// was and tries again on a later tick.
    /// </summary>
    [Fact]
    public async Task BuildAsync_reports_waiting_for_capacity_when_the_concurrency_gate_refuses()
    {
        var gate = new FakeConcurrencyGate("No room for another session against this repository.");
        var builder = new AgentAutopilotBuilder(
            _registry, _broker, _work, _plans,
            new FakeTrackerResolver(ItemWithPlan(), SecondItem()),
            new FakeSync(CopyPath), _sessions,
            workspaceIdFor: _ => WorkspaceId,
            concurrency: gate,
            delay: (_, ct) => Task.Delay(1, ct));

        var result = await builder.BuildAsync(Context(), Item1, "build the item");

        result.WaitingForCapacity.Should().BeTrue();
        result.CostUsd.Should().Be(0m);
        result.Problem.Should().Be("No room for another session against this repository.");
        gate.Calls.Should().Be(1);
        _sessions.SessionFor(Item1).Should().BeNull("the gate refused before a session was ever bound");
    }

    /// <summary>
    /// The gate is consulted only before a FRESH session starts: a follow-up turn to one already running must
    /// never be refused by a cap that was only ever meant to bound how many sessions START.
    /// </summary>
    [Fact]
    public async Task The_concurrency_gate_is_never_consulted_for_a_follow_up_turn_to_an_already_running_session()
    {
        var gate = new FakeConcurrencyGate(null);
        var builder = new AgentAutopilotBuilder(
            _registry, _broker, _work, _plans,
            new FakeTrackerResolver(ItemWithPlan(), SecondItem()),
            new FakeSync(CopyPath), _sessions,
            workspaceIdFor: _ => WorkspaceId,
            concurrency: gate,
            delay: (_, ct) => Task.Delay(1, ct));

        var first = builder.BuildAsync(Context(), Item1, "first prompt");
        var sessionId = await WaitForBoundSessionAsync();
        Complete(sessionId);
        await first;
        gate.Calls.Should().Be(1);

        var second = builder.BuildAsync(Context(), Item1, "second prompt");
        await WaitUntilAsync(() => _runner.SentTurns.Any(t => t.Id == sessionId));
        Complete(sessionId);
        await second;

        gate.Calls.Should().Be(1, "a follow-up turn to an already-running session never starts a fresh one");
    }

    /// <summary>
    /// Closes the chain from a dispatched run's own lowered cap to the gate that enforces it: the context's
    /// ConcurrencyCapOverride (which AutopilotBuilderContext.From reads off the run's settings) must reach the
    /// gate's own call exactly as given, not merely ride on the saved run (AutopilotDispatcherTests' concern).
    /// </summary>
    [Fact]
    public async Task The_contexts_concurrency_cap_override_reaches_the_gates_own_call()
    {
        var gate = new FakeConcurrencyGate(null);
        var builder = new AgentAutopilotBuilder(
            _registry, _broker, _work, _plans,
            new FakeTrackerResolver(ItemWithPlan(), SecondItem()),
            new FakeSync(CopyPath), _sessions,
            workspaceIdFor: _ => WorkspaceId,
            concurrency: gate,
            delay: (_, ct) => Task.Delay(1, ct));

        var build = builder.BuildAsync(Context(concurrencyCapOverride: 1), Item1, "build the item");
        var sessionId = await WaitForBoundSessionAsync();
        Complete(sessionId);
        await build;

        gate.ReceivedOverrides.Should().ContainSingle().Which.Should().Be(1);
    }

    [Fact]
    public async Task StartFresh_on_the_workspace_does_not_stop_the_autopilots_live_session()
    {
        var build = _builder.BuildAsync(Context(), Item1, "build the item");
        var sessionId = await WaitForBoundSessionAsync();
        Complete(sessionId);
        await build;

        _registry.StartFresh(WorkspaceId);

        _registry.RunningFor(WorkspaceId).Should().NotBeNull("StartFresh only hides what a pane shows, it never stops the process");
        _registry.RunningFor(WorkspaceId)!.Id.Should().Be(sessionId);

        var second = _builder.BuildAsync(Context(), Item1, "second prompt");
        await WaitUntilAsync(() => _runner.SentTurns.Any(t => t.Id == sessionId && t.Text == "second prompt"));
        Complete(sessionId);
        await second;

        _runner.Requests.Should().ContainSingle("the same session kept running through StartFresh");
    }

    // ---- #308: a parallel item's own worktree ------------------------------------------------------------------

    /// <summary>
    /// A run whose effective cap is more than 1 gives the item its own worktree (#308 decision 2): registered
    /// in the workspace store BEFORE the session starts (point 2), the worktree's own branch pre-recorded as
    /// the attempt branch so attempt_branch makes no second one (decision 3), and pruned once the item ends
    /// (point 8), dropping the record so a later item cannot find a stale one.
    /// </summary>
    [Fact]
    public async Task A_parallel_items_worktree_is_registered_before_the_session_starts_and_pruned_once_it_ends()
    {
        var worktrees = new FakeWorktrees();
        var workspaceStore = new FakeWorkspaceStore(isSessionAlreadyRunning: id => _registry.RunningFor(id) is not null);
        var branches = new FakeBranches();
        var builder = new AgentAutopilotBuilder(
            _registry, _broker, _work, _plans,
            new FakeTrackerResolver(ItemWithPlan(), SecondItem()),
            new FakeSync(CopyPath), _sessions,
            workspaceIdFor: _ => WorkspaceId,
            worktrees: worktrees,
            workspaceStore: workspaceStore,
            branches: branches,
            delay: (_, ct) => Task.Delay(1, ct));
        var context = Context(baseBranch: "main", effectiveConcurrencyCap: 2);

        var build = builder.BuildAsync(context, Item1, "build the item");
        await WaitUntilAsync(() => _sessions.SessionFor(Item1) is not null);
        var sessionId = _sessions.SessionFor(Item1)!.Value;

        workspaceStore.Touched.Should().ContainSingle(t => t.Id == "w1-worktree-1")
            .Which.RootPath.Should().Be("/repos/widget.fravashi-worktrees/1");
        workspaceStore.SessionWasAlreadyRunningWhenTouched.Should().BeFalse(
            "Touch must land before LiveSessionRegistry.Start, not merely before this assertion gets around to checking");

        Complete(sessionId);
        await build;

        branches.Recorded[sessionId].Should().Be(new SessionBranch("fravashi/worktree/item-1", "main", branches.Recorded[sessionId].StartedUtc));

        await builder.EndItemAsync(context, Item1);

        worktrees.Pruned.Should().ContainSingle().Which.Should().Be(("w1", 1));
        _registry.Find(sessionId)!.IsRunning.Should().BeFalse("EndItemAsync stops the item's own session before pruning its worktree");
        workspaceStore.Touched.Should().BeEmpty("the studio's own registration is dropped once the bridge confirms the worktree is gone");
    }

    /// <summary>
    /// Backlog 332: a resumed item's worktree (created once per item number and reused) can already hold
    /// uncommitted work an earlier, stopped attempt left there. The fresh building conversation's first turn
    /// is told so in one sentence, read off the worktree's own host-side git status before the session starts.
    /// </summary>
    [Fact]
    public async Task A_resumed_items_dirty_worktree_is_announced_in_the_first_turns_preamble()
    {
        var worktrees = new FakeWorktrees();
        var workspaceStore = new FakeWorkspaceStore();
        var branches = new FakeBranches();
        var git = Substitute.For<IGitService>();
        git.IsAvailable.Returns(true);
        var tree = Substitute.For<IGitWorkingTreeService>();
        tree.GetStatusAsync().Returns(Task.FromResult(new GitWorkingStatus(
            "fravashi/worktree/item-1", null, 0, 0,
            StagedEntries: [],
            UnstagedEntries: [new GitWorkingEntry("scripts/hello.sh", GitEntryState.Untracked, Staged: false)])));
        var services = new WorkspaceGitServices(
            "w1-worktree-1", Substitute.For<IGitCommandRunner>(), git, tree, Substitute.For<IGitRemoteService>());
        var workspaceGit = new FakeWorkspaceGit(services);
        var builder = new AgentAutopilotBuilder(
            _registry, _broker, _work, _plans,
            new FakeTrackerResolver(ItemWithPlan(), SecondItem()),
            new FakeSync(CopyPath), _sessions,
            workspaceIdFor: _ => WorkspaceId,
            worktrees: worktrees,
            workspaceStore: workspaceStore,
            workspaceGit: workspaceGit,
            branches: branches,
            delay: (_, ct) => Task.Delay(1, ct));
        var context = Context(baseBranch: "main", effectiveConcurrencyCap: 2);

        var build = builder.BuildAsync(context, Item1, "build the item");
        var sessionId = await WaitForBoundSessionAsync();
        Complete(sessionId);
        await build;

        var prompt = _runner.Requests.Should().ContainSingle().Which.Prompt;
        prompt.Should().Contain("already holds uncommitted work from an earlier attempt");
    }

    /// <summary>
    /// The counterpart: a FRESH worktree (nothing dirty) says nothing about an earlier attempt, since there
    /// is none.
    /// </summary>
    [Fact]
    public async Task A_freshly_made_worktree_gets_no_resumed_work_note()
    {
        var worktrees = new FakeWorktrees();
        var workspaceStore = new FakeWorkspaceStore();
        var branches = new FakeBranches();
        var git = Substitute.For<IGitService>();
        git.IsAvailable.Returns(true);
        var tree = Substitute.For<IGitWorkingTreeService>();
        // A genuinely CLEAN worktree, never the failed-read shape (GitWorkingStatus.Empty, an empty branch):
        // a real status read always carries its branch, even when there is nothing to report.
        tree.GetStatusAsync().Returns(Task.FromResult(new GitWorkingStatus(
            "fravashi/worktree/item-1", null, 0, 0, StagedEntries: [], UnstagedEntries: [])));
        var services = new WorkspaceGitServices(
            "w1-worktree-1", Substitute.For<IGitCommandRunner>(), git, tree, Substitute.For<IGitRemoteService>());
        var workspaceGit = new FakeWorkspaceGit(services);
        var builder = new AgentAutopilotBuilder(
            _registry, _broker, _work, _plans,
            new FakeTrackerResolver(ItemWithPlan(), SecondItem()),
            new FakeSync(CopyPath), _sessions,
            workspaceIdFor: _ => WorkspaceId,
            worktrees: worktrees,
            workspaceStore: workspaceStore,
            workspaceGit: workspaceGit,
            branches: branches,
            delay: (_, ct) => Task.Delay(1, ct));
        var context = Context(baseBranch: "main", effectiveConcurrencyCap: 2);

        var build = builder.BuildAsync(context, Item1, "build the item");
        var sessionId = await WaitForBoundSessionAsync();
        Complete(sessionId);
        await build;

        var prompt = _runner.Requests.Should().ContainSingle().Which.Prompt;
        prompt.Should().NotContain("already holds uncommitted work");
    }

    /// <summary>
    /// The other way <c>IsDirtyAsync</c> answers false: a status read that FAILED (named by its own empty
    /// branch, <see cref="GitWorkingStatus.Empty"/>'s own shape) withholds the note too, never crashes, and
    /// never guesses dirty. Unlike the test above, this is not a clean worktree; the note is simply not told
    /// when it cannot be told honestly, exactly as <c>IsDirtyAsync</c>'s own doc says.
    /// </summary>
    [Fact]
    public async Task A_worktree_whose_status_could_not_be_read_also_gets_no_resumed_work_note()
    {
        var worktrees = new FakeWorktrees();
        var workspaceStore = new FakeWorkspaceStore();
        var branches = new FakeBranches();
        var git = Substitute.For<IGitService>();
        git.IsAvailable.Returns(true);
        var tree = Substitute.For<IGitWorkingTreeService>();
        tree.GetStatusAsync().Returns(Task.FromResult(GitWorkingStatus.Empty));
        var services = new WorkspaceGitServices(
            "w1-worktree-1", Substitute.For<IGitCommandRunner>(), git, tree, Substitute.For<IGitRemoteService>());
        var workspaceGit = new FakeWorkspaceGit(services);
        var builder = new AgentAutopilotBuilder(
            _registry, _broker, _work, _plans,
            new FakeTrackerResolver(ItemWithPlan(), SecondItem()),
            new FakeSync(CopyPath), _sessions,
            workspaceIdFor: _ => WorkspaceId,
            worktrees: worktrees,
            workspaceStore: workspaceStore,
            workspaceGit: workspaceGit,
            branches: branches,
            delay: (_, ct) => Task.Delay(1, ct));
        var context = Context(baseBranch: "main", effectiveConcurrencyCap: 2);

        var build = builder.BuildAsync(context, Item1, "build the item");
        var sessionId = await WaitForBoundSessionAsync();
        Complete(sessionId);
        await build;

        var prompt = _runner.Requests.Should().ContainSingle().Which.Prompt;
        prompt.Should().NotContain("already holds uncommitted work");
    }

    /// <summary>Backlog 332: the worktree's own git status, read through <see cref="IWorkspaceGit"/>.</summary>
    private sealed class FakeWorkspaceGit(WorkspaceGitServices services) : IWorkspaceGit
    {
        public WorkspaceGitServices? For(string workspaceId) =>
            workspaceId == services.WorkspaceId ? services : null;
    }

    /// <summary>
    /// Backlog 312: at a cap of 1 (today's shared-workspace path, untouched) the preamble still says the
    /// folder is returned to the branch it started from, and never mentions a worktree.
    /// </summary>
    [Fact]
    public async Task The_preamble_describes_the_shared_workspace_at_a_cap_of_one()
    {
        var build = _builder.BuildAsync(Context(effectiveConcurrencyCap: 1), Item1, "build the item");
        var sessionId = await WaitForBoundSessionAsync();
        Complete(sessionId);
        await build;

        var prompt = _runner.Requests.Should().ContainSingle().Which.Prompt;
        prompt.Should().Contain("folder is returned to the branch it started from");
        prompt.Should().NotContain("worktree");
    }

    /// <summary>
    /// Backlog 312: at a cap above 1 (#308's own worktree-per-item path) the preamble says the worktree's
    /// branch is already the attempt branch and that the worktree is pruned once the item ends, never that
    /// the folder is returned to anything (nothing here ever switches the person's own folder).
    /// </summary>
    [Fact]
    public async Task The_preamble_describes_its_own_worktree_above_a_cap_of_one()
    {
        var worktrees = new FakeWorktrees();
        var workspaceStore = new FakeWorkspaceStore();
        var branches = new FakeBranches();
        var builder = new AgentAutopilotBuilder(
            _registry, _broker, _work, _plans,
            new FakeTrackerResolver(ItemWithPlan(), SecondItem()),
            new FakeSync(CopyPath), _sessions,
            workspaceIdFor: _ => WorkspaceId,
            worktrees: worktrees,
            workspaceStore: workspaceStore,
            branches: branches,
            delay: (_, ct) => Task.Delay(1, ct));
        var context = Context(baseBranch: "main", effectiveConcurrencyCap: 2);

        var build = builder.BuildAsync(context, Item1, "build the item");
        await WaitUntilAsync(() => _sessions.SessionFor(Item1) is not null);
        var sessionId = _sessions.SessionFor(Item1)!.Value;
        Complete(sessionId);
        await build;

        var prompt = _runner.Requests.Should().ContainSingle().Which.Prompt;
        prompt.Should().Contain("its own git worktree");
        prompt.Should().Contain("pruned once this item ends");
        prompt.Should().NotContain("the folder is returned to the branch it started from");
    }

    /// <summary>
    /// Commit review's finding: the real DI wiring resolves a project's workspace by "most recently opened",
    /// which Touch (above) skews toward whatever was registered last. A second item's own worktree must still
    /// be made from the REPOSITORY, never from the first item's own worktree, which is exactly what this
    /// fake, flawed lookup (mirroring the real one) would answer without context.RepositoryWorkspaceId.
    /// </summary>
    [Fact]
    public async Task A_second_items_worktree_is_still_made_from_the_repository_after_the_first_ones_was_touched()
    {
        var worktrees = new FakeWorktrees();
        var workspaceStore = new FakeWorkspaceStore();
        workspaceStore.Touch(WorkspaceId, "widget", "/repos/widget");
        var branches = new FakeBranches();
        var builder = new AgentAutopilotBuilder(
            _registry, _broker, _work, _plans,
            new FakeTrackerResolver(ItemWithPlan(), SecondItem()),
            new FakeSync(CopyPath), _sessions,
            // Mimics IWorkspaceStore's own "most recently opened for this project" lookup: whichever record
            // was Touched last. Without context.RepositoryWorkspaceId, item 2's own worktree would be made
            // from item 1's, since item 1's worktree is the newest thing Touched by the time item 2 starts.
            workspaceIdFor: _ => workspaceStore.Touched.LastOrDefault()?.Id,
            worktrees: worktrees,
            workspaceStore: workspaceStore,
            branches: branches,
            delay: (_, ct) => Task.Delay(1, ct));
        var context = Context(baseBranch: "main", effectiveConcurrencyCap: 2);

        var firstBuild = builder.BuildAsync(context, Item1, "build item 1");
        await WaitUntilAsync(() => _sessions.SessionFor(Item1) is not null);
        Complete(_sessions.SessionFor(Item1)!.Value);
        await firstBuild;

        var secondBuild = builder.BuildAsync(context, Item2, "build item 2");
        await WaitUntilAsync(() => _sessions.SessionFor(Item2) is not null);
        Complete(_sessions.SessionFor(Item2)!.Value);
        await secondBuild;

        worktrees.Created.Should().HaveCount(2)
            .And.OnlyContain(c => c.RepositoryWorkspaceId == WorkspaceId,
                "every item's worktree must be made from the repository, never from another item's own worktree");
    }

    /// <summary>
    /// The acceptance's own "two starts racing for the last free slot", proven through BuildAsync itself (not
    /// a race composed inline by the test), over the REAL AutopilotConcurrencyGate and LiveSessionRegistry,
    /// with a cap of 1 PER REPOSITORY grouping two different items' own worktrees together.
    /// </summary>
    [Fact]
    public async Task The_cap_holds_when_two_parallel_items_build_calls_race_for_the_last_free_slot()
    {
        var worktrees = new FakeWorktrees();
        var workspaceStore = new FakeWorkspaceStore();
        workspaceStore.Touch(WorkspaceId, "widget", "/repos/widget");
        var branches = new FakeBranches();
        var gate = new AutopilotConcurrencyGate(_registry, workspaceStore, new FixedCap(1), new AbundantMemory());
        var builder = new AgentAutopilotBuilder(
            _registry, _broker, _work, _plans,
            new FakeTrackerResolver(ItemWithPlan(), SecondItem()),
            new FakeSync(CopyPath), _sessions,
            workspaceIdFor: _ => WorkspaceId,
            concurrency: gate,
            worktrees: worktrees,
            workspaceStore: workspaceStore,
            branches: branches,
            delay: (_, ct) => Task.Delay(1, ct));
        var context = Context(baseBranch: "main", effectiveConcurrencyCap: 2);

        var firstResult = builder.BuildAsync(context, Item1, "build item 1");
        var secondResult = builder.BuildAsync(context, Item2, "build item 2");

        // Whichever call actually won the lock binds a session; the other is refused almost at once. Complete
        // the winner's turn so both calls can finish (a refused call never starts one, so completing twice
        // would find nothing the second time).
        await WaitUntilAsync(() => _sessions.SessionFor(Item1) is not null || _sessions.SessionFor(Item2) is not null);
        if (_sessions.SessionFor(Item1) is { } s1)
        {
            Complete(s1);
        }

        if (_sessions.SessionFor(Item2) is { } s2)
        {
            Complete(s2);
        }

        var results = await Task.WhenAll(firstResult, secondResult);

        results.Count(r => r.WaitingForCapacity).Should().Be(
            1, "only one of two concurrent starts may take the repository's last free slot");
        results.Count(r => !r.WaitingForCapacity).Should().Be(1);
    }

    // ---- #315: every item of a sequential run starts from the run's base branch -----------------------------------

    private AgentAutopilotBuilder SequentialBuilder(FakeItemStart itemStart, ISessionAttemptBases attemptBases) =>
        new(
            _registry, _broker, _work, _plans,
            new FakeTrackerResolver(ItemWithPlan(), SecondItem()),
            new FakeSync(CopyPath), _sessions,
            workspaceIdFor: _ => WorkspaceId,
            delay: (_, ct) => Task.Delay(1, ct),
            itemStart: itemStart,
            attemptBases: attemptBases);

    /// <summary>
    /// The live check's run (#315): item 2 of a cap-1 run started on the folder item 1 left on its own attempt
    /// branch. Now each fresh start puts the folder back on the run's base branch, the second one only after the
    /// first item's session has stopped, and tells attempt_branch to branch from that base and record it.
    /// </summary>
    [Fact]
    public async Task Each_item_of_a_sequential_run_starts_from_the_runs_base_branch_not_the_previous_items_branch()
    {
        var itemStart = new FakeItemStart(() => _registry.RunningFor(WorkspaceId)?.Id);
        var attemptBases = new InMemorySessionAttemptBases();
        var builder = SequentialBuilder(itemStart, attemptBases);
        var context = Context(baseBranch: "main");

        var first = builder.BuildAsync(context, Item1, "first item's prompt");
        var firstSession = await WaitForBoundSessionAsync();
        Complete(firstSession);
        await first;

        var second = builder.BuildAsync(context, Item2, "second item's prompt");
        await WaitUntilAsync(() => _sessions.SessionFor(Item2) is not null);
        var secondSession = _sessions.SessionFor(Item2)!.Value;
        Complete(secondSession);
        (await second).Refused.Should().BeFalse();

        itemStart.Calls.Should().Equal(("w1", "main"), ("w1", "main"));
        itemStart.RunningAtCall[1].Should().BeNull("the first item's session is stopped before the folder is switched under it");
        attemptBases.For(firstSession).Should().Be("main");
        attemptBases.For(secondSession).Should().Be("main", "the second attempt is made from the run's base, never the first item's branch");
    }

    [Fact]
    public async Task A_folder_left_unclean_between_items_refuses_the_next_item_in_words_and_starts_nothing()
    {
        var itemStart = new FakeItemStart(answers: [null, "left on fravashi/item-1-0a1b2c3d, uncommitted changes"]);
        var attemptBases = new InMemorySessionAttemptBases();
        var builder = SequentialBuilder(itemStart, attemptBases);
        var context = Context(baseBranch: "main");

        var first = builder.BuildAsync(context, Item1, "first item's prompt");
        var firstSession = await WaitForBoundSessionAsync();
        Complete(firstSession);
        await first;

        var result = await builder.BuildAsync(context, Item2, "second item's prompt");

        result.Refused.Should().BeTrue();
        result.CommitMade.Should().BeFalse();
        result.NeedsPerson.Should().BeFalse();
        result.CostUsd.Should().Be(0m);
        result.Problem.Should().Contain("#2 was not started")
            .And.Contain("left on fravashi/item-1-0a1b2c3d, uncommitted changes")
            .And.Contain("'main'");
        _runner.Requests.Should().ContainSingle("nothing is started on top of the first item's leftovers");
        _sessions.SessionFor(Item2).Should().BeNull();
    }

    [Fact]
    public async Task A_worktree_item_never_switches_the_shared_folder_or_sets_an_attempt_base()
    {
        var itemStart = new FakeItemStart();
        var attemptBases = new InMemorySessionAttemptBases();
        var builder = new AgentAutopilotBuilder(
            _registry, _broker, _work, _plans,
            new FakeTrackerResolver(ItemWithPlan(), SecondItem()),
            new FakeSync(CopyPath), _sessions,
            workspaceIdFor: _ => WorkspaceId,
            worktrees: new FakeWorktrees(),
            workspaceStore: new FakeWorkspaceStore(),
            branches: new FakeBranches(),
            delay: (_, ct) => Task.Delay(1, ct),
            itemStart: itemStart,
            attemptBases: attemptBases);

        var build = builder.BuildAsync(Context(baseBranch: "main", effectiveConcurrencyCap: 2), Item1, "build the item");
        await WaitUntilAsync(() => _sessions.SessionFor(Item1) is not null);
        var sessionId = _sessions.SessionFor(Item1)!.Value;
        Complete(sessionId);
        await build;

        itemStart.Calls.Should().BeEmpty("a worktree's own branch is already made from the run's base (#308)");
        attemptBases.For(sessionId).Should().BeNull();
    }

    [Fact]
    public async Task A_run_with_no_recorded_base_branch_starts_as_it_always_did()
    {
        var itemStart = new FakeItemStart();
        var attemptBases = new InMemorySessionAttemptBases();
        var builder = SequentialBuilder(itemStart, attemptBases);

        var build = builder.BuildAsync(Context(), Item1, "build the item");
        var sessionId = await WaitForBoundSessionAsync();
        Complete(sessionId);
        await build;

        itemStart.Calls.Should().BeEmpty();
        attemptBases.For(sessionId).Should().BeNull();
    }

    /// <summary>Records every preparation and answers from a script (null, meaning ready, when it runs out).</summary>
    private sealed class FakeItemStart(Func<AgentSessionId?>? runningNow = null, IReadOnlyList<string?>? answers = null)
        : IAutopilotItemStart
    {
        public List<(string WorkspaceId, string BaseBranch)> Calls { get; } = [];

        public List<AgentSessionId?> RunningAtCall { get; } = [];

        public Task<string?> PrepareAsync(string workspaceId, string baseBranch, CancellationToken ct = default)
        {
            RunningAtCall.Add(runningNow?.Invoke());
            Calls.Add((workspaceId, baseBranch));
            return Task.FromResult(answers is not null && Calls.Count <= answers.Count ? answers[Calls.Count - 1] : null);
        }
    }

    private sealed class FixedCap(int cap) : IAutopilotConcurrencySettings
    {
        public int Cap() => cap;
    }

    private sealed class AbundantMemory : IAutopilotMemoryAvailability
    {
        public long? AvailableBytes() => long.MaxValue;
    }

    /// <summary>
    /// The worktree is created and bound BEFORE the cap is even checked (commit review's finding): a refused
    /// start must still leave it findable, or nothing would ever prune or unregister it.
    /// </summary>
    [Fact]
    public async Task A_refused_parallel_start_still_leaves_its_worktree_findable_for_end_item_to_clean_up()
    {
        var worktrees = new FakeWorktrees();
        var workspaceStore = new FakeWorkspaceStore();
        var branches = new FakeBranches();
        var gate = new FakeConcurrencyGate("No room for another session against this repository.");
        var builder = new AgentAutopilotBuilder(
            _registry, _broker, _work, _plans,
            new FakeTrackerResolver(ItemWithPlan(), SecondItem()),
            new FakeSync(CopyPath), _sessions,
            workspaceIdFor: _ => WorkspaceId,
            concurrency: gate,
            worktrees: worktrees,
            workspaceStore: workspaceStore,
            branches: branches,
            delay: (_, ct) => Task.Delay(1, ct));
        var context = Context(baseBranch: "main", effectiveConcurrencyCap: 2);

        var result = await builder.BuildAsync(context, Item1, "build the item");

        result.WaitingForCapacity.Should().BeTrue();
        worktrees.Created.Should().ContainSingle("the worktree is created before the cap is even checked");
        workspaceStore.Touched.Should().ContainSingle(t => t.Id == "w1-worktree-1");

        await builder.EndItemAsync(context, Item1);

        worktrees.Pruned.Should().ContainSingle().Which.Should().Be(("w1", 1));
        workspaceStore.Touched.Should().BeEmpty("the refused start's own worktree is still findable and gets cleaned up");
    }

    /// <summary>
    /// A missing worktree seam on a parallel run must refuse outright, never fall back to the shared
    /// workspace (which would then stop an earlier item's own session mid-turn to make room, exactly the
    /// mixing of shared-workspace and worktree items ADR 0031 decision 15 forbids). <c>_builder</c> itself
    /// has no worktree seams wired, the same as every other test in this file that never asked for them.
    /// </summary>
    [Fact]
    public async Task BuildAsync_throws_rather_than_fall_back_to_the_shared_workspace_when_a_parallel_run_has_no_worktree_seam()
    {
        var earlierBuild = _builder.BuildAsync(Context(), Item2, "earlier item");
        await WaitUntilAsync(() => _sessions.SessionFor(Item2) is not null);
        var earlierSessionId = _sessions.SessionFor(Item2)!.Value;
        Complete(earlierSessionId);
        await earlierBuild;
        _registry.Find(earlierSessionId)!.IsRunning.Should().BeTrue("an ordinary session outlives the turn that just finished");

        var parallelContext = Context(baseBranch: "main", effectiveConcurrencyCap: 2);
        var act = () => _builder.BuildAsync(parallelContext, Item1, "build the item");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*worktree*");
        _registry.Find(earlierSessionId)!.IsRunning.Should().BeTrue(
            "the missing worktree seam refuses outright; it must never stop another item's session to share the workspace instead");
    }

    /// <summary>
    /// A dirty worktree (or any other prune refusal) is handed BACK by EndItemAsync, never only logged
    /// (commit review's finding): the engine is what folds it onto the run's own record.
    /// </summary>
    [Fact]
    public async Task EndItemAsync_hands_back_a_prune_refusal_rather_than_only_logging_it()
    {
        var worktrees = new FakeWorktrees { PruneResult = () => "the worktree has uncommitted changes, so it was left as it is." };
        var workspaceStore = new FakeWorkspaceStore();
        var branches = new FakeBranches();
        var builder = new AgentAutopilotBuilder(
            _registry, _broker, _work, _plans,
            new FakeTrackerResolver(ItemWithPlan(), SecondItem()),
            new FakeSync(CopyPath), _sessions,
            workspaceIdFor: _ => WorkspaceId,
            worktrees: worktrees,
            workspaceStore: workspaceStore,
            branches: branches,
            delay: (_, ct) => Task.Delay(1, ct));
        var context = Context(baseBranch: "main", effectiveConcurrencyCap: 2);

        var build = builder.BuildAsync(context, Item1, "build the item");
        await WaitUntilAsync(() => _sessions.SessionFor(Item1) is not null);
        Complete(_sessions.SessionFor(Item1)!.Value);
        await build;

        var result = await builder.EndItemAsync(context, Item1);

        result.Should().NotBeNull().And.Contain("uncommitted changes");
        workspaceStore.Touched.Should().ContainSingle("a refusal never drops the registration it could not confirm is gone");
    }

    private sealed class FakeWorktrees : IAutopilotWorktrees
    {
        public List<(string RepositoryWorkspaceId, int Item, string BaseBranch)> Created { get; } = [];

        public List<(string RepositoryWorkspaceId, int Item)> Pruned { get; } = [];

        public Task<AutopilotWorktree> CreateAsync(string repositoryWorkspaceId, int itemNumber, string baseBranch, CancellationToken ct = default)
        {
            Created.Add((repositoryWorkspaceId, itemNumber, baseBranch));
            return Task.FromResult(new AutopilotWorktree(
                $"{repositoryWorkspaceId}-worktree-{itemNumber}",
                $"widget.fravashi-worktrees/{itemNumber}",
                $"/repos/widget.fravashi-worktrees/{itemNumber}",
                $"fravashi/worktree/item-{itemNumber}",
                baseBranch));
        }

        /// <summary>Answered by PruneAsync: null (the default, pruned cleanly) or a refusal's own words.</summary>
        public Func<string?> PruneResult { get; set; } = () => null;

        public Task<string?> PruneAsync(string repositoryWorkspaceId, int itemNumber, CancellationToken ct = default)
        {
            Pruned.Add((repositoryWorkspaceId, itemNumber));
            return Task.FromResult(PruneResult());
        }
    }

    /// <summary>
    /// <paramref name="isSessionAlreadyRunning"/> is asked at the exact moment <see cref="Touch"/> is called,
    /// not after: that is what lets a test prove registration happened BEFORE <c>LiveSessionRegistry.Start</c>,
    /// rather than merely before the test's own later assertion got around to checking.
    /// </summary>
    private sealed class FakeWorkspaceStore(Func<string, bool>? isSessionAlreadyRunning = null) : Application.Persistence.IWorkspaceStore
    {
        public List<Application.Persistence.WorkspaceRecord> Touched { get; } = [];

        /// <summary>Whether a session was already running in the touched workspace AT THE MOMENT of the first Touch.</summary>
        public bool? SessionWasAlreadyRunningWhenTouched { get; private set; }

        public IReadOnlyList<Application.Persistence.WorkspaceRecord> Recent(int max = 10) => Touched;

        public Application.Persistence.WorkspaceRecord? Get(string id) => Touched.Find(w => w.Id == id);

        public void Touch(string id, string name, string rootPath)
        {
            SessionWasAlreadyRunningWhenTouched ??= isSessionAlreadyRunning?.Invoke(id) ?? false;
            Touched.Add(new Application.Persistence.WorkspaceRecord(id, name, rootPath, DateTimeOffset.UtcNow));
        }

        public void Remove(string id) => Touched.RemoveAll(w => w.Id == id);
    }

    private sealed class FakeBranches : ISessionBranches
    {
        public Dictionary<AgentSessionId, SessionBranch> Recorded { get; } = [];

        public SessionBranch? For(AgentSessionId session) => Recorded.TryGetValue(session, out var branch) ? branch : null;

        public void Record(AgentSessionId session, SessionBranch branch) => Recorded[session] = branch;

        public void Forget(AgentSessionId session) => Recorded.Remove(session);
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    private async Task<AgentSessionId> WaitForBoundSessionAsync(Func<AgentSessionId, bool>? isRegistered = null)
    {
        await WaitUntilAsync(() => _sessions.SessionFor(Item1) is not null);
        var id = _sessions.SessionFor(Item1)!.Value;

        // Bound is not registered: the builder binds the session before the registry's pump thread starts the
        // runner, and a broker call in between is refused as coming from no running session.
        var registered = isRegistered ?? _runner.IsRegistered;
        await WaitUntilAsync(() => registered(id));
        return id;
    }

    /// <summary>Ends the turn cleanly: no commit, no question, nothing pending.</summary>
    private void Complete(AgentSessionId sessionId) =>
        _runner.ChannelFor(sessionId).Writer.TryWrite(
            new TurnCompleted(DateTimeOffset.UtcNow, "done", IsError: false, TerminalReason: null));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(10);
        }

        condition().Should().BeTrue();
    }

    // ---- fakes ------------------------------------------------------------------------------------------------

    /// <summary>
    /// A runner that keeps one event channel per session, standing in for the CLI's own protocol. The real
    /// runner registers a session with the approval broker (hook files, the per-session secret) the moment it
    /// starts handling it; this fake does the same, so a tool call in these tests parks like a real one
    /// rather than being refused for "no running session to ask about this call".
    /// </summary>
    private sealed class ScriptedRunner : IAgentSessionRunner
    {
        private readonly ConcurrentDictionary<AgentSessionId, Channel<AgentEvent>> _channels = new();
        private readonly ConcurrentDictionary<AgentSessionId, bool> _registered = new();

        public IAgentApprovalBroker? Broker { get; set; }

        public List<AgentSessionRequest> Requests { get; } = [];

        public List<(AgentSessionId Id, string Text)> SentTurns { get; } = [];

        public Channel<AgentEvent> ChannelFor(AgentSessionId id) =>
            _channels.GetOrAdd(id, _ => Channel.CreateUnbounded<AgentEvent>());

        public IAsyncEnumerable<AgentEvent> RunAsync(AgentSessionRequest request, CancellationToken cancellationToken = default)
        {
            // Registered BEFORE the request is recorded, as the real runner registers before it starts the CLI:
            // the registry calls this on the session pump's own thread, and the tests wait on Requests (or
            // IsRegistered) before they call the broker. Recorded first, a loaded CI runner let a test's call
            // reach the broker unregistered, which refuses it outright ("no running session to ask"), so no
            // review was captured and nothing became pending (run 501, 2026-10-17).
            Broker?.RegisterSession(request.SessionId, request.WorkingDirectory, workspaceId: request.WorkspaceId);
            _registered[request.SessionId] = true;
            Requests.Add(request);
            return ChannelFor(request.SessionId).Reader.ReadAllAsync(cancellationToken);
        }

        public bool IsRegistered(AgentSessionId id) => _registered.ContainsKey(id);

        public Task<bool> SendAsync(AgentSessionId sessionId, string text, CancellationToken cancellationToken = default)
        {
            SentTurns.Add((sessionId, text));
            return Task.FromResult(_channels.ContainsKey(sessionId));
        }

        public bool Stop(AgentSessionId sessionId)
        {
            if (_channels.TryGetValue(sessionId, out var channel))
            {
                // A real CLI process reports its own ending before the pipe closes; completing the channel
                // with no terminal event would leave the session's State exactly as it was (LiveAgentSession
                // .Apply has nothing that reacts to the stream simply running out), so IsRunning would stay
                // true forever after a stop.
                channel.Writer.TryWrite(new SessionEnded(DateTimeOffset.UtcNow, AgentSessionState.Cancelled, "Stopped."));
                channel.Writer.TryComplete();
                return true;
            }

            return false;
        }

        public bool ChangeModel(AgentSessionId sessionId, string? model) => false;
    }

    /// <summary>
    /// A runner whose Stop asks nothing of the channel at all, standing in for a real CLI process that
    /// ignores (or has not yet acted on) the kill signal, so IsRunning stays true forever (#310, commit
    /// review's fifth pass).
    /// </summary>
    private sealed class StuckRunner : IAgentSessionRunner
    {
        private readonly ConcurrentDictionary<AgentSessionId, Channel<AgentEvent>> _channels = new();
        private readonly ConcurrentDictionary<AgentSessionId, bool> _registered = new();

        public IAgentApprovalBroker? Broker { get; set; }

        public Channel<AgentEvent> ChannelFor(AgentSessionId id) =>
            _channels.GetOrAdd(id, _ => Channel.CreateUnbounded<AgentEvent>());

        public IAsyncEnumerable<AgentEvent> RunAsync(AgentSessionRequest request, CancellationToken cancellationToken = default)
        {
            Broker?.RegisterSession(request.SessionId, request.WorkingDirectory, workspaceId: request.WorkspaceId);
            _registered[request.SessionId] = true;
            return ChannelFor(request.SessionId).Reader.ReadAllAsync(cancellationToken);
        }

        public bool IsRegistered(AgentSessionId id) => _registered.ContainsKey(id);

        public Task<bool> SendAsync(AgentSessionId sessionId, string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(_channels.ContainsKey(sessionId));

        public bool Stop(AgentSessionId sessionId) => _channels.ContainsKey(sessionId);

        public bool ChangeModel(AgentSessionId sessionId, string? model) => false;
    }

    /// <summary>
    /// A gate that always answers the same way, counts how many times it was asked, and records the
    /// dispatchCapOverride each call carried (#307), so a test can prove the override on a context actually
    /// REACHES the gate rather than only riding on a saved run (AutopilotDispatcherTests' own concern).
    /// </summary>
    private sealed class FakeConcurrencyGate(string? refusal) : IAutopilotConcurrencyGate
    {
        private readonly SemaphoreSlim _lock = new(1, 1);

        public int Calls { get; private set; }

        public List<int?> ReceivedOverrides { get; } = [];

        public string? Refuse(string workspaceId, int? dispatchCapOverride)
        {
            Calls++;
            ReceivedOverrides.Add(dispatchCapOverride);
            return refusal;
        }

        public async Task<IDisposable> LockAsync(string workspaceId, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            return new Releaser(_lock);
        }

        private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
        {
            public void Dispose() => semaphore.Release();
        }
    }

    /// <summary>A commit reviewer that always answers the same way, for the Failed-review test.</summary>
    private sealed class FixedReviewer(CommitReview review) : ICommitReviewer
    {
        public CommitReviewerRole Role => review.Role;

        public Task<CommitReview> ReviewAsync(AgentSessionId builder, CommitReviewRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(review);
    }

    /// <summary>A commit reviewer that answers each call from a script in order, repeating its last answer once exhausted.</summary>
    private sealed class SequencedReviewer(params CommitReview[] reviews) : ICommitReviewer
    {
        private int _next;

        public CommitReviewerRole Role => reviews[0].Role;

        public Task<CommitReview> ReviewAsync(AgentSessionId builder, CommitReviewRequest request, CancellationToken cancellationToken = default)
        {
            var review = reviews[Math.Min(_next, reviews.Length - 1)];
            _next++;
            return Task.FromResult(review);
        }
    }

    /// <summary>
    /// Already ready the instant it is asked: every test in this file but the #328 ones below never cares
    /// how the copy got there, only that the builder reads it as ready at once.
    /// </summary>
    private sealed class FakeSync(string copyPath) : Application.AgentSync.IWorkspaceAgentSync
    {
        public void EnsureStarted(string workspaceId)
        {
        }

        public string? CopyPathFor(string workspaceId) => copyPath;

        public object? StatusFor(string workspaceId) =>
            new WorkspaceSyncStatus(workspaceId, copyPath, true, 0, 0, 0, null);
    }

    /// <summary>
    /// #328: a copy that answers <c>CopyPathFor</c> at once (as the real engine's placeholder session does) but
    /// is not ready until a test says so with <see cref="SetReady"/>, or never, or has failed to start
    /// (<see cref="Fail"/>, which leaves <c>CopyPathFor</c> answering the path, the state the real engine holds
    /// only for the instant between recording the failure and removing the session; <see cref="FailBeforeStart"/>,
    /// which makes <c>CopyPathFor</c> answer null from then on, the state <c>WorkspaceAgentSync</c> actually
    /// settles into once it has removed that session) - standing in for <c>WorkspaceAgentSync</c>'s own
    /// background pull and its <c>StartFailureFor</c>.
    /// </summary>
    private sealed class SlowSync(string copyPath) : Application.AgentSync.IWorkspaceAgentSync
    {
        private readonly ConcurrentDictionary<string, byte> _ready = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _failures = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> _noPathOnceFailed = new(StringComparer.Ordinal);

        public void EnsureStarted(string workspaceId)
        {
        }

        public string? CopyPathFor(string workspaceId) => _noPathOnceFailed.ContainsKey(workspaceId) ? null : copyPath;

        public object? StatusFor(string workspaceId) =>
            new WorkspaceSyncStatus(workspaceId, copyPath, _ready.ContainsKey(workspaceId), 0, 0, 0, null);

        public string? StartFailureFor(string workspaceId) =>
            _failures.TryGetValue(workspaceId, out var reason) ? reason : null;

        public void SetReady(string workspaceId) => _ready[workspaceId] = 0;

        public void FailBeforeStart(string workspaceId, string reason)
        {
            _failures[workspaceId] = reason;
            _noPathOnceFailed[workspaceId] = 0;
        }

        public void Fail(string workspaceId, string reason) => _failures[workspaceId] = reason;
    }

    private sealed class FakeWork : ISessionWorkItems
    {
        public List<(AgentSessionId Session, WorkItemId Item, ProjectId Project, string LinkedBy)> Links { get; } = [];

#pragma warning disable CS0067 // never raised: nothing in these tests reads this surface
        public event Action<AgentSessionId>? Changed;
#pragma warning restore CS0067

        public bool HasWork(AgentSessionId session) => Links.Any(l => l.Session == session);

        public IReadOnlyList<SessionWorkItem> For(AgentSessionId session) =>
            [.. Links.Where(l => l.Session == session).Select(l => new SessionWorkItem(l.Item, l.Project, DateTimeOffset.UtcNow, l.LinkedBy))];

        public void Link(AgentSessionId session, WorkItemId item, ProjectId project, string linkedBy)
        {
            if (!Links.Any(l => l.Session == session && l.Item == item))
            {
                Links.Add((session, item, project, linkedBy));
            }
        }

        public void Unlink(AgentSessionId session, WorkItemId item) => Links.RemoveAll(l => l.Session == session && l.Item == item);

        public void Forget(AgentSessionId session) => Links.RemoveAll(l => l.Session == session);
    }

    private sealed class FakePlans : ISessionPlans
    {
        public List<(AgentSessionId Session, string WikiItemId, string LinkedBy)> Links { get; } = [];

#pragma warning disable CS0067 // never raised: nothing in these tests reads this surface
        public event Action<AgentSessionId>? Changed;
#pragma warning restore CS0067

        public bool HasPlan(AgentSessionId session) => Links.Any(l => l.Session == session);

        public IReadOnlyList<SessionPlan> For(AgentSessionId session) =>
            [.. Links.Where(l => l.Session == session).Select(l => new SessionPlan(l.WikiItemId, DateTimeOffset.UtcNow, l.LinkedBy))];

        public void Link(AgentSessionId session, string wikiItemId, string linkedBy)
        {
            if (!Links.Any(l => l.Session == session && l.WikiItemId == wikiItemId))
            {
                Links.Add((session, wikiItemId, linkedBy));
            }
        }

        public void Unlink(AgentSessionId session, string wikiItemId) =>
            Links.RemoveAll(l => l.Session == session && l.WikiItemId == wikiItemId);

        public void Forget(AgentSessionId session) => Links.RemoveAll(l => l.Session == session);
    }

    /// <summary>A tracker whose only implemented operation this builder calls: reading one item.</summary>
    private sealed class FakeTracker(params WorkItem[] items) : ITracker
    {
        public ProjectId Project => AgentAutopilotBuilderTests.Project;

        public string ProviderName => "local";

        public TrackerCapabilities Capabilities => TrackerCapabilities.ReadOnly;

        public Task<TrackerResult<WorkItem>> CreateAsync(NewWorkItem newItem, MutationContext context, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default) =>
            Task.FromResult(items.FirstOrDefault(i => i.Id == id));

        public Task<WorkItem?> GetByNumberAsync(int number, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<WorkItemPage> QueryAsync(WorkItemQuery query, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<WorkItemEvent>> HistoryAsync(WorkItemId id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<WorkItemLink>> LinksAsync(WorkItemId id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TrackerResult<WorkItem>> UpdateAsync(WorkItemId id, WorkItemPatch patch, long expectedVersion, MutationContext context, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TrackerResult<WorkItem>> TransitionAsync(WorkItemId id, WorkItemStatus to, WorkItemResolution? resolution, WorkItemId? after, WorkItemId? before, long expectedVersion, MutationContext context, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TrackerResult<WorkItem>> MoveAsync(WorkItemId id, WorkItemId? after, WorkItemId? before, long expectedVersion, MutationContext context, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TrackerResult<WorkItemLink>> LinkAsync(WorkItemId from, WorkItemLinkType type, WorkItemId to, MutationContext context, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TrackerResult<bool>> UnlinkAsync(WorkItemId from, WorkItemLinkType type, WorkItemId to, MutationContext context, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TrackerResult<EvidenceRecord>> RecordEvidenceAsync(WorkItemId id, NewEvidence evidence, MutationContext context, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<EvidenceRecord>> EvidenceAsync(WorkItemId id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<StartabilityReport> WhatCanStartNowAsync(StartableQuery query, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeTrackerResolver(params WorkItem[] items) : ITrackerResolver
    {
        private readonly ITracker _tracker = new FakeTracker(items);

        public ITracker For(ProjectId project) => _tracker;
    }
}
