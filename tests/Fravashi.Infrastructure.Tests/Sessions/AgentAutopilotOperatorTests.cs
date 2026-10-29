using System.Runtime.CompilerServices;
using System.Threading.Channels;
using AwesomeAssertions;
using Fravashi.Application.Autopilot;
using Fravashi.Application.Sessions;
using Fravashi.Application.Wiki;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Autopilot;
using Fravashi.Domain.Projects;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;
using Fravashi.Infrastructure.Sessions;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Backlog 300 (ADR 0031 decision 10): the autopilot operator, a read-only advising conversation for the
/// engine's three judgement steps, following <c>AgentCommitReviewer</c>'s own pattern. Driven here with a
/// fake <see cref="IAgentSessionRunner"/> rather than a real CLI child process, which is what the acceptance
/// asks for ("unit tests with a fake runtime").
/// </summary>
public sealed class AgentAutopilotOperatorTests
{
    private static readonly WorkItemId ItemId = new("item-1");
    private static readonly ProjectId Project = ProjectId.Default;
    private static readonly AutopilotRole Role = new(AgentTool.Claude, "claude-sonnet-5");
    private static readonly AutopilotOperatorContext Context = new(Project, Role);

    private readonly ScriptedRunner _runner = new();
    private readonly IAgentApprovalBroker _broker = Substitute.For<IAgentApprovalBroker>();
    private readonly ISessionStore _store = Substitute.For<ISessionStore>();
    private readonly ITracker _tracker = Substitute.For<ITracker>();
    private readonly ITrackerResolver _trackers = Substitute.For<ITrackerResolver>();
    private readonly IWikiDocuments _wiki = Substitute.For<IWikiDocuments>();

    public AgentAutopilotOperatorTests()
    {
        _trackers.For(Project).Returns(_tracker);
        _tracker.GetAsync(ItemId, Arg.Any<CancellationToken>()).Returns(Task.FromResult<WorkItem?>(Item()));
        _broker.RegisterSession(Arg.Any<AgentSessionId>()).Returns("secret");
    }

    private AgentAutopilotOperator Operator(TimeSpan? timeout = null, long? maxTokens = null, IAgentApprovalBroker? broker = null) => new(
        tool => tool == AgentTool.Claude ? _runner : null,
        broker ?? _broker,
        _store,
        _trackers,
        _wiki,
        workspaceIdFor: _ => "w1",
        timeout: timeout,
        maxTokens: maxTokens,
        copyFor: _ => "/workspaces/w1");

    private static WorkItem Item() =>
        WorkItem.Restore(new WorkItemState
        {
            Id = ItemId,
            Project = Project,
            Number = 42,
            Level = WorkItemLevel.Task,
            Title = "Do the thing",
            Description = "Make the widget spin.",
            Status = WorkItemStatus.InProgress,
            AcceptanceCriteria = [new AcceptanceCriterion("ac-1", "The widget spins.")],
        }).Value!;

    // ---- the three steps, each a valid typed answer -----------------------------------------------

    [Fact]
    public async Task Writing_the_prompt_reads_back_an_answer_document_as_the_prompt_text()
    {
        _runner.NextStats = Stats(0.42m);
        _runner.NextTurn = new TurnCompleted(
            DateTimeOffset.UtcNow, "ignored", IsError: false, "completed",
            Document: new AgentTurnDocument("1", "answer", "Build the spinning widget.", AgentTurnDocumentKind.Answer));

        var result = await Operator().WritePromptAsync(Context, ItemId);

        result.Text.Should().Be("Build the spinning widget.");
        result.CostUsd.Should().Be(0.42m);
        _broker.Received(1).HoldToReview(Arg.Any<AgentSessionId>());
        _runner.LastRequest!.Model.Should().Be("claude-sonnet-5");
        _runner.LastRequest.DocumentKinds.Should().Equal(AgentTurnDocumentKind.Answer);
    }

