// RETIRED to v2. Decided, 2026-09-22: Codex and Copilot are not part of this version and support for
// them is a v2 feature. The decision, everything that was measured about these two CLIs, and the
// read-containment question it parks are all in docs/v2.md, section 1 ("Codex and Copilot runtimes").
//
// The whole file is disabled rather than deleted, behind a symbol nothing defines. It is kept because the
// protocol it speaks was read off the pinned binary's own generated schema and bundled code (docs/v2.md
// Appendix B), and because a live Codex turn proved part of it against a real signed-in CLI. Re-deriving
// either in v2 would cost far more than carrying inert text here.
//
// Before restoring any of it, read docs/v2.md section 1.2 (the uid question, which is what stands in front of
// offering these runtimes at all) and 1.5 (a real, unfixed defect in CodexSessionRunner).
#if V2_CODEX_COPILOT

using System.Text.Json;
using AwesomeAssertions;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Sessions;
using NSubstitute;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// Backlog 66. The Codex runner driven against a REAL child process speaking the app-server protocol
/// (<c>FakeCli/fake-codex-app-server.js</c>), whose messages are constructed from the schema the pinned codex
/// 0.146.0 generates about itself. What the runner ANSWERED is read back from the messages the stand-in logged,
/// never from what the test hoped it sent.
/// </summary>
public class CodexSessionRunnerTests : IDisposable
{
    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "fravashi-codex-runner-" + Guid.NewGuid().ToString("N"));

    private readonly string _workspaceCopy;
    private readonly string _reportPath;
    private readonly AgentApprovalBroker _broker = new(TimeSpan.FromSeconds(5));

    public CodexSessionRunnerTests()
    {
        _workspaceCopy = Path.Combine(_scratch, "workspaces", "w1");
        _reportPath = Path.Combine(_scratch, "codex-report.json");
        Directory.CreateDirectory(_workspaceCopy);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _broker.Dispose();
        try
        {
            if (Directory.Exists(_scratch))
            {
                Directory.Delete(_scratch, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ---- harness ------------------------------------------------------------------------

    private static string FakeServer() =>
        Path.Combine(AppContext.BaseDirectory, "Sessions", "FakeCli", "fake-codex-app-server.js");

    private CodexSessionRunner Runner(
        string mode = "command",
        int exitAfter = 1,
        IAgentApprovalBroker? broker = null,
        IAgentRuntimeCredentialStore? credentials = null,
        bool missingExecutable = false) =>
        new(
            broker ?? _broker,
            credentials: credentials,
            launcher: missingExecutable
                ? new AgentCliLauncher("fravashi-no-such-codex-on-this-machine", [])
                : new AgentCliLauncher("node", [FakeServer()]),
            environment: new Dictionary<string, string>
            {
                ["FAKE_CLI_REPORT"] = _reportPath,
                ["FAKE_CLI_MODE"] = mode,
                ["FAKE_CLI_EXIT_AFTER"] = exitAfter.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });

    private AgentSessionRequest Request(string prompt = "hello", string? resume = null, string? model = null, decimal? budget = null) =>
        new(AgentSessionId.New(), "w1", _workspaceCopy, prompt, resume, model, MaxBudgetUsd: budget);

    /// <summary>Answers every parked approval the same way, as the user.</summary>
    private void AnswerEveryApproval(bool approved) =>
        _broker.Requested += request => _broker.Answer(request.RequestId, approved, approved ? "ok" : "no");

    private static async Task<List<AgentEvent>> DrainAsync(IAsyncEnumerable<AgentEvent> events)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var collected = new List<AgentEvent>();
        await foreach (var e in events.WithCancellation(timeout.Token))
        {
            collected.Add(e);
        }

        return collected;
    }

    private string[] Args()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(_reportPath));
        return [.. document.RootElement.GetProperty("args").EnumerateArray().Select(a => a.GetString()!)];
    }

    private IReadOnlyDictionary<string, string> Env()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(_reportPath));
        return document.RootElement.GetProperty("env").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty, StringComparer.OrdinalIgnoreCase);
    }

    private string Cwd()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(_reportPath));
        return document.RootElement.GetProperty("cwd").GetString()!;
    }

    /// <summary>Every message the runner sent the stand-in, in order.</summary>
    private List<JsonElement> Sent()
    {
        var path = _reportPath + ".messages.jsonl";
        File.Exists(path).Should().BeTrue("the runner should have spoken to the CLI");
        return [.. File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement.Clone())];
    }

    private List<JsonElement> Requests(string method) =>
        [.. Sent().Where(m => m.TryGetProperty("method", out var name) && name.GetString() == method)];

    /// <summary>The runner's answer to the stand-in's server request with that id.</summary>
    private JsonElement AnswerTo(string serverRequestId) =>
        Sent().Single(m => !m.TryGetProperty("method", out _)
            && m.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.String
            && id.GetString() == serverRequestId);

    private static string? Decision(JsonElement answer) =>
        answer.TryGetProperty("result", out var result) && result.TryGetProperty("decision", out var decision)
            && decision.ValueKind == JsonValueKind.String
            ? decision.GetString()
            : null;

    // ---- the command and the handshake ---------------------------------------------------

    [Fact]
    public async Task The_cli_is_started_as_an_app_server_in_the_contained_copy_with_the_reaching_features_off()
    {
        AnswerEveryApproval(true);

        await DrainAsync(Runner().RunAsync(Request()));

        var args = Args();
        args.Take(3).Should().Equal("app-server", "--listen", "stdio://");
        foreach (var feature in CodexSessionRunner.DisabledFeatures)
        {
            args.Should().ContainInConsecutiveOrder("--disable", feature);
        }

        Path.GetFullPath(Cwd()).TrimEnd(Path.DirectorySeparatorChar)
            .Should().BeEquivalentTo(Path.GetFullPath(_workspaceCopy).TrimEnd(Path.DirectorySeparatorChar));
    }

    [Fact]
    public async Task The_thread_opens_with_the_strictest_policy_in_the_copy_after_the_handshake()
    {
        AnswerEveryApproval(true);

        await DrainAsync(Runner().RunAsync(Request(model: "gpt-5.5")));

        var methods = Sent().Where(m => m.TryGetProperty("method", out _)).Select(m => m.GetProperty("method").GetString()).ToList();
        methods.Take(4).Should().Equal("initialize", "initialized", "thread/start", "turn/start");

        var thread = Requests("thread/start").Single().GetProperty("params");
        thread.GetProperty("approvalPolicy").GetString().Should().Be("untrusted");
        thread.GetProperty("sandbox").GetString().Should().Be("workspace-write");
        thread.GetProperty("cwd").GetString().Should().Be(_workspaceCopy);
        thread.GetProperty("model").GetString().Should().Be("gpt-5.5");

        var turn = Requests("turn/start").Single().GetProperty("params");
        turn.GetProperty("threadId").GetString().Should().Be("019a0000-0000-7000-8000-00000000c0de");
        turn.GetProperty("input")[0].GetProperty("text").GetString().Should().Be("hello");
    }

    [Fact]
    public async Task A_resumed_session_resumes_the_thread_it_names()
    {
        AnswerEveryApproval(true);

        var events = await DrainAsync(Runner().RunAsync(Request(resume: "019a-previous-thread")));

        Requests("thread/start").Should().BeEmpty();
        Requests("thread/resume").Single().GetProperty("params").GetProperty("threadId").GetString()
            .Should().Be("019a-previous-thread");
        events.OfType<SessionRegistered>().Single().ProviderSessionId.Should().Be("019a-previous-thread");
    }

    [Fact]
    public async Task A_stored_codex_credential_is_injected_and_the_run_keeps_its_path()
    {
        AnswerEveryApproval(true);
        var credentials = Substitute.For<IAgentRuntimeCredentialStore>();
        credentials.EnvironmentFor(AgentTool.Codex).Returns(new Dictionary<string, string> { ["OPENAI_API_KEY"] = "sk-test" });

        await DrainAsync(Runner(credentials: credentials).RunAsync(Request()));

        var env = Env();
        env["OPENAI_API_KEY"].Should().Be("sk-test");
        env["PATH"].Should().NotBeNullOrWhiteSpace();
    }

    // ---- the transcript -------------------------------------------------------------------

    [Fact]
    public async Task A_turn_becomes_the_same_transcript_events_a_claude_turn_does()
    {
        AnswerEveryApproval(true);

        var events = await DrainAsync(Runner().RunAsync(Request()));

        events[0].Should().BeOfType<SessionStarted>().Which.TypedTurns.Should().BeFalse("a Codex session is never typed");
        events.OfType<SessionRegistered>().Single().Model.Should().Be("gpt-the-cli-picked");
        events.OfType<TurnActivity>().Select(a => a.Phase).Should().Contain([AgentTurnPhase.Submitted, AgentTurnPhase.Thinking]);

        var started = events.OfType<ToolCallStarted>().Single();
        started.ToolName.Should().Be("Bash");
        started.Detail.Should().Be("echo hi > out.txt");
        var completed = events.OfType<ToolCallCompleted>().Single();
        completed.Success.Should().BeTrue();
        completed.Detail.Should().Be("hi\n");

        string.Concat(events.OfType<PartialText>().Select(p => p.Delta)).Should().Be("ran it: hello");
        events.OfType<MessageEmitted>().Should().ContainSingle(m => m.Role == TranscriptRole.Assistant)
            .Which.Text.Should().Be("ran it: hello");

        var stats = events.OfType<StatsUpdated>().Last().Stats;
        stats.InputTokens.Should().Be(600, "the cached share of OpenAI's input count is split out");
        stats.CacheReadTokens.Should().Be(400);
        stats.OutputTokens.Should().Be(50);
        stats.CostUsd.Should().Be(0m, "Codex reports no cost and the studio invents none");
        stats.ContextWindow.Should().Be(272000);
        stats.ToolCalls.Should().Be(1);
        stats.Turns.Should().Be(1);

        var turn = events.OfType<TurnCompleted>().Single();
        turn.IsError.Should().BeFalse();
        turn.TerminalReason.Should().Be("completed");

        events.Last().Should().BeOfType<SessionEnded>().Which.FinalState.Should().Be(AgentSessionState.Completed);
    }

    [Fact]
    public async Task A_follow_up_turn_carries_a_model_change_without_restarting_the_cli()
    {
        AnswerEveryApproval(true);
        var runner = Runner(exitAfter: 2);
        var request = Request();

        var events = new List<AgentEvent>();
        await foreach (var e in runner.RunAsync(request))
        {
            events.Add(e);
            if (e is TurnCompleted && events.OfType<TurnCompleted>().Count() == 1)
            {
                runner.ChangeModel(request.SessionId, "gpt-5.5-mini").Should().BeTrue();
                (await runner.SendAsync(request.SessionId, "again")).Should().BeTrue();
            }
        }

        events.OfType<TurnCompleted>().Should().HaveCount(2);
        events.OfType<MessageEmitted>().Should().Contain(m => m.Text == "The conversation continues on gpt-5.5-mini.");
        events.OfType<MessageEmitted>().Should().Contain(m => m.Text == "ran it: again");
    }

    // ---- approvals: fail closed ------------------------------------------------------------

    [Fact]
    public async Task An_approved_command_is_accepted_and_nothing_broader_is_granted()
    {
        PendingToolApproval? parked = null;
        _broker.Requested += request =>
        {
            parked = request;
            _broker.Answer(request.RequestId, true, "ok");
        };

        await DrainAsync(Runner().RunAsync(Request()));

        parked.Should().NotBeNull("a command must park in front of the user");
        parked!.ToolName.Should().Be("Bash");
        parked.Input["command"].Should().Be("echo hi > out.txt");
        Decision(AnswerTo("srv-1")).Should().Be("accept", "never acceptForSession: every change asks, every time");
    }

    [Fact]
    public async Task A_denied_command_is_declined_and_the_transcript_shows_it()
    {
        AnswerEveryApproval(false);

        var events = await DrainAsync(Runner().RunAsync(Request()));

        Decision(AnswerTo("srv-1")).Should().Be("decline");
        var completed = events.OfType<ToolCallCompleted>().Single();
        completed.Success.Should().BeFalse();
        completed.Detail.Should().Be("The command was declined.");
    }

    [Fact]
    public async Task An_approval_nobody_answers_is_declined_when_the_broker_times_out()
    {
        using var impatient = new AgentApprovalBroker(TimeSpan.FromMilliseconds(200));

        await DrainAsync(Runner(broker: impatient).RunAsync(Request()));

        Decision(AnswerTo("srv-1")).Should().Be("decline");
    }

    [Fact]
    public async Task A_broker_that_fails_is_a_decline_not_an_accept()
    {
        var broken = Substitute.For<IAgentApprovalBroker>();
        broken.RequestAsync(default, default!, default, default!, default)
            .ReturnsForAnyArgs<Task<ToolApprovalDecision>>(_ => throw new InvalidOperationException("broker down"));

        await DrainAsync(Runner(broker: broken).RunAsync(Request()));

        Decision(AnswerTo("srv-1")).Should().Be("decline");
    }

    [Fact]
    public async Task A_patch_asks_about_every_file_with_its_resolved_path_and_one_denial_declines_it()
    {
        var asked = new List<PendingToolApproval>();
        _broker.Requested += request =>
        {
            asked.Add(request);
            _broker.Answer(request.RequestId, asked.Count == 1, "first yes, then no");
        };

        var events = await DrainAsync(Runner(mode: "patch").RunAsync(Request()));

        asked.Select(a => a.ToolName).Should().Equal("Write", "Edit");
        asked[0].Input["file_path"].Should().Be(Path.Combine(_workspaceCopy, "notes/new.txt"));
        asked[1].Input["file_path"].Should().Be(Path.Combine(_workspaceCopy, "README.md"));
        Decision(AnswerTo("srv-1")).Should().Be("decline", "a patch is accepted only when every file in it is");
        events.OfType<ToolCallStarted>().Single().ToolName.Should().Be("apply_patch");
        events.OfType<ToolCallCompleted>().Single().Detail.Should().Be("The change was declined.");
    }

    [Fact]
    public async Task A_patch_approved_file_by_file_is_accepted()
    {
        AnswerEveryApproval(true);

        await DrainAsync(Runner(mode: "patch").RunAsync(Request()));

        Decision(AnswerTo("srv-1")).Should().Be("accept");
    }

    [Fact]
    public async Task Every_request_the_studio_does_not_grant_is_refused_rather_than_ignored()
    {
        var asked = 0;
        _broker.Requested += request =>
        {
            asked++;
            _broker.Answer(request.RequestId, true, "would approve, but nothing should ask");
        };

        await DrainAsync(Runner(mode: "requests").RunAsync(Request()));

        AnswerTo("srv-1").GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32000, "user input is not something the studio answers");
        AnswerTo("srv-2").GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32000, "a permission profile is a standing grant");
        AnswerTo("srv-3").GetProperty("result").GetProperty("decision").GetProperty("denied").ValueKind
            .Should().Be(JsonValueKind.Object, "the legacy approval is refused in its own vocabulary");
        AnswerTo("srv-4").GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32601, "a method nobody named is not found");
        Decision(AnswerTo("srv-5")).Should().Be("decline", "a command approval that names no command cannot be approved");
        asked.Should().Be(0, "none of these may reach the user as if it were an ordinary approval");
    }

    // ---- stopping and failing ----------------------------------------------------------------

    [Fact]
    public async Task Stop_kills_the_process_and_the_session_ends_cancelled()
    {
        var runner = Runner(mode: "hang");
        var request = Request();
        var run = DrainAsync(runner.RunAsync(request));

        var heartbeat = _reportPath + ".alive";
        for (var attempt = 0; attempt < 100 && !File.Exists(heartbeat); attempt++)
        {
            await Task.Delay(50); // poll interval inside a wait helper
        }

        File.Exists(heartbeat).Should().BeTrue("the stand-in should be running by now");
        runner.Stop(request.SessionId).Should().BeTrue();

        var events = await run;
        events.Last().Should().BeOfType<SessionEnded>().Which.FinalState.Should().Be(AgentSessionState.Cancelled);
        runner.IsRunning(request.SessionId).Should().BeFalse();
    }

    [Fact]
    public async Task A_cli_that_will_not_start_ends_the_session_with_the_reason()
    {
        var events = await DrainAsync(Runner(missingExecutable: true).RunAsync(Request()));

        var ended = events.OfType<SessionEnded>().Single();
        ended.FinalState.Should().Be(AgentSessionState.Failed);
        ended.FailureReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_crashing_cli_fails_the_session_with_its_own_words()
    {
        var events = await DrainAsync(Runner(mode: "crash").RunAsync(Request()));

        var ended = events.OfType<SessionEnded>().Single();
        ended.FinalState.Should().Be(AgentSessionState.Failed);
        ended.FailureReason.Should().Contain("not logged in");
    }

    [Fact]
    public async Task A_thread_the_cli_refuses_to_open_is_said_in_the_transcript()
    {
        var events = await DrainAsync(Runner(mode: "refuse-thread").RunAsync(Request()));

        events.OfType<MessageEmitted>().Should().Contain(m =>
            m.Role == TranscriptRole.System && m.Text.Contains("not logged in", StringComparison.Ordinal));
        events.Last().Should().BeOfType<SessionEnded>();
    }

    [Fact]
    public async Task A_usd_ceiling_is_named_as_unenforceable_rather_than_silently_dropped()
    {
        AnswerEveryApproval(true);

        var events = await DrainAsync(Runner().RunAsync(Request(budget: 5m)));

        events.OfType<MessageEmitted>().Should().Contain(m => m.Text == CodexSessionRunner.BudgetNotice);
    }
}
#endif
