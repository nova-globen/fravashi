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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Fravashi.Application.AgentRuntimes;
using Fravashi.Application.AgentSync;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.AgentRuntimes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// Runs a session against the Copilot CLI's Agent Client Protocol server (backlog 66): <c>copilot --acp</c>,
/// JSON-RPC 2.0 over the child's stdin and stdout, one long-lived process per conversation.
/// <para>
/// <b>Why ACP and not <c>copilot -p --output-format json</c></b> (measured against the pinned 1.0.78, see
/// <c>docs/plans/codex-copilot-runners.md</c>): prompt mode runs one prompt per process and its own help says it
/// needs <c>--allow-all-tools</c>, the opposite of "every change asks". Under ACP the CLI asks the CLIENT through
/// <c>session/request_permission</c>.
/// </para>
/// <para>
/// <b>Fail closed, on both sides.</b> This runner selects the <c>allow_once</c> option only when the broker says
/// the user approved, and <c>reject_once</c> (or the <c>cancelled</c> outcome) for everything else. It never
/// selects <c>allow_always</c>. And the pinned CLI's own handler turns a client error, a cancelled outcome or an
/// unknown option into a rejection (read off its bundled code), so a broken answer here cannot become an allow
/// there. A permission to read "paths outside trusted directories" is parked under a tool name the broker has
/// never heard of, so it ALWAYS asks: its paths arrive joined into one string, and a joined string must never
/// reach the path-scoped read auto-allow, which would measure it as one path.
/// </para>
/// <para>
/// <b>Not offered in the UI</b>, for the same reasons as the Codex runner: Copilot decides which calls reach the
/// client (reads inside its trusted directories do not), no live turn has been driven, and a session row does not
/// yet record which CLI ran it.
/// </para>
/// </summary>
public sealed class CopilotSessionRunner : IAgentSessionRunner
{
    /// <summary>The ACP protocol version this runner speaks (the pinned code's constant).</summary>
    public const int ProtocolVersion = 1;

    /// <summary>The tool name an out-of-directory read permission parks under, deliberately off every allowlist.</summary>
    public const string OutsideReadTool = "copilot_read_outside_directory";

    private readonly IAgentApprovalBroker _broker;
    private readonly IWorkspaceAgentSync? _sync;
    private readonly IAgentRuntimeCredentialStore? _credentials;
    private readonly NodePathProvisioner? _nodePaths;
    private readonly AgentCliLauncher _launcher;
    private readonly IReadOnlyDictionary<string, string>? _environment;
    private readonly IAgentRuntimeStatusReport? _runtimeReport;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<AgentSessionId, LiveSession> _live = new();

