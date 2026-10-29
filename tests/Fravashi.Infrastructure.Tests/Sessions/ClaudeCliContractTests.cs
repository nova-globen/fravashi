using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Infrastructure.AgentRuntimes;
using Fravashi.Application.Sessions;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Application.Updates;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Sessions.Tools;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions;

/// <summary>
/// The contract tests backlog 61 asks for: they drive the REAL agent CLI and fail loudly when its protocol
/// shifts, which is the gate ADR 0023 puts in front of promoting a new pin. Running this whole file green
/// against a binary is what earns the right to record that version as
/// <see cref="AgentRuntimeDescriptor.ProtocolVerifiedVersion"/>, which is the claim the transcript makes to
/// the user.
/// <para>
/// They SKIP, with the reason printed, unless someone points <c>FRAVASHI_CLAUDE_CLI</c> at a CLI binary or the
/// suite runs inside the studio container. The standard suite therefore never depends on a container, and never
/// silently binds itself to a developer's own install (see <see cref="RealClaudeCli"/> for why PATH is not
/// searched). Two tiers: the first two are offline and free, the rest take real billable turns.
/// </para>
/// <para>
/// A live turn needs an approval channel, and WHICH one is part of what each test asserts. The hook-gating
/// test points the hook at nothing on purpose, so what the CLI receives is the script's fail-closed refusal.
/// A typed turn cannot be driven that way: the CLI builds its <c>StructuredOutput</c> tool out of the
/// schema, the <c>"*"</c> matcher sends that call through the same hook, and the studio's auto-allow for it
/// lives in <see cref="AgentApprovalBroker"/>, BEHIND the endpoint. Measured 2026-08-07: with the dead URL
/// the model answers "I attempted to call the StructuredOutput tool ... but it's currently unavailable due
/// to a connectivity issue with the studio", so the turn ends with no document and the test could not pass
/// against any binary. Those tests therefore run a real <see cref="ApprovalChannel"/> over the studio's own
/// <see cref="AgentApprovalEndpoint"/>, which is the path the product uses.
/// </para>
/// <para>
/// Every live turn also carries the studio's tool server (backlog 84), because production writes its MCP config
/// for every session: a <see cref="ToolServerChannel"/> serves the product's own <see cref="StudioToolServer"/> over
/// the runner's broker, so each turn's <c>--mcp-config</c> is really connected, and one test drives a real call
/// through it end to end.
/// </para>
/// </summary>
public class ClaudeCliContractTests(Xunit.Abstractions.ITestOutputHelper output) : IDisposable
{
    /// <summary>
    /// The model EVERY live turn runs on. The first live test used to leave it unset, because "no --model
    /// means the CLI's own default" is decision 7, but the account default was measured on
    /// 2026-09-13 to emit no <c>stream_event</c> lines at all on the owner's host (ADR 0023), so that test
    /// could not pass there whatever the binary was, and it cost more per turn. Since 2026-09-14 every live
    /// call pins this model; that an ABSENT flag still resolves is left to the offline argument tests.
    /// </summary>
    private const string CheapModel = "claude-haiku-4-5-20251001";

    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "fravashi-cli-contract-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        GC.SuppressFinalize(this);
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
    }

    [RealClaudeCliFact]
    public void The_cli_under_test_is_the_pinned_version()
    {
        var pinned = AgentRuntimeCatalog.For(AgentTool.Claude).PinnedVersion;

        RealClaudeCli.ReportedVersion.Should().Be(
            pinned,
            $"the contract is only meaningful against the pinned CLI; probed {RealClaudeCli.Path}");
    }

    [RealClaudeCliFact]
    public void The_cli_still_declares_every_flag_the_session_runner_passes()
    {
        // The flag list is DERIVED from production code, not hand-written, so a flag added to
        // BuildArguments tomorrow is covered by this test without anyone remembering to update it.
        var help = RealClaudeCli.Run(RealClaudeCli.Path!, ["--help"], TimeSpan.FromSeconds(30));
        help.Should().NotBeNullOrWhiteSpace($"{RealClaudeCli.Path} --help should answer");

        foreach (var flag in FlagsTheRunnerPasses())
        {
            // Anchored at the option COLUMN (exactly two spaces), not at any mention. Wrapped description
            // text is indented far deeper and routinely names other flags: the real help lists
            // "--settings, --agents, --plugin-dir." inside one option's prose, so a looser match would keep
            // passing after the vendor deleted the option it is meant to be guarding. The optional
            // "-x, " prefix covers the short-alias forms ("-p, --print", "-r, --resume [value]").
            Regex.IsMatch(help!, $@"(?m)^  (?:-\w, )?{Regex.Escape(flag)}(?![\w-])")
                .Should().BeTrue($"the CLI should still declare {flag}; the session runner passes it on every run");
        }
    }

    [LiveClaudeCliFact]
    public async Task A_real_turn_parses_into_the_events_the_studio_depends_on()
    {
        var events = await DriveAsync("Reply with exactly: OK. Do not use any tools.", model: CheapModel);

        // Each assertion is one shape the studio genuinely depends on, and would break silently without.
        events.OfType<SessionRegistered>().Should().ContainSingle()
            .Which.ProviderSessionId.Should().NotBeNullOrWhiteSpace("the init line still carries a session id");
        events.OfType<MessageEmitted>().Should().Contain(m => m.Role == TranscriptRole.Assistant);
        events.OfType<PartialText>().Should().NotBeEmpty("--include-partial-messages should still stream deltas");
        events.OfType<TurnCompleted>().Should().NotBeEmpty();
        events.OfType<StatsUpdated>().Should().Contain(s => s.Stats.OutputTokens > 0, "the result line's usage block should still parse");
        events.Last().Should().BeOfType<SessionEnded>();
    }

    [LiveClaudeCliFact]
    public async Task The_search_tools_are_there_for_a_session_that_names_them()
    {
        // Backlog 162: the native CLI drops Glob and Grep unless --allowedTools names one of them, and then every
        // search is a Bash call that asks a person. What matters is that the tools EXIST for an ordinary session, so
        // this reads the init line of a session started with the runner's own command line (#353, 2026-10-23). It used
        // to ask the cheap model to call Grep, which stopped proving anything once every new session's first turn
        // carried the studio's opening note (2026-10-16 and 2026-10-18): the model answered the note in text and
        // never called a tool, which reads exactly like a missing tool. The init line's tool list cannot be talked out
        // of, and it is how backlog 248's reading-conversation test already proves which tools a session has.
        var tools = (await ProductionInitLineAsync(OrdinaryRequest())).GetProperty("tools").EnumerateArray()
            .Select(t => t.GetString() ?? string.Empty)
            .ToList();
        output.WriteLine("init tools: " + string.Join(", ", tools));

        tools.Should().Contain(["Grep", "Glob"], "an ordinary session names Grep and Glob in --allowedTools, so both must exist");
    }

    /// <summary>An ordinary conversation's request: no built-in tool list, as a person's conversation starts.</summary>
    private static AgentSessionRequest OrdinaryRequest() =>
        new(AgentSessionId.New(), "contract", "/tmp", "hello", null, CheapModel);

    [LiveClaudeCliFact]
    public async Task A_session_carries_nothing_the_signed_in_account_brings()
    {
        // Backlog 174. Measured 2026-09-30 on the pinned 2.1.280 signed in with a person's subscription: the init line
        // listed their claude.ai connectors (Gmail, Drive, Calendar, and Claude Docs with eight tools, create and
        // delete among them), nineteen MCP servers of four plugins synced from claude.ai, and those plugins' skills.
        // Started the way the runner starts a session (the studio's settings file, the environment every child is
        // restricted to), none of it may be there. Against an account that carries nothing this passes trivially,
        // which is why the tier is driven with the owner's own sign-in.
        var init = await InitLineAsync();

        init.GetProperty("mcp_servers").EnumerateArray().Select(s => s.GetProperty("name").GetString() ?? string.Empty)
            .Should().NotContain(
                name => name.StartsWith("claude.ai", StringComparison.Ordinal) || name.StartsWith("plugin:", StringComparison.Ordinal),
                "the account's connectors and synced plugins' servers must not load into a session");
        init.GetProperty("tools").EnumerateArray().Select(t => t.GetString() ?? string.Empty)
            .Should().NotContain(tool => tool.StartsWith("mcp__claude_ai_", StringComparison.Ordinal));
        init.GetProperty("plugins").EnumerateArray().Select(p => p.GetProperty("source").GetString() ?? string.Empty)
            .Should().NotContain(source => source.EndsWith("@synced", StringComparison.Ordinal));
    }

    [LiveClaudeCliFact]
    public async Task A_second_turn_is_served_by_the_same_process()
    {
        // Decision 6 rests on this: ONE long-lived process serves a whole conversation, so a
        // follow-up written to stdin has to be answered without the CLI being restarted. A release that
        // exits after the first result would still pass every other tier here.
        var events = await DriveAsync(
            "Reply with exactly: ONE. Do not use any tools.",
            followUp: "Reply with exactly: TWO. Do not use any tools.",
            model: CheapModel);

        events.OfType<TurnCompleted>().Should().HaveCount(2, "the follow-up went to the process already running");
        events.OfType<SessionRegistered>().Should().ContainSingle("re-announcing the same session is not a new one");
    }

    [LiveClaudeCliFact]
    public async Task A_real_tool_call_is_gated_by_the_pre_tool_use_hook()
    {
        // The safety story end to end, against the real binary: the per-session settings file, the "*"
        // matcher that has to fire for EVERY tool, and the decision document the CLI must still honor.
        // The approval URL points at nothing here, so what the CLI receives is the script's fail-closed
        // refusal, and a release that stopped honoring the hook would let the command run instead.
        var events = await DriveAsync(
            "Use the Bash tool to run exactly this command: echo fravashi-contract. Run it, do not ask first.",
            model: CheapModel);

        events.OfType<ToolCallStarted>().Should().NotBeEmpty(
            "the CLI should still announce a tool call as a tool_use block the parser understands");
        // Any refused call counts, not exactly one: measured 2026-09-15 on the owner's Windows host, the model
        // answered a refused Bash by trying PowerShell, which the hook refused the same way, and ".Which" over
        // two matches threw although the gate had held both times.
        events.OfType<ToolCallCompleted>().Should().Contain(
            c => !c.Success && c.Detail.Contains("could not be reached", StringComparison.Ordinal),
            "the PreToolUse hook's refusal must still stop the call, and the CLI should hand the hook's own "
            + "refusal reason back, which is what the transcript shows");
        events.OfType<ToolCallCompleted>().Should().NotContain(
            c => c.Success && (c.ToolName == "Bash" || c.ToolName == "PowerShell"),
            "no shell command may run past a hook that cannot reach the studio");
    }

    [LiveClaudeCliFact]
    public async Task A_hook_script_that_ends_without_answering_still_stops_the_call()
    {
        // Backlog 115, against the real binary. Measured 2026-09-15 on the pinned 2.1.222: a PreToolUse hook
        // that exits 1, is killed or times out is NOT a refusal, the CLI hands the call to its own permission
        // system, and with those checks bypassed the command ran. Exit 2 is the failure the CLI does treat as
        // blocking, so the studio's hook COMMAND turns every unclean end of its script into exit 2. This
        // drives exactly that command, from the real writer, with the script sabotaged, and with the CLI's
        // own permission checks bypassed so that nothing but the hook can be what stops the call.
        //
        // The control goes first and uses the same wiring with a script that ALLOWS: if the command does not
        // run there, the premise is gone and the refusals below would prove nothing.
        var allow = AgentApprovalEndpoint.Render(ToolApprovalDecision.Allow("contract control"));
        (await DriveSabotagedHookAsync("#!/bin/sh\nprintf '%s\\n' '" + allow + "'\n")).Ran
            .Should().BeTrue("an allowed call runs under bypassed permissions, so the refusals below are the hook's");

        foreach (var (what, body) in new (string What, string? Body)[]
        {
            ("crashes", "#!/bin/sh\nexit 1\n"),
            ("is killed", "#!/bin/sh\nkill -9 $$\n"),
            ("is missing", null),
        })
        {
            var (ran, output) = await DriveSabotagedHookAsync(body);

            output.Should().Contain("PreToolUse:Bash", $"the model should really have attempted the call when the script {what}");
            ran.Should().BeFalse($"a hook script that {what} must still stop the call");
            output.Should().Contain(SessionHookWriter.CrashedHookRefusal, "the CLI hands a blocking hook's reason back");
        }
    }

        [LiveClaudeCliFact]
    public async Task A_real_turn_under_a_schema_comes_back_as_a_document()
    {
        // ADR 0022's whole claim, against the real binary: --json-schema still makes the CLI enforce the
        // shape, and the terminating line still carries the validated document in structured_output. The
        // approval channel is answered here because the terminating call goes through the hook like every
        // other one, so a refused hook is a turn with no document (see the class remarks).
        // With a per-session ceiling, because that is the opening command line a studio with a configured
        // limit sends now that typed turns are the default (backlog 62, backlog 25): the CLI has to ACCEPT
        // --max-budget-usd beside --json-schema, not only declare it. The ceiling is far above what one short
        // turn costs, so it constrains nothing here.
        var events = await DriveAsync(
            "What is 17 times 3? Do not use any tools.",
            model: CheapModel,
            kinds: AgentTurnDocumentSchema.DefaultKinds,
            answerApprovals: true,
            maxBudgetUsd: ContractCeilingUsd);

        var turn = events.OfType<TurnCompleted>().Last();
        turn.Document.Should().NotBeNull("the flag is what makes a turn end in a document");
        turn.Document!.Version.Should().Be(AgentTurnDocument.CurrentVersion);
        turn.Document.KnownKind.Should().Be(AgentTurnDocumentKind.Answer);
        turn.Document.Message.Should().NotBeNullOrWhiteSpace();

        // The turn's outcome, never a tool card beside Read and Edit.
        events.OfType<ToolCallStarted>().Should().NotContain(t => t.ToolName == AgentTurnDocumentSchema.ToolName);

        // Backlog 25's acceptance: one answer rendered once, and it is the document's. What is asserted is what a
        // reader sees OPEN, through the same fold the transcript renders with, and not the raw message count: the
        // parser commits every text block where it arrived, and on the reviewer's three runs of 2026-09-14 the
        // model wrote a second bubble in two of them (a markdown rewording of the answer, and a sentence about the
        // StructuredOutput tool). Counting raw messages passed only when the model happened to write none.
        events.OfType<MessageEmitted>().Where(m => m.Role == TranscriptRole.Assistant)
            .Should().Contain(m => m.Text.Trim() == turn.Document.Message.Trim(), "the answer is committed as a message");
        var transcript = events.ToList();
        var drafts = AgentTurnDrafts.Superseded(transcript);
        transcript.Select((e, i) => (e, i))
            .Where(x => x.e is MessageEmitted { Role: TranscriptRole.Assistant } && !drafts.Contains(x.i))
            .Select(x => ((MessageEmitted)x.e).Text.Trim())
            .Should().ContainSingle().Which.Should().Be(turn.Document.Message.Trim());
    }

    [LiveClaudeCliFact]
    public async Task A_typed_turn_keeps_the_working_indicator_fed_even_when_it_says_nothing()
    {
        // The old assertion here was "text still streams". Measured 2026-08-06: on a turn the model
        // answers straight through the tool there is no assistant text block at all, so PartialText is
        // the wrong thing to depend on. What every measured turn carried was thinking and json deltas.
        var events = await DriveAsync(
            "Reply with exactly: OK. Do not use any tools.",
            model: CheapModel,
            kinds: AgentTurnDocumentSchema.DefaultKinds,
            answerApprovals: true);

        events.OfType<TurnActivity>().Select(a => a.Phase).Should().NotBeEmpty(
            "a typed turn can stream no text at all, so the indicator is all the user has");
    }

    [LiveClaudeCliFact]
    public async Task A_continued_conversation_waits_for_the_next_turn_and_remembers_the_last()
    {
        // Backlog 25 increment 6 rests on this. When a turn exhausts its validation attempts the CLI exits,
        // and Continue starts --resume on the same conversation WITHOUT a prompt: the resumed process must
        // then wait on stdin rather than end or invent a turn, and the next turn the user writes must be
        // answered with the conversation's memory. Same working directory, because the CLI files a
        // conversation under the folder it ran in.
        var workingDirectory = Path.Combine(_scratch, "workspace-" + Guid.NewGuid().ToString("N"));
        var first = await DriveAsync(
            "Remember the word PELICAN. Reply with exactly: NOTED. Do not use any tools.",
            model: CheapModel,
            workingDirectory: workingDirectory);
        var provider = first.OfType<SessionRegistered>().Single().ProviderSessionId;

        var events = await DriveAsync(
            string.Empty,
            model: CheapModel,
            workingDirectory: workingDirectory,
            resumeSessionId: provider,
            sendAfterStart: "Which word did I ask you to remember? Reply with that one word only. Do not use any tools.");

        events.OfType<TurnCompleted>().Should().ContainSingle("nothing is sent on a continue, so the only turn is the follow-up");
        events.OfType<MessageEmitted>().Where(m => m.Role == TranscriptRole.Assistant)
            .Should().Contain(m => m.Text.Contains("PELICAN", StringComparison.OrdinalIgnoreCase),
                "the resumed process answers with the conversation it resumed");
    }

    [LiveClaudeCliFact]
    public async Task A_studio_tool_is_called_through_the_session_tool_server_and_recorded_once()
    {
        // Backlog 84 end to end against the real binary: the MCP config SessionHookWriter writes, the CLI
        // connecting to the studio's server with the session secret, the tool arriving deferred (the model has to
        // find it through ToolSearch), the PreToolUse hook parking the call like any other (answered here as the
        // user would), and the ledger recording the call under the CLI's own tool-use id.
        var events = await DriveAsync(
            "Call the studio_version tool from the fravashi MCP server, then reply with exactly the version "
            + "number it returned and nothing else.",
            model: CheapModel,
            approveStudioTools: true);

        var name = StudioToolServer.CliToolName(StudioVersionTool.ToolName);
        var started = events.OfType<ToolCallStarted>().Should().Contain(
            t => t.ToolName == name, "the model should reach the studio's tool through the session's MCP config").Which;
        events.OfType<ToolCallCompleted>().Should()
            .Contain(c => c.ToolUseId == started.ToolUseId && c.Success, "the studio's server answered the call")
            .Which.Detail.Should().Contain(ContractStudioVersion).And.Contain("toolServerProtocol");

        var session = events.OfType<SessionStarted>().Single().SessionId;
        var ledger = File.ReadAllText(new FileToolCallLedger(_scratch).PathFor(session));

        // Printed so a run can be quoted as evidence rather than summarised.
        output.WriteLine("tool call: " + started.ToolName + " " + started.ToolUseId);
        output.WriteLine("result: " + events.OfType<ToolCallCompleted>().First(c => c.ToolUseId == started.ToolUseId).Detail);
        output.WriteLine("ledger: " + ledger);
        ledger.Should().Contain("\"key\":\"" + started.ToolUseId + "\"", "the ledger keys the call on the CLI's tool-use id")
            .And.Contain("\"state\":\"completed\"");
    }

    [LiveClaudeCliFact]
    public async Task A_reading_conversation_starts_with_only_its_read_tools()
    {
        // Backlog 248. The commit reviewer (backlog 243) and the autopilot operator (backlog 300) are started with
        // --tools naming AgentReviewTools.BuiltIn, so Edit, Write and Bash do not exist for them. Started with the
        // runner's OWN command line for such a conversation (typed, with the tool server), the init line must list
        // those four and no other built-in tool. Measured by hand on 2026-10-08 against 2.1.280; this holds it.
        var tools = (await ProductionInitLineAsync(ReadingRequest())).GetProperty("tools").EnumerateArray()
            .Select(t => t.GetString() ?? string.Empty)
            .ToList();
        output.WriteLine("init tools: " + string.Join(", ", tools));

        tools.Where(t => !t.StartsWith("mcp__", StringComparison.Ordinal) && t != AgentTurnDocumentSchema.ToolName)
            .Should().BeEquivalentTo(AgentReviewTools.BuiltIn, "a reading conversation is started with exactly these built-in tools");
        tools.Should().NotContain(["Edit", "Write", "Bash", "NotebookEdit", "PowerShell"]);
    }

    [LiveClaudeCliFact]
    public async Task A_reading_conversation_still_reaches_the_studio_tools_through_tool_search()
    {
        // Backlog 248. The studio's tools arrive DEFERRED behind ToolSearch (backlog 84), and the reviewer loads
        // git_diff, work_item_read and wiki_read that way. Under --tools naming only the four reading tools, ToolSearch
        // must still find them: a typed turn, the tool server on, the studio's call approved as a person would, and the
        // turn ending in its document. studio_version stands for the rest because it is the contract server's one tool.
        var events = await DriveAsync(
            "Use ToolSearch to load the studio_version tool from the fravashi MCP server, call it once, and answer "
            + "with exactly the version number it returned.",
            model: CheapModel,
            kinds: AgentTurnDocumentSchema.DefaultKinds,
            answerApprovals: true,
            approveStudioTools: true,
            builtInTools: AgentReviewTools.BuiltIn);

        var name = StudioToolServer.CliToolName(StudioVersionTool.ToolName);
        var started = events.OfType<ToolCallStarted>().Should().Contain(
            t => t.ToolName == name, "ToolSearch should still reach the studio's tools in a reading conversation").Which;
        events.OfType<ToolCallCompleted>().Should()
            .Contain(c => c.ToolUseId == started.ToolUseId && c.Success, "the studio's server answered the call")
            .Which.Detail.Should().Contain(ContractStudioVersion);
        output.WriteLine("tool calls: " + string.Join(", ", events.OfType<ToolCallStarted>().Select(t => t.ToolName)));
        events.OfType<ToolCallStarted>().Should().NotContain(
            t => t.ToolName == "Edit" || t.ToolName == "Write" || t.ToolName == "Bash",
            "a reading conversation has no tool that changes anything");
        events.OfType<TurnCompleted>().Last().Document.Should().NotBeNull("a reading conversation ends in its document");
    }

    /// <summary>
    /// Drives one real session to completion: the opening prompt, an optional follow-up written to stdin
    /// after the first turn, then a stop. The stop is needed because, unlike the fake CLI the other tests
    /// use, the real one stays alive waiting for more stdin once a turn is done.
    /// <para>
    /// <paramref name="answerApprovals"/> chooses which approval channel the hook gets, and that choice is
    /// load-bearing rather than convenience: false points it at a port nothing listens on, which is how the
    /// gating test observes the script's fail-closed refusal, and true serves the studio's real endpoint
    /// over the same broker the runner registered the session with, which is the only way a call the studio
    /// auto-allows can actually be allowed.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<AgentEvent>> DriveAsync(
        string prompt,
        string? followUp = null,
        string? model = null,
        IReadOnlyList<AgentTurnDocumentKind>? kinds = null,
        bool answerApprovals = false,
        decimal? maxBudgetUsd = null,
        string? workingDirectory = null,
        string? resumeSessionId = null,
        string? sendAfterStart = null,
        bool approveStudioTools = false,
        IReadOnlyList<string>? builtInTools = null)
    {
        workingDirectory ??= Path.Combine(_scratch, "workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        using var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(30));
        using var approvals = answerApprovals || approveStudioTools ? new ApprovalChannel(broker) : null;
        using var tools = new ToolServerChannel(broker, new FileToolCallLedger(_scratch));
        if (approveStudioTools)
        {
            // Standing in for the user clicking Approve, and ONLY for the studio's own tools: anything else the
            // model tries still parks until the broker's timeout denies it.
            broker.Requested += pending =>
            {
                if (pending.ToolName.StartsWith("mcp__" + StudioToolServer.ServerName + "__", StringComparison.Ordinal))
                {
                    broker.Answer(pending.RequestId, approved: true, "contract test");
                }
            };
        }

        var runner = new ClaudeSessionRunner(
            broker,
            new SessionHookWriter(
                _scratch,
                approvals?.Url ?? "http://127.0.0.1:8080" + AgentApprovalEndpoint.Route,
                tools.Url),
            launcher: new AgentCliLauncher(RealClaudeCli.Path!, []),
            environment: RealClaudeCli.ChildEnvironment);

        var request = new AgentSessionRequest(
            AgentSessionId.New(), "contract", workingDirectory, prompt, resumeSessionId, model, kinds, maxBudgetUsd)
        {
            BuiltInTools = builtInTools,
        };

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(240));
        var events = new List<AgentEvent>();
        var followUpSent = false;
        try
        {
            await foreach (var e in runner.RunAsync(request, cancellation.Token))
            {
                events.Add(e);
                if (e is SessionStarted && sendAfterStart is not null)
                {
                    // A continue says nothing: the turn goes in only after the resumed process has been
                    // left waiting, which is the state the studio's Continue leaves it in.
                    _ = Task.Run(
                        async () =>
                        {
                            await Task.Delay(TimeSpan.FromSeconds(5), cancellation.Token);
                            await runner.SendAsync(request.SessionId, sendAfterStart, cancellation.Token);
                        },
                        cancellation.Token);
                }

                if (e is not TurnCompleted)
                {
                    continue;
                }

                if (followUp is not null && !followUpSent)
                {
                    followUpSent = true;
                    (await runner.SendAsync(request.SessionId, followUp, cancellation.Token))
                        .Should().BeTrue("the process should still be accepting stdin after a turn");
                    continue;
                }

                runner.Stop(request.SessionId);
            }
        }
        catch (OperationCanceledException)
        {
            throw new Xunit.Sdk.XunitException(
                "The real CLI produced no complete turn within 240s; the protocol may have shifted. Events seen: "
                + string.Join(", ", events.Select(e => e.GetType().Name)));
        }

        return events;
    }

    /// <summary>
    /// The init line of one real turn started with the studio's own settings file and the environment every agent
    /// child is restricted to, which are the two places backlog 174 keeps the signed-in account's connectors, plugins
    /// and skills out. The runner is not used because nothing it parses keeps the init line's server and plugin lists.
    /// </summary>
    private async Task<JsonElement> InitLineAsync()
    {
        var workingDirectory = Path.Combine(_scratch, "account-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        var files = new SessionHookWriter(_scratch, "http://127.0.0.1:8080" + AgentApprovalEndpoint.Route)
            .Write(AgentSessionId.New(), "contract-secret");

        var info = new System.Diagnostics.ProcessStartInfo(RealClaudeCli.Path!)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        AgentProcessEnvironment.Restrict(info);
        RealClaudeCli.ApplyTo(info);
        foreach (var argument in new[]
        {
            "-p", "Reply with exactly: OK. Do not use any tools.",
            "--output-format", "stream-json",
            "--verbose",
            "--settings", files.SettingsPath,
            "--model", CheapModel,
        })
        {
            info.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(info)!;
        process.StandardInput.Close();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var stdout = process.StandardOutput.ReadToEndAsync(cancellation.Token);
        var stderr = process.StandardError.ReadToEndAsync(cancellation.Token);
        await process.WaitForExitAsync(cancellation.Token);

        foreach (var line in (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("subtype", out var subtype) && subtype.GetString() == "init")
            {
                return document.RootElement.Clone();
            }
        }

        throw new Xunit.Sdk.XunitException("The real CLI wrote no init line. Its error output: " + await stderr);
    }

    /// <summary>
    /// A conversation's request as the commit reviewer and the autopilot operator send it (backlog 248): typed, on the
    /// cheap model, with its built-in tools narrowed to <see cref="AgentReviewTools.BuiltIn"/>.
    /// </summary>
    private static AgentSessionRequest ReadingRequest() =>
        new(AgentSessionId.New(), "contract", "/tmp", "hello", null, CheapModel, AgentTurnDocumentSchema.DefaultKinds)
        {
            BuiltInTools = AgentReviewTools.BuiltIn,
        };

    /// <summary>
    /// The init line of a session started with EXACTLY the runner's command line for <paramref name="request"/>
    /// (<see cref="ClaudeSessionRunner.BuildArguments"/> over the hook settings and the tool server's MCP config the
    /// writer produces) and the environment every agent child is restricted to. The prompt goes in on stdin the way the
    /// runner writes a turn. Only the init line is wanted, so the process is stopped once it has written it, and the
    /// approval URL points at nothing because no call is meant to run.
    /// </summary>
    private async Task<JsonElement> ProductionInitLineAsync(AgentSessionRequest request)
    {
        var workingDirectory = Path.Combine(_scratch, "init-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        using var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(30));
        using var tools = new ToolServerChannel(broker, new FileToolCallLedger(_scratch));
        var files = new SessionHookWriter(_scratch, "http://127.0.0.1:8080" + AgentApprovalEndpoint.Route, tools.Url)
            .Write(request.SessionId, "contract-secret");

        var info = new System.Diagnostics.ProcessStartInfo(RealClaudeCli.Path!)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        AgentProcessEnvironment.Restrict(info);
        RealClaudeCli.ApplyTo(info);
        foreach (var argument in ClaudeSessionRunner.BuildArguments(request, files.SettingsPath, files.ToolServerConfigPath))
        {
            info.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(info)!;
        await process.StandardInput.WriteLineAsync(ClaudeSessionRunner.UserTurn("Reply with exactly: OK. Do not use any tools."));
        await process.StandardInput.FlushAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var stderr = process.StandardError.ReadToEndAsync(cancellation.Token);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancellation.Token) is { } line)
            {
                if (!line.StartsWith('{'))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("subtype", out var subtype) && subtype.GetString() == "init")
                {
                    return document.RootElement.Clone();
                }
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }

        throw new Xunit.Sdk.XunitException("The real CLI wrote no init line. Its error output: " + await stderr);
    }

    /// <summary>
    /// One real turn whose PreToolUse hook is the studio's own settings and command with the script replaced
    /// by <paramref name="scriptBody"/> (null deletes it), run under <c>bypassPermissions</c> so the CLI's own
    /// permission checks cannot be what stops the call. Returns whether the command really ran (its file
    /// exists) and the raw stream, which carries the hook events.
    /// </summary>
    private async Task<(bool Ran, string Output)> DriveSabotagedHookAsync(string? scriptBody)
    {
        var workingDirectory = Path.Combine(_scratch, "hook-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        var files = new SessionHookWriter(_scratch, "http://127.0.0.1:8080" + AgentApprovalEndpoint.Route)
            .Write(AgentSessionId.New(), "contract-secret");
        if (scriptBody is null)
        {
            File.Delete(files.ScriptPath);
        }
        else
        {
            File.WriteAllText(files.ScriptPath, scriptBody);
        }

        var info = new System.Diagnostics.ProcessStartInfo(RealClaudeCli.Path!)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        RealClaudeCli.ApplyTo(info);
        foreach (var argument in new[]
        {
            "-p",
            "Use the Bash tool to run exactly this command: echo ran > ran.txt . Run it once. Do not ask first, "
                + "do not retry, and do not use any other tool.",
            "--output-format", "stream-json",
            "--verbose",
            "--include-hook-events",
            "--settings", files.SettingsPath,
            "--model", CheapModel,
            "--permission-mode", "bypassPermissions",
        })
        {
            info.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(info)!;
        process.StandardInput.Close();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var stdout = process.StandardOutput.ReadToEndAsync(cancellation.Token);
        var stderr = process.StandardError.ReadToEndAsync(cancellation.Token);
        await process.WaitForExitAsync(cancellation.Token);

        return (File.Exists(Path.Combine(workingDirectory, "ran.txt")), await stdout + await stderr);
    }

        /// <summary>
    /// The studio's approval endpoint, on loopback, for the turns whose hook has to be ANSWERED. It is the
    /// product's own <see cref="AgentApprovalEndpoint"/> over the runner's own broker, not a stub that says
    /// yes: a stub would pass this file green while the studio denied every terminating document.
    /// </summary>
    private sealed class ApprovalChannel : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stopping = new();

        public ApprovalChannel(AgentApprovalBroker broker)
        {
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}" + AgentApprovalEndpoint.Route;
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(() => ServeAsync(broker));
        }

        /// <summary>Where the session's hook script should post its pending calls.</summary>
        public string Url { get; }

        public void Dispose()
        {
            _stopping.Cancel();
            _listener.Close();
            _stopping.Dispose();
        }

        /// <summary>
        /// A port the OS just handed out and nothing holds. There is a race between closing the probe and
        /// binding the listener, which is why this is a test-only helper: the alternative is a fixed port,
        /// and two contract runs on one machine would then collide every time rather than never.
        /// </summary>
        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task ServeAsync(AgentApprovalBroker broker)
        {
            while (!_stopping.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                using var reader = new StreamReader(context.Request.InputStream);
                var body = await reader.ReadToEndAsync();
                var answer = await AgentApprovalEndpoint.HandleAsync(
                    context.Request.Headers[AgentApprovalEndpoint.SecretHeader],
                    body,
                    broker,
                    _stopping.Token);

                var bytes = Encoding.UTF8.GetBytes(answer.Json);
                context.Response.StatusCode = answer.StatusCode;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        }
    }

    /// <summary>The version the contract's tool server reports, so the live call can be told from any other answer.</summary>
    private const string ContractStudioVersion = "84.0.0-contract";

    /// <summary>
    /// The studio's tool server, on loopback, for every live turn: the product's own <see cref="StudioToolServer"/>
    /// over the runner's own broker, with the product's one shipped tool. Like <see cref="ApprovalChannel"/>, not a
    /// stub: a stub would answer whatever the test expected while the studio's server refused the CLI.
    /// </summary>
    private sealed class ToolServerChannel : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stopping = new();

        public ToolServerChannel(AgentApprovalBroker broker, IToolCallLedger ledger)
        {
            var server = new StudioToolServer(
                broker, [new StudioVersionTool(new ContractProductInfo())], ledger, ContractStudioVersion);
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            Url = $"http://127.0.0.1:{port}" + StudioToolServer.Route;
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(() => ServeAsync(server));
        }

        /// <summary>Where the session's MCP config points the CLI.</summary>
        public string Url { get; }

        public void Dispose()
        {
            _stopping.Cancel();
            _listener.Close();
            _stopping.Dispose();
        }

        private async Task ServeAsync(StudioToolServer server)
        {
            while (!_stopping.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                _ = Task.Run(() => AnswerAsync(server, context));
            }
        }

        private async Task AnswerAsync(StudioToolServer server, HttpListenerContext context)
        {
            try
            {
                using var reader = new StreamReader(context.Request.InputStream);
                var body = await reader.ReadToEndAsync();
                var answer = await server.HandleAsync(
                    new StudioToolHttpRequest(
                        context.Request.HttpMethod,
                        context.Request.Headers[StudioToolServer.SecretHeader],
                        context.Request.Headers[StudioToolServer.ProtocolVersionHeader],
                        context.Request.Headers["Origin"],
                        body),
                    _stopping.Token);

                context.Response.StatusCode = answer.StatusCode;
                if (answer.Allow is { } allow)
                {
                    context.Response.Headers["Allow"] = allow;
                }

                if (answer.Json is { } json)
                {
                    var bytes = Encoding.UTF8.GetBytes(json);
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                }

                context.Response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
            {
            }
        }

        private sealed class ContractProductInfo : IProductInfo
        {
            public string Version => ContractStudioVersion;

            public string Variant => "full";
        }
    }

    /// <summary>
    /// The per-session ceiling the contract sends, in USD: well above one short turn, so it constrains
    /// nothing, and present, so the flag it becomes is both scanned for and really accepted.
    /// </summary>
    private const decimal ContractCeilingUsd = 1m;

    /// <summary>
    /// Every flag <see cref="ClaudeSessionRunner.BuildArguments"/> can emit, taken from the production method
    /// rather than a list somebody has to remember to maintain. The full request carries a ceiling, so the
    /// conditional <c>--max-budget-usd</c> is in the list (ADR 0023, 2026-09-13 debt), and both carry the
    /// tool-server config production always writes, so <c>--mcp-config</c> is too (backlog 84). A reading
    /// conversation's request is the third, so the conditional <c>--tools</c> is in the list (backlog 248).
    /// </summary>
    private static IReadOnlyList<string> FlagsTheRunnerPasses()
    {
        var plain = new AgentSessionRequest(AgentSessionId.New(), "w", "/tmp", "hello", null, null);
        var full = new AgentSessionRequest(
            AgentSessionId.New(),
            "w",
            "/tmp",
            "hello",
            "ea12436c-b6eb-455e-b670-687beea9c6d2",
            "claude-haiku-4-5-20251001",
            AgentTurnDocumentSchema.DefaultKinds,
            ContractCeilingUsd);

        return
        [
            .. ClaudeSessionRunner.BuildArguments(plain, "/tmp/settings.json", "/tmp/tools.mcp.json")
                .Concat(ClaudeSessionRunner.BuildArguments(full, "/tmp/settings.json", "/tmp/tools.mcp.json"))
                .Concat(ClaudeSessionRunner.BuildArguments(ReadingRequest(), "/tmp/settings.json", "/tmp/tools.mcp.json"))
                .Where(a => a.StartsWith('-'))
                .Distinct(StringComparer.Ordinal),
        ];
    }
}
