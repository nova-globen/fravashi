using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.AgentRuntimes.Broker;
using Fravashi.Application.AgentSync;
using Fravashi.Application.Sessions;
using Fravashi.Application.Telemetry;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Telemetry;
using Fravashi.Infrastructure.AgentRuntimes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Fravashi.Domain;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>How the agent CLI is launched. Tests point this at a stand-in; production runs the real one.</summary>
public sealed record AgentCliLauncher(string FileName, IReadOnlyList<string> LeadingArguments)
{
    public static AgentCliLauncher Claude { get; } = new("claude", []);
}

/// <summary>
/// Runs a session against the agent CLI's structured protocol (plan 2e Task 4). There is no terminal
/// emulator and no screen scraping: the CLI is started headless with stream-json in BOTH directions,
/// its stdout is parsed into domain events, and user turns are written to its stdin as stream-json.
/// <para>
/// The process is long-lived, which is what makes decision 6 work: one process serves many
/// turns, so the session keeps running while the window is away and the transcript is still there
/// when it comes back.
/// </para>
/// <para>
/// The flags below and the event shapes <see cref="ClaudeStreamJsonParser"/> expects were established
/// against claude 2.1.221 on 2026-08-04 and re-driven, live, against
/// <c>AgentRuntimeDescriptor.ProtocolVerifiedVersion</c> (2.1.222), most recently on 2026-09-15 when every
/// session gained the studio's tool server through <c>--mcp-config</c> (backlog 84), after 2026-09-14 made typed
/// turns the default and put the spend ceiling in the recorded vectors (backlog 25), which is why the
/// product pins it (backlog 61 / ADR 0023). The
/// claim is <c>ClaudeCliContractTests</c>, not this comment: running that file green against a binary is
/// what earns the recorded version, and <c>AgentCliPinTests</c> holds the file it was run against so the
/// claim cannot outlive it. A CLI that is not the pinned one still runs, but the transcript says so up front.
/// </para>
/// <para>
/// Containment (plan 2d decision 2) is absolute: the child's working directory is the workspace's
/// CONTAINED copy, never a host path. The child's ENVIRONMENT is an allowlist for the same reason
/// (<see cref="Fravashi.Infrastructure.AgentRuntimes.AgentProcessEnvironment"/>, backlog 72), so the
/// studio's host-bridge credential is not in it, and the copy is handed to the approval broker so a
/// read that leaves it asks the user instead of running unattended (<see cref="AgentReadScope"/>).
/// </para>
/// </summary>
public sealed class ClaudeSessionRunner : IAgentSessionRunner
{
    private readonly IAgentApprovalBroker _broker;
    private readonly SessionHookWriter _hooks;
    private readonly SessionFrameworkProjector? _framework;
    private readonly CliTranscriptScrubber? _transcripts;
    private readonly IWorkspaceAgentSync? _sync;
    private readonly IAgentRuntimeCredentialStore? _credentials;
    private readonly NodePathProvisioner? _nodePaths;
    private readonly AgentCliLauncher _launcher;
    private readonly IReadOnlyDictionary<string, string>? _environment;
    private readonly IAgentRuntimeStatusReport? _runtimeReport;

    // Backlog 154: the variables the session's PROJECT hands its agents, asked by workspace id at each start so a
    // change in Settings applies to the next process. Null on a wiring without projects.
    private readonly Func<string, IReadOnlyDictionary<string, string>>? _workspaceEnvironment;

    // Backlog 74: this class is the only one that knows the difference between "the agent CLI is not
    // there" and "a session failed later", because it is the one that tries to launch it.
    private readonly IProductEventRecorder _telemetry;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<AgentSessionId, LiveSession> _live = new();

    public ClaudeSessionRunner(
        IAgentApprovalBroker broker,
        SessionHookWriter hooks,
        IWorkspaceAgentSync? sync = null,
        IAgentRuntimeCredentialStore? credentials = null,
        NodePathProvisioner? nodePaths = null,
        AgentCliLauncher? launcher = null,
        IReadOnlyDictionary<string, string>? environment = null,
        IAgentRuntimeStatusReport? runtimeReport = null,
        Func<DateTimeOffset>? clock = null,
        ILogger<ClaudeSessionRunner>? logger = null,
        SessionFrameworkProjector? framework = null,
        IProductEventRecorder? telemetry = null,
        CliTranscriptScrubber? transcripts = null,
        Func<string, IReadOnlyDictionary<string, string>>? workspaceEnvironment = null)
    {
        _telemetry = telemetry ?? IProductEventRecorder.Off;
        _transcripts = transcripts;
        _workspaceEnvironment = workspaceEnvironment;
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _hooks = hooks ?? throw new ArgumentNullException(nameof(hooks));
        _framework = framework;
        _sync = sync;
        _credentials = credentials;
        _nodePaths = nodePaths;
        _launcher = launcher ?? AgentCliLauncher.Claude;
        _environment = environment;
        _runtimeReport = runtimeReport;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _logger = logger ?? NullLogger<ClaudeSessionRunner>.Instance;
    }

    /// <summary>The model a run uses for <paramref name="requested"/>: as asked, unless the broker does not cover it.</summary>
    private AgentModelChoice ModelChoiceFor(string? requested) =>
        _credentials?.ModelFor(AgentTool.Claude, requested) ?? AgentModelChoice.AsAsked(requested);

    /// <summary>True while this process holds a live session with that id.</summary>
    public bool IsRunning(AgentSessionId sessionId) => _live.ContainsKey(sessionId);

