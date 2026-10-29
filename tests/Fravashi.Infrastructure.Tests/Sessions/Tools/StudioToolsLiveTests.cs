using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using Fravashi.Application.Sessions;
using Fravashi.Application.Sessions.Tools;
using Fravashi.Application.Wiki;
using Fravashi.Application.WorkItems;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Projects;
using Fravashi.Domain.Wiki;
using Fravashi.Domain.WorkItems;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Infrastructure.Sessions.Tools;
using Fravashi.Infrastructure.Tests.Persistence;
using Fravashi.Infrastructure.Wiki;
using Fravashi.Infrastructure.WorkItems;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Sessions.Tools;

/// <summary>
/// The LIVE proof backlog 41 and 42 asked for, and the thing their siblings <see cref="WorkItemToolsTests"/>
/// and <see cref="WikiToolsTests"/> deliberately cannot be: there, the studio calls its own tool server with
/// arguments a person wrote, which proves the server and the store and nothing about whether a real model can
/// drive them. Here a real agent CLI, a real model and the studio's real tool server meet over one
/// conversation, and the acceptance sentence is read back off the board and the wiki afterwards rather than
/// out of the answers.
/// <para>
/// Why this is a NEW file rather than four more tests in <c>ClaudeCliContractTests</c>: that file is
/// FINGERPRINTED by <c>AgentCliPinTests</c> as the file someone drove against the pinned binary (ADR 0023), so
/// growing it silently un-earns the "protocol verified" claim the transcript makes. This file makes no claim
/// about the protocol; it claims that item 41's acceptance holds end to end.
/// </para>
/// <para>
/// It SKIPS unless <c>FRAVASHI_CLAUDE_CLI</c> points at a CLI and <c>FRAVASHI_CLI_CONTRACT_LIVE=1</c> is set,
/// because each test starts a real CLI and takes real billable turns. The model is pinned to the cheap one
/// wherever the question is whether the schemas are usable by a model at all, rather than whether a frontier
/// model can be clever.
/// </para>
/// <para>
/// <b>What was measured, 2026-09-20</b>, against claude 2.1.222 in a throwaway prefix on the owner's host
/// (the pin; the studio's own install and container were not touched). Item 41's four turns ran on
/// claude-haiku-4-5: the model found the item, created the child, wrote both acceptance criteria and made the
/// link, and every one of the three changes was parked on the approval hook first. It reached each tool
/// through ToolSearch, which is how a studio tool arrives (deferred), and it stated the link in the inverse
/// reading ("2 blocks 3" rather than "3 blocked_by 2"), which the store turns round for itself. The second
/// test resumed that conversation on the ACCOUNT DEFAULT model, measured as <c>claude-opus-5[1m]</c>: it
/// answered with a word only the earlier model had been told (item 113: the transcript IS carried across a
/// model switch) and emitted 57 partial-text events on a four-sentence answer (item 120: the default model
/// streams normally; the old zero-stream observation was about Fable, which is no longer the account default).
/// Item 42's turns found a document, answered with the citation the tool handed back, wrote a plan and linked
/// it to a work item; asked for "unattended change" the find returned NOTHING, because it is a substring over
/// title and body, and the model recovered only by retrying with one word. That is the gap item 40 fills.
/// </para>
/// </summary>
public sealed class StudioToolsLiveTests : IDisposable
{
    /// <summary>The model every turn here runs on. See ADR 0023 and the money discipline in item 41.</summary>
    private const string CheapModel = "claude-haiku-4-5-20251001";

    private readonly string _scratch =
        Path.Combine(Path.GetTempPath(), "fravashi-studio-live-" + Guid.NewGuid().ToString("N"));

    private readonly SqliteTestContext _store = new();
    private readonly WorkItemChanges _changes = new();
    private readonly LocalTrackerResolver _trackers;
    private readonly ITracker _board;
    private readonly SqliteWikiLibrary _wiki;

