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
/// Turns the Codex app-server's notifications into the studio's <see cref="AgentEvent"/>s (backlog 66).
/// <para>
/// Every shape read here is from the schema the PINNED binary generates about itself
/// (<c>codex app-server generate-ts</c> at 0.146.0, see <c>docs/plans/codex-copilot-runners.md</c>), not from
/// a captured run: no Codex turn has been driven against a model yet, so this class is a claim about the
/// schema, and the plan says what would turn it into a claim about the CLI.
/// </para>
/// <para>
/// Stateful for the same reason the Claude parser is: an item's completion carries its final state but the
/// studio has to remember what it announced when the item started (a file change's paths are what an approval
/// prompt shows, because the approval request itself names none). Deliberately tolerant: a notification it
/// does not recognise, or one missing a field, produces nothing rather than throwing, because a malformed line
/// must never take a running session down.
/// </para>
/// </summary>
public sealed class CodexAppServerEvents
{
    /// <summary>The tool name a Codex shell command is shown and approved as.</summary>
    public const string ShellTool = "Bash";

    /// <summary>The tool name a Codex patch is shown as in the transcript.</summary>
    public const string PatchTool = "apply_patch";

    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, string> _toolNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<CodexFileChange>> _fileChanges = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private long _inputTokens;
    private long _cachedInputTokens;
    private long _outputTokens;
    private long _contextTokens;
    private long _contextWindow;
    private long _baselineTokens;
    private int _toolCalls;
    private int _turns;
    private int _retries;

    public CodexAppServerEvents(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The id of the turn currently running, or null between turns.</summary>
    public string? ActiveTurnId { get; private set; }

    /// <summary>The files a <c>fileChange</c> item announced, which is what its approval request is about.</summary>
    public IReadOnlyList<CodexFileChange>? FileChangesFor(string itemId)
    {
        lock (_gate)
        {
            return _fileChanges.TryGetValue(itemId, out var changes) ? changes : null;
        }
    }

    /// <summary>The events one notification produces, possibly none.</summary>
    public IReadOnlyList<AgentEvent> Map(string method, JsonElement parameters)
    {
        ArgumentNullException.ThrowIfNull(method);

        if (parameters.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        lock (_gate)
        {
            return method switch
            {
                "turn/started" => TurnStarted(parameters),
                "item/agentMessage/delta" => Text(parameters, "delta") is { Length: > 0 } delta
                    ? [new PartialText(_clock(), delta)]
                    : [],
                "item/reasoning/summaryTextDelta" or "item/reasoning/textDelta" or "item/reasoning/summaryPartAdded"
                    => [new TurnActivity(_clock(), AgentTurnPhase.Thinking)],
                "item/started" => ItemStarted(parameters),
                "item/completed" => ItemCompleted(parameters),
                "thread/tokenUsage/updated" => TokenUsage(parameters),
                "turn/completed" => TurnCompleted(parameters),
                "error" => Error(parameters),
                "warning" => Text(parameters, "message") is { Length: > 0 } warning
                    ? [new MessageEmitted(_clock(), TranscriptRole.System, "Codex warning: " + warning)]
                    : [],
                "model/rerouted" => Rerouted(parameters),
                _ => [],
            };
        }
    }

    private List<AgentEvent> TurnStarted(JsonElement parameters)
    {
        if (parameters.TryGetProperty("turn", out var turn) && Text(turn, "id") is { } id)
        {
            ActiveTurnId = id;
        }

        return [new TurnActivity(_clock(), AgentTurnPhase.Submitted)];
    }

    private List<AgentEvent> ItemStarted(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("item", out var item) || Text(item, "id") is not { } id)
        {
            return [];
        }

        var (tool, detail) = Describe(item);
        if (tool is null)
        {
            return [];
        }

        if (Text(item, "type") == "fileChange")
        {
            _fileChanges[id] = FileChanges(item);
        }

        _toolNames[id] = tool;
        _toolCalls++;
        return [new ToolCallStarted(_clock(), id, tool, detail)];
    }

    private List<AgentEvent> ItemCompleted(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("item", out var item) || Text(item, "id") is not { } id)
        {
            return [];
        }

        var type = Text(item, "type");
        if (type == "agentMessage")
        {
            return Text(item, "text") is { Length: > 0 } text
                ? [new MessageEmitted(_clock(), TranscriptRole.Assistant, text)]
                : [];
        }

        var (tool, detail) = Describe(item);
        if (tool is null)
        {
            return [];
        }

        List<AgentEvent> events = [];
        if (!_toolNames.Remove(id))
        {
            // Completed without a start we saw (a resumed turn, a dropped line): the card still needs opening.
            _toolCalls++;
            events.Add(new ToolCallStarted(_clock(), id, tool, detail));
        }

        _fileChanges.Remove(id);
        var status = Text(item, "status");
        events.Add(type switch
        {
            "commandExecution" => new ToolCallCompleted(
                _clock(),
                id,
                tool,
                status == "completed" && (!item.TryGetProperty("exitCode", out var exit) || exit.ValueKind != JsonValueKind.Number || (exit.TryGetInt64(out var code) && code == 0)),
                status == "declined" ? "The command was declined." : Text(item, "aggregatedOutput") ?? string.Empty),
            "fileChange" => new ToolCallCompleted(
                _clock(),
                id,
                tool,
                status == "completed",
                status == "declined" ? "The change was declined." : Diffs(item)),
            "mcpToolCall" => new ToolCallCompleted(
                _clock(),
                id,
                tool,
                status == "completed",
                item.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
                    ? result.GetRawText()
                    : string.Empty,
                item.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                    ? Text(error, "message")
                    : null),
            _ => new ToolCallCompleted(_clock(), id, tool, status is null or "completed", string.Empty),
        });

        return events;
    }

