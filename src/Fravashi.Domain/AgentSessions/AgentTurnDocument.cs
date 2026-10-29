namespace Fravashi.Domain.AgentSessions;

/// <summary>
/// The payload kinds the studio's document library knows (ADR 0022). A turn under
/// <c>--json-schema</c> ends by calling <c>StructuredOutput</c> with one envelope, and
/// <see cref="AgentTurnDocument.Kind"/> selects which payload rides along.
/// <para>
/// The vocabulary is complete from the first commit because ADR 0022 requires the library to be
/// versioned from the first commit, but a kind is only ever SENT once something renders it
/// (<c>docs/plans/typed-agent-turns.md</c>): sending a kind nothing renders buys a document the
/// studio can only print as prose, at a token cost charged on every turn.
/// </para>
/// </summary>
public enum AgentTurnDocumentKind
{
    /// <summary>Prose only: the envelope's message is the whole document. The one kind v1 sends.</summary>
    Answer,

    /// <summary>Questions with selectable options, rendered as real controls (backlog item 92).</summary>
    Questions,

    /// <summary>A reviewable plan: steps, affected work items, risks.</summary>
    Plan,

    /// <summary>A work item to create, rendered as a confirm-and-create card.</summary>
    Proposal,

    /// <summary>Review findings with file, line and severity, rendered as a list with jumps.</summary>
    Findings,

    /// <summary>The agent cannot proceed and says what is missing (ADR 0020's governance panel).</summary>
    Blocked,

    /// <summary>
    /// The autopilot operator's retry-or-park decision after a build attempt that made no commit (ADR 0031
    /// decision 10, backlog 300): a boolean, never prose, because a retry/park decision read out of English
    /// is the "a question is only a paragraph" trap this library exists to avoid.
    /// </summary>
    Advice,

    /// <summary>
    /// Draft work items the autopilot operator wrote from findings (ADR 0031 decision 10, backlog 300): drafts
    /// only, never sent to the tracker by the operator itself.
    /// </summary>
    FollowUpDrafts,
}

/// <summary>
/// Why a document can only be rendered as its <see cref="AgentTurnDocument.Message"/> rather than by the
/// renderer its kind would normally get.
/// <para>
/// Every value other than <see cref="None"/> is a READ-TIME degradation, never an exception: a live
/// document is constrained by the schema this build sent, so these can only be reached by a stored
/// transcript written under a different version of the studio, and dropping such a turn would break
/// the promise that a transcript stored under an older version still renders.
/// </para>
/// </summary>
public enum AgentTurnDocumentDegradation
{
    /// <summary>Nothing degraded: kind and version are known and the payload the kind implies read cleanly.</summary>
    None,

    /// <summary>The envelope names a version this build does not know, so no payload is trusted.</summary>
    UnknownVersion,

    /// <summary>The envelope names a kind this build does not know.</summary>
    UnknownKind,

    /// <summary>The kind is known and carries a payload, but the payload is absent or not the shape the kind declares.</summary>
    MissingPayload,
}

/// <summary>
/// One agent turn's outcome as data (ADR 0022): the parsed <c>StructuredOutput</c> envelope.
/// <para>
/// Named <c>AgentTurnDocument</c> and not <c>AgentDocument</c>, which is what
/// <c>docs/plans/typed-agent-turns.md</c> proposed: <see cref="Framework.AgentDocument"/> already exists
/// in this assembly and parses a Claude Code subagent definition file, which is a completely different
/// thing. Two same-named types one namespace apart would have been a trap for every later reader.
/// </para>
/// <para>
/// <see cref="Kind"/> and <see cref="Version"/> are kept as the strings the wire carried, not as parsed
/// values, because an unknown one must survive a round trip through the transcript: a later build has to
/// be able to recognise the kind that this build could only degrade. <see cref="KnownKind"/> is this
/// build's reading of <see cref="Kind"/>, and is null exactly when it could not read it.
/// </para>
/// <para>
/// The payload stays as raw JSON text rather than a parsed element for the same reason plus one more:
/// the document is persisted verbatim on the turn's transcript row, so text is what is stored and what
/// is compared. Kind-specific parsing belongs to the renderer that understands the kind.
/// </para>
/// </summary>
/// <param name="Version">The envelope's <c>version</c>, as written. <see cref="CurrentVersion"/> is what this build sends.</param>
/// <param name="Kind">The envelope's <c>kind</c>, as written.</param>
/// <param name="Message">Markdown prose for the user. Always present, and always renderable, whatever else degraded.</param>
/// <param name="KnownKind">This build's reading of <paramref name="Kind"/>, or null when it does not know it.</param>
/// <param name="PayloadJson">The raw JSON of the property named by the kind, or null when the kind carries no payload.</param>
/// <param name="Degradation">Why only <paramref name="Message"/> can be rendered, or <see cref="AgentTurnDocumentDegradation.None"/>.</param>
public sealed record AgentTurnDocument(
    string Version,
    string Kind,
    string Message,
    AgentTurnDocumentKind? KnownKind = null,
    string? PayloadJson = null,
    AgentTurnDocumentDegradation Degradation = AgentTurnDocumentDegradation.None)
{
    /// <summary>
    /// The envelope version this build writes into the schema it sends and accepts on read.
    /// <para>
    /// Bump only when an old renderer would be WRONG. Adding an optional property or a new kind does
    /// not bump; changing the meaning or the type of an existing field does.
    /// </para>
    /// </summary>
    public const string CurrentVersion = "1";

    /// <summary>True when the document can be handed to the renderer its kind implies.</summary>
    public bool IsTyped => Degradation == AgentTurnDocumentDegradation.None;
}

/// <summary>
/// The wire vocabulary for <see cref="AgentTurnDocumentKind"/>. Spelled out one case at a time rather than
/// derived from the enum name, so a future kind whose C# name is two words cannot silently acquire a
/// wire name nobody chose.
/// </summary>
public static class AgentTurnDocumentKinds
{
    /// <summary>Every kind the library defines, in the order the assembled schema lists them.</summary>
    public static IReadOnlyList<AgentTurnDocumentKind> All { get; } = Enum.GetValues<AgentTurnDocumentKind>();

    /// <summary>The kind's name on the wire, which is also the envelope property its payload sits under.</summary>
    public static string Name(AgentTurnDocumentKind kind) => kind switch
    {
        AgentTurnDocumentKind.Answer => "answer",
        AgentTurnDocumentKind.Questions => "questions",
        AgentTurnDocumentKind.Plan => "plan",
        AgentTurnDocumentKind.Proposal => "proposal",
        AgentTurnDocumentKind.Findings => "findings",
        AgentTurnDocumentKind.Blocked => "blocked",
        AgentTurnDocumentKind.Advice => "advice",
        AgentTurnDocumentKind.FollowUpDrafts => "follow_up_drafts",
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), kind, "every document kind needs a wire name before it can be sent or read"),
    };

    /// <summary>Reads a wire name back. Case sensitive: the schema constrains the model to these exact spellings.</summary>
    public static bool TryParse(string? name, out AgentTurnDocumentKind kind)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(Name(candidate), name, StringComparison.Ordinal))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }
}
