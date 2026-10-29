namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// The one tool call that is not a tool call: the CLI builds <c>StructuredOutput</c> out of the
/// <c>--json-schema</c> the studio itself sent, and the model calls it to END its turn (ADR 0022).
/// <para>
/// It is deliberately NOT in <see cref="AgentToolSafety"/>. That list is the read-only auto-allow, and
/// every entry on it declares WHERE it reads so <c>AgentReadScope</c> can prove the read stays inside the
/// session's contained copy. This tool reads nothing, writes nothing, runs nothing and names no location:
/// it hands the studio the document the studio asked for, and the CLI has already validated it against
/// that schema. Adding it to a list about paths would have widened a list whose entire value is being
/// narrow, and it would have described the thing wrongly on top of that. ADR 0022 says the same thing
/// about rendering: <c>StructuredOutput</c> is the turn's outcome, never a card beside <c>Read</c> and
/// <c>Edit</c>. This is that rule at the approval gate.
/// </para>
/// <para>
/// <b>Why this exists at all.</b> The per-session PreToolUse hook is registered with a <c>"*"</c> matcher,
/// so the CLI runs it before EVERY tool call, this one included. Measured in the container on 2026-08-07
/// against claude 2.1.223: an ordinary typed turn fires the hook once for <c>StructuredOutput</c>, and a
/// turn whose document keeps failing validation fires it FIVE times, once per attempt, before the turn
/// ends. Without the branch this type serves, switching the typed-turn flag on would have parked an
/// approval in front of the user at the end of every single turn.
/// </para>
/// <para>
/// <b>Being this tool is not enough on its own.</b> The caller must also know that THIS session asked for a
/// document, because a session that never sent a schema has no reason to see a call by this name, and an
/// unexplained call asks like everything else. Matching is ORDINAL and exact for the same reason: a case
/// variant, or an MCP-namespaced <c>mcp__server__StructuredOutput</c>, is a different tool that the studio
/// knows nothing about, so it must fall through to the user rather than inherit this one's silence.
/// </para>
/// </summary>
public static class AgentTurnOutcomeTool
{
    /// <summary>
    /// The name the CLI gives the tool it builds from the schema, measured on claude 2.1.222 (2026-08-06)
    /// and again on 2.1.223 (2026-08-07): the <c>tool_use</c> block, the matching <c>tool_result</c>, the
    /// validation errors and the PreToolUse hook payload all carry this exact spelling.
    /// </summary>
    public const string Name = "StructuredOutput";

    /// <summary>
    /// True when this call is the turn's terminating document rather than work the agent chose to do.
    /// <para>
    /// Ordinal and exact on purpose: this is the one name the studio lets past without asking, so anything
    /// that is merely close to it is something else and must ask.
    /// </para>
    /// </summary>
    public static bool Is(string? toolName) => string.Equals(toolName, Name, StringComparison.Ordinal);
}