    public async IAsyncEnumerable<AgentEvent> RunAsync(
        AgentSessionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Infra D46: where the credential broker carries the CLI, its free tier serves one model, so a role's or a
        // person's choice it does not cover runs on that model instead, and the transcript says so below.
        var choice = ModelChoiceFor(request.Model);
        request = request with { Model = choice.Model };

        var installed = await AwaitFirstInstallAsync(cancellationToken).ConfigureAwait(false);
        var start = Start(request);

        // The child is running from here on, and the events below are handed to the caller before the
        // teardown further down owns it. A caller that throws while handling one of them, or stops reading
        // (disposing this enumerator at a yield), used to leave the child running with its hook script on
        // disk. Session ids are reused by an in-place resume, so that orphan's hook would then have
        // authenticated with the NEXT process's secret once the resume rewrote the script. Until the main
        // try takes over, this finally is what kills the child and takes the secret away.
        var handedOver = false;
        try
        {
            // Whether this process was asked for typed turns rides on the studio's own first event, so a
            // transcript can tell outcome ii (a typed session's turn that came back as prose) from an ordinary
            // untyped turn, live and after a restart alike. The same predicate the flag and the approval gate read.
            yield return new SessionStarted(_clock(), request.SessionId, request.TurnsAreTyped);

            if (installed is { } waited)
            {
                yield return new MessageEmitted(_clock(), TranscriptRole.System, waited);
            }

            // Backlog 61: a CLI the product does not recognise degrades with an honest message rather than a
            // strange failure halfway through. It still runs, because refusing would be worse than warning.
            if (PinWarning() is { } warning)
            {
                yield return new MessageEmitted(_clock(), TranscriptRole.System, warning);
            }

            if (choice.Notice is { } substituted)
            {
                yield return new MessageEmitted(_clock(), TranscriptRole.System, substituted);
            }

            handedOver = true;
        }
        finally
        {
            if (!handedOver)
            {
                start.Session?.KillAndWait();
                _live.TryRemove(request.SessionId, out _);
                Cleanup(request.SessionId);
            }
        }

        if (start.Session is not { } live)
        {
            Cleanup(request.SessionId);
            yield return new SessionEnded(_clock(), AgentSessionState.Failed, start.Failure);
            yield break;
        }

        var channel = Channel.CreateUnbounded<AgentEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        // The parser deliberately does NOT know whether a schema went out with the process. It commits
        // every assistant text block where it arrives either way, and the document at the result line is
        // suppressed only when it repeats a bubble already committed, which needs no such flag.
        var parser = new ClaudeStreamJsonParser(_clock, () => _credentials?.BrokerCarries(AgentTool.Claude) == true);
        var errors = new StringBuilder();
        using var pumps = new CancellationTokenSource();

        // One task owns every child this session runs, in order. The usual session has exactly one; a
        // model change (ChangeModel) is the only way there is ever a second, and it stays the only
        // writer, so SingleWriter above remains true.
        var pump = Task.Run(
            () => PumpChildrenAsync(request, live, parser, channel.Writer, errors, pumps.Token),
            CancellationToken.None);

        try
        {
            // The opening user turn goes in over stdin, exactly like every follow-up (decision
            // 6). It is INSIDE the try because a caller who cancels this early would otherwise leave
            // the process running and the session's secret on disk.
            //
            // A blank prompt is a CONTINUE (backlog 25 increment 6): the conversation is resumed with
            // nothing said, and the child idles on stdin until the user's next turn arrives through
            // SendAsync, exactly as a model-change restart does. Measured against the pinned 2.1.222 on
            // 2026-09-14: a --resume child given no stdin for eight seconds kept waiting, then answered a
            // follow-up with the resumed conversation's memory.
            //
            // Pasted images make a turn even with no words (backlog 26), so they are not a Continue.
            if (!string.IsNullOrWhiteSpace(request.Prompt) || request.PromptImages is { Count: > 0 })
            {
                // Work item #323 (git), extended by #333 (the work gate's steps and the commit reviewer's
                // failure classes): told once, on a brand new, ordinary session's opening turn only. Not on a
                // resume (--resume keeps the same conversation; a conversation begun on a build before this
                // change and resumed after an upgrade or a cut-off is not told either, and learns the facts only
                // by meeting the refusal or the review that would have named them). Not on a later turn
                // (SendAsync passes no note). And not on any session BuiltInTools narrows (backlog 243's commit
                // reviewer and backlog 299's autopilot operator both set it to AgentReviewTools.BuiltIn): those
                // sessions are held to reading and cannot commit at all, so telling them to use git_commit, or to
                // check a commit against the reviewer's classes, would name a tool or a step they do not have.
                //
                // Work item #352: a graph-only conversation is narrowed too, but it is one a person talks to, so its
                // brand new first turn is told what it can and cannot do (AgentFirstTurnBriefing.GraphOnlyNote)
                // instead of the git fact and the commit steps, which name tools it does not have.
                var fresh = string.IsNullOrWhiteSpace(request.ResumeSessionId);
                // Work item #359: the studio assistant is narrowed the same way and told who it is and how it behaves.
                var note = !fresh
                    ? null
                    : request.Assistant
                        ? AgentFirstTurnBriefing.AssistantNote
                    : request.GraphOnly
                        ? AgentFirstTurnBriefing.GraphOnlyNote
                        : request.BuiltInTools is null
                            ? AgentGitInBash.OpeningNote + " " + AgentFirstTurnBriefing.Note
                            : null;
                await live.WriteAsync(UserTurn(request.Prompt, request.PromptImages, note), cancellationToken)
                    .ConfigureAwait(false);
            }

            await foreach (var e in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return e;
            }
        }
        finally
        {
            live.KillAndWait();
            _live.TryRemove(request.SessionId, out _);
            ScrubCliTranscript(live);
            Cleanup(request.SessionId);
            await pumps.CancelAsync().ConfigureAwait(false);
        }

        await Task.WhenAny(pump, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None))
            .ConfigureAwait(false);

        // A turn that never reached its terminal result line still said things. Every COMPLETED text
        // block is already in the transcript, because the parser commits each one where it arrives, so
        // what is left is the block that was still being typed when Stop killed the process tree (or a
        // crash, an OOM kill or a dropped pipe ended the stream the same way). It comes out HERE,
        // before the session is reported ended, or it leaves with the process.
        foreach (var outstanding in parser.Drain())
        {
            yield return outstanding;
        }

