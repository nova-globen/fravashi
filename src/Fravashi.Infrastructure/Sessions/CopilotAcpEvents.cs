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

using System.Text;
using System.Text.Json;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// Turns the Copilot CLI's Agent Client Protocol <c>session/update</c> notifications, and the result of each
/// <c>session/prompt</c>, into the studio's <see cref="AgentEvent"/>s (backlog 66).
/// <para>
/// The shapes are read off the pinned 1.0.78 package's bundled code (the function that maps its internal
/// events to ACP updates, and the zod schemas beside it), which follows the public ACP specification. The binary
/// generates no schema of its own, so this is weaker evidence than the Codex runner's and the plan says so.
/// </para>
/// <para>
/// ACP streams assistant text only as chunks and has no "message complete" event, so the chunks are buffered and
/// committed as one bubble at a block boundary: when a tool call opens, and when the prompt returns. That is the
/// same "commit each text block where it ends" rule the Claude parser follows.
/// </para>
/// </summary>
public sealed class CopilotAcpEvents
{
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, (string Tool, string Detail)> _open = new(StringComparer.Ordinal);
    private readonly StringBuilder _text = new();
    private readonly Lock _gate = new();

    private long _inputTokens;
    private long _cachedInputTokens;
    private long _outputTokens;
    private long _contextTokens;
    private long _contextWindow;
    private long _baselineTokens;
    private int _toolCalls;
    private int _turns;

    public CopilotAcpEvents(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// True while <c>session/load</c> is replaying an earlier conversation. The replay arrives as ordinary updates,
    /// and the studio already holds that transcript, so everything is swallowed until the load returns.
    /// </summary>
    public bool Replaying { get; set; }

    /// <summary>The events one <c>session/update</c> produces, possibly none.</summary>
    public IReadOnlyList<AgentEvent> Update(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("update", out var update)
            || update.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        lock (_gate)
        {
            if (Replaying)
            {
                return [];
            }

            return Text(update, "sessionUpdate") switch
            {
                "agent_message_chunk" => Chunk(update),
                "agent_thought_chunk" => [new TurnActivity(_clock(), AgentTurnPhase.Thinking)],
                "tool_call" => ToolCall(update),
                "tool_call_update" => ToolCallUpdate(update),
                "usage_update" => Usage(update),
                _ => [],
            };
        }
    }

    /// <summary>The events a finished <c>session/prompt</c> produces: the last text block, usage, and the turn end.</summary>
    public IReadOnlyList<AgentEvent> PromptReturned(JsonElement result)
    {
        lock (_gate)
        {
            List<AgentEvent> events = [.. Flush()];
            _turns++;

            if (result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("usage", out var usage)
                && usage.ValueKind == JsonValueKind.Object)
            {
                // The pinned code builds this from the session's accumulated usage, so it SETS the totals. If it
                // turned out to be per prompt, the footer would under-count rather than double-count.
                var input = Number(usage, "inputTokens");
                _cachedInputTokens = Math.Max(0, Number(usage, "cachedReadTokens"));
                _inputTokens = Math.Max(0, input - _cachedInputTokens);
                _outputTokens = Math.Max(0, Number(usage, "outputTokens"));
            }

            var stopReason = Text(result, "stopReason");
            events.Add(new StatsUpdated(_clock(), Snapshot()));
            events.Add(new TurnCompleted(_clock(), null, false, stopReason));
            return events;
        }
    }

    /// <summary>The events for a <c>session/prompt</c> the CLI answered with an error instead of a stop reason.</summary>
    public IReadOnlyList<AgentEvent> PromptFailed(string message)
    {
        lock (_gate)
        {
            List<AgentEvent> events = [.. Flush()];
            _turns++;
            events.Add(new StatsUpdated(_clock(), Snapshot()));
            events.Add(new TurnCompleted(_clock(), null, true, "error", ErrorText: message));
            return events;
        }
    }

    private List<AgentEvent> Chunk(JsonElement update)
    {
        if (update.TryGetProperty("content", out var content) && Text(content, "text") is { Length: > 0 } text)
        {
            _text.Append(text);
            return [new PartialText(_clock(), text)];
        }

        return [];
    }

    private List<AgentEvent> ToolCall(JsonElement update)
    {
        if (Text(update, "toolCallId") is not { } id)
        {
            return [];
        }

        List<AgentEvent> events = [.. Flush()];
        var tool = ToolName(Text(update, "kind"));
        var detail = Text(update, "title") ?? string.Empty;
        _open[id] = (tool, detail);
        _toolCalls++;
        events.Add(new ToolCallStarted(_clock(), id, tool, detail));
        return events;
    }

    private List<AgentEvent> ToolCallUpdate(JsonElement update)
    {
        if (Text(update, "toolCallId") is not { } id)
        {
            return [];
        }

        var status = Text(update, "status");
        if (status is not ("completed" or "failed"))
        {
            // A partial result or a status change mid-flight: the card is already open.
            return [];
        }

        List<AgentEvent> events = [];
        if (!_open.Remove(id, out var opened))
        {
            opened = ("tool", string.Empty);
            _toolCalls++;
            events.Add(new ToolCallStarted(_clock(), id, opened.Tool, opened.Detail));
        }

        events.Add(new ToolCallCompleted(_clock(), id, opened.Tool, status == "completed", ContentText(update)));
        return events;
    }

    private List<AgentEvent> Usage(JsonElement update)
    {
        var used = Number(update, "used");
        if (used > 0)
        {
            if (_baselineTokens == 0)
            {
                _baselineTokens = used;
            }

            _contextTokens = used;
        }

        var size = Number(update, "size");
        if (size > 0)
        {
            _contextWindow = size;
        }

        return [new StatsUpdated(_clock(), Snapshot())];
    }

    private List<AgentEvent> Flush()
    {
        if (_text.Length == 0)
        {
            return [];
        }

        var text = _text.ToString();
        _text.Clear();
        return string.IsNullOrWhiteSpace(text) ? [] : [new MessageEmitted(_clock(), TranscriptRole.Assistant, text)];
    }

    private AgentStatsSnapshot Snapshot() =>
        new(_inputTokens, _outputTokens, _cachedInputTokens, 0m, _toolCalls, 0, _turns, _contextTokens, _contextWindow, _baselineTokens);

    /// <summary>ACP's tool kinds, in the studio's tool vocabulary where there is a faithful match.</summary>
    public static string ToolName(string? kind) => kind switch
    {
        "execute" => "Bash",
        "edit" => "Edit",
        "delete" => "Delete",
        "move" => "Move",
        "read" => "Read",
        "search" => "Search",
        "fetch" => "WebFetch",
        "think" => "Think",
        null or "" => "tool",
        _ => kind,
    };

    private static string ContentText(JsonElement update)
    {
        if (!update.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (Text(block, "type") == "content" && block.TryGetProperty("content", out var inner) && Text(inner, "text") is { } text)
            {
                builder.Append(text);
            }
            else if (Text(block, "type") == "diff")
            {
                builder.Append(Text(block, "path")).Append('\n').Append(Text(block, "newText"));
            }
        }

        return builder.ToString();
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long Number(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : 0;
}
#endif
