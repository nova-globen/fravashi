using System.Text.Json;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.Sessions;

/// <summary>
/// Reads an incoming <c>StructuredOutput</c> envelope against the library in
/// <see cref="AgentTurnDocumentSchema"/> (ADR 0022, spec <c>docs/plans/typed-agent-turns.md</c>).
/// <para>
/// <b>It never throws and it never drops the message.</b> A live document is already constrained by
/// the schema this build sent, so the interesting inputs are the ones a live turn cannot produce: a
/// transcript row written by a newer studio, a kind a later library added, a payload that does not
/// match the kind beside it. Every one of those degrades to "render the message, mark it" rather than
/// raising, which is the same choice <c>AgentEventJson</c> already makes for an unknown event kind.
/// </para>
/// <para>
/// The one thing it does refuse is a document that is not one: no object, or no
/// <c>version</c>/<c>kind</c>/<c>message</c>. Then there is nothing to render and nothing to mark, and
/// the caller's honest reading is that the turn was untyped. Null says exactly that.
/// </para>
/// </summary>
public static class AgentTurnDocumentReader
{
    /// <summary>Reads a document from raw JSON text. Null when the text is not a document.</summary>
    public static AgentTurnDocument? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return Read(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a document from an already-parsed element, which is what the terminal <c>result</c> line's
    /// <c>structured_output</c> field is. Null when the element is not a document.
    /// </summary>
    public static AgentTurnDocument? Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (Version(root) is not { Length: > 0 } version
            || Text(root, "kind") is not { Length: > 0 } kind
            || Text(root, "message") is not { } message)
        {
            return null;
        }

        // Version first, and it short-circuits the kind: a bump means an old renderer would be WRONG,
        // so recognising the kind string under a version this build does not know would be exactly the
        // wrong kind of helpfulness. The strings are kept verbatim so a later build can still read them.
        if (!string.Equals(version, AgentTurnDocument.CurrentVersion, StringComparison.Ordinal))
        {
            return new AgentTurnDocument(
                version, kind, message, Degradation: AgentTurnDocumentDegradation.UnknownVersion);
        }

        if (!AgentTurnDocumentKinds.TryParse(kind, out var known))
        {
            return new AgentTurnDocument(version, kind, message, Degradation: AgentTurnDocumentDegradation.UnknownKind);
        }

        if (AgentTurnDocumentSchema.PayloadValueKind(known) is not { } expected)
        {
            return new AgentTurnDocument(version, kind, message, known);
        }

        // The envelope is flat (spec decision 2), so the payload sits under a property named for the
        // kind and the pairing is checked here rather than by the schema the model had to satisfy.
        if (!root.TryGetProperty(kind, out var payload) || payload.ValueKind != expected)
        {
            return new AgentTurnDocument(
                version, kind, message, known, Degradation: AgentTurnDocumentDegradation.MissingPayload);
        }

        return new AgentTurnDocument(version, kind, message, known, payload.GetRawText());
    }

    /// <summary>
    /// The version as text, whichever way it was written. A model asked for the string "1" writes the
    /// numeral far more often than not (measured, see <see cref="AgentTurnDocumentSchema"/>), and the schema
    /// now permits both, so a numeral reads as its own digits. It is read by VALUE, because the harness's
    /// validator compares numbers by value and lets 1.0 through as 1. Anything else there is not a version.
    /// </summary>
    private static string? Version(JsonElement root)
    {
        if (!root.TryGetProperty("version", out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return Text(root, "version");
        }

        return value.TryGetDecimal(out var number)
            ? number.ToString("0.############", System.Globalization.CultureInfo.InvariantCulture)
            : value.GetRawText();
    }

    private static string? Text(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
