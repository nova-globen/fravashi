using System.Text.Json.Nodes;
using Fravashi.Application.Network;
using Fravashi.Application.Sessions.Tools;

namespace Fravashi.Infrastructure.Sessions.Tools;

/// <summary>
/// How a studio tool tells the agent the NETWORK failed (backlog 233, plan @59), in the shape <c>git_push</c> answers
/// with since backlog 231: <c>refused</c> is a code the agent branches on, <c>retry_safe</c> says whether doing it again
/// can do anything twice, <c>network</c> names slice A's kind and the host, and the message is the plain sentence.
/// Nothing here retries by itself: the agent decides.
/// </summary>
public static class NetworkToolAnswer
{
    /// <summary>The call did not get through, and repeating it is safe (a read).</summary>
    public const string Unreachable = "network_unreachable";

    /// <summary>An act was made and what followed it failed on the network, so whether it arrived is unknown.</summary>
    public const string OutcomeUnknown = "network_outcome_unknown";

    /// <summary>The failure answer.</summary>
    /// <param name="failure">Slice A's kind.</param>
    /// <param name="host">The host that could not be reached.</param>
    /// <param name="field">The argument the answer is about.</param>
    /// <param name="message">The plain sentence.</param>
    /// <param name="retrySafe">Whether repeating the call can do anything twice.</param>
    /// <param name="page">For the browser: the page as it is after the failure.</param>
    public static StudioToolResult Failure(
        NetworkFailure failure, string host, string field, string message, bool retrySafe, JsonObject? page = null)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var code = retrySafe ? Unreachable : OutcomeUnknown;
        var answer = new JsonObject
        {
            ["refused"] = code,
            ["retry_safe"] = retrySafe,
            ["network"] = new JsonObject
            {
                ["kind"] = KindName(failure.Kind),
                ["host"] = host,
            },
            ["errors"] = new JsonArray(new JsonObject
            {
                ["code"] = code,
                ["field"] = field,
                ["message"] = message,
            }),
        };
        if (page is not null)
        {
            answer["page"] = page;
        }

        return StudioToolResult.Failure(answer.ToJsonString());
    }

    /// <summary>A kind as the agent reads it: <c>name_not_resolved</c>, <c>refused</c>, <c>left_with_no_answer</c> and so on.</summary>
    public static string KindName(NetworkFailureKind kind) => kind switch
    {
        NetworkFailureKind.NameNotResolved => "name_not_resolved",
        NetworkFailureKind.Refused => "refused",
        NetworkFailureKind.Unreachable => "unreachable",
        NetworkFailureKind.TimedOutConnecting => "timed_out_connecting",
        NetworkFailureKind.ResetBeforeSend => "reset_before_send",
        NetworkFailureKind.LeftWithNoAnswer => "left_with_no_answer",
        _ => "not_network",
    };

    /// <summary>The sentence for a read that did not get through: it changed nothing, so it is safe to repeat.</summary>
    public static string ReadFailed(string host, NetworkFailure failure, string notWhat)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return $"Could not reach {host} ({failure.Words}). This is the network, not {notWhat}; it was only a read, so it is "
            + "safe to try again in a minute.";
    }
}
