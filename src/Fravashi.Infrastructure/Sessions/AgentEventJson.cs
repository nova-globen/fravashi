using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>
/// Writes and reads transcript events as JSON for the session store. Hand-rolled rather than
/// polymorphic serialization: the persisted shape is then an explicit, reviewable decision (and one
/// that survives renaming a record), and it needs no reflection.
/// <para>
/// An event kind this build does not know is skipped on read rather than thrown, so a transcript
/// written by a newer build still opens in an older one.
/// </para>
/// </summary>
public static class AgentEventJson
{
    /// <summary>The stable name a transcript row is stored under, or null when the event is transient.</summary>
    public static string? KindOf(AgentEvent e) => e switch
    {
        SessionStarted => "session_started",
        SessionRegistered => "session_registered",
        EffortChosen => "effort_chosen",
        GraphOnlyChosen => "graph_only",
        AssistantChosen => "assistant",
        MessageEmitted => "message",
        UserImagesAttached => "user_images",
        ToolCallStarted => "tool_started",
        ToolCallCompleted => "tool_completed",
        ToolApprovalRequested => "approval_requested",
        ToolApprovalResolved => "approval_resolved",
        ToolCallRefused => "tool_refused",
        ToolCallTrusted => "tool_trusted",
        ToolCallAutopilotAllowed => "tool_autopilot_allowed",
        StatsUpdated => "stats",
        TurnCompleted => "turn_completed",
        SessionEnded => "session_ended",
        // PartialText is the message being typed; the finished text is persisted instead. TurnActivity
        // is the same kind of thing for the working indicator: true for an instant, never history.
        _ => null,
    };

    public static string Serialize(AgentEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);