    private List<AgentEvent> TokenUsage(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("tokenUsage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        if (usage.TryGetProperty("total", out var total) && total.ValueKind == JsonValueKind.Object)
        {
            // The schema's totals are cumulative for the thread, so they SET the counters rather than adding
            // to them. OpenAI input tokens include the cached ones; the studio's input count does not (it is
            // what Claude reports), so the cached share is split out to keep both runtimes' footers comparable.
            var input = Number(total, "inputTokens");
            var cached = Number(total, "cachedInputTokens");
            _cachedInputTokens = Math.Max(0, cached);
            _inputTokens = Math.Max(0, input - _cachedInputTokens);
            _outputTokens = Math.Max(0, Number(total, "outputTokens"));
        }

        if (usage.TryGetProperty("last", out var last) && last.ValueKind == JsonValueKind.Object)
        {
            // The last request's input is the whole conversation as it was sent: a level, not a flow.
            var context = Number(last, "inputTokens");
            if (context > 0)
            {
                if (_baselineTokens == 0)
                {
                    _baselineTokens = context;
                }

                _contextTokens = context;
            }
        }

        if (usage.TryGetProperty("modelContextWindow", out var window) && window.ValueKind == JsonValueKind.Number
            && window.TryGetInt64(out var size) && size > 0)
        {
            _contextWindow = size;
        }

        return [new StatsUpdated(_clock(), Snapshot())];
    }

    private List<AgentEvent> TurnCompleted(JsonElement parameters)
    {
        ActiveTurnId = null;
        _turns++;

        string? status = null;
        string? errorText = null;
        if (parameters.TryGetProperty("turn", out var turn) && turn.ValueKind == JsonValueKind.Object)
        {
            status = Text(turn, "status");
            if (turn.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                errorText = Text(error, "message");
            }
        }

        // "completed", "interrupted" and "failed" are the schema's words; AgentTerminalReason already maps an
        // interruption to Cancelled, and is_error is what marks a failed turn.
        return
        [
            new StatsUpdated(_clock(), Snapshot()),
            new TurnCompleted(_clock(), null, status == "failed", status, ErrorText: errorText),
        ];
    }

    private List<AgentEvent> Error(JsonElement parameters)
    {
        var message = parameters.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
            ? Text(error, "message")
            : null;
        var retrying = parameters.TryGetProperty("willRetry", out var retry) && retry.ValueKind == JsonValueKind.True;
        if (retrying)
        {
            _retries++;
        }

        var text = retrying
            ? $"Codex hit an error and is retrying: {message ?? "no detail given"}"
            : $"Codex reported an error: {message ?? "no detail given"}";
        return [new MessageEmitted(_clock(), TranscriptRole.System, text)];
    }

    private List<AgentEvent> Rerouted(JsonElement parameters) =>
        Text(parameters, "toModel") is { Length: > 0 } to
            ? [new MessageEmitted(_clock(), TranscriptRole.System, $"Codex moved this turn to {to}.")]
            : [];

    private AgentStatsSnapshot Snapshot() =>
        new(
            _inputTokens,
            _outputTokens,
            _cachedInputTokens,
            // Codex reports no cost. Zero here is "none reported", and the plan says the footer must not
            // present it as a measured price.
            0m,
            _toolCalls,
            _retries,
            _turns,
            _contextTokens,
            _contextWindow,
            _baselineTokens);

    /// <summary>
    /// The transcript name and subject for a tool-like item, or a null name for an item that is not a tool
    /// call (a message, reasoning, a plan, a compaction marker).
    /// </summary>
    private static (string? Tool, string Detail) Describe(JsonElement item) =>
        Text(item, "type") switch
        {
            "commandExecution" => (ShellTool, Text(item, "command") ?? string.Empty),
            "fileChange" => (PatchTool, string.Join('\n', FileChanges(item).Select(c => c.Describe()))),
            "mcpToolCall" => ($"mcp__{Text(item, "server") ?? "unknown"}__{Text(item, "tool") ?? "unknown"}", Raw(item, "arguments")),
            "dynamicToolCall" => (Text(item, "tool") ?? "tool", Raw(item, "arguments")),
            "collabAgentToolCall" => ("Agent", Text(item, "prompt") ?? Text(item, "tool") ?? string.Empty),
            "webSearch" => ("WebSearch", Text(item, "query") ?? string.Empty),
            "imageView" => ("ViewImage", Text(item, "path") ?? string.Empty),
            "imageGeneration" => ("ImageGeneration", string.Empty),
            _ => (null, string.Empty),
        };

    private static List<CodexFileChange> FileChanges(JsonElement item)
    {
        List<CodexFileChange> changes = [];
        if (!item.TryGetProperty("changes", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return changes;
        }

        foreach (var change in list.EnumerateArray())
        {
            if (change.ValueKind != JsonValueKind.Object || Text(change, "path") is not { } path)
            {
                continue;
            }

            string? kind = null;
            string? movePath = null;
            if (change.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.Object)
            {
                kind = Text(k, "type");
                movePath = Text(k, "move_path");
            }

            changes.Add(new CodexFileChange(path, kind ?? "update", movePath, Text(change, "diff") ?? string.Empty));
        }

        return changes;
    }

    private static string Diffs(JsonElement item)
    {
        var builder = new StringBuilder();
        foreach (var change in FileChanges(item))
        {
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(change.Describe()).Append('\n').Append(change.Diff);
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
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var number)
            ? number
            : 0;

    private static string Raw(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Undefined
            ? value.GetRawText()
            : string.Empty;
}

/// <summary>One file a Codex patch touches, as its <c>fileChange</c> item announced it.</summary>
/// <param name="Kind">The schema's <c>add</c>, <c>delete</c> or <c>update</c>.</param>
/// <param name="MovePath">For an update that also moves the file, where it moves to.</param>
public sealed record CodexFileChange(string Path, string Kind, string? MovePath, string Diff)
{
    /// <summary>The one-line subject: what happens to which path.</summary>
    public string Describe() =>
        MovePath is { Length: > 0 } to ? $"{Kind} {Path} -> {to}" : $"{Kind} {Path}";
}
#endif