        yield return Ended(live, errors.ToString());
    }

    /// <summary>
    /// Reads every child this session runs, in order, into the event channel. A model change
    /// (<see cref="ChangeModel"/>) ends the current child and continues the SAME conversation in a new
    /// one via <c>--resume</c>, so the loop's next pass serves the same transcript while the broker
    /// registration, the hook files and the approval channel stay exactly where they were.
    /// </summary>
    private async Task PumpChildrenAsync(
        AgentSessionRequest request,
        LiveSession live,
        ClaudeStreamJsonParser parser,
        ChannelWriter<AgentEvent> writer,
        StringBuilder errors,
        CancellationToken token)
    {
        try
        {
            while (true)
            {
                var process = live.Process;
                var stderr = Task.Run(
                    async () =>
                    {
                        try
                        {
                            errors.Append(await process.StandardError.ReadToEndAsync(token).ConfigureAwait(false));
                        }
                        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                        {
                        }
                    },
                    CancellationToken.None);

                while (await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is { } line)
                {
                    foreach (var e in parser.Parse(line))
                    {
                        if (e is SessionRegistered registered)
                        {
                            // What --resume needs, kept where a model change can reach it.
                            live.ProviderSessionId = registered.ProviderSessionId;

                            // Backlog 338: a fresh session's provider id (what decides where the CLI saves a
                            // long tool result) is only known now; a resumed session already knew it and was
                            // registered with the root in place at Start(). Set directly, never through
                            // RegisterSession again, which would rotate the secret the running hook script
                            // already authenticates with.
                            if (live.ConfigDirectory is { } home && registered.ProviderSessionId is { Length: > 0 } providerSessionId)
                            {
                                _broker.SetToolResultsRoot(
                                    request.SessionId,
                                    SessionFrameworkProjector.ToolResultsDirectoryFor(home, live.WorkingDirectory, providerSessionId));
                            }
                        }

                        if (e is TurnCompleted turn)
                        {
                            // How the last turn ended decides what the process exiting means (Ended).
                            live.LastTurn = turn;
                        }

                        await writer.WriteAsync(e, token).ConfigureAwait(false);
                    }
                }

                // The child exited (on its own, or killed for the model change below). Its stderr
                // closes with it; the wait is bounded because nothing may hang the session's ending.
                await Task.WhenAny(stderr, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None))
                    .ConfigureAwait(false);

                if (live.TakeModelChange() is not { } change)
                {
                    break;
                }

                // The studio notice line, in the same voice as the pin warning: the transcript records
                // the switch where it happened, before the restarted child says anything.
                await writer.WriteAsync(
                        new MessageEmitted(_clock(), TranscriptRole.System, RestartNotice(change, live)),
                        token)
                    .ConfigureAwait(false);

                var restartChoice = ModelChoiceFor(change.Model);
                if (restartChoice.Notice is { } substituted)
                {
                    await writer.WriteAsync(new MessageEmitted(_clock(), TranscriptRole.System, substituted), token)
                        .ConfigureAwait(false);
                }

                if (RestartOn(request, live, restartChoice.Model, change.Effort) is { } failure)
                {
                    await writer.WriteAsync(new MessageEmitted(_clock(), TranscriptRole.System, failure), token)
                        .ConfigureAwait(false);
                    break;
                }

                // The resumed child announces the SAME conversation again; the parser must not swallow
                // that init line as a repeat, because its model field is the first sign of the switch.
                parser.ExpectRestart();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The agent session's output stream ended abruptly.");
        }
        finally
        {
            // No new child is coming, so a turn waiting for one finds the process gone rather than waiting.
            live.SwapSettled();
            writer.TryComplete();
        }
    }

    public async Task<bool> SendAsync(AgentSessionId sessionId, string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text) || !_live.TryGetValue(sessionId, out var live))
        {
            return false;
        }

        return await live.WriteAsync(UserTurn(text), cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SendAsync(
        AgentSessionId sessionId,
        string text,
        IReadOnlyList<AgentImageAttachment> images,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(images);
        if ((string.IsNullOrWhiteSpace(text) && images.Count == 0) || !_live.TryGetValue(sessionId, out var live))
        {
            return false;
        }

        return await live.WriteAsync(UserTurn(text, images), cancellationToken).ConfigureAwait(false);
    }

    public bool Stop(AgentSessionId sessionId)
    {
        if (!_live.TryGetValue(sessionId, out var live))
        {
            return false;
        }

        live.Stop();
        return true;
    }

    public bool ChangeModel(AgentSessionId sessionId, string? model) =>
        _live.TryGetValue(sessionId, out var live) && live.RequestModelChange(model);

    /// <summary>
    /// Requested 2026-10-22 (model and effort per work item): the model change's own restart, carrying a new
    /// <c>--effort</c> and keeping the model the child runs on.
    /// </summary>
    public bool ChangeEffort(AgentSessionId sessionId, AgentEffort? effort) =>
        _live.TryGetValue(sessionId, out var live) && live.RequestEffortChange(effort);

    /// <summary>
    /// Backlog 235: restarts the child on the model it is already on, so its environment is built again and carries
    /// the credential store's CURRENT broker token. The model change's own path (<see cref="RestartOn"/>), measured with
    /// the pinned lineage; the registry asks for it only between turns.
    /// </summary>
    public bool RefreshCredentials(AgentSessionId sessionId) =>
        _live.TryGetValue(sessionId, out var live) && live.RequestCredentialRefresh();

    /// <summary>The transcript line recording a restart that handed the agent process a renewed broker token.</summary>
    public const string CredentialRefreshNotice =
        "The agent process restarted to take a renewed broker token; the conversation continues.";

    /// <summary>
    /// The transcript line recording a model switch, in the studio's own voice like the pin warning:
    /// the marker is what makes the switch findable in the history, because the restarted child's init
    /// line only echoes the flag back.
    /// </summary>
    public static string ModelChangeNotice(string? model) =>
        string.IsNullOrWhiteSpace(model)
            ? "The conversation continues on the CLI's default model."
            : $"The conversation continues on {model.Trim()}.";

    /// <summary>The transcript line recording an effort switch (requested 2026-10-22), in the same voice.</summary>
    public static string EffortChangeNotice(AgentEffort? effort) =>
        effort is { } level
            ? $"The conversation continues at {level.CliValue()} effort."
            : "The conversation continues at the CLI's default effort.";

    /// <summary>
    /// What the transcript says about a restart: the credential notice for a token hand-over, otherwise whichever of
    /// the model and the effort the restart moves, both when both move. A restart that moves neither (a pick of what
    /// the child already runs) says the model line, which is what it said before efforts existed.
    /// </summary>
    private static string RestartNotice(PendingModelChange change, LiveSession live)
    {
        if (change.Credentials)
        {
            return CredentialRefreshNotice;
        }

        var modelMoves = !string.Equals(change.Model, live.CurrentModel, StringComparison.Ordinal);
        var effortMoves = change.Effort != live.CurrentEffort;
        return (modelMoves, effortMoves) switch
        {
            (false, true) => EffortChangeNotice(change.Effort),
            (true, true) => ModelChangeNotice(change.Model) + " " + EffortChangeNotice(change.Effort),
            _ => ModelChangeNotice(change.Model),
        };
    }

    /// <summary>
    /// Starts the resumed child for a model change and swaps it in. Null on success, otherwise the
    /// sentence the transcript gets. The prompt is NOT resent: the restarted child idles on stdin
    /// exactly like a freshly started one, and the next user turn reaches it through
    /// <see cref="SendAsync"/> as if nothing had happened.
    /// </summary>
    private string? RestartOn(AgentSessionRequest request, LiveSession live, string? model, AgentEffort? effort)
    {
        if (live.ProviderSessionId is not { } resume)
        {
            // RequestModelChange refuses before the init line, so this is a belt for a race, not a path.
            return "The conversation cannot switch model before the CLI has announced its session.";
        }

        var restarted = request with { ResumeSessionId = resume, Model = model, Effort = effort };
        try
        {
            var process = Process.Start(BuildStartInfo(
                restarted, live.WorkingDirectory, live.SettingsPath, live.ConfigDirectory, live.ToolServerConfigPath));
            if (process is null)
            {
                return $"The agent CLI ({_launcher.FileName}) did not restart for the model change.";
            }

            live.CurrentModel = model;
            live.CurrentEffort = effort;
            live.Adopt(process);
            return null;
        }
        catch (Exception ex)
        {
            return $"The agent CLI ({_launcher.FileName}) could not restart for the model change: {ex.Message}";
        }
    }

    /// <summary>
    /// The command that drives the CLI headlessly. Every flag here was exercised against the real CLI in
    /// the container on 2026-08-04, and is re-checked against the pinned binary by
    /// <c>ClaudeCliContractTests.The_cli_still_declares_every_flag_the_session_runner_passes</c>, which
    /// derives its list from this method rather than from a copy that would rot.
    /// </summary>
    /// <param name="request">The session being started or resumed.</param>
    /// <param name="settingsPath">The per-session hook settings file.</param>
    /// <param name="toolServerConfigPath">
    /// The session's MCP config naming the studio's tool server (backlog 84), or null for none. Production always
    /// has one; a runner built without a tool-server URL (most tests) sends no <c>--mcp-config</c> at all.
    /// </param>
    public static IReadOnlyList<string> BuildArguments(
        AgentSessionRequest request, string settingsPath, string? toolServerConfigPath = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<string> arguments =
        [
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            "--include-hook-events",
            "--settings", settingsPath,
        ];

        // ADR 0019 / backlog 84: the studio's own tool server, reached over MCP with the session's secret. Measured
        // against 2.1.222 on 2026-09-15 before it was wired: the init line reports the server connected, its tools
        // arrive deferred behind ToolSearch as mcp__fravashi__*, and a call to one still passes the PreToolUse hook.
        // Inside the recorded verification (ADR 0023): AgentCliPinTests' vectors carry it and the contract file
        // drives a real call through it.
        if (!string.IsNullOrWhiteSpace(toolServerConfigPath))
        {
            arguments.Add("--mcp-config");
            arguments.Add(toolServerConfigPath);
        }

        // Backlog 162: the native CLI drops its own Glob and Grep tools and routes searching through Bash, unless a
        // session opts in by naming either one in --allowedTools (read from the 2.1.222 binary, 2026-09-27). Bash asks
        // a person every time and waits for the work gate; Glob and Grep inside the copy run unattended like Read
        // (AgentToolSafety, path-scoped). Naming them also lets the CLI's own permission system allow them, which
        // matters only if the studio's hook does not answer, and the hook's command turns any failure into a
        // refusal (backlog 115), so the studio's path-scoped answer is still the one that decides.
        arguments.Add("--allowedTools");
        arguments.Add("Grep,Glob");

        // Backlog 243: a conversation that must only read (the commit reviewer's) is started with its built-in tools
        // narrowed to exactly that list, so Edit, Write and Bash do not exist for it; the approval broker refuses
        // anything else it reaches for too. The flag is the one the wiki summary and the board-mapping suggester
        // already pass the pinned CLI (with an empty list). An ordinary conversation names no list and sends no flag,
        // so the recorded command lines (AgentCliPinTests) are unchanged; this conditional branch is outside the
        // recorded live verification until ClaudeCliContractTests drives it (ADR 0023, said on #243).
        //
        // Work item #352: a graph-only conversation gets the same reading list (AgentGraphOnlyTools.BuiltIn) whatever
        // BuiltInTools says, so a caller that forgets to narrow it still cannot hand that conversation Edit, Write or
        // Bash. Like the reviewer's, this branch is outside the recorded live verification (ADR 0023).
        if (request.EffectiveBuiltInTools is { } tools)
        {
            arguments.Add("--tools");
            arguments.Add(string.Join(',', tools));
        }

        // ADR 0022: the CLI turns this schema into a StructuredOutput tool the model must call to end
        // its turn, so the harness enforces the shape instead of a system prompt asking politely. The
        // schema is assembled for exactly the kinds this session could produce, because it is charged on
        // EVERY turn (measured: about 140 tokens per KB). No kinds means no flag at all: an empty
        // --json-schema would be a schema nothing can satisfy.
        if (request.TurnsAreTyped)
        {
            arguments.Add("--json-schema");

            // TurnsAreTyped is exactly "DocumentKinds is non-empty", so this is not an assumption: it is
            // the same read, and keeping it as one property is what stops the flag and the approval gate
            // ever disagreeing about whether this session has a StructuredOutput tool.
            arguments.Add(AgentTurnDocumentSchema.For(request.DocumentKinds!));
        }

        // Backlog 62: the CLI's own per-invocation ceiling, which is the only one that can stop an
        // unattended run from INSIDE the invocation rather than at the next turn boundary. Passed ONLY
        // when the owner configured one: an absent flag is the CLI's own no-ceiling behaviour, and a
        // "0" would be a ceiling that refuses everything. Formatted with a fixed pattern rather than
        // the decimal's own scale, so the argument reads the same whether the settings row stored
        // "5", "5.0" or "5.00".
        //
        // ADR 0023: inside the recorded verification since 2026-09-14 (backlog 25). The contract file's
        // derived flag list carries a ceiling, so the --help scan looks for this flag, its typed document
        // test sends one to the pinned 2.1.222 and the CLI accepted it, and AgentCliPinTests records that
        // opening command line as a third vector. Decision the same day: it stays OPT-IN exactly as
        // item 62 left it, because the CLI was measured overshooting its ceiling by about 10x, so the
        // Spending settings say the ceiling is the CLI's and can overshoot rather than presenting it as a stop.
        if (request.MaxBudgetUsd is { } budget)
        {
            arguments.Add("--max-budget-usd");
            arguments.Add(budget.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
        }

        // Decision 7: no --model means the CLI's own default (the subscription's).
        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            arguments.Add("--model");
            arguments.Add(request.Model.Trim());
        }

        // Requested 2026-10-22 (model and effort per work item): the pinned 2.1.280 documents --effort with the
        // levels low, medium, high, xhigh and max in its own --help. Passed ONLY when a level was chosen, so a request
        // that names none has exactly the recorded command line (AgentCliPinTests), and the value is always one of
        // the closed set's written-out CLI values, never text a person or an agent typed. Like --tools, this
        // conditional branch is outside the recorded live verification until ClaudeCliContractTests drives it
        // (ADR 0023); the contract file's derived flag list carries no effort, so its --help scan does not look for it.
        if (request.Effort is { } effort)
        {
            arguments.Add("--effort");
            arguments.Add(effort.CliValue());
        }

        if (!string.IsNullOrWhiteSpace(request.ResumeSessionId))
        {
            // A resumed run keeps the id it is resuming; asking for a new one would contradict it.
            arguments.Add("--resume");
            arguments.Add(request.ResumeSessionId.Trim());
        }
        else
        {
            arguments.Add("--session-id");
            arguments.Add(request.SessionId.Value.ToString("D"));
        }

        return arguments;
    }

    /// <summary>
    /// The transcript line to open with when the installed CLI is not the pinned one, or null when there is
    /// nothing honest to say. Null covers BOTH silent cases deliberately: no status report at all (a developer
    /// host, where nothing installs the CLI) and an unknown version (the startup pass has not finished), because
    /// a warning without an observation behind it is noise the user would learn to ignore.
    /// <para>
    /// The sentence names <see cref="AgentRuntimeDescriptor.ProtocolVerifiedVersion"/>, never the pin, and drops
    /// the word "verified" entirely when nothing was. Interpolating the pin is what made this line lie once
    /// already (ADR 0023): the pin moved to a build nobody had driven, and the line went on claiming the studio
    /// had verified it.
    /// </para>
    /// </summary>
    public static string? PinWarning(AgentRuntimeDescriptor descriptor, string? installedVersion)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (!descriptor.IsOffPin(installedVersion))
        {
            return null;
        }

        var opening = $"The installed {descriptor.DisplayName} CLI is {installedVersion}, but {ProductName.Short} ";
        var closing = " This session will still run, but if it behaves oddly, that difference is the first "
            + "thing to check.";

        return descriptor.ProtocolVerifiedVersion is { } verified
            ? opening + $"verified its protocol against {verified}." + closing
            : opening + $"pins {descriptor.PinnedVersion}." + closing;
    }

    /// <summary>The longest a conversation waits for the first install of the agent CLI before it starts anyway.</summary>
    internal static readonly TimeSpan FirstInstallWait = TimeSpan.FromMinutes(10);

    /// <summary>How often the wait looks again.</summary>
    internal static TimeSpan FirstInstallPoll { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The image does not carry the agent CLI (decided 2026-10-29: the published image does not redistribute it), so
    /// the update-on-launch pass installs the pinned version from npm at the first start. A conversation started
    /// while that pass is still running, with no CLI on PATH yet, waits for it instead of failing with "could not
    /// start", and its transcript says why it took a moment. Once the CLI is there, nothing waits: the pass also
    /// runs at every later start, and a conversation then starts on the CLI already installed.
    /// </summary>
    private async Task<string?> AwaitFirstInstallAsync(CancellationToken cancellationToken)
    {
        if (_runtimeReport is not { InProgress: true } report
            || CliOnPath(_launcher.FileName, Environment.GetEnvironmentVariable("PATH")))
        {
            return null;
        }

        var waited = Stopwatch.StartNew();
        while (report.InProgress && waited.Elapsed < FirstInstallWait)
        {
            await Task.Delay(FirstInstallPoll, cancellationToken).ConfigureAwait(false);
        }

        return $"The agent CLI was being installed (the first start of this version), so this conversation waited "
            + $"{Math.Max(1, (int)Math.Round(waited.Elapsed.TotalSeconds))} s for it.";
    }

    /// <summary>True when <paramref name="fileName"/> is a file on the given PATH (or, when rooted, exists).</summary>
    internal static bool CliOnPath(string fileName, string? path)
    {
        if (Path.IsPathRooted(fileName))
        {
            return File.Exists(fileName);
        }

        string[] suffixes = OperatingSystem.IsWindows() ? ["", ".exe", ".cmd"] : [""];
        return (path ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => suffixes.Any(s => File.Exists(Path.Combine(dir, fileName + s))));
    }

    /// <summary>The warning for THIS runner's Claude runtime, from what the last update-on-launch pass saw.</summary>
    private string? PinWarning() =>
        PinWarning(AgentRuntimeCatalog.For(AgentTool.Claude), _runtimeReport?.Latest(AgentTool.Claude)?.ToVersion);

    // ---- starting -----------------------------------------------------------------------

    private (LiveSession? Session, string? Failure) Start(AgentSessionRequest request)
    {
        var workingDirectory = ResolveWorkingDirectory(request);
        if (workingDirectory is null)
        {
            return (null, $"Workspace {request.WorkspaceId} has no contained copy for an agent to work in.");
        }

        // Backlog 69: the stored Settings > Framework artifacts become real files the CLI reads, rebuilt
        // fresh from the current rows so a removed row simply is not there. Only when something was actually
        // projected does the child get CLAUDE_CONFIG_DIR, so an empty framework leaves the run byte-identical.
        // A resume is the exception: it gets the config home that HOLDS the conversation it resumes, because
        // that is the only place --resume looks (owner report 2026-09-15, "No conversation found").
        //
        // This is computed BEFORE RegisterSession below (backlog 202), because the session's own memory root
        // is this session's slice of the config home, and the registration is what hands that root to the
        // approval broker; neither the secret it returns nor anything SessionHookWriter.Write does depends on
        // the config home, so moving this ahead of it changes nothing else about the order below.
        var configDirectory = _framework?.Project(request.SessionId) is { } projection
            ? _framework.ConfigHomeFor(request.SessionId, projection, request.ResumeSessionId)
            : null;

        // Backlog 156: a projected config home is per session, so the CLI's memory for this workspace is linked to
        // the workspace's shared folder; with no projection the CLI's default home is already shared by every
        // session in the copy. Backlog 202: a session whose link actually landed gets that same link path
        // registered as a second containment root, so a read that stays inside it runs unattended; a link the
        // platform refused leaves the session with its own, unshared folder and every read of it keeps asking,
        // exactly as before this existed. memoryTarget is the REAL folder the link is supposed to resolve to
        // (the same MemoryDirectoryFor LinkMemory just linked it to), named independently of the link itself so
        // the broker can tell a link that still points there from one a later command re-pointed.
        string? memoryRoot = null;
        string? memoryTarget = null;
        if (configDirectory is not null && _framework!.LinkMemory(configDirectory, workingDirectory))
        {
            memoryRoot = SessionFrameworkProjector.MemoryLinkPathFor(configDirectory, workingDirectory);
            memoryTarget = _framework.MemoryDirectoryFor(workingDirectory);
        }

        // Backlog 338: the CLI's own conversation id decides where it saves a long tool result, and a
        // RESUMED session already knows it (it is the id being resumed), so the root is registered
        // up front like the roots above. A FRESH session does not announce its id until the CLI's first
        // stream line, so this stays null here; PumpChildrenAsync sets it once that line arrives
        // (SetToolResultsRoot, never a second RegisterSession, which would rotate the secret this call
        // is about to hand out).
        var toolResultsRoot = configDirectory is not null && !string.IsNullOrWhiteSpace(request.ResumeSessionId)
            ? SessionFrameworkProjector.ToolResultsDirectoryFor(configDirectory, workingDirectory, request.ResumeSessionId.Trim())
            : null;

        // The copy goes with the registration: it is the scope the read-only auto-allow is measured
        // against, so the broker never has to guess where "inside" is (AgentReadScope). So does whether
        // this session's turns are typed, because that is what tells the broker the CLI has a
        // StructuredOutput tool at all, and it is the SAME predicate BuildArguments passes the flag on.
        var secret = _broker.RegisterSession(
            request.SessionId, workingDirectory, request.TurnsAreTyped, request.WorkspaceId,
            memoryRoot, memoryTarget, toolResultsRoot);

        // Work item #352: held graph-only at the broker before the child exists, so not one call it makes can reach
        // the broker unheld. A registration never lifts it (only a release does), and the registry holds it too.
        if (request.GraphOnly)
        {
            _broker.HoldToGraphOnly(request.SessionId);
        }

        // Work item #359: the studio assistant is held the same way, before the child exists.
        if (request.Assistant)
        {
            _broker.HoldToAssistant(request.SessionId);
        }

        try
        {
            var files = _hooks.Write(request.SessionId, secret);

            var process = Process.Start(BuildStartInfo(
                request, workingDirectory, files.SettingsPath, configDirectory, files.ToolServerConfigPath));
            if (process is null)
            {
                // Backlog 74: there was no agent to discover, which is an INSTALL problem and reads
                // nothing like a protocol failure later in a run. Only the CLI name would identify it
                // and the event carries no text, so the kind is the whole signal.
                _telemetry.Record(ProductEvent.ToolDiscoveryFailed(_clock(), request.WorkspaceId));
                return (null, $"The agent CLI ({_launcher.FileName}) did not start.");
            }

            var live = new LiveSession(
                process, workingDirectory, files.SettingsPath, configDirectory, files.ToolServerConfigPath)
            {
                // A session that starts by resuming already knows what a model change would resume.
                ProviderSessionId = string.IsNullOrWhiteSpace(request.ResumeSessionId)
                    ? null
                    : request.ResumeSessionId.Trim(),
                CurrentModel = request.Model,
                CurrentEffort = request.Effort,
            };
            _live[request.SessionId] = live;
            return (live, null);
        }
        catch (Exception ex)
        {
            _telemetry.Record(ProductEvent.ToolDiscoveryFailed(_clock(), request.WorkspaceId));
            return (null, $"The agent CLI ({_launcher.FileName}) could not start: {ex.Message}");
        }
    }

    /// <summary>
    /// The contained copy the agent works in. An explicit directory (the caller already resolved it)
    /// wins; otherwise the sync engine is warmed and asked. Null means there is nowhere safe to run.
    /// </summary>
    private string? ResolveWorkingDirectory(AgentSessionRequest request)
    {
        _sync?.EnsureStarted(request.WorkspaceId);

        return string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? _sync?.CopyPathFor(request.WorkspaceId)
            : request.WorkingDirectory;
    }

    private ProcessStartInfo BuildStartInfo(
        AgentSessionRequest request,
        string workingDirectory,
        string settingsPath,
        string? configDirectory,
        string? toolServerConfigPath)
    {
        var info = new ProcessStartInfo
        {
            FileName = _launcher.FileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Backlog 72: containment is enforced by the working directory and was undone by the
        // environment. A child started with UseShellExecute = false inherits the studio's own, which
        // carries a live host-bridge credential for the user's real folders. This must stay the FIRST
        // statement here: everything below it is added on purpose and survives.
        AgentProcessEnvironment.Restrict(info);

        foreach (var argument in _launcher.LeadingArguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var argument in BuildArguments(request, settingsPath, toolServerConfigPath))
        {
            info.ArgumentList.Add(argument);
        }

        // Backlog 154: what the project hands its agents, on purpose, after the allowlist and BEFORE the credential,
        // PATH and the config home, so none of those can be displaced by a variable a person typed; the names that
        // could are refused at the door anyway (ProjectVariableNames). A source that throws hands the child nothing
        // of the project's rather than no child at all.
        if (_workspaceEnvironment is not null)
        {
            try
            {
                foreach (var (key, value) in _workspaceEnvironment(request.WorkspaceId))
                {
                    info.Environment[key] = value;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                _logger.LogWarning(ex, "The project's variables could not be read for workspace {WorkspaceId}.", request.WorkspaceId);
            }
        }

        if (_credentials is not null)
        {
            foreach (var (key, value) in _credentials.EnvironmentFor(AgentTool.Claude))
            {
                info.Environment[key] = value;
            }
        }

        // Plan 2d decision 10: a project that asks for a Node version gets the one provisioned on the
        // volume; nothing provisioned means the image Node serves, honestly.
        var nodePath = _nodePaths?.PathPrefixFor(ProjectNodeResolver.Resolve(workingDirectory));
        if (!string.IsNullOrWhiteSpace(nodePath))
        {
            // The child's own PATH, not the parent's: after the allowlist pass there is one source of
            // truth for the value the child gets.
            var current = info.Environment.TryGetValue("PATH", out var inherited) ? inherited ?? string.Empty : string.Empty;
            info.Environment["PATH"] = nodePath + Path.PathSeparator + current;
        }

        if (_environment is not null)
        {
            foreach (var (key, value) in _environment)
            {
                info.Environment[key] = value;
            }
        }

        // Backlog 69: point the CLI at the per-session config home the framework artifacts were projected into,
        // so its skills, subagents, commands and hooks take effect. Set last so nothing above overrides it; the
        // CLAUDE_ prefix is on the environment allowlist, so it survives Restrict regardless. Left unset when
        // nothing was projected, which keeps a subscription's default config home in play for an empty framework.
        if (!string.IsNullOrWhiteSpace(configDirectory))
        {
            info.Environment["CLAUDE_CONFIG_DIR"] = configDirectory;
        }

        return info;
    }

    // ---- ending -------------------------------------------------------------------------

    private AgentEvent Ended(LiveSession live, string errorOutput)
    {
        if (live.Stopped)
        {
            return new SessionEnded(_clock(), AgentSessionState.Cancelled, null);
        }

        var exitCode = live.ExitCode();
        if (exitCode == 0)
        {
            return new SessionEnded(_clock(), AgentSessionState.Completed, null);
        }

        // ADR 0022 outcome iii: the turn's document failed validation on every attempt the harness allows,
        // and the CLI answers that by EXITING (code 1, measured), which in a runner holding one process per
        // conversation would otherwise read as the conversation failing. It did not: the turn failed, and
        // the provider session id --resume needs is already known. So the session ends RESUMABLE, and the
        // turn's own row already says why (attempts and the last validator message ride on it).
        if (live.LastTurn is { TerminalReason: AgentTurnEnding.RetriesExhausted, Document: null }
            && live.ProviderSessionId is { Length: > 0 })
        {
            return new SessionEnded(
                _clock(),
                AgentSessionState.Failed,
                ExhaustedEnding,
                Resumable: true);
        }

        var detail = errorOutput.Trim();
        return new SessionEnded(
            _clock(),
            AgentSessionState.Failed,
            string.IsNullOrEmpty(detail)
                ? $"The agent CLI exited with code {exitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}."
                : detail);
    }

    /// <summary>
    /// The ending a resumable session carries. It says what happened to the PROCESS and what the user can
    /// do; the turn's own row is where the attempt count and the validator's message are.
    /// </summary>
    public const string ExhaustedEnding =
        "The agent CLI closed the conversation's process after a turn failed validation. "
        + "Continue resumes the same conversation.";

    /// <summary>
    /// Backlog 116: runs the transcript redactor over the CLI's OWN conversation files for this session, which
    /// the CLI wrote verbatim into its config home. Done after the teardown has waited for the child to be gone
    /// (so nothing is still appending) and BEFORE <see cref="Cleanup"/> (so a projected config home still exists
    /// to scrub). The files are redacted in place and never removed, because Continue resumes from them. Never
    /// throws: a scrub that cannot run is deferred to the next studio start, which scrubs every conversation file.
    /// </summary>
    private void ScrubCliTranscript(LiveSession live)
    {
        if (_transcripts is null)
        {
            return;
        }

        try
        {
            _transcripts.ScrubSession(live.ConfigDirectory, live.ConversationIds());
        }
        catch (Exception ex)
        {
            _logger.LogWarning("The agent CLI transcript scrub at session end failed: {Error}", ex.GetType().Name);
        }
    }

    /// <summary>Ends the session's approval channel and takes its secret off disk.</summary>
    private void Cleanup(AgentSessionId sessionId)
    {
        _broker.ReleaseSession(sessionId);
        _hooks.Remove(sessionId);
    }

    /// <summary>
    /// One user turn, in the stream-json shape the CLI reads from stdin. Images come first as base64 image
    /// blocks, then the text block (omitted when the images are the whole turn). That exact shape was driven
    /// against the pinned 2.1.222 on 2026-09-15 with claude-haiku-4-5-20251001, which read the text rendered
    /// into the image back correctly; the stdin line and the CLI's output are recorded in
    /// <c>Sessions/ImageTurnFixtures</c>.
    /// </summary>
    /// <param name="note">
    /// Work item #323, extended by #333: a studio-authored sentence (or two, concatenated by the caller) sent as
    /// its own leading text block, ahead of the user's own words, on a brand new, ordinary session's opening turn
    /// only. Null for every follow-up turn (<see cref="SendAsync"/>), for a resumed session (not because it was
    /// already told; a conversation begun before this change and resumed after an upgrade or a cut-off is never
    /// told either), and for a review conversation, which cannot commit. This changes stdin content, never the
    /// command line <see cref="BuildArguments"/> emits, so it needs no re-drive of the pinned CLI's recorded
    /// contract.
    /// </param>
    public static string UserTurn(string text, IReadOnlyList<AgentImageAttachment>? images = null, string? note = null)
    {
        var content = new JsonArray();
        if (!string.IsNullOrWhiteSpace(note))
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = note });
        }

        foreach (var image in images ?? [])
        {
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = image.MediaType,
                    ["data"] = image.Base64,
                },
            });
        }

        if (!string.IsNullOrWhiteSpace(text) || content.Count == 0)
        {
            content.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        }

        return new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject
            {
                ["role"] = "user",
                ["content"] = content,
            },
        }.ToJsonString();
    }

    private sealed class LiveSession(
        Process process,
        string workingDirectory,
        string settingsPath,
        string? configDirectory,
        string? toolServerConfigPath)
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly Lock _stateGate = new();
        private string? _providerSessionId;
        private readonly HashSet<string> _conversationIds = new(StringComparer.Ordinal);
        private PendingModelChange? _modelChange;

        // Backlog 235: open while a restart is under way, so a turn written meanwhile waits for the new child instead of
        // landing in the dying one (where WriteAsync would find it exited and drop the turn). Settled from the start.
        private TaskCompletionSource _swap = Settled();

        public Process Process { get; private set; } = process;

        /// <summary>The model the current child runs on (null is the CLI's default); a credential restart keeps it.</summary>
        public string? CurrentModel { get; set; }

        /// <summary>The effort the current child runs at (null is the CLI's default); a model or credential restart keeps it.</summary>
        public AgentEffort? CurrentEffort { get; set; }

        /// <summary>The contained copy the session runs in; a restarted child runs in the same one.</summary>
        public string WorkingDirectory { get; } = workingDirectory;

        /// <summary>The hook settings the session started with; a restarted child reuses them unchanged.</summary>
        public string SettingsPath { get; } = settingsPath;

        /// <summary>The projected framework config home (CLAUDE_CONFIG_DIR), or null; a restarted child reuses it.</summary>
        public string? ConfigDirectory { get; } = configDirectory;

        /// <summary>The session's tool-server MCP config, or null; a restarted child reuses it.</summary>
        public string? ToolServerConfigPath { get; } = toolServerConfigPath;

        /// <summary>The CLI's own conversation id, which is exactly what a model change resumes.</summary>
        public string? ProviderSessionId
        {
            get
            {
                lock (_stateGate)
                {
                    return _providerSessionId;
                }
            }

            set
            {
                lock (_stateGate)
                {
                    _providerSessionId = value;
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        _conversationIds.Add(value);
                    }
                }
            }
        }

        /// <summary>True when the user stopped the session, which is not a failure.</summary>
        public bool Stopped { get; private set; }

        /// <summary>
        /// The most recent turn's terminating event. Written only by the pump, and read once the pump is done,
        /// so it needs no lock of its own.
        /// </summary>
        public TurnCompleted? LastTurn { get; set; }

        /// <summary>
        /// Asks for the next child to run on another model. It ends the CURRENT child; the pump sees
        /// the pending change when the stream closes and resumes the conversation in a new one.
        /// Refused until the CLI has announced the conversation id a restart would resume, and after
        /// Stop, because Stop means stop.
        /// </summary>
        public bool RequestModelChange(string? model)
        {
            lock (_stateGate)
            {
                if (Stopped || _providerSessionId is null)
                {
                    return false;
                }

                // The effort a change already pending asked for, else the one the child runs at: a model change never
                // moves the effort (requested 2026-10-22).
                _modelChange = new PendingModelChange(model, _modelChange is { } pending ? pending.Effort : CurrentEffort);
                OpenSwap();
            }

            Kill();
            return true;
        }

        /// <summary>
        /// Asks for the next child to run at another effort (requested 2026-10-22, model and effort per work item),
        /// on the model a change already pending asked for, else the one the current child runs on. The same restart
        /// and the same refusals as <see cref="RequestModelChange"/>.
        /// </summary>
        public bool RequestEffortChange(AgentEffort? effort)
        {
            lock (_stateGate)
            {
                if (Stopped || _providerSessionId is null)
                {
                    return false;
                }

                _modelChange = new PendingModelChange(_modelChange is { } pending ? pending.Model : CurrentModel, effort);
                OpenSwap();
            }

            Kill();
            return true;
        }

        /// <summary>
        /// Asks for the next child to start with the credential store's current token (backlog 235), on the model
        /// the current one runs on. A model change already pending rebuilds the environment too, so it is kept.
        /// </summary>
        public bool RequestCredentialRefresh()
        {
            lock (_stateGate)
            {
                if (Stopped || _providerSessionId is null)
                {
                    return false;
                }

                _modelChange ??= new PendingModelChange(CurrentModel, CurrentEffort, Credentials: true);
                OpenSwap();
            }

            Kill();
            return true;
        }

        /// <summary>The restart is over, one way or the other: a turn waiting for it goes ahead.</summary>
        public void SwapSettled()
        {
            lock (_stateGate)
            {
                _swap.TrySetResult();
            }
        }

        // Under _stateGate.
        private void OpenSwap()
        {
            if (_swap.Task.IsCompleted)
            {
                _swap = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        private static TaskCompletionSource Settled()
        {
            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            settled.SetResult();
            return settled;
        }

        /// <summary>How long a turn waits for a restart before it is written anyway (and found to have nowhere to go).</summary>
        private static readonly TimeSpan SwapWait = TimeSpan.FromSeconds(30);

        /// <summary>The pending model change, taken exactly once. Null after Stop: stopping wins.</summary>
        public PendingModelChange? TakeModelChange()
        {
            lock (_stateGate)
            {
                if (Stopped)
                {
                    return null;
                }

                var change = _modelChange;
                _modelChange = null;
                return change;
            }
        }

        /// <summary>
        /// The restarted child takes over. Under the write lock, so a follow-up turn is never written
        /// into the dying child mid-swap.
        /// </summary>
        public void Adopt(Process process)
        {
            _writeLock.Wait();
            try
            {
                Process = process;
            }
            finally
            {
                _writeLock.Release();
            }

            SwapSettled();
        }

        public async Task<bool> WriteAsync(string line, CancellationToken cancellationToken)
        {
            Task swap;
            lock (_stateGate)
            {
                swap = _swap.Task;
            }

            try
            {
                await swap.WaitAsync(SwapWait, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A restart that never settles is a bug elsewhere; the write below then finds what is there.
            }

            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Process.HasExited)
                {
                    return false;
                }

                await Process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                await Process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                return false;
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void Stop()
        {
            lock (_stateGate)
            {
                Stopped = true;
                _modelChange = null;
                _swap.TrySetResult();
            }

            Kill();
        }

        /// <summary>Every CLI conversation id this session announced or resumed, for the transcript scrub.</summary>
        public IReadOnlyList<string> ConversationIds()
        {
            lock (_stateGate)
            {
                return [.. _conversationIds];
            }
        }

        /// <summary>Waits (bounded) for the current child to be gone, so its files are no longer being written.</summary>
        public void WaitForExit(TimeSpan timeout)
        {
            try
            {
                Process.WaitForExit(timeout);
            }
            catch (Exception ex) when (ex is InvalidOperationException or SystemException)
            {
                // Already gone or never started.
            }
        }

        /// <summary>
        /// The teardown's kill (backlog 203). <see cref="Kill"/> only sends the signal and returns, so a teardown
        /// that went straight on to take the hook script and secret off disk could do so while the child was still
        /// running. This waits (bounded) for it to be gone first, on every teardown path and whether or not a
        /// transcript scrubber is configured; that wait used to exist only inside the scrub.
        /// </summary>
        public void KillAndWait()
        {
            Kill();
            WaitForExit(TimeSpan.FromSeconds(2));
        }

        public void Kill()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
            {
                // Already gone.
            }
        }

        public int? ExitCode()
        {
            try
            {
                return Process.HasExited ? Process.ExitCode : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// What the next child should run on. Null model means the CLI's own default again, and so does a null
    /// <paramref name="Effort"/> (requested 2026-10-22): a change always carries BOTH, so the restart keeps
    /// whichever of the two was not asked to move. <paramref name="Credentials"/> marks a restart that only hands the
    /// child a renewed broker token (backlog 235), on the model and effort it already runs with.
    /// </summary>
    private sealed record PendingModelChange(string? Model, AgentEffort? Effort, bool Credentials = false);
}
