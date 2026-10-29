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
/// Runs a session against the Codex CLI's app-server (backlog 66): <c>codex app-server --listen stdio://</c>,
/// JSON-RPC over the child's stdin and stdout, one long-lived process per conversation exactly like the
/// Claude runner.
/// <para>
/// <b>Why app-server and not <c>codex exec --json</c></b> (measured against the pinned 0.146.0, see
/// <c>docs/plans/codex-copilot-runners.md</c>): exec runs one prompt per process and offers no approval
/// channel at all, so "every change asks" would be impossible. App-server sends its approvals to the CLIENT as
/// requests, so the studio's <see cref="IAgentApprovalBroker"/> is the approver in-protocol, without a hook.
/// </para>
/// <para>
/// <b>Fail closed.</b> An approval is <c>accept</c> only when the broker says the user approved; a denial, a
/// timeout, a released session and a broker failure are all <c>decline</c>. It never answers
/// <c>acceptForSession</c> or an execpolicy amendment, because those are standing grants. Every other request
/// the CLI can send (user input, MCP elicitation, permission profiles, auth refresh) is refused, and a method
/// this class does not name is answered "method not found" rather than left to hang.
/// </para>
/// <para>
/// <b>What this does NOT enforce, and why it is not offered in the UI.</b> Codex decides which calls reach the
/// client. Under <c>approvalPolicy: "untrusted"</c> its own trusted command set (<c>ls</c>, <c>cat</c>,
/// <c>sed</c>, per its help) runs without a request, so a read outside the copy is not path-scoped by
/// <see cref="AgentReadScope"/> the way a Claude <c>Read</c> is. Nothing registers this runner until the owner
/// decides about that gap and a live turn has been driven (the plan's "What the owner must do").
/// </para>
/// </summary>
public sealed class CodexSessionRunner : IAgentSessionRunner
{
    /// <summary>
    /// Features switched off on the command line because they reach outside the contained copy or off the
    /// machine. Every name is one <c>codex features list</c> prints at the pinned 0.146.0; the unrecognised-name
    /// behaviour of <c>--disable</c> was not measured, so a pin move must re-check this list.
    /// </summary>
    public static readonly IReadOnlyList<string> DisabledFeatures =
    [
        "apps",
        "browser_use",
        "browser_use_external",
        "computer_use",
        "in_app_browser",
        "image_generation",
        "multi_agent",
        "plugins",
        "remote_plugin",
        "tool_suggest",
    ];

    /// <summary>The approval policy every thread starts with: the strictest one the pinned schema offers.</summary>
    public const string ApprovalPolicy = "untrusted";

    /// <summary>The sandbox every thread starts with: writes confined to the copy, beyond that they ask.</summary>
    public const string SandboxMode = "workspace-write";

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