        var payload = new JsonObject { ["timestamp"] = e.Timestamp.ToString("O", CultureInfo.InvariantCulture) };
        switch (e)
        {
            case SessionStarted started:
                payload["sessionId"] = started.SessionId.Value.ToString("D");
                // Written only when true, so an untyped session's row is byte identical to the rows
                // written before typed turns existed, and those read back as untyped, which they were.
                if (started.TypedTurns)
                {
                    payload["typedTurns"] = true;
                }

                break;
            case SessionRegistered registered:
                payload["providerSessionId"] = registered.ProviderSessionId;
                payload["model"] = registered.Model;
                break;
            case EffortChosen chosen:
                // The CLI's own value, null for its default; a build that does not know the kind skips the row.
                payload["effort"] = chosen.Effort?.CliValue();
                break;
            case MessageEmitted message:
                payload["role"] = message.Role.ToString();
                payload["text"] = message.Text;
                break;
            case ToolCallStarted tool:
                payload["toolUseId"] = tool.ToolUseId;
                payload["toolName"] = tool.ToolName;
                payload["detail"] = tool.Detail;
                // Work item #351: written only for a studio tool call, so every other row stays byte identical to the
                // rows written before the arguments were kept, and those read back with none.
                if (tool.Arguments is { Count: > 0 } arguments)
                {
                    var stored = new JsonObject();
                    foreach (var (name, value) in arguments)
                    {
                        stored[name] = value;
                    }

                    payload["arguments"] = stored;
                }

                break;
            case ToolCallCompleted completed:
                payload["toolUseId"] = completed.ToolUseId;
                payload["toolName"] = completed.ToolName;
                payload["success"] = completed.Success;
                payload["detail"] = completed.Detail;
                // Written only when there is one, so a row that had no stderr reads back as null rather
                // than an empty string, and older rows (no property at all) do the same (backlog 104 D3).
                if (completed.Error is { } error)
                {
                    payload["error"] = error;
                }

                break;
            case ToolApprovalRequested requested:
                payload["requestId"] = requested.RequestId;
                payload["toolName"] = requested.ToolName;
                payload["detail"] = requested.Detail;
                payload["warning"] = requested.Warning;
                break;
            case ToolApprovalResolved resolved:
                payload["requestId"] = resolved.RequestId;
                payload["toolName"] = resolved.ToolName;
                payload["approved"] = resolved.Approved;
                payload["reason"] = resolved.Reason;
                // Written ALWAYS, unlike the optional properties above: this one says who refused a
                // change (backlog 114), so a stored row should state it rather than leave a reader to
                // infer it. Rows written before it existed have no property here and read back as the
                // studio's own decision, which is the fallback that cannot blame the wrong party.
                payload["decidedBy"] = resolved.DecidedBy.ToString();
                // Written only for a repeat (backlog 117), so every row that is not one stays byte
                // identical to the rows written before the count existed, and those read back as 1.
                if (resolved.RepeatCount > 1)
                {
                    payload["repeatCount"] = resolved.RepeatCount;
                }

                break;
            case ToolCallRefused refused:
                payload["toolName"] = refused.ToolName;
                payload["detail"] = refused.Detail;
                payload["reason"] = refused.Reason;
                payload["repeatCount"] = refused.RepeatCount;
                break;
            case ToolCallTrusted trusted:
                payload["toolName"] = trusted.ToolName;
                payload["detail"] = trusted.Detail;
                payload["reason"] = trusted.Reason;
                break;
            case ToolCallAutopilotAllowed allowed:
                payload["toolName"] = allowed.ToolName;
                payload["detail"] = allowed.Detail;
                payload["reason"] = allowed.Reason;
                break;
            case StatsUpdated stats:
                payload["inputTokens"] = stats.Stats.InputTokens;
                payload["outputTokens"] = stats.Stats.OutputTokens;
                payload["cacheReadTokens"] = stats.Stats.CacheReadTokens;
                payload["costUsd"] = stats.Stats.CostUsd;
                payload["toolCalls"] = stats.Stats.ToolCalls;
                payload["rateLimitHits"] = stats.Stats.RateLimitHits;
                payload["turns"] = stats.Stats.Turns;
                payload["contextTokens"] = stats.Stats.ContextTokens;
                payload["contextWindow"] = stats.Stats.ContextWindow;
                payload["baselineTokens"] = stats.Stats.BaselineTokens;
                payload["cacheWriteTokens"] = stats.Stats.CacheWriteTokens;
                payload["freeTierUnits"] = stats.Stats.FreeTierUnits;
                break;
            case TurnCompleted turn:
                payload["resultText"] = turn.ResultText;
                payload["isError"] = turn.IsError;
                payload["terminalReason"] = turn.TerminalReason;
                if (turn.Document is { } document)
                {
                    payload["document"] = Envelope(document);
                }

                if (turn.ValidationFailures > 0)
                {
                    payload["validationFailures"] = turn.ValidationFailures;
                    payload["lastValidationError"] = turn.LastValidationError;
                }

                if (turn.ErrorText is { } errorText)
                {
                    payload["errorText"] = errorText;
                }

                break;
            case SessionEnded ended:
                payload["finalState"] = ended.FinalState.ToString();
                payload["failureReason"] = ended.FailureReason;
                if (ended.Resumable)
                {
                    payload["resumable"] = true;
                }

                break;
        }

        // Backlog 102: the row about to be persisted is the durable copy, so secrets are removed HERE,
        // not only at display. One shared redactor with the startup scrub and the display components.
        TranscriptRedactor.RedactInPlace(payload);

        // Backlog 26: image bytes are added AFTER the redactor, deliberately. Base64 is a long run of the very
        // charset the provider-key patterns match, so redacting it could only ever corrupt an image (and
        // would regex megabytes to do it); an image is not text an agent printed, which is what the redactor
        // exists for.
        if (e is UserImagesAttached attached)
        {
            payload["images"] = new JsonArray([.. attached.Images.Select(static image => (JsonNode)new JsonObject
            {
                ["mediaType"] = image.MediaType,
                ["data"] = image.Base64,
            })]);
        }

