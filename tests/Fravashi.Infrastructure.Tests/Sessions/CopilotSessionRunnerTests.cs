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
/// Backlog 66. The Copilot runner driven against a REAL child process speaking the Agent Client Protocol
/// (<c>FakeCli/fake-copilot-acp.js</c>), whose messages are constructed from the pinned 1.0.78 package's bundled
/// code. What the runner answered is read back from the messages the stand-in logged.
/// </summary>
public class CopilotSessionRunnerTests : IDisposable
{
    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "fravashi-copilot-runner-" + Guid.NewGuid().ToString("N"));

    private readonly string _workspaceCopy;
    private readonly string _reportPath;
    private readonly AgentApprovalBroker _broker = new(TimeSpan.FromSeconds(5));

    public CopilotSessionRunnerTests()
    {
        _workspaceCopy = Path.Combine(_scratch, "workspaces", "w1");
        _reportPath = Path.Combine(_scratch, "copilot-report.json");
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
        Path.Combine(AppContext.BaseDirectory, "Sessions", "FakeCli", "fake-copilot-acp.js");

    private CopilotSessionRunner Runner(
        string mode = "edit",
        int exitAfter = 1,
        bool canLoad = true,
        IAgentApprovalBroker? broker = null,
        IAgentRuntimeCredentialStore? credentials = null,
        int exitDelayMs = 20) =>
        new(
            broker ?? _broker,
            credentials: credentials,
            launcher: new AgentCliLauncher("node", [FakeServer()]),
            environment: new Dictionary<string, string>
            {
                ["FAKE_CLI_REPORT"] = _reportPath,
                ["FAKE_CLI_MODE"] = mode,
                ["FAKE_CLI_EXIT_AFTER"] = exitAfter.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["FAKE_ACP_LOAD"] = canLoad ? "true" : "false",
                ["FAKE_CLI_EXIT_DELAY_MS"] = exitDelayMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });

    private AgentSessionRequest Request(string prompt = "hello", string? resume = null, string? model = null) =>
        new(AgentSessionId.New(), "w1", _workspaceCopy, prompt, resume, model);

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

    private List<JsonElement> Sent()
    {
        var path = _reportPath + ".messages.jsonl";
        File.Exists(path).Should().BeTrue("the runner should have spoken to the CLI");
        return [.. ReadLines(path).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement.Clone())];
    }

    /// <summary>
    /// The stand-in writes this file from its own process, so the last line can still be in flight when the
    /// run's events have all been drained here: the child writes the reply, then exits, and only the exit is
    /// ordered against the drain. Under a full-solution run that gap is wide enough to lose a line, which is
    /// how <c>A_client_capability_that_was_never_offered_is_refused</c> failed once in a loaded run and passed
    /// alone. So a read waits briefly for the file to stop growing rather than taking one snapshot.
    /// </summary>
    private static string[] ReadLines(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var lines = Array.Empty<string>();
        var stable = 0;
        while (DateTime.UtcNow < deadline)
        {
            string[] current;
            try
            {
                current = File.ReadAllLines(path);
            }
            catch (IOException)
            {
                // Being written to this instant; ask again.
                Thread.Sleep(20);
                continue;
            }

            if (current.Length == lines.Length && ++stable >= 3)
            {
                return current;
            }

            if (current.Length != lines.Length)
            {
                stable = 0;
            }

            lines = current;
            Thread.Sleep(20);
        }

        return lines;
    }

    private List<JsonElement> Requests(string method) =>
        [.. Sent().Where(m => m.TryGetProperty("method", out var name) && name.GetString() == method)];

    /// <summary>The runner's answer to the stand-in's first request (the stand-in numbers its own from 1).</summary>
    private JsonElement FirstAnswer() =>
        Sent().First(m => !m.TryGetProperty("method", out _) && m.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.Number && id.GetInt32() == 1);

    private static JsonElement Outcome(JsonElement answer) =>
        answer.GetProperty("result").GetProperty("outcome");

    // ---- the command and the handshake ---------------------------------------------------

    [Fact]
    public async Task The_cli_is_started_as_an_acp_server_that_cannot_update_itself_or_reach_off_the_machine()
    {
        AnswerEveryApproval(true);

        await DrainAsync(Runner().RunAsync(Request(model: "gpt-5.4")));

        Args().Should().ContainInOrder("--acp", "--no-auto-update", "--disallow-temp-dir", "--disable-builtin-mcps", "--no-remote");
        Args().Should().ContainInConsecutiveOrder("--model", "gpt-5.4");
        Args().Should().NotContain(a => a.StartsWith("--allow-all", StringComparison.Ordinal) || a == "--yolo");
    }

    [Fact]
    public async Task Every_message_is_json_rpc_2_and_the_client_offers_no_file_system_or_terminal()
    {
        AnswerEveryApproval(true);

        await DrainAsync(Runner().RunAsync(Request()));

        Sent().Should().OnlyContain(m => m.GetProperty("jsonrpc").GetString() == "2.0");
        var initialize = Requests("initialize").Single().GetProperty("params");
        initialize.GetProperty("protocolVersion").GetInt32().Should().Be(1);
        initialize.GetProperty("clientCapabilities").GetProperty("fs").GetProperty("readTextFile").GetBoolean().Should().BeFalse();
        initialize.GetProperty("clientCapabilities").GetProperty("terminal").GetBoolean().Should().BeFalse();
        Requests("session/new").Single().GetProperty("params").GetProperty("cwd").GetString().Should().Be(_workspaceCopy);
    }

    [Fact]
    public async Task A_stored_copilot_token_is_injected()
    {
        AnswerEveryApproval(true);
        var credentials = Substitute.For<IAgentRuntimeCredentialStore>();
        credentials.EnvironmentFor(AgentTool.Copilot).Returns(new Dictionary<string, string> { ["COPILOT_GITHUB_TOKEN"] = "gho_test" });

        await DrainAsync(Runner(credentials: credentials).RunAsync(Request()));

        using var document = JsonDocument.Parse(File.ReadAllText(_reportPath));
        document.RootElement.GetProperty("env").GetProperty("COPILOT_GITHUB_TOKEN").GetString().Should().Be("gho_test");
    }

    // ---- the transcript -------------------------------------------------------------------

    [Fact]
    public async Task A_prompt_becomes_text_blocks_a_tool_card_usage_and_a_turn_end()
    {
        AnswerEveryApproval(true);

        var events = await DrainAsync(Runner().RunAsync(Request()));

        events[0].Should().BeOfType<SessionStarted>().Which.TypedTurns.Should().BeFalse();
        events.OfType<SessionRegistered>().Single().ProviderSessionId.Should().Be("acp-session-1");
        events.OfType<MessageEmitted>().Where(m => m.Role == TranscriptRole.Assistant).Select(m => m.Text)
            .Should().Equal("Let me look.", "allow_once: hello");

        events.OfType<ToolCallStarted>().Single().ToolName.Should().Be("Edit");
        var completed = events.OfType<ToolCallCompleted>().Single();
        completed.Success.Should().BeTrue();
        completed.Detail.Should().Be("edited");

        var stats = events.OfType<StatsUpdated>().Last().Stats;
        stats.InputTokens.Should().Be(700);
        stats.CacheReadTokens.Should().Be(300);
        stats.OutputTokens.Should().Be(40);
        stats.ContextTokens.Should().Be(1200);
        stats.ContextWindow.Should().Be(128000);
        stats.CostUsd.Should().Be(0m);

        events.OfType<TurnCompleted>().Single().TerminalReason.Should().Be("end_turn");
        events.Last().Should().BeOfType<SessionEnded>().Which.FinalState.Should().Be(AgentSessionState.Completed);
    }

    [Fact]
    public async Task A_resume_loads_the_conversation_without_replaying_it_into_the_transcript()
    {
        AnswerEveryApproval(true);

        var events = await DrainAsync(Runner().RunAsync(Request(resume: "acp-earlier")));

        Requests("session/new").Should().BeEmpty();
        Requests("session/load").Single().GetProperty("params").GetProperty("sessionId").GetString().Should().Be("acp-earlier");
        events.OfType<MessageEmitted>().Should().NotContain(m => m.Text.Contains("the old answer", StringComparison.Ordinal));
        events.OfType<PartialText>().Should().NotContain(p => p.Delta.Contains("the old answer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_resume_the_cli_cannot_load_says_so_and_starts_fresh()
    {
        AnswerEveryApproval(true);

        var events = await DrainAsync(Runner(canLoad: false).RunAsync(Request(resume: "acp-earlier")));

        Requests("session/load").Should().BeEmpty();
        Requests("session/new").Should().ContainSingle();
        events.OfType<MessageEmitted>().Should().Contain(m => m.Role == TranscriptRole.System && m.Text.Contains("starts fresh", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_model_change_goes_to_the_live_session_and_a_follow_up_is_served_by_the_same_process()
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
                runner.ChangeModel(request.SessionId, null).Should().BeFalse("ACP names no default to return to");
                runner.ChangeModel(request.SessionId, "claude-sonnet-4.6").Should().BeTrue();
                (await runner.SendAsync(request.SessionId, "again")).Should().BeTrue();
            }
        }

        Requests("session/set_model").Single().GetProperty("params").GetProperty("modelId").GetString().Should().Be("claude-sonnet-4.6");
        Requests("session/prompt").Should().HaveCount(2);
        events.OfType<TurnCompleted>().Should().HaveCount(2);
    }

    // ---- approvals: fail closed ------------------------------------------------------------

    [Fact]
    public async Task An_approved_edit_selects_allow_once_and_never_allow_always()
    {
        PendingToolApproval? parked = null;
        _broker.Requested += request =>
        {
            parked = request;
            _broker.Answer(request.RequestId, true, "ok");
        };

        await DrainAsync(Runner().RunAsync(Request()));

        parked!.ToolName.Should().Be("Edit");
        parked.Input["file_path"].Should().Be(Path.Combine(_workspaceCopy, "README.md"));
        var outcome = Outcome(FirstAnswer());
        outcome.GetProperty("outcome").GetString().Should().Be("selected");
        outcome.GetProperty("optionId").GetString().Should().Be("allow_once");
    }

    [Fact]
    public async Task A_denied_edit_selects_reject_once()
    {
        AnswerEveryApproval(false);

        var events = await DrainAsync(Runner().RunAsync(Request()));

        Outcome(FirstAnswer()).GetProperty("optionId").GetString().Should().Be("reject_once");
        events.OfType<ToolCallCompleted>().Single().Success.Should().BeFalse();
    }

    [Fact]
    public async Task An_approval_when_only_a_standing_allow_is_offered_is_still_a_rejection()
    {
        AnswerEveryApproval(true);

        await DrainAsync(Runner(mode: "always-only").RunAsync(Request()));

        Outcome(FirstAnswer()).GetProperty("optionId").GetString().Should().Be("reject_once");
    }

    [Fact]
    public async Task A_permission_request_with_no_options_is_cancelled()
    {
        AnswerEveryApproval(true);

        await DrainAsync(Runner(mode: "no-options").RunAsync(Request()));

        Outcome(FirstAnswer()).GetProperty("outcome").GetString().Should().Be("cancelled");
    }

    [Fact]
    public async Task An_out_of_directory_read_always_asks_and_its_joined_paths_never_reach_the_read_auto_allow()
    {
        PendingToolApproval? parked = null;
        _broker.Requested += request =>
        {
            parked = request;
            _broker.Answer(request.RequestId, false, "no");
        };

        await DrainAsync(Runner(mode: "read").RunAsync(Request()));

        parked.Should().NotBeNull("a joined path string that starts inside the copy must still ask");
        parked!.ToolName.Should().Be(CopilotSessionRunner.OutsideReadTool);
        AgentToolSafety.IsReadOnly(parked.ToolName).Should().BeFalse();
        parked.Input["paths"].Should().Be("/workspaces/w1/a, /data/fravashi.db");
        Outcome(FirstAnswer()).GetProperty("optionId").GetString().Should().Be("reject_once");
    }

    [Fact]
    public async Task A_broker_that_fails_is_a_rejection()
    {
        var broken = Substitute.For<IAgentApprovalBroker>();
        broken.RequestAsync(default, default!, default, default!, default)
            .ReturnsForAnyArgs<Task<ToolApprovalDecision>>(_ => throw new InvalidOperationException("broker down"));

        await DrainAsync(Runner(broker: broken).RunAsync(Request()));

        Outcome(FirstAnswer()).GetProperty("optionId").GetString().Should().Be("reject_once");
    }

    [Fact]
    public async Task A_client_capability_that_was_never_offered_is_refused()
    {
        var events = await DrainAsync(Runner(mode: "fs").RunAsync(Request()));

        FirstAnswer().GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32601);
        events.OfType<MessageEmitted>().Should().Contain(m => m.Text.EndsWith("fs-refused: hello", StringComparison.Ordinal), "with no tool call between them the two chunks are one text block");
    }

    [Fact]
    public async Task A_cli_that_exits_the_instant_it_answers_still_delivers_the_turns_last_words()
    {
        // Backlog 66's recorded flake, made deterministic. The runner turns a prompt RESULT into the turn's
        // message on the task awaiting that result, while a DIFFERENT task (the stdout reader) completes the
        // event channel the moment the CLI's output closes. A write to a completed channel is silently
        // dropped, so when the CLI exits promptly after answering, the whole turn's text disappears: no
        // message events at all, while the answer the runner wrote to the CLI is already on disk. That is
        // exactly the shape seen three times in CI (runs 103, 107 and 118) and never once in isolation,
        // because the stand-in normally waits 20ms before exiting and that is usually enough to win the race.
        //
        // Here the stand-in exits in the SAME TICK as the result, which is what a real CLI with nothing left
        // to do does. The bug is a product fault rather than a test one: a user loses the last words of a turn.
        AnswerEveryApproval(true);

        var events = await DrainAsync(Runner(exitDelayMs: 0).RunAsync(Request()));

        events.OfType<MessageEmitted>().Should().Contain(
            m => m.Text.EndsWith("hello", StringComparison.Ordinal),
            "the turn's text must survive the CLI exiting as soon as it has answered");
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

        (await run).Last().Should().BeOfType<SessionEnded>().Which.FinalState.Should().Be(AgentSessionState.Cancelled);
    }

    [Fact]
    public async Task A_crashing_cli_fails_the_session_with_its_own_words()
    {
        var events = await DrainAsync(Runner(mode: "crash").RunAsync(Request()));

        var ended = events.OfType<SessionEnded>().Single();
        ended.FinalState.Should().Be(AgentSessionState.Failed);
        ended.FailureReason.Should().Contain("no authentication information found");
    }

    [Fact]
    public async Task A_session_the_cli_refuses_to_open_is_said_in_the_transcript()
    {
        var events = await DrainAsync(Runner(mode: "refuse-session").RunAsync(Request()));

        events.OfType<MessageEmitted>().Should().Contain(m =>
            m.Role == TranscriptRole.System && m.Text.Contains("Authentication required", StringComparison.Ordinal));
        events.Last().Should().BeOfType<SessionEnded>();
    }
}
#endif