    public CopilotSessionRunner(
        IAgentApprovalBroker broker,
        IWorkspaceAgentSync? sync = null,
        IAgentRuntimeCredentialStore? credentials = null,
        NodePathProvisioner? nodePaths = null,
        AgentCliLauncher? launcher = null,
        IReadOnlyDictionary<string, string>? environment = null,
        IAgentRuntimeStatusReport? runtimeReport = null,
        Func<DateTimeOffset>? clock = null,
        ILogger<CopilotSessionRunner>? logger = null)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _sync = sync;
        _credentials = credentials;
        _nodePaths = nodePaths;
        _launcher = launcher ?? new AgentCliLauncher("copilot", []);
        _environment = environment;
        _runtimeReport = runtimeReport;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _logger = logger ?? NullLogger<CopilotSessionRunner>.Instance;
    }

    /// <summary>True while this process holds a live session with that id.</summary>
    public bool IsRunning(AgentSessionId sessionId) => _live.ContainsKey(sessionId);

    /// <summary>
    /// The command line after the executable. Every flag is in the pinned binary's help. <c>--no-auto-update</c> is
    /// what keeps the pin true: without it the pinned package was measured running a newer cached version.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(string? model)
    {
        List<string> arguments =
        [
            "--acp",
            "--no-auto-update",
            // Its default trusted directories include the system temp directory; the copy is the only one.
            "--disallow-temp-dir",
            // The built-in GitHub MCP server and remote control both reach off the machine.
            "--disable-builtin-mcps",
            "--no-remote",
        ];

        if (!string.IsNullOrWhiteSpace(model))
        {
            arguments.Add("--model");
            arguments.Add(model.Trim());
        }

        return arguments;
    }

    public async IAsyncEnumerable<AgentEvent> RunAsync(
        AgentSessionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        yield return new SessionStarted(_clock(), request.SessionId, TypedTurns: false);

        if (ClaudeSessionRunner.PinWarning(AgentRuntimeCatalog.For(AgentTool.Copilot), _runtimeReport?.Latest(AgentTool.Copilot)?.ToVersion) is { } warning)
        {
            yield return new MessageEmitted(_clock(), TranscriptRole.System, warning);
        }

        if (request.MaxBudgetUsd is not null)
        {
            yield return new MessageEmitted(_clock(), TranscriptRole.System, BudgetNotice);
        }

        var start = Start(request);
        if (start.Session is not { } live)
        {
            _broker.ReleaseSession(request.SessionId);
            yield return new SessionEnded(_clock(), AgentSessionState.Failed, start.Failure);
            yield break;
        }

        var errors = new StringBuilder();
        using var pumps = new CancellationTokenSource();
        var stderr = Task.Run(
            async () =>
            {
                try
                {
                    errors.Append(await live.Rpc.Process.StandardError.ReadToEndAsync(pumps.Token).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                {
                }
            },
            CancellationToken.None);

        var reader = Task.Run(
            async () =>
            {
                try
                {
                    await live.Rpc.ReadAsync(
                            (method, parameters) =>
                            {
                                if (method == "session/update")
                                {
                                    foreach (var e in live.Parser.Update(parameters))
                                    {
                                        live.Write(e);
                                    }
                                }

                                return Task.CompletedTask;
                            },
                            (method, parameters) => OnRequestAsync(live, method, parameters),
                            line => _logger.LogDebug("Copilot wrote a line that is not JSON ({Length} characters).", line.Length),
                            pumps.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "The Copilot session's output stream ended abruptly.");
                }
                finally
                {
                    // NOT TryComplete() straight away: the prompt's result is the last thing the CLI writes,
                    // and the message built out of it is written from another task. See DrainTurnWorkAsync.
                    await live.DrainTurnWorkAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                    live.Events.Writer.TryComplete();
                }
            },
            CancellationToken.None);

        var opening = Task.Run(() => OpenAsync(request, live, pumps.Token), CancellationToken.None);
        live.Track(opening); // it awaits the FIRST prompt, so it is that turn's writing task

        try
        {
            await foreach (var e in live.Events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return e;
            }
        }
        finally
        {
            live.Kill();
            _live.TryRemove(request.SessionId, out _);
            _broker.ReleaseSession(request.SessionId);
            await pumps.CancelAsync().ConfigureAwait(false);
        }

        await Task.WhenAny(Task.WhenAll(reader, stderr, opening), Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None))
            .ConfigureAwait(false);

        yield return Ended(live, errors.ToString());
    }

    public Task<bool> SendAsync(AgentSessionId sessionId, string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text) || !_live.TryGetValue(sessionId, out var live) || live.Stopped || live.AcpSessionId is null)
        {
            return Task.FromResult(false);
        }

        // A new session/prompt ABORTS the one running (read off the pinned code), so a follow-up waits its turn.
        if (live.QueueOrClaimTurn(text) is { } claimed)
        {
            live.Track(Task.Run(() => PromptAsync(live, claimed), CancellationToken.None));
        }

        return Task.FromResult(true);
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

    /// <summary>
    /// <c>session/set_model</c> on the live session. ACP names no "the CLI's default" to return to, so a null model
    /// is refused rather than approximated.
    /// </summary>
    public bool ChangeModel(AgentSessionId sessionId, string? model)
    {
        if (string.IsNullOrWhiteSpace(model) || !_live.TryGetValue(sessionId, out var live) || live.Stopped
            || live.AcpSessionId is not { } acpSession)
        {
            return false;
        }

        var trimmed = model.Trim();
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await live.Rpc.RequestAsync(
                            "session/set_model",
                            new JsonObject { ["sessionId"] = acpSession, ["modelId"] = trimmed },
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    live.Write(new MessageEmitted(_clock(), TranscriptRole.System, ClaudeSessionRunner.ModelChangeNotice(trimmed)));
                }
                catch (JsonRpcException ex)
                {
                    live.Write(new MessageEmitted(_clock(), TranscriptRole.System, "Copilot did not change the model: " + ex.Message));
                }
            },
            CancellationToken.None);
        return true;
    }

    /// <summary>The transcript line for a request that carried a USD ceiling Copilot cannot honour.</summary>
    public const string BudgetNotice =
        "This session has a spending ceiling, but Copilot reports no cost and its own ceiling is in AI credits, "
        + "so the ceiling cannot stop this session.";

    // ---- protocol -----------------------------------------------------------------------

    private async Task OpenAsync(AgentSessionRequest request, LiveSession live, CancellationToken token)
    {
        try
        {
            var initialized = await live.Rpc.RequestAsync(
                    "initialize",
                    new JsonObject
                    {
                        ["protocolVersion"] = ProtocolVersion,
                        // Nothing is offered: the CLI reads and writes files itself, inside its own path checks, and
                        // never through the studio's file system or a studio terminal.
                        ["clientCapabilities"] = new JsonObject
                        {
                            ["fs"] = new JsonObject { ["readTextFile"] = false, ["writeTextFile"] = false },
                            ["terminal"] = false,
                        },
                    },
                    token)
                .ConfigureAwait(false);

            var canLoad = initialized.ValueKind == JsonValueKind.Object
                && initialized.TryGetProperty("agentCapabilities", out var capabilities)
                && capabilities.ValueKind == JsonValueKind.Object
                && capabilities.TryGetProperty("loadSession", out var load)
                && load.ValueKind == JsonValueKind.True;

            var resume = string.IsNullOrWhiteSpace(request.ResumeSessionId) ? null : request.ResumeSessionId.Trim();
            string? acpSession = null;
            if (resume is not null && canLoad)
            {
                live.Parser.Replaying = true;
                try
                {
                    await live.Rpc.RequestAsync(
                            "session/load",
                            new JsonObject { ["sessionId"] = resume, ["cwd"] = live.WorkingDirectory, ["mcpServers"] = new JsonArray() },
                            token)
                        .ConfigureAwait(false);
                    acpSession = resume;
                }
                finally
                {
                    live.Parser.Replaying = false;
                }
            }
            else
            {
                if (resume is not null)
                {
                    live.Write(new MessageEmitted(
                        _clock(),
                        TranscriptRole.System,
                        "Copilot does not offer to reload a conversation, so this one starts fresh without the earlier turns."));
                }

                var created = await live.Rpc.RequestAsync(
                        "session/new",
                        new JsonObject { ["cwd"] = live.WorkingDirectory, ["mcpServers"] = new JsonArray() },
                        token)
                    .ConfigureAwait(false);
                acpSession = created.ValueKind == JsonValueKind.Object
                    && created.TryGetProperty("sessionId", out var id)
                    && id.ValueKind == JsonValueKind.String
                        ? id.GetString()
                        : null;
            }

            if (string.IsNullOrWhiteSpace(acpSession))
            {
                live.Write(new MessageEmitted(_clock(), TranscriptRole.System, "Copilot did not name the conversation it opened."));
                live.Kill();
                return;
            }

            live.AcpSessionId = acpSession;
            live.Write(new SessionRegistered(_clock(), acpSession, string.IsNullOrWhiteSpace(request.Model) ? null : request.Model.Trim()));

            if (!string.IsNullOrWhiteSpace(request.Prompt) && live.QueueOrClaimTurn(request.Prompt) is { } first)
            {
                await PromptAsync(live, first).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (JsonRpcException ex)
        {
            live.Write(new MessageEmitted(_clock(), TranscriptRole.System, "Copilot refused to open the conversation: " + ex.Message));
            live.Kill();
        }
    }

    /// <summary>Runs one prompt to its end, then the next queued one, until the queue is empty.</summary>
    private async Task PromptAsync(LiveSession live, string text)
    {
        var next = text;
        while (next is not null && live.AcpSessionId is { } acpSession)
        {
            live.Write(new TurnActivity(_clock(), AgentTurnPhase.Submitted));
            IReadOnlyList<AgentEvent> ending;
            try
            {
                var result = await live.Rpc.RequestAsync(
                        "session/prompt",
                        new JsonObject
                        {
                            ["sessionId"] = acpSession,
                            ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = next }),
                        },
                        CancellationToken.None)
                    .ConfigureAwait(false);
                ending = live.Parser.PromptReturned(result);
            }
            catch (JsonRpcException ex)
            {
                ending = live.Parser.PromptFailed(ex.Message);
            }

            foreach (var e in ending)
            {
                live.Write(e);
            }

            next = live.CompleteTurn();
        }
    }

    /// <summary>Every request the CLI can make of the client. Anything not named is refused, never ignored.</summary>
    private async Task<JsonRpcAnswer> OnRequestAsync(LiveSession live, string method, JsonElement parameters)
    {
        if (method != "session/request_permission")
        {
            // fs/*, terminal/* and elicitation were never advertised, so they are not the studio's to answer.
            return JsonRpcAnswer.Error(JsonRpcStdioChild.MethodNotFound, $"Fravashi does not handle {method}.");
        }

        var options = Options(parameters);
        var approved = await ApproveAsync(live, parameters).ConfigureAwait(false);

        var chosen = approved
            ? options.FirstOrDefault(o => o.Kind == "allow_once")
            : options.FirstOrDefault(o => o.Kind == "reject_once") ?? options.FirstOrDefault(o => o.Kind == "reject_always");

        // Approved with no allow-once option means the only allow on offer is a standing one, which is refused.
        if (approved && chosen is null)
        {
            chosen = options.FirstOrDefault(o => o.Kind == "reject_once") ?? options.FirstOrDefault(o => o.Kind == "reject_always");
        }

        var outcome = chosen is null
            ? new JsonObject { ["outcome"] = "cancelled" }
            : new JsonObject { ["outcome"] = "selected", ["optionId"] = chosen.OptionId };
        return JsonRpcAnswer.Ok(new JsonObject { ["outcome"] = outcome });
    }

    private async Task<bool> ApproveAsync(LiveSession live, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("toolCall", out var call)
            || call.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var kind = Text(call, "kind");
        var title = Text(call, "title") ?? string.Empty;
        var toolCallId = Text(call, "toolCallId");
        var input = call.TryGetProperty("rawInput", out var raw) && raw.ValueKind == JsonValueKind.Object ? raw : default;

        string tool;
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["title"] = title };
        switch (kind)
        {
            case "execute" when Text(input, "command") is { Length: > 0 } command:
                tool = "Bash";
                values["command"] = command;
                break;

            case "edit" when Text(input, "fileName") is { Length: > 0 } file:
                tool = "Edit";
                values["file_path"] = Path.IsPathRooted(file) ? file : Path.Combine(live.WorkingDirectory, file);
                break;

            case "read":
                // Never "Read": see the class summary. The paths are shown as sent, and this always asks.
                tool = OutsideReadTool;
                values["paths"] = Text(input, "path") ?? title;
                break;

            case "fetch" when Text(input, "url") is { Length: > 0 } url:
                tool = "WebFetch";
                values["url"] = url;
                break;

            case "execute" or "edit" or "fetch":
                // The kind promised a subject the call did not carry: nothing a user could meaningfully approve.
                return false;

            default:
                tool = "copilot_" + (string.IsNullOrWhiteSpace(kind) ? "tool" : kind);
                values["input"] = input.ValueKind == JsonValueKind.Object ? input.GetRawText() : string.Empty;
                break;
        }

        try
        {
            var decision = await _broker.RequestAsync(live.SessionId, tool, toolCallId, AgentToolInput.FromStrings(values))
                .ConfigureAwait(false);
            return decision.Approved;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "The approval broker failed; the Copilot call is rejected.");
            return false;
        }
    }

    private static List<PermissionOption> Options(JsonElement parameters)
    {
        List<PermissionOption> options = [];
        if (parameters.ValueKind == JsonValueKind.Object
            && parameters.TryGetProperty("options", out var list)
            && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var option in list.EnumerateArray())
            {
                if (Text(option, "optionId") is { } id && Text(option, "kind") is { } kind)
                {
                    options.Add(new PermissionOption(id, kind));
                }
            }
        }

        return options;
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // ---- starting and ending -----------------------------------------------------------

    private (LiveSession? Session, string? Failure) Start(AgentSessionRequest request)
    {
        _sync?.EnsureStarted(request.WorkspaceId);
        var workingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? _sync?.CopyPathFor(request.WorkspaceId)
            : request.WorkingDirectory;
        if (workingDirectory is null)
        {
            return (null, $"Workspace {request.WorkspaceId} has no contained copy for an agent to work in.");
        }

        _broker.RegisterSession(request.SessionId, workingDirectory, typedTurns: false);
        try
        {
            var process = Process.Start(BuildStartInfo(request, workingDirectory));
            if (process is null)
            {
                return (null, $"The agent CLI ({_launcher.FileName}) did not start.");
            }

            var live = new LiveSession(
                request.SessionId,
                new JsonRpcStdioChild(process, stampVersion: true),
                workingDirectory,
                new CopilotAcpEvents(_clock));
            _live[request.SessionId] = live;
            return (live, null);
        }
        catch (Exception ex)
        {
            return (null, $"The agent CLI ({_launcher.FileName}) could not start: {ex.Message}");
        }
    }

    private ProcessStartInfo BuildStartInfo(AgentSessionRequest request, string workingDirectory)
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

        // Backlog 72, first, as in the other runners.
        AgentProcessEnvironment.Restrict(info);

        foreach (var argument in _launcher.LeadingArguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var argument in BuildArguments(request.Model))
        {
            info.ArgumentList.Add(argument);
        }

        if (_credentials is not null)
        {
            foreach (var (key, value) in _credentials.EnvironmentFor(AgentTool.Copilot))
            {
                info.Environment[key] = value;
            }
        }

        var nodePath = _nodePaths?.PathPrefixFor(ProjectNodeResolver.Resolve(workingDirectory));
        if (!string.IsNullOrWhiteSpace(nodePath))
        {
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

        return info;
    }

    private SessionEnded Ended(LiveSession live, string errorOutput)
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

        var detail = errorOutput.Trim();
        return new SessionEnded(
            _clock(),
            AgentSessionState.Failed,
            string.IsNullOrEmpty(detail)
                ? $"The agent CLI exited with code {exitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}."
                : detail);
    }

    private sealed record PermissionOption(string OptionId, string Kind);

    private sealed class LiveSession(
        AgentSessionId sessionId,
        JsonRpcStdioChild rpc,
        string workingDirectory,
        CopilotAcpEvents parser)
    {
        private readonly Lock _gate = new();

        private Task _turnWork = Task.CompletedTask;
        private readonly Queue<string> _queued = new();
        private bool _turnActive;
        private string? _acpSessionId;

        public AgentSessionId SessionId { get; } = sessionId;

        public JsonRpcStdioChild Rpc { get; } = rpc;

        public string WorkingDirectory { get; } = workingDirectory;

        public CopilotAcpEvents Parser { get; } = parser;

        public Channel<AgentEvent> Events { get; } =
            Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true });

        /// <summary>The ACP session id, which is what a resume loads.</summary>
        public string? AcpSessionId
        {
            get
            {
                lock (_gate)
                {
                    return _acpSessionId;
                }
            }

            set
            {
                lock (_gate)
                {
                    _acpSessionId = value;
                }
            }
        }

        public bool Stopped { get; private set; }

        public void Write(AgentEvent e) => Events.Writer.TryWrite(e);

        /// <summary>
        /// Remembers the task that is currently turning a prompt into events, so the stdout reader can wait for
        /// it before it closes the channel those events are written to. See <see cref="DrainTurnWorkAsync"/>.
        /// </summary>
        public void Track(Task work)
        {
            lock (_gate)
            {
                _turnWork = work;
            }
        }

        /// <summary>
        /// Waits, bounded, for the in-flight turn to finish writing. This exists because of a measured fault,
        /// not for tidiness (backlog 66): a prompt's RESULT is turned into the turn's message on the task
        /// AWAITING that result, while the stdout reader completes the event channel the instant the CLI's
        /// output closes, and a write to a completed channel is silently dropped. A CLI that exits as soon as
        /// it has answered therefore loses the whole turn's text, with no error anywhere. It showed up three
        /// times in CI as "no message events at all" and never once in isolation, because the stand-in waits
        /// 20ms before exiting and that is usually, but not always, long enough to win the race.
        /// <para>
        /// The budget matters only when the CLI died mid-turn, and even then it is rarely reached:
        /// <see cref="JsonRpcStdioChild"/> fails every pending request when its input ends, so the prompt task
        /// unwinds through its own failure path instead of hanging. The ceiling is here for the case nobody
        /// anticipated, because a reader that never completes the channel would hang the whole enumeration.
        /// </para>
        /// </summary>
        public async Task DrainTurnWorkAsync(TimeSpan budget)
        {
            var deadline = DateTime.UtcNow + budget;
            while (true)
            {
                Task work;
                lock (_gate)
                {
                    work = _turnWork;
                }

                if (work.IsCompleted)
                {
                    return;
                }

                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero)
                {
                    return;
                }

                try
                {
                    await work.WaitAsync(left).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Whatever that turn did or failed to do is its own business and is already an event;
                    // the only question here is whether it has stopped writing.
                    return;
                }
            }
        }

        public string? QueueOrClaimTurn(string text)
        {
            lock (_gate)
            {
                if (_turnActive)
                {
                    _queued.Enqueue(text);
                    return null;
                }

                _turnActive = true;
                return text;
            }
        }

        public string? CompleteTurn()
        {
            lock (_gate)
            {
                if (_queued.Count > 0 && !Stopped)
                {
                    return _queued.Dequeue();
                }

                _turnActive = false;
                return null;
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                Stopped = true;
                _queued.Clear();
            }

            Kill();
        }

        public void Kill()
        {
            try
            {
                if (!Rpc.Process.HasExited)
                {
                    Rpc.Process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
            {
            }
        }

        public int? ExitCode()
        {
            try
            {
                if (!Rpc.Process.HasExited)
                {
                    Rpc.Process.WaitForExit(TimeSpan.FromSeconds(10)); // its output has closed, so it is exiting; bounded all the same
                }

                return Rpc.Process.HasExited ? Rpc.Process.ExitCode : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
}
#endif