        return payload.ToJsonString();
    }

    /// <summary>Reads a stored row back, or null when this build does not know the kind.</summary>
    public static AgentEvent? Deserialize(string kind, string json)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (node is not JsonObject payload)
        {
            return null;
        }

        var timestamp = DateTimeOffset.TryParse(
            Text(payload, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;

        return kind switch
        {
            "session_started" => new SessionStarted(
                timestamp,
                Guid.TryParse(Text(payload, "sessionId"), out var id) ? new AgentSessionId(id) : default,
                Flag(payload, "typedTurns")),
            "session_registered" => new SessionRegistered(
                timestamp, Text(payload, "providerSessionId") ?? string.Empty, Text(payload, "model")),
            "effort_chosen" => new EffortChosen(timestamp, AgentEfforts.Parse(Text(payload, "effort"))),
            "graph_only" => new GraphOnlyChosen(timestamp),
            "assistant" => new AssistantChosen(timestamp),
            "message" => new MessageEmitted(
                timestamp,
                Enum.TryParse<TranscriptRole>(Text(payload, "role"), out var role) ? role : TranscriptRole.System,
                Text(payload, "text") ?? string.Empty),
            "user_images" => Images(timestamp, payload),
            "tool_started" => new ToolCallStarted(
                timestamp,
                Text(payload, "toolUseId") ?? string.Empty,
                Text(payload, "toolName") ?? string.Empty,
                Text(payload, "detail") ?? string.Empty)
            {
                Arguments = Arguments(payload),
            },
            "tool_completed" => new ToolCallCompleted(
                timestamp,
                Text(payload, "toolUseId") ?? string.Empty,
                Text(payload, "toolName") ?? string.Empty,
                Flag(payload, "success"),
                Text(payload, "detail") ?? string.Empty,
                Text(payload, "error")),
            "approval_requested" => new ToolApprovalRequested(
                timestamp,
                Text(payload, "requestId") ?? string.Empty,
                Text(payload, "toolName") ?? string.Empty,
                Text(payload, "detail") ?? string.Empty,
                Text(payload, "warning")),
            "approval_resolved" => new ToolApprovalResolved(
                timestamp,
                Text(payload, "requestId") ?? string.Empty,
                Text(payload, "toolName") ?? string.Empty,
                Flag(payload, "approved"),
                Text(payload, "reason") ?? string.Empty,
                // Absent (a row from before backlog 114) or unreadable both mean the studio's own
                // decision, so an old refusal is reported neutrally instead of being blamed on the user.
                Enum.TryParse<ToolApprovalDecidedBy>(Text(payload, "decidedBy"), out var decidedBy)
                    ? decidedBy
                    : ToolApprovalDecidedBy.Studio,
                Count(payload)),
            "tool_refused" => new ToolCallRefused(
                timestamp,
                Text(payload, "toolName") ?? string.Empty,
                Text(payload, "detail") ?? string.Empty,
                Text(payload, "reason") ?? string.Empty,
                Count(payload)),
            "tool_trusted" => new ToolCallTrusted(
                timestamp,
                Text(payload, "toolName") ?? string.Empty,
                Text(payload, "detail") ?? string.Empty,
                Text(payload, "reason") ?? string.Empty),
            "tool_autopilot_allowed" => new ToolCallAutopilotAllowed(
                timestamp,
                Text(payload, "toolName") ?? string.Empty,
                Text(payload, "detail") ?? string.Empty,
                Text(payload, "reason") ?? string.Empty),
            "stats" => new StatsUpdated(timestamp, new AgentStatsSnapshot(
                Integer(payload, "inputTokens"),
                Integer(payload, "outputTokens"),
                Integer(payload, "cacheReadTokens"),
                Money(payload, "costUsd"),
                (int)Integer(payload, "toolCalls"),
                (int)Integer(payload, "rateLimitHits"),
                (int)Integer(payload, "turns"),
                // Absent in rows written before context usage was measured: they read as unknown, which
                // is what they are, rather than as a full or an empty window. Same for the baseline: an
                // older row simply has no fixed-prefix split to offer.
                Integer(payload, "contextTokens"),
                Integer(payload, "contextWindow"),
                Integer(payload, "baselineTokens"),
                // Backlog 377: an older row recorded neither, and reads as none.
                Integer(payload, "cacheWriteTokens"),
                Integer(payload, "freeTierUnits"))),
            "turn_completed" => new TurnCompleted(
                timestamp,
                Text(payload, "resultText"),
                Flag(payload, "isError"),
                Text(payload, "terminalReason"),
                Document(payload),
                (int)Integer(payload, "validationFailures"),
                Text(payload, "lastValidationError"),
                Text(payload, "errorText")),
            "session_ended" => new SessionEnded(
                timestamp,
                Enum.TryParse<AgentSessionState>(Text(payload, "finalState"), out var state)
                    ? state
                    : AgentSessionState.Completed,
                Text(payload, "failureReason"),
                Flag(payload, "resumable")),
            _ => null,
        };
    }

    /// <summary>
    /// A turn's document is stored in its WIRE shape, the envelope the model produced, rather than as
    /// this build's reading of it. Then the stored row and a live turn go through exactly one reader
    /// (<see cref="AgentTurnDocumentReader"/>) and mean the same thing, and a row written under a kind a
    /// later library added degrades on read the same way it would live, instead of being frozen into
    /// whatever the writing build happened to believe.
    /// </summary>
    private static JsonObject Envelope(AgentTurnDocument document)
    {
        var envelope = new JsonObject
        {
            ["version"] = document.Version,
            ["kind"] = document.Kind,
            ["message"] = document.Message,
        };

        // The envelope is flat, so a payload sits under a property named for the kind. A document this
        // build could only degrade has no payload to write back; that is recorded honestly by its absence,
        // which reads back as the same degradation.
        if (document.PayloadJson is { } raw)
        {
            try
            {
                envelope[document.Kind] = JsonNode.Parse(raw);
            }
            catch (JsonException)
            {
                // Unreachable for a payload this reader produced, and not worth losing the turn over.
            }
        }

        return envelope;
    }

    private static AgentTurnDocument? Document(JsonObject payload) =>
        payload.TryGetPropertyValue("document", out var stored) && stored is JsonObject envelope
            ? AgentTurnDocumentReader.Read(envelope.ToJsonString())
            : null;

    private static string? Text(JsonObject payload, string name) =>
        payload.TryGetPropertyValue(name, out var value) ? value?.GetValue<string>() : null;

    /// <summary>A studio tool call's stored arguments (work item #351), or null for a row that has none.</summary>
    private static Dictionary<string, string>? Arguments(JsonObject payload)
    {
        if (!payload.TryGetPropertyValue("arguments", out var node) || node is not JsonObject stored)
        {
            return null;
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in stored)
        {
            if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
            {
                arguments[name] = text;
            }
        }

        return arguments.Count == 0 ? null : arguments;
    }

    /// <summary>
    /// A stored image row. An entry this build cannot trust (unknown type, broken base64, bytes that are not
    /// the type they claim) is dropped rather than rendered; a row with nothing left reads as no event at all.
    /// </summary>
    private static UserImagesAttached? Images(DateTimeOffset timestamp, JsonObject payload)
    {
        List<AgentImageAttachment> images = [];
        if (payload["images"] is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is JsonObject entry
                    && AgentImageAttachment.FromStored(Text(entry, "mediaType"), Text(entry, "data")) is { } image)
                {
                    images.Add(image);
                }
            }
        }

        return images.Count == 0 ? null : new UserImagesAttached(timestamp, images);
    }
    // A repeat count is at least 1 by definition: absent (an older row), zero or negative all mean "once".
    private static int Count(JsonObject payload) =>
        (int)Math.Clamp(Integer(payload, "repeatCount"), 1, int.MaxValue);

    private static bool Flag(JsonObject payload, string name) =>
        payload.TryGetPropertyValue(name, out var value) && value is not null && value.GetValue<bool>();

    private static long Integer(JsonObject payload, string name) =>
        payload.TryGetPropertyValue(name, out var value) && value is not null ? value.GetValue<long>() : 0;

    private static decimal Money(JsonObject payload, string name) =>
        payload.TryGetPropertyValue(name, out var value) && value is not null ? value.GetValue<decimal>() : 0m;
}
