namespace Fravashi.Domain.AgentSessions;

/// <summary>Strongly-typed identifier for an <see cref="AgentSession"/>.</summary>
public readonly record struct AgentSessionId(Guid Value)
{
    public static AgentSessionId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

/// <summary>What a session asks the agent to do.</summary>
public enum AgentSessionKind
{
    /// <summary>Author or refine a plan for a feature.</summary>
    Plan,

    /// <summary>Implement an existing plan.</summary>
    Implement,

    /// <summary>A free-form question / ad-hoc prompt.</summary>
    Ask,

    /// <summary>A free-form interactive chat conversation.</summary>
    Chat,

    /// <summary>A read-only session restored from the Claude CLI's own session history.</summary>
    CliNative,
}

/// <summary>The headless coding-agent CLI backing a session.</summary>
public enum AgentTool
{
    Claude,
    Copilot,
    Codex,
}

/// <summary>Lifecycle state of a session.</summary>
public enum AgentSessionState
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>Who/what produced a transcript entry.</summary>
public enum TranscriptRole
{
    User,
    Assistant,
    Tool,
    ToolResult,
    System,
    /// <summary>A harness-injected background-task completion event (e.g. &lt;task-notification&gt;).</summary>
    Event,
    /// <summary>
    /// A turn's prompt the studio sent on its own, such as the one that resumes a conversation after a restart or an
    /// upgrade (backlog 184). It starts a turn like a person's prompt does, but the person did not say it.
    /// </summary>
    StudioPrompt,
}

/// <summary>
/// One entry in a session transcript. Tool entries always carry a <see cref="ToolName"/> and human
/// text so the UI never renders a bare "[Tool]" marker; tool-result entries also carry an outcome.
/// </summary>
public sealed record TranscriptEntry(
    DateTimeOffset Timestamp,
    TranscriptRole Role,
    string Text,
    string? ToolName = null,
    bool? Success = null)
{
    public static TranscriptEntry User(DateTimeOffset ts, string text) => new(ts, TranscriptRole.User, text);

    public static TranscriptEntry Assistant(DateTimeOffset ts, string text) => new(ts, TranscriptRole.Assistant, text);

    public static TranscriptEntry ToolCall(DateTimeOffset ts, string toolName, string text) =>
        new(ts, TranscriptRole.Tool, text, toolName);

    public static TranscriptEntry ToolOutcome(DateTimeOffset ts, string toolName, bool success, string text) =>
        new(ts, TranscriptRole.ToolResult, text, toolName, success);

    public static TranscriptEntry System(DateTimeOffset ts, string text) => new(ts, TranscriptRole.System, text);

    public static TranscriptEntry Event(DateTimeOffset ts, string text) => new(ts, TranscriptRole.Event, text);
}