    public CodexSessionRunner(
        IAgentApprovalBroker broker,
        IWorkspaceAgentSync? sync = null,
        IAgentRuntimeCredentialStore? credentials = null,
        NodePathProvisioner? nodePaths = null,
        AgentCliLauncher? launcher = null,
        IReadOnlyDictionary<string, string>? environment = null,
        IAgentRuntimeStatusReport? runtimeReport = null,
        Func<DateTimeOffset>? clock = null,
        ILogger<CodexSessionRunner>? logger = null)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _sync = sync;
        _credentials = credentials;
        _nodePaths = nodePaths;
        _launcher = launcher ?? new AgentCliLauncher("codex", []);
        _environment = environment;
        _runtimeReport = runtimeReport;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _logger = logger ?? NullLogger<CodexSessionRunner>.Instance;
    }

    /// <summary>True while this process holds a live session with that id.</summary>
    public bool IsRunning(AgentSessionId sessionId) => _live.ContainsKey(sessionId);

    /// <summary>The command line after the executable. Everything on it is in the pinned binary's own help.</summary>
    public static IReadOnlyList<string> BuildArguments()
    {
        List<string> arguments = ["app-server", "--listen", "stdio://"];
        foreach (var feature in DisabledFeatures)
        {
            arguments.Add("--disable");
            arguments.Add(feature);
        }

        return arguments;
    }

    public async IAsyncEnumerable<AgentEvent> RunAsync(
        AgentSessionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A Codex session is never typed (ADR 0022's StructuredOutput contract has no Codex counterpart), and
        // the first event says so whatever the request asked for.
        yield return new SessionStarted(_clock(), request.SessionId, TypedTurns: false);

        if (ClaudeSessionRunner.PinWarning(AgentRuntimeCatalog.For(AgentTool.Codex), _runtimeReport?.Latest(AgentTool.Codex)?.ToVersion) is { } warning)
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
                            (method, parameters) => OnNotificationAsync(live, method, parameters),
                            (method, parameters) => OnRequestAsync(live, method, parameters),
                            line => _logger.LogDebug("Codex wrote a line that is not JSON ({Length} characters).", line.Length),
                            pumps.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "The Codex session's output stream ended abruptly.");
                }
                finally
                {
                    live.Events.Writer.TryComplete();
                }
            },
            CancellationToken.None);

        var handshake = Task.Run(() => OpenAsync(request, live, pumps.Token), CancellationToken.None);

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

        await Task.WhenAny(Task.WhenAll(reader, stderr, handshake), Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None))
            .ConfigureAwait(false);

        yield return Ended(live, errors.ToString());
    }

    public async Task<bool> SendAsync(AgentSessionId sessionId, string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text) || !_live.TryGetValue(sessionId, out var live) || live.Stopped)
        {
            return false;
        }

        if (live.QueueOrClaimTurn(text) is not { } claimed)
        {
            // A turn is running; this one starts when it completes, the way stdin queues for the Claude CLI.
            return true;
        }

        return await StartTurnAsync(live, claimed, cancellationToken).ConfigureAwait(false);
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
    /// Codex needs no restart for this: <c>turn/start</c> takes a <c>model</c> "for this turn and subsequent
    /// turns" (pinned schema), so the change rides on the next turn and the transcript records it now.
    /// </summary>
    public bool ChangeModel(AgentSessionId sessionId, string? model)
    {
        if (!_live.TryGetValue(sessionId, out var live) || live.Stopped || live.ThreadId is null)
        {
            return false;
        }

        live.SetPendingModel(new PendingModel(string.IsNullOrWhiteSpace(model) ? null : model.Trim()));
        live.Events.Writer.TryWrite(new MessageEmitted(_clock(), TranscriptRole.System, ClaudeSessionRunner.ModelChangeNotice(model)));
        return true;
    }

    /// <summary>The transcript line for a request that carried a USD ceiling Codex cannot honour.</summary>
    public const string BudgetNotice =
        "This session has a spending ceiling, but Codex reports no cost and has no ceiling of its own, "
        + "so the ceiling cannot stop this session.";

    // ---- protocol -----------------------------------------------------------------------

    /// <summary>initialize, initialized, then thread/start (or thread/resume), then the opening turn.</summary>
    private async Task OpenAsync(AgentSessionRequest request, LiveSession live, CancellationToken token)
    {
        try
        {
            await live.Rpc.RequestAsync(
                    "initialize",
                    new JsonObject
                    {
                        ["clientInfo"] = new JsonObject { ["name"] = "fravashi", ["title"] = "Fravashi", ["version"] = "1" },
                        ["capabilities"] = new JsonObject { ["experimentalApi"] = false, ["requestAttestation"] = false },
                    },
                    token)
                .ConfigureAwait(false);
            await live.Rpc.NotifyAsync("initialized", null, token).ConfigureAwait(false);

            var resume = string.IsNullOrWhiteSpace(request.ResumeSessionId) ? null : request.ResumeSessionId.Trim();
            var parameters = new JsonObject
            {
                ["cwd"] = live.WorkingDirectory,
                ["approvalPolicy"] = ApprovalPolicy,
                ["sandbox"] = SandboxMode,
            };
            if (!string.IsNullOrWhiteSpace(request.Model))
            {
                parameters["model"] = request.Model.Trim();
            }

            if (resume is not null)
            {
                parameters["threadId"] = resume;
            }

            var result = await live.Rpc.RequestAsync(resume is null ? "thread/start" : "thread/resume", parameters, token)
                .ConfigureAwait(false);

            var threadId = result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("thread", out var thread)
                && thread.ValueKind == JsonValueKind.Object
                && thread.TryGetProperty("id", out var id)
                && id.ValueKind == JsonValueKind.String
                    ? id.GetString()
                    : null;
            if (string.IsNullOrWhiteSpace(threadId))
            {
                live.Write(new MessageEmitted(_clock(), TranscriptRole.System, "Codex did not name the conversation it opened."));
                live.Kill();
                return;
            }

            live.ThreadId = threadId;
            var model = result.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            live.Write(new SessionRegistered(_clock(), threadId, model));

            if (!string.IsNullOrWhiteSpace(request.Prompt) && live.QueueOrClaimTurn(request.Prompt) is { } opening)
            {
                await StartTurnAsync(live, opening, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (JsonRpcException ex)
        {
            live.Write(new MessageEmitted(_clock(), TranscriptRole.System, "Codex refused to open the conversation: " + ex.Message));
            live.Kill();
        }
    }

    private async Task<bool> StartTurnAsync(LiveSession live, string text, CancellationToken token)
    {
        if (live.ThreadId is not { } threadId)
        {
            live.ReleaseTurn();
            return false;
        }

        var parameters = new JsonObject
        {
            ["threadId"] = threadId,
            ["input"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = text,
                ["text_elements"] = new JsonArray(),
            }),
        };
        if (live.TakePendingModel() is { } change && change.Model is { } model)
        {
            parameters["model"] = model;
        }

        try
        {
            await live.Rpc.RequestAsync("turn/start", parameters, token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is JsonRpcException or OperationCanceledException)
        {
            live.ReleaseTurn();
            if (ex is JsonRpcException)
            {
                live.Write(new MessageEmitted(_clock(), TranscriptRole.System, "Codex did not start the turn: " + ex.Message));
            }

            return false;
        }
    }

    private Task OnNotificationAsync(LiveSession live, string method, JsonElement parameters)
    {
        foreach (var e in live.Parser.Map(method, parameters))
        {
            live.Write(e);
            if (e is TurnCompleted && live.CompleteTurn() is { } next)
            {
                // Off the read loop: turn/start waits for a response that only this loop can read.
                _ = Task.Run(() => StartTurnAsync(live, next, CancellationToken.None), CancellationToken.None);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Every request the CLI can make of the client, answered. Anything not named here is refused: a request a
    /// later Codex adds must never be granted, or ignored into a hang, by omission.
    /// </summary>
    private async Task<JsonRpcAnswer> OnRequestAsync(LiveSession live, string method, JsonElement parameters)
    {
        switch (method)
        {
            case "item/commandExecution/requestApproval":
                return Decision(await ApproveCommandAsync(live, parameters).ConfigureAwait(false));

            case "item/fileChange/requestApproval":
                return Decision(await ApproveFileChangeAsync(live, parameters).ConfigureAwait(false));

            case "execCommandApproval":
            case "applyPatchApproval":
                // The v1 approval shapes. The studio starts v2 threads, so these should never arrive; if they do,
                // they are refused in their own vocabulary.
                return JsonRpcAnswer.Ok(new JsonObject
                {
                    ["decision"] = new JsonObject
                    {
                        ["denied"] = new JsonObject { ["rejection"] = "Fravashi answers approvals only through the v2 protocol." },
                    },
                });

            case "item/permissions/requestApproval":
                return JsonRpcAnswer.Error(JsonRpcStdioChild.Refused, "Fravashi does not grant standing permissions; every change asks.");

            case "item/tool/requestUserInput":
            case "mcpServer/elicitation/request":
            case "item/tool/call":
            case "account/chatgptAuthTokens/refresh":
            case "attestation/generate":
                return JsonRpcAnswer.Error(JsonRpcStdioChild.Refused, $"Fravashi does not answer {method}.");

            default:
                return JsonRpcAnswer.Error(JsonRpcStdioChild.MethodNotFound, $"Fravashi does not handle {method}.");
        }
    }

    private async Task<bool> ApproveCommandAsync(LiveSession live, JsonElement parameters)
    {
        var command = Text(parameters, "command");
        if (string.IsNullOrWhiteSpace(command))
        {
            // Nothing to show the user means nothing they could meaningfully approve.
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["command"] = command };
        if (Text(parameters, "cwd") is { Length: > 0 } cwd)
        {
            values["cwd"] = cwd;
        }

        if (Text(parameters, "reason") is { Length: > 0 } reason)
        {
            values["reason"] = reason;
        }

        return await AskAsync(live, CodexAppServerEvents.ShellTool, Text(parameters, "itemId"), values).ConfigureAwait(false);
    }

    /// <summary>
    /// A patch is approved only when EVERY file it touches is approved, each asked about on its own so each
    /// prompt carries its own resolved destination. The request itself names no file, so the files are the ones
    /// the item announced; a patch the studio never saw announced is declined, because an empty prompt is not
    /// something a user can approve.
    /// </summary>
    private async Task<bool> ApproveFileChangeAsync(LiveSession live, JsonElement parameters)
    {
        if (Text(parameters, "grantRoot") is { Length: > 0 })
        {
            return false;
        }

        var itemId = Text(parameters, "itemId");
        if (itemId is null || live.Parser.FileChangesFor(itemId) is not { Count: > 0 } changes)
        {
            return false;
        }

        foreach (var change in changes)
        {
            var tool = change.Kind == "add" ? "Write" : "Edit";
            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["file_path"] = Absolute(live.WorkingDirectory, change.Path),
                ["change"] = change.Kind,
            };
            if (!await AskAsync(live, tool, itemId, values).ConfigureAwait(false))
            {
                return false;
            }

            if (change.MovePath is { Length: > 0 } destination
                && !await AskAsync(
                        live,
                        "Write",
                        itemId,
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["file_path"] = Absolute(live.WorkingDirectory, destination),
                            ["change"] = "move",
                        })
                    .ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<bool> AskAsync(LiveSession live, string tool, string? toolUseId, Dictionary<string, string> values)
    {
        try
        {
            var decision = await _broker.RequestAsync(live.SessionId, tool, toolUseId, AgentToolInput.FromStrings(values))
                .ConfigureAwait(false);
            return decision.Approved;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "The approval broker failed; the Codex call is declined.");
            return false;
        }
    }

    private static JsonRpcAnswer Decision(bool approved) =>
        JsonRpcAnswer.Ok(new JsonObject { ["decision"] = approved ? "accept" : "decline" });

    /// <summary>
    /// A relative patch path is relative to the thread's cwd, which is the copy. Absolute paths are left exactly as
    /// sent: the broker resolves containment from the raw value.
    /// </summary>
    private static string Absolute(string root, string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(root, path);

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

        // Registered for its approval channel only: no hook script reads the secret, the runner asks in-process.
        _broker.RegisterSession(request.SessionId, workingDirectory, typedTurns: false);
        try
        {
            var process = Process.Start(BuildStartInfo(workingDirectory));
            if (process is null)
            {
                return (null, $"The agent CLI ({_launcher.FileName}) did not start.");
            }

            var live = new LiveSession(
                request.SessionId,
                new JsonRpcStdioChild(process, stampVersion: false),
                workingDirectory,
                new CodexAppServerEvents(_clock));
            _live[request.SessionId] = live;
            return (live, null);
        }
        catch (Exception ex)
        {
            return (null, $"The agent CLI ({_launcher.FileName}) could not start: {ex.Message}");
        }
    }

    private ProcessStartInfo BuildStartInfo(string workingDirectory)
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

        // Backlog 72, and FIRST for the same reason as in the Claude runner: nothing inherited survives it.
        AgentProcessEnvironment.Restrict(info);

        foreach (var argument in _launcher.LeadingArguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var argument in BuildArguments())
        {
            info.ArgumentList.Add(argument);
        }

        if (_credentials is not null)
        {
            foreach (var (key, value) in _credentials.EnvironmentFor(AgentTool.Codex))
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

    private sealed record PendingModel(string? Model);

    private sealed class LiveSession(
        AgentSessionId sessionId,
        JsonRpcStdioChild rpc,
        string workingDirectory,
        CodexAppServerEvents parser)
    {
        private readonly Lock _gate = new();
        private readonly Queue<string> _queued = new();
        private bool _turnActive;
        private PendingModel? _pendingModel;
        private string? _threadId;

        public AgentSessionId SessionId { get; } = sessionId;

        public JsonRpcStdioChild Rpc { get; } = rpc;

        public string WorkingDirectory { get; } = workingDirectory;

        public CodexAppServerEvents Parser { get; } = parser;

        /// <summary>Several writers (the reader, the handshake, a model change), one reader (RunAsync).</summary>
        public Channel<AgentEvent> Events { get; } =
            Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions { SingleReader = true });

        /// <summary>The Codex thread id, which is what a resume sends back.</summary>
        public string? ThreadId
        {
            get
            {
                lock (_gate)
                {
                    return _threadId;
                }
            }

            set
            {
                lock (_gate)
                {
                    _threadId = value;
                }
            }
        }

        public bool Stopped { get; private set; }

        public void SetPendingModel(PendingModel change)
        {
            lock (_gate)
            {
                _pendingModel = change;
            }
        }

        public PendingModel? TakePendingModel()
        {
            lock (_gate)
            {
                var change = _pendingModel;
                _pendingModel = null;
                return change;
            }
        }

        public void Write(AgentEvent e) => Events.Writer.TryWrite(e);

        /// <summary>The text to start now, or null when a turn is running and this one was queued behind it.</summary>
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

        /// <summary>A turn ended: the next queued turn, already claimed, or null when there is none.</summary>
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

        public void ReleaseTurn()
        {
            lock (_gate)
            {
                _turnActive = false;
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