    /// <summary>
    /// Work item #335: git_diff, git_show, work_item_read and wiki_read are marked to load upfront
    /// (StudioToolServer.AlwaysLoadedTools), but whether the marker takes under a reading conversation's own
    /// --tools list against the studio's real server has not been driven live (only a stdio probe has), so the
    /// three prompts say they are marked rather than assert they load without a round trip, and still name
    /// ToolSearch as the fallback. Covers all three of the operator's prompts.
    /// </summary>
    [Fact]
    public async Task The_operators_three_prompts_no_longer_assert_its_studio_tools_load_without_a_fallback()
    {
        _runner.NextTurn = new TurnCompleted(
            DateTimeOffset.UtcNow, "ignored", IsError: false, "completed",
            Document: new AgentTurnDocument("1", "answer", "Build it.", AgentTurnDocumentKind.Answer));
        await Operator().WritePromptAsync(Context, ItemId);
        _runner.LastRequest!.Prompt.Should().NotContain("load them with ToolSearch")
            .And.Contain("marked to load upfront").And.Contain("load it with ToolSearch first");

        _runner.NextTurn = new TurnCompleted(
            DateTimeOffset.UtcNow, "ignored", IsError: false, "completed",
            Document: new AgentTurnDocument(
                "1", "advice", "Retry.", AgentTurnDocumentKind.Advice,
                PayloadJson: "{\"retry\":true,\"reason\":\"ok\"}"));
        await Operator().AdviseOnFailureAsync(Context, ItemId, "the build timed out");
        _runner.LastRequest!.Prompt.Should().NotContain("load them with ToolSearch")
            .And.Contain("marked to load upfront").And.Contain("load it with ToolSearch first");

        _runner.NextTurn = new TurnCompleted(
            DateTimeOffset.UtcNow, "ignored", IsError: false, "completed",
            Document: new AgentTurnDocument(
                "1", "follow_up_drafts", "None.", AgentTurnDocumentKind.FollowUpDrafts, PayloadJson: "[]"));
        await Operator().DraftFollowUpsAsync(Context, ItemId, "no findings");
        _runner.LastRequest!.Prompt.Should().NotContain("load them with ToolSearch")
            .And.Contain("marked to load upfront").And.Contain("load it with ToolSearch first");
    }

    // ---- backlog 331: the operator's own copy root never reaches the building prompt -----------------

    [Fact]
    public async Task A_prompt_naming_the_operators_own_copy_root_reaches_the_builder_with_that_root_rewritten()
    {
        // "Operator()" wires workspaceIdFor to "w1" and copyFor to "/workspaces/w1", so this IS the folder
        // this conversation ran in: exactly the root #331 says must never reach the builder as written.
        _runner.NextStats = Stats(0.42m);
        _runner.NextTurn = new TurnCompleted(
            DateTimeOffset.UtcNow, "ignored", IsError: false, "completed",
            Document: new AgentTurnDocument(
                "1", "answer",
                "Run `chmod +x /workspaces/w1/scripts/hello.sh` and start from /workspaces/w1 itself.",
                AgentTurnDocumentKind.Answer));

        var result = await Operator().WritePromptAsync(Context, ItemId);

        result.Text.Should().Be("Run `chmod +x scripts/hello.sh` and start from . itself.");
        result.Text.Should().NotContain("/workspaces/w1");
    }

    [Fact]
    public async Task A_sibling_path_that_only_shares_a_prefix_with_the_copy_root_is_left_alone()
    {
        // The same case #324's own rewrite (before the autopilot's judge) is tested against: a hyphen is a
        // legal folder-name character, so "/workspaces/w1-old" is a different, unrelated folder, not the
        // copy itself, and must not be read as though it were.
        _runner.NextTurn = new TurnCompleted(
            DateTimeOffset.UtcNow, "ignored", IsError: false, "completed",
            Document: new AgentTurnDocument(
                "1", "answer", "Compare against /workspaces/w1-old/run.sh.", AgentTurnDocumentKind.Answer));

        var result = await Operator().WritePromptAsync(Context, ItemId);

        result.Text.Should().Be("Compare against /workspaces/w1-old/run.sh.");
    }