    public StudioToolsLiveTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        Output = output;
        _trackers = new LocalTrackerResolver(_store.Factory, TimeProvider.System);
        _board = _trackers.For(ProjectId.Default);
        _wiki = new SqliteWikiLibrary(_store.Factory, TimeProvider.System, Fravashi.Domain.Projects.ProjectId.Default);
        Directory.CreateDirectory(_scratch);
    }

    private Xunit.Abstractions.ITestOutputHelper Output { get; }

    public void Dispose()
    {
        _store.Dispose();
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

    [LiveClaudeCliFact]
    public async Task A_live_session_finds_an_item_creates_a_child_writes_its_acceptance_and_links_it()
    {
        var feature = await SeedAsync("Resume a conversation in place", WorkItemLevel.Feature);
        var blocker = await SeedAsync("Redact the transcript before it is stored", WorkItemLevel.Feature);

        var approvals = new List<string>();
        var announcements = 0;
        _changes.Changed += _ => Interlocked.Increment(ref announcements);

        // The model's own words are discarded on purpose here: every clause of this acceptance is read back
        // off the board below, and a test that believed the answers would pass on a model that said DONE
        // four times and called nothing.
        var (transcript, _) = await DriveAsync(
            approvals,
            [
                "Use the fravashi MCP server's tools for everything below; do not read or write any files. "
                + "Find the work item whose title mentions \"Resume a conversation\". "
                + "Reply with its number and nothing else.",

                "Create a child work item under that item. Level: increment. Title: "
                + "\"Keep the CLI conversation file\". Description: \"The per-session config home is deleted at "
                + "teardown, so a resumed conversation has nothing to resume from.\" "
                + "Reply with the new item's number and nothing else.",

                "Give that new item exactly two acceptance criteria: \"A resumed conversation shows its earlier "
                + "turns.\" and \"A second turn appends to the same file.\" Reply with the word DONE and nothing "
                + "else.",

                "Link that new item as blocked by the work item titled \"Redact the transcript before it is "
                + "stored\". Reply with the word DONE and nothing else.",
            ]);

        // ---- the acceptance, read off the BOARD rather than out of the model's answers ----------
        var children = (await _board.QueryAsync(new WorkItemQuery())).Items
            .Where(i => i.Parent == feature.Id)
            .ToList();

        children.Should().ContainSingle(
            "the session was asked for one child; the transcript was:\n" + transcript).Which.Should().NotBeNull();

        // Re-read through GetByNumberAsync rather than trusting the query row. QueryAsync selects from
        // work_items alone and hydrates no child table, so acceptance criteria, scenarios, readiness and refs
        // come back EMPTY on every item it returns. That is deliberate (a board page does not need them) and it
        // reads exactly like the product having dropped them: the first run of this test failed on an empty
        // AcceptanceCriteria while the tool answer in the transcript showed both criteria written.
        var child = (await _board.GetByNumberAsync(children[0].Number))!;

        child.Level.Should().Be(WorkItemLevel.Increment);
        child.Description.Should().NotBeNullOrWhiteSpace();
        child.AcceptanceCriteria.Should().HaveCount(
            2, "the third turn asked for exactly two; the transcript was:\n" + transcript);

        var links = await _board.LinksAsync(child.Id);
        links.Should().ContainSingle("the fourth turn asked for one link; the transcript was:\n" + transcript)
            .Which.ReadingFrom(child.Id).Should().Be(WorkItemLinkType.BlockedBy);
        links[0].Touches(blocker.Id).Should().BeTrue("the link names the item the fourth turn pointed at");

        // Every mutation is attributable to the session that made it, and to a SESSION rather than the user.
        var history = await _board.HistoryAsync(child.Id);
        history.Should().NotBeEmpty();
        history.Should().OnlyContain(h => h.Actor.Kind == WorkItemActorKind.Session);
        history.Select(h => h.Actor.SessionId).Distinct().Should().ContainSingle(
            "one conversation made every change to this item");

        // Mutating tools went through the existing approval path: the studio ASKED before each of the three
        // changes, and this test answered as the user would. A tool that mutated without asking would not
        // appear here at all, which is the failure this assertion exists to catch.
        approvals.Should().Contain(StudioToolServer.CliToolName(WorkItemCreateTool.ToolName));
        approvals.Should().Contain(StudioToolServer.CliToolName(WorkItemUpdateTool.ToolName));
        approvals.Should().Contain(StudioToolServer.CliToolName(WorkItemLinkTool.ToolName));

        // And the board was told, which is what makes the change appear in the UI without a reload.
        announcements.Should().BeGreaterThanOrEqualTo(3);

        Output.WriteLine(transcript);
    }

    [LiveClaudeCliFact]
    public async Task A_live_session_cites_a_document_it_found_and_writes_a_plan_linked_to_a_work_item()
    {
        // Item 42's live half. The OTHER half of its acceptance, finding a document BY MEANING, is item 40's
        // embedding index and is not built, so this drives what exists: finding by the words in a document,
        // citing it in the form the answer hands the model, and writing a plan linked to the work item it
        // affects with the link type stated.
        var affected = await SeedAsync("Resume a conversation in place", WorkItemLevel.Feature);
        var target = await WikiAsync(
            "How the approval gate works",
            WikiItemType.Governance,
            "Nothing an agent does changes a file unattended: every change is parked until the user answers.");
        await WikiAsync("Something else entirely", WikiItemType.Draft, "Unrelated prose about the weather.");

        var approvals = new List<string>();
        var (transcript, assistant) = await DriveAsync(
            approvals,
            [
                "Use the fravashi MCP server's tools for everything below; do not read or write any files. "
                + "Which of the studio's documents explains what happens to a change made unattended? "
                + "Reply with its citation reference exactly as the tool gave it to you, and nothing else.",

                "Write a new document of type plan, titled \"Plan: keeping the conversation file\", with a body "
                + "explaining that the CLI's conversation file is deleted at teardown so a resume has nothing to "
                + "resume from. Reply with the new document's number and nothing else.",

                "Link that plan to the work item titled \"Resume a conversation in place\", as a work item link. "
                + "Reply with the word DONE and nothing else.",
            ],
            WikiTools());

        // The citation it was asked for is the one the tool offers, so an agent never invents the syntax.
        // Asserted on the MODEL's words, not the transcript: the find tool's own result carries
        // "reference":"@1", so a transcript assertion here would pass without the model saying anything.
        assistant.Should().Contain(
            "@" + target.Number.ToString(CultureInfo.InvariantCulture),
            "the first turn asked for the reference the tool handed back; the transcript was:\n" + transcript);

        var plans = await _wiki.ListAsync(new WikiItemQuery { Types = [WikiItemType.Plan] });
        plans.Should().ContainSingle("the session was asked for one plan; the transcript was:\n" + transcript);

        var plan = (await _wiki.GetByNumberAsync(plans[0].Number))!;
        plan.Body.Should().NotBeNullOrWhiteSpace();
        var link = plan.Links.Should().ContainSingle(
            "the third turn asked for one link; the transcript was:\n" + transcript).Subject;
        link.Kind.Should().Be(WikiLinkKind.WorkItem);
        link.Number.Should().Be(affected.Number);

        // And the work item can be asked what points at it, which is what makes a plan findable from the board.
        (await _wiki.BacklinksAsync(new WikiReference(WikiReferenceKind.WorkItem, affected.Number)))
            .Should().ContainSingle().Which.Number.Should().Be(plan.Number);

        // Both wiki changes asked first, like every other change.
        approvals.Should().Contain(StudioToolServer.CliToolName(WikiWriteTool.ToolName));
        approvals.Should().Contain(StudioToolServer.CliToolName(WikiLinkTool.ToolName));

        Output.WriteLine(transcript);
    }

    [LiveClaudeCliFact]
    public async Task A_model_switch_mid_conversation_carries_the_conversation_and_reports_its_streaming()
    {
        // Items 113 and 120 in ONE billed pair of turns, because they ask about the same event: a resume that
        // changes the model. 113 asks whether the new model receives the earlier transcript; 120 asks whether
        // the ACCOUNT DEFAULT model emits partial-text events at all, since a conversation on a model that
        // streams nothing shows the user a blank panel until the turn ends. Both were blocked until now on the
        // container CLI's expired credential; driven from the host against a throwaway copy of the pinned
        // binary, that blocker does not apply.
        var workingDirectory = Path.Combine(_scratch, "switch");
        Directory.CreateDirectory(workingDirectory);

        var first = await RunOneTurnAsync(
            workingDirectory,
            "Remember the word ORRERY. Reply with exactly: NOTED. Do not use any tools.",
            CheapModel,
            null);

        var provider = first.Events.OfType<SessionRegistered>().Single().ProviderSessionId;
        first.Text.Should().Contain("NOTED");

        // The switch: same conversation, resumed, with NO --model, which is the account default.
        var second = await RunOneTurnAsync(
            workingDirectory,
            "Which word did I ask you to remember? Start your reply with that one word, then write four "
            + "sentences about what such a device is. Do not use any tools.",
            null,
            provider);

        var model = second.Events.OfType<SessionRegistered>().LastOrDefault()?.Model;
        var partials = second.Events.OfType<PartialText>().Count();
        Output.WriteLine($"item 113/120: the resumed turn ran on {model ?? "(unreported)"} "
            + $"and emitted {partials.ToString(CultureInfo.InvariantCulture)} PartialText event(s).");
        Output.WriteLine(second.Text);

        // Item 113's acceptance: the switched-to model answers with a fact only the earlier model was told.
        second.Text.Should().Contain(
            "ORRERY",
            "a resumed conversation carries its transcript across a model change; the turn said:\n" + second.Text);

        // Item 120's measurement, asserted rather than only printed, so a regression to a silent stream is
        // caught rather than noticed. If this ever goes red the finding has changed and item 120 reopens.
        // Several deltas, not one: a one-word answer can legitimately arrive as a single chunk, which would say
        // nothing about whether a real answer streams. The prompt asks for four sentences for exactly that
        // reason, so a passing count here means the panel fills as the model writes.
        partials.Should().BeGreaterThan(
            3,
            "the account default model streams partial text, so the panel is not blank until the turn ends");
    }

    // ---- the harness ---------------------------------------------------------------------------

    /// <summary>One turn on the real CLI with no studio tools wired: the smallest thing that can be measured.</summary>
    private async Task<(IReadOnlyList<AgentEvent> Events, string Text)> RunOneTurnAsync(
        string workingDirectory, string prompt, string? model, string? resumeSessionId)
    {
        using var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(30));
        using var approvalChannel = new LoopbackChannel(
            async request =>
            {
                var answer = await AgentApprovalEndpoint.HandleAsync(
                    request.Secret, request.Body, broker, CancellationToken.None);
                return new EndpointAnswer(answer.StatusCode, answer.Json);
            },
            AgentApprovalEndpoint.Route);

        var runner = new ClaudeSessionRunner(
            broker,
            new SessionHookWriter(_scratch, approvalChannel.Url, approvalChannel.Url),
            launcher: new AgentCliLauncher(RealClaudeCli.Path!, []));

        var request = new AgentSessionRequest(
            AgentSessionId.New(), "live-113", workingDirectory, prompt, resumeSessionId, model, null, 2m);

        var events = new List<AgentEvent>();
        var text = new StringBuilder();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await foreach (var e in runner.RunAsync(request, cancellation.Token))
            {
                events.Add(e);
                if (e is MessageEmitted { Role: TranscriptRole.Assistant } message)
                {
                    text.Append(message.Text).Append('\n');
                }

                if (e is TurnCompleted)
                {
                    runner.Stop(request.SessionId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw new Xunit.Sdk.XunitException("The live turn did not complete in time. It said:\n" + text);
        }

        return (events, text.ToString());
    }


    /// <summary>
    /// Runs one conversation through the real CLI, sending each prompt as its own turn, auto-approving the
    /// studio's own tools and recording which ones were asked about.
    /// <para>
    /// It returns the model's own words SEPARATELY from the full transcript, and that separation is
    /// load-bearing rather than tidy. The transcript carries tool results too, and a tool result contains
    /// the very strings a turn is asked to repeat back (the wiki find hands back <c>"reference":"@1"</c>),
    /// so asserting on the transcript would pass whether the model answered or said nothing at all. The
    /// transcript is for the FAILURE MESSAGE; the assistant text is what an assertion may look at.
    /// </para>
    /// </summary>
    private async Task<(string Transcript, string Assistant)> DriveAsync(
        List<string> approvals, IReadOnlyList<string> turns, IStudioTool[]? tools = null)
    {
        var workingDirectory = Path.Combine(_scratch, "workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);

        using var broker = new AgentApprovalBroker(TimeSpan.FromSeconds(60));
        using var approvalChannel = new LoopbackChannel(
            async request =>
            {
                var answer = await AgentApprovalEndpoint.HandleAsync(
                    request.Secret, request.Body, broker, CancellationToken.None);
                return new EndpointAnswer(answer.StatusCode, answer.Json);
            },
            AgentApprovalEndpoint.Route);

        var toolServer = new StudioToolServer(
            broker,
            tools ?? BoardTools(),
            new FileToolCallLedger(Path.Combine(_scratch, "ledger-" + Guid.NewGuid().ToString("N"))),
            "41.0.0-live");

        using var toolChannel = new LoopbackChannel(
            async request =>
            {
                var answer = await toolServer.HandleAsync(new StudioToolHttpRequest(
                    request.Method, request.Secret, request.ProtocolVersion, request.Origin, request.Body));
                return new EndpointAnswer(answer.StatusCode, answer.Json, answer.Allow);
            },
            StudioToolServer.Route);

        broker.Requested += pending =>
        {
            lock (approvals)
            {
                approvals.Add(pending.ToolName);
            }

            // The user's answer, given the way the UI gives it. Only the studio's own tools are answered:
            // anything else is left to time out, because this session was told to use no others.
            if (pending.ToolName.StartsWith("mcp__" + StudioToolServer.ServerName + "__", StringComparison.Ordinal))
            {
                broker.Answer(pending.RequestId, approved: true, "item 41 live proof");
            }
        };

        var runner = new ClaudeSessionRunner(
            broker,
            new SessionHookWriter(_scratch, approvalChannel.Url, toolChannel.Url),
            launcher: new AgentCliLauncher(RealClaudeCli.Path!, []));

        var request = new AgentSessionRequest(
            AgentSessionId.New(),
            "live-41",
            workingDirectory,
            turns[0],
            null,
            CheapModel,
            null,
            MaxBudgetUsd: 2m);

        var log = new StringBuilder();
        var assistant = new StringBuilder();
        var sent = 1;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        try
        {
            await foreach (var e in runner.RunAsync(request, cancellation.Token))
            {
                // The transcript records TOOL traffic as well as prose, because the interesting live failure is
                // a model that answers DONE without having called anything, and prose alone cannot tell that
                // apart from a tool that ran and refused.
                switch (e)
                {
                    case MessageEmitted message:
                        log.Append(CultureInfo.InvariantCulture, $"[{message.Role}] {message.Text}\n");
                        if (message.Role == TranscriptRole.Assistant)
                        {
                            assistant.Append(message.Text).Append('\n');
                        }

                        break;
                    case ToolCallStarted started:
                        log.Append(CultureInfo.InvariantCulture, $"  -> {started.ToolName} {started.Detail}\n");
                        break;
                    case ToolCallCompleted completed:
                        log.Append(CultureInfo.InvariantCulture,
                            $"  <- {completed.ToolName} success={completed.Success} {completed.Detail}\n");
                        break;
                    case ToolCallRefused refused:
                        log.Append(CultureInfo.InvariantCulture, $"  xx {refused.ToolName} refused\n");
                        break;
                    default:
                        break;
                }

                if (e is not TurnCompleted)
                {
                    continue;
                }

                if (sent < turns.Count)
                {
                    log.Append(CultureInfo.InvariantCulture, $"[turn {sent + 1}] {turns[sent]}\n");
                    (await runner.SendAsync(request.SessionId, turns[sent], cancellation.Token))
                        .Should().BeTrue("the process should still accept stdin between turns");
                    sent++;
                    continue;
                }

                runner.Stop(request.SessionId);
            }
        }
        catch (OperationCanceledException)
        {
            throw new Xunit.Sdk.XunitException(
                $"The live session did not finish {turns.Count} turns in time. It got through {sent}. "
                + "What it said:\n" + log);
        }

        return (log.ToString(), assistant.ToString());
    }

    /// <summary>The board's seven tools, which is what a session gets in production.</summary>
    private IStudioTool[] BoardTools() =>
    [
        new WorkItemFindTool(_trackers, _changes),
        new WorkItemReadTool(_trackers, _changes),
        new WorkItemStartableTool(_trackers, _changes),
        new WorkItemCreateTool(_trackers, _changes),
        new WorkItemUpdateTool(_trackers, _changes),
        new WorkItemTransitionTool(_trackers, _changes),
        new WorkItemLinkTool(_trackers, _changes),
    ];

    /// <summary>The wiki's four, plus the board's, because a plan names work items and has to find them.</summary>
    private IStudioTool[] WikiTools() =>
    [
        .. BoardTools(),
        new WikiFindTool(new OneWiki(_wiki), _trackers),
        new WikiReadTool(new OneWiki(_wiki), _trackers),
        new WikiWriteTool(new OneWiki(_wiki), _trackers),
        new WikiLinkTool(new OneWiki(_wiki), _trackers),
    ];

    private async Task<WikiItem> WikiAsync(string title, WikiItemType type, string body)
    {
        var created = await _wiki.CreateAsync(new NewWikiItem { Type = type, Title = title, Body = body });
        created.IsValid.Should().BeTrue(string.Join("; ", created.Errors.Select(e => e.Code)));
        return created.Value;
    }

    private async Task<WorkItem> SeedAsync(string title, WorkItemLevel level)
    {
        var created = await _board.CreateAsync(
            new NewWorkItem { Level = level, Title = title, Description = "Why it exists." },
            MutationContext.User);
        created.Outcome.Should().Be(TrackerOutcome.Ok, string.Join("; ", created.Errors.Select(e => e.Code)));
        return created.Value;
    }

    /// <summary>What a loopback endpoint answers: the same three fields both studio endpoints produce.</summary>
    private sealed record EndpointAnswer(int StatusCode, string? Json, string? Allow = null);

    /// <summary>The pieces of an inbound request either studio endpoint looks at.</summary>
    private sealed record EndpointRequest(
        string Method, string? Secret, string? ProtocolVersion, string? Origin, string Body);

    /// <summary>
    /// One studio endpoint served on loopback, so the CLI really reaches the product's own code rather than a
    /// stub that would agree with whatever this test expected. The two channels in
    /// <c>ClaudeCliContractTests</c> do the same job and are deliberately NOT shared with this file: sharing
    /// them would mean editing the file ADR 0023 fingerprints, which would un-earn a claim about a binary in
    /// order to tidy up a test.
    /// </summary>
    private sealed class LoopbackChannel : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stopping = new();

        public LoopbackChannel(Func<EndpointRequest, Task<EndpointAnswer>> handle, string route)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            Url = $"http://127.0.0.1:{port}" + route;
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(() => ServeAsync(handle));
        }

        public string Url { get; }

        public void Dispose()
        {
            _stopping.Cancel();
            _listener.Close();
            _stopping.Dispose();
        }

        private async Task ServeAsync(Func<EndpointRequest, Task<EndpointAnswer>> handle)
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

                // Answered off the accept loop: an approval parks until the user answers, and the CLI opens a
                // second connection for its next tool call while it waits.
                _ = Task.Run(() => AnswerAsync(handle, context));
            }
        }

        private async Task AnswerAsync(Func<EndpointRequest, Task<EndpointAnswer>> handle, HttpListenerContext context)
        {
            try
            {
                using var reader = new StreamReader(context.Request.InputStream);
                var body = await reader.ReadToEndAsync();
                var answer = await handle(new EndpointRequest(
                    context.Request.HttpMethod,
                    context.Request.Headers[StudioToolServer.SecretHeader],
                    context.Request.Headers[StudioToolServer.ProtocolVersionHeader],
                    context.Request.Headers["Origin"],
                    body));

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
    }
}
