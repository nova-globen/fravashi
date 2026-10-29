using System.Text.Json;
using Fravashi.Domain.AgentSessions;
using Fravashi.Domain.Projects;

namespace Fravashi.Application.Sessions.Tools;

/// <summary>
/// Whether a studio tool can change anything (ADR 0019). A <see cref="ReadOnly"/> tool may run without an
/// idempotency key; a <see cref="Mutating"/> one is refused without one, because a mutation that cannot be
/// matched to its earlier attempt is exactly the double apply the ledger exists to prevent.
/// </summary>
public enum StudioToolAccess
{
    /// <summary>Observes; running it twice changes nothing.</summary>
    ReadOnly,

    /// <summary>Changes something; needs an operation key and goes through the ledger before it runs.</summary>
    Mutating,
}

/// <summary>One call to a studio tool, bound to the session that made it.</summary>
/// <param name="SessionId">
/// The session the call's SECRET resolved to. Never taken from the request body: a tool only ever acts for the
/// session that authenticated.
/// </param>
/// <param name="ToolName">The tool's bare name, as the server lists it (not the CLI's <c>mcp__</c> form).</param>
/// <param name="Arguments">The call's arguments object, cloned out of the request (an empty object when none).</param>
/// <param name="OperationKey">
/// The idempotency key the call carried, or null. For the Claude CLI this is the model's own tool-use id
/// (<c>_meta["claudecode/toolUseId"]</c>, measured against 2.1.222), which is stable across a transport retry
/// of the same call and unique across calls.
/// </param>
/// <param name="Project">
/// The project this call acts on (backlog 121): the project of the workspace the authenticated session runs in,
/// resolved by the server for THIS call. Like the session, never taken from the request body.
/// </param>
public sealed record StudioToolCall(
    AgentSessionId SessionId,
    string ToolName,
    JsonElement Arguments,
    string? OperationKey,
    ProjectId Project);

/// <summary>
/// What a studio tool answered: text for the model, whether it is an error, and (backlog 159) any images, sent as MCP
/// <c>image</c> content blocks after the text. Images are never kept in the tool ledger, so a replayed call says so
/// in words instead of returning a picture of a moment that has passed.
/// </summary>
public sealed record StudioToolResult(string Text, bool IsError = false, IReadOnlyList<StudioToolImage>? Images = null)
{
    public static StudioToolResult Success(string text) => new(text);

    public static StudioToolResult Failure(string text) => new(text, IsError: true);
}

/// <summary>One image a tool returns, with its media type (<c>image/jpeg</c> or <c>image/png</c>).</summary>
public sealed record StudioToolImage(string MediaType, byte[] Bytes);

/// <summary>
/// A tool the studio's own tool server exposes to agent sessions (ADR 0019, backlog 84). Items 41 and 42 add
/// the work-item and wiki tools on top of this seam; the foundation ships only a harmless read-only one.
/// </summary>
public interface IStudioTool
{
    /// <summary>The bare tool name the server lists. The CLI presents it as <c>mcp__fravashi__{Name}</c>.</summary>
    string Name { get; }

    /// <summary>
    /// What the model reads to decide whether to call it. Deferred behind tool search unless the server's
    /// <c>tools/list</c> answer marks this tool to load upfront (the Infrastructure project's
    /// <c>StudioToolServer.AlwaysLoadedTools</c>).
    /// </summary>
    string Description { get; }

    /// <summary>The JSON Schema of the arguments object, as literal JSON.</summary>
    string InputSchemaJson { get; }

    StudioToolAccess Access { get; }

    Task<StudioToolResult> InvokeAsync(StudioToolCall call, CancellationToken cancellationToken = default);
}

/// <summary>What the ledger says about a call before it runs.</summary>
public enum ToolCallClaimOutcome
{
    /// <summary>Never seen: the claim was DURABLY recorded, so the call may run now, exactly once.</summary>
    Fresh,

    /// <summary>Already completed: return the recorded result, do not run it again.</summary>
    Replayed,

    /// <summary>The same key is running right now in this process: refuse, do not run it twice.</summary>
    InFlight,

    /// <summary>
    /// A previous studio process claimed it and never recorded an outcome: whether it took effect is unknown,
    /// so it is answered with an explicit failure and never run again.
    /// </summary>
    Interrupted,

    /// <summary>The key was already used for a different tool or different arguments: refuse.</summary>
    Conflict,
}

/// <summary>A call an earlier studio process started and never recorded an outcome for.</summary>
public sealed record InterruptedToolCall(AgentSessionId SessionId, string OperationKey, string ToolName);

/// <summary>The ledger's answer to a claim, with the recorded result when there is one.</summary>
public sealed record ToolCallClaim(ToolCallClaimOutcome Outcome, StudioToolResult? Recorded = null);

/// <summary>
/// The durable idempotency ledger behind the tool server (ADR 0019: "recorded in a durable ledger before it is
/// attempted"). Keys are scoped per session, so one session's key can never replay another session's answer.
/// </summary>
public interface IToolCallLedger
{
    /// <summary>
    /// Claims a key for one attempt. <see cref="ToolCallClaimOutcome.Fresh"/> is returned only AFTER the claim is
    /// on disk, so a crash at any later point leaves a record that the call may have started.
    /// </summary>
    /// <param name="fingerprint">A digest of the tool name and arguments, so a reused key with a different call is caught.</param>
    ToolCallClaim Claim(AgentSessionId sessionId, string operationKey, string toolName, string fingerprint);

    /// <summary>Records the outcome of a <see cref="ToolCallClaimOutcome.Fresh"/> claim, exactly as it was delivered.</summary>
    void Complete(AgentSessionId sessionId, string operationKey, StudioToolResult result);

    /// <summary>Drops a session's ledger, when the conversation itself is deleted. Absent is fine.</summary>
    void Forget(AgentSessionId sessionId);

    /// <summary>
    /// At studio start: every call a PREVIOUS process claimed and never finished, now recorded as interrupted, so
    /// the studio can say so in that session's transcript instead of leaving the call silently unresolved.
    /// </summary>
    IReadOnlyList<InterruptedToolCall> RecoverInterrupted();
}