    [Fact]
    public async Task A_bare_root_at_the_end_of_a_sentence_is_rewritten_without_fusing_into_a_parent_reference()
    {
        // The dot ending the sentence must not be read as part of a longer folder name ("w1.bak" would be
        // left alone), and the replacement must not fuse with that dot into "..", a parent-directory
        // reference rather than the bare root this is meant to be.
        _runner.NextTurn = new TurnCompleted(
            DateTimeOffset.UtcNow, "ignored", IsError: false, "completed",
            Document: new AgentTurnDocument(
                "1", "answer", "Run everything from /workspaces/w1.", AgentTurnDocumentKind.Answer));

        var result = await Operator().WritePromptAsync(Context, ItemId);

        result.Text.Should().Be("Run everything from the repository root.");
    }

    [Fact]
    public async Task A_bare_root_with_a_trailing_slash_is_rewritten_to_a_dot_rather_than_vanishing()
    {
        _runner.NextTurn = new TurnCompleted(
            DateTimeOffset.UtcNow, "ignored", IsError: false, "completed",
            Document: new AgentTurnDocument(
                "1", "answer", "cd /workspaces/w1/ then build.", AgentTurnDocumentKind.Answer));

        var result = await Operator().WritePromptAsync(Context, ItemId);

        result.Text.Should().Be("cd . then build.");
    }

    [Fact]
    public async Task A_prompt_that_never_names_the_copy_root_is_returned_unchanged()
    {
        _runner.NextTurn = new TurnCompleted(
            DateTimeOffset.UtcNow, "ignored", IsError: false, "completed",
            Document: new AgentTurnDocument(
                "1", "answer", "Add scripts/hello.sh and make it executable.", AgentTurnDocumentKind.Answer));

        var result = await Operator().WritePromptAsync(Context, ItemId);

        result.Text.Should().Be("Add scripts/hello.sh and make it executable.");
    }

    [Fact]
    public async Task Advising_on_a_failure_reads_back_an_advice_document()
    {
        _runner.NextStats = Stats(0.10m);
        _runner.NextTurn = new TurnCompleted(
            DateTimeOffset.UtcNow, "ignored", IsError: false, "completed",
            Document: new AgentTurnDocument(
                "1", "advice", "Retry once the flaky test is quarantined.", AgentTurnDocumentKind.Advice,
                PayloadJson: "{\"retry\":true,\"reason\":\"a flaky test, not the change\"}"));

        var result = await Operator().AdviseOnFailureAsync(Context, ItemId, "the build timed out");

        result.RetryBuild.Should().BeTrue();
        result.Reason.Should().Be("a flaky test, not the change");
        result.CostUsd.Should().Be(0.10m);
        _runner.LastRequest!.Prompt.Should().Contain("UNTRUSTED").And.Contain("the build timed out");
        _runner.LastRequest.DocumentKinds.Should().Equal(AgentTurnDocumentKind.Advice);
    }

    [Fact]
    public async Task Drafting_follow_ups_reads_back_a_follow_up_drafts_document()
    {
        _runner.NextStats = Stats(0.07m);
        _runner.NextTurn = new TurnCompleted(
            DateTimeOffset.UtcNow, "ignored", IsError: false, "completed",
            Document: new AgentTurnDocument(
                "1", "follow_up_drafts", "One follow-up found.", AgentTurnDocumentKind.FollowUpDrafts,
                PayloadJson: "[{\"title\":\"Fix the flaky test\",\"description\":\"It fails one run in twenty.\","
                    + "\"acceptance\":\"The test passes fifty runs in a row.\"}]"));

        var result = await Operator().DraftFollowUpsAsync(Context, ItemId, "the scenario failed twice");

        result.Drafts.Should().ContainSingle();
        result.Drafts[0].Title.Should().Be("Fix the flaky test");
        result.Drafts[0].Description.Should().Be("It fails one run in twenty.");
        result.Drafts[0].AcceptanceCriteria.Should().Equal("The test passes fifty runs in a row.");
        result.CostUsd.Should().Be(0.07m);
        _runner.LastRequest!.Prompt.Should().Contain("UNTRUSTED").And.Contain("the scenario failed twice");
    }

