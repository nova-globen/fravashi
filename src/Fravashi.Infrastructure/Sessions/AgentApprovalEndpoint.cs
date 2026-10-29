using System.Text.Json;
using System.Text.Json.Nodes;
using Fravashi.Application.Sessions;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Infrastructure.Sessions;

/// <summary>The status code and JSON body the approval endpoint answers the blocked hook with.</summary>
public sealed record AgentApprovalResponse(int StatusCode, string Json);

/// <summary>
/// The logic behind <c>POST /api/agent/approval</c> (plan 2e Task 3), kept out of the request pipeline
/// so it is testable against the payload the real hook sends. <c>/api</c> is exempt from every studio
/// gate by design, so the per-session secret is the ONLY authentication here: a caller without it is
/// refused, and every outcome that is not an explicit approval is a denial.
/// </summary>
public static class AgentApprovalEndpoint
{
    /// <summary>The route the per-session hook script posts to.</summary>
    public const string Route = "/api/agent/approval";

    /// <summary>The header carrying the session's secret.</summary>
    public const string SecretHeader = "X-Fravashi-Session";

    public static async Task<AgentApprovalResponse> HandleAsync(
        string? secret,
        string requestJson,
        IAgentApprovalBroker broker,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(broker);

        if (broker.ResolveSecret(secret) is not { } sessionId)
        {
            return new AgentApprovalResponse(
                StatusCodes401,
                Render(ToolApprovalDecision.Deny("The studio did not recognise this session.")));
        }

        if (!TryReadCall(requestJson, out var toolName, out var toolUseId, out var input))
        {
            // The session IS known here, so this refusal belongs in its transcript (backlog 117): a hook
            // that keeps sending something unreadable is an agent pushing against a refusal too. The body
            // itself is not kept: it is not something the studio understood.
            return new AgentApprovalResponse(
                StatusCodes400,
                Render(broker.Refuse(sessionId, toolName, string.Empty, "The studio could not read this tool call.")));
        }

        var decision = await broker
            .RequestAsync(sessionId, toolName, toolUseId, input, cancellationToken)
            .ConfigureAwait(false);

        return new AgentApprovalResponse(StatusCodes200, Render(decision));
    }

    /// <summary>The CLI's PreToolUse decision document, in the shape it honored during the spikes.</summary>
    public static string Render(ToolApprovalDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        // Built as nodes rather than serialized from an anonymous type: no reflection, so this keeps
        // working under any trimming/AOT publish the app grows later.
        var payload = new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PreToolUse",
                ["permissionDecision"] = decision.Approved ? "allow" : "deny",
                ["permissionDecisionReason"] = decision.Reason,
            },
        };

        return payload.ToJsonString();
    }

    /// <summary>
    /// Reads the hook's stdin payload: which tool, which call, and its arguments.
    /// <para>
    /// The arguments come out EXACTLY as the CLI sent them. They are what containment is decided on
    /// (<see cref="AgentReadScope"/>), and the CLI will act on the value it sent, not on anything the
    /// studio shortened: capping here once made a padded path resolve to the copy while the real one
    /// left it. Capping for the UI happens where the UI's copy is built
    /// (<see cref="AgentToolSubject.ForDisplay"/>).
    /// </para>
    /// <para>
    /// A value that is not a JSON string is still rendered for the prompt (the user has to see what
    /// the agent sent), but it is MARKED as not text, because rendering one is not the same as
    /// understanding it. <c>GetRawText()</c> on an object turns JSON punctuation into what reads as
    /// directory segments, and a check that walked it manufactured a base deep enough for a pattern's
    /// <c>..</c> chain to land back inside the copy. So the JSON kind travels with the value
    /// (<see cref="AgentToolInput"/>) and a path-bearing field that is not text is refused rather than
    /// interpreted.
    /// </para>
    /// </summary>
    private static bool TryReadCall(
        string requestJson,
        out string toolName,
        out string? toolUseId,
        out AgentToolInput input)
    {
        toolName = string.Empty;
        toolUseId = null;
        input = AgentToolInput.Empty;

        if (string.IsNullOrWhiteSpace(requestJson))
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(requestJson);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tool_name", out var name)
                || name.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(name.GetString()))
            {
                return false;
            }

            toolName = name.GetString()!;
            if (root.TryGetProperty("tool_use_id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                toolUseId = id.GetString();
            }

            var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
            var text = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("tool_input", out var toolInput) && toolInput.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in toolInput.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        arguments[property.Name] = property.Value.GetString() ?? string.Empty;
                        text.Add(property.Name);
                    }
                    else
                    {
                        // Rendered for the prompt, never readable as a value the tool named.
                        arguments[property.Name] = property.Value.GetRawText();
                        text.Remove(property.Name);
                    }
                }
            }

            input = AgentToolInput.Create(arguments, text);
            return true;
        }
    }

    // Plain integers rather than a reference to ASP.NET: this type stays free of the web stack so the
    // Infrastructure tests can drive it directly.
    private const int StatusCodes200 = 200;
    private const int StatusCodes400 = 400;
    private const int StatusCodes401 = 401;
}