    // ---- a malformed answer, no answer, a timeout -------------------------------------------------

    [Fact]
    public async Task A_document_of_the_wrong_kind_is_a_malformed_answer_and_throws()
    {
        _runner.NextTurn = new TurnCompleted(
            DateTimeOffset.UtcNow, "ignored", IsError: false, "completed",
            Document: new AgentTurnDocument(
                "1", "findings", "Wrong kind entirely.", AgentTurnDocumentKind.Findings, PayloadJson: "[]"));

        var act = () => Operator().AdviseOnFailureAsync(Context, ItemId, "no commit yet");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not 'advice'*");
    }

    [Fact]
    public async Task A_degraded_document_is_a_malformed_answer_and_throws()
    {
        _runner.NextTurn = new TurnCompleted(
            DateTimeOffset.UtcNow, "ignored", IsError: false, "completed",
            Document: new AgentTurnDocument(
                "1", "advice", "A later library's kind.", Degradation: AgentTurnDocumentDegradation.UnknownKind));

        var act = () => Operator().AdviseOnFailureAsync(Context, ItemId, "no commit yet");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*degraded*");
    }

    [Fact]
    public async Task An_empty_answer_is_treated_as_a_missing_one_and_throws()
    {
        _runner.NextTurn = new TurnCompleted(
            DateTimeOffset.UtcNow, "ignored", IsError: false, "completed",
            Document: new AgentTurnDocument("1", "answer", "   ", AgentTurnDocumentKind.Answer));

        var act = () => Operator().WritePromptAsync(Context, ItemId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*was empty*");
    }

    [Fact]
    public async Task No_document_at_all_throws_naming_the_reason()
    {
        _runner.NextTurn = new TurnCompleted(DateTimeOffset.UtcNow, null, IsError: true, "error", ErrorText: "the model gave up");

        var act = () => Operator().WritePromptAsync(Context, ItemId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*the model gave up*");
    }

    [Fact]
    public async Task No_answer_and_no_turn_at_all_still_throws()
    {
        _runner.EndInstead = new SessionEnded(DateTimeOffset.UtcNow, AgentSessionState.Failed, "the process crashed");

        var act = () => Operator().WritePromptAsync(Context, ItemId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*the process crashed*");
    }

    [Fact]
    public async Task A_runner_that_throws_is_caught_and_leaves_the_conversation_recorded_as_failed_not_running()
    {
        _runner.ThrowInstead = new InvalidOperationException("the agent process crashed");

        var act = () => Operator().WritePromptAsync(Context, ItemId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*the agent process crashed*");
        _store.Received(1).Save(Arg.Is<SessionRecord>(r => r.State == AgentSessionState.Failed));
    }

    [Fact]
    public async Task A_step_that_runs_past_its_bound_times_out_and_throws()
    {
        // The runner never completes the turn, so the operator's own short timeout is what ends the call.
        var act = () => Operator(timeout: TimeSpan.FromMilliseconds(20)).WritePromptAsync(Context, ItemId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*bound*");
    }

    [Fact]
    public async Task A_step_that_spends_past_its_token_bound_stops_and_throws_naming_the_bound()
    {
        // The stats update alone crosses the bound; the operator breaks out before a turn ever arrives, the
        // same way a real run that kept talking past its budget would be stopped mid-turn.
        _runner.NextStats = Stats(cost: 0m) with { InputTokens = 1_000, OutputTokens = 0 };

        var act = () => Operator(maxTokens: 500).WritePromptAsync(Context, ItemId);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*token bound*");
    }

    [Fact]
    public async Task An_advice_document_with_no_payload_throws_naming_the_payload_not_the_kind()
    {
        _runner.NextTurn = new TurnCompleted(
            DateTimeOffset.UtcNow, "ignored", IsError: false, "completed",
            Document: new AgentTurnDocument("1", "advice", "No payload at all.", AgentTurnDocumentKind.Advice));

        var act = () => Operator().AdviseOnFailureAsync(Context, ItemId, "no commit yet");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("carried no payload").And.NotContain("not 'advice'");
    }

    [Fact]
    public async Task An_advice_document_whose_payload_names_no_boolean_retry_throws()
    {
        _runner.NextTurn = new TurnCompleted(
            DateTimeOffset.UtcNow, "ignored", IsError: false, "completed",
            Document: new AgentTurnDocument(
                "1", "advice", "A reason with no decision.", AgentTurnDocumentKind.Advice,
                PayloadJson: "{\"reason\":\"not sure\"}"));

        var act = () => Operator().AdviseOnFailureAsync(Context, ItemId, "no commit yet");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*retry*");
    }

    [Fact]
    public async Task A_follow_up_drafts_payload_with_a_titleless_entry_throws_rather_than_yielding_fewer_drafts()
    {
        // The schema requires "title" on every entry, so one without it is not "nothing to draft" there: it is
        // the malformed document ADR 0031 decision 10 calls a stop, never a guess silently narrowed down.
        _runner.NextTurn = new TurnCompleted(
            DateTimeOffset.UtcNow, "ignored", IsError: false, "completed",
            Document: new AgentTurnDocument(
                "1", "follow_up_drafts", "One entry, no title.", AgentTurnDocumentKind.FollowUpDrafts,
                PayloadJson: "[{\"description\":\"x\"}]"));

        var act = () => Operator().DraftFollowUpsAsync(Context, ItemId, "no findings worth drafting");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*title*");
    }

    [Fact]
    public async Task A_follow_up_drafts_document_with_no_payload_throws_naming_the_payload_not_the_kind()
    {
        _runner.NextTurn = new TurnCompleted(
            DateTimeOffset.UtcNow, "ignored", IsError: false, "completed",
            Document: new AgentTurnDocument("1", "follow_up_drafts", "Nothing drafted.", AgentTurnDocumentKind.FollowUpDrafts));

        var act = () => Operator().DraftFollowUpsAsync(Context, ItemId, "no findings worth drafting");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("carried no payload").And.NotContain("not 'follow_up_drafts'");
    }

    // ---- the tool list is read-only -----------------------------------------------------------------

    [Fact]
    public async Task The_session_is_started_with_exactly_the_commit_reviewers_read_only_tool_list()
    {
        _runner.NextTurn = new TurnCompleted(
            DateTimeOffset.UtcNow, "ignored", IsError: false, "completed",
            Document: new AgentTurnDocument("1", "answer", "ok", AgentTurnDocumentKind.Answer));

        await Operator().WritePromptAsync(Context, ItemId);

        _runner.LastRequest!.BuiltInTools.Should().BeEquivalentTo(AgentReviewTools.BuiltIn);
        _runner.LastRequest.BuiltInTools.Should().NotContain(["Write", "Edit", "MultiEdit", "NotebookEdit", "Bash"]);
    }

    [Fact]
    public async Task The_operators_session_is_refused_bash_edit_and_write_while_it_runs_and_no_card_is_raised()
    {
        // A REAL broker, not a substitute (the commit reviewer's own held-session gate), probed WHILE the
        // operator's conversation is still running (the fake runner's stream itself does the probing, which
        // is the only moment HoldToReview is actually in effect before ReleaseSession lifts it).
        using var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(5));
        var asked = 0;
        broker.Requested += _ => asked++;
        var decisions = new List<ToolApprovalDecision>();
        _runner.DuringRun = async request =>
        {
            // Registered the way the real runner would (the same shape CommitReviewTests uses): without this,
            // Bash/Edit/Write would be refused anyway at the broker's "no running session" branch, which would
            // pass this test even if HoldToReview were never called.
            broker.RegisterSession(request.SessionId, "/workspaces/w1", typedTurns: true, workspaceId: "w1");
            decisions.Add(await broker.RequestAsync(request.SessionId, "Bash", "t1", AgentToolInput.Empty));
            decisions.Add(await broker.RequestAsync(request.SessionId, "Edit", "t2", AgentToolInput.Empty));
            decisions.Add(await broker.RequestAsync(request.SessionId, "Write", "t3", AgentToolInput.Empty));
        };
        _runner.NextTurn = new TurnCompleted(
            DateTimeOffset.UtcNow, "ignored", IsError: false, "completed",
            Document: new AgentTurnDocument("1", "answer", "ok", AgentTurnDocumentKind.Answer));

        await Operator(broker: broker).WritePromptAsync(Context, ItemId);

        decisions.Should().HaveCount(3);
        decisions.Should().AllSatisfy(d =>
        {
            d.Approved.Should().BeFalse();
            // Attributable to the HoldToReview gate specifically, not to the session being unregistered or
            // unknown, which would refuse for an unrelated reason and pass this test for the wrong cause.
            d.Reason.Should().Contain("refused without asking anyone");
        });
        asked.Should().Be(0, "a refusal the broker imposes on its own account never puts a card in front of anyone");
    }

    private static AgentStatsSnapshot Stats(decimal cost) => new(1, 1, 0, cost, 0, 0, 1);

    // ---- a fake runtime, scripted with the one turn the test wants --------------------------------

    private sealed class ScriptedRunner : IAgentSessionRunner
    {
        public AgentSessionRequest? LastRequest { get; private set; }

        public AgentStatsSnapshot? NextStats { get; set; }

        public TurnCompleted? NextTurn { get; set; }

        public SessionEnded? EndInstead { get; set; }

        public Exception? ThrowInstead { get; set; }

        /// <summary>
        /// Run once, right as the stream starts (so the operator has already held the session and is awaiting
        /// this call), for a test that needs to probe the broker WHILE the hold is still in effect.
        /// </summary>
        public Func<AgentSessionRequest, Task>? DuringRun { get; set; }

        public IAsyncEnumerable<AgentEvent> RunAsync(AgentSessionRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Stream(request, cancellationToken);
        }

        private async IAsyncEnumerable<AgentEvent> Stream(
            AgentSessionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (DuringRun is { } probe)
            {
                await probe(request).ConfigureAwait(false);
            }

            if (ThrowInstead is { } failure)
            {
                throw failure;
            }

            if (NextStats is { } stats)
            {
                // Yielded before the turn (or alone, for the over-token test, which never reaches a turn at
                // all: the operator breaks out of its own loop the moment this one crosses its bound).
                yield return new StatsUpdated(DateTimeOffset.UtcNow, stats);
            }

            if (NextTurn is { } turn)
            {
                yield return turn;
                yield break;
            }

            if (EndInstead is { } ended)
            {
                yield return ended;
                yield break;
            }

            // No script at all: block until the operator's own timeout cancels this call, the same way a real
            // CLI child that never answers would.
            var never = Channel.CreateUnbounded<AgentEvent>();
            await foreach (var e in never.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return e;
            }
        }

        public Task<bool> SendAsync(AgentSessionId sessionId, string text, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public bool Stop(AgentSessionId sessionId) => false;

        public bool ChangeModel(AgentSessionId sessionId, string? model) => true;
    }
}
