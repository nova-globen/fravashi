using System.Text;
using System.Text.Json;
using Fravashi.Domain.AgentSessions;

namespace Fravashi.Application.Sessions;

/// <summary>
/// The versioned JSON Schema library the studio supplies to the agent CLI on every turn (ADR 0022,
/// spec <c>docs/plans/typed-agent-turns.md</c>). The CLI turns the schema into a
/// <c>StructuredOutput</c> tool the model must call to end its turn, so the shape is enforced by the
/// harness rather than by asking a model politely in a system prompt.
/// <para>
/// <b>The schema is assembled per turn from a kind set, never shipped as one library-wide blob.</b>
/// ADR 0022 measured the schema at about 140 tokens per KB, charged on EVERY turn, on top of about
/// 108 tokens for adding the tool at all. So the studio sends only the kinds a turn could legitimately
/// produce, and <see cref="DefaultKinds"/> is what v1 sends.
/// </para>
/// <para>
/// <b>v1 is permissive on purpose</b> (spec decision 2): no <c>pattern</c>, no <c>format</c>,
/// <c>required</c> kept to the fields a renderer genuinely cannot do without, and no
/// <c>additionalProperties: false</c>. Every extra constraint is another way to burn one of the CLI's
/// five validation attempts, and the fifth failure ends the turn AND exits the process. The envelope is
/// flat rather than an <c>if</c>/<c>then</c> discriminated union for the same reason; the kind/payload
/// pairing is checked on read instead, by <see cref="AgentTurnDocumentReader"/>.
/// </para>
/// </summary>
public static class AgentTurnDocumentSchema
{
    /// <summary>The envelope version this build sends, pinned by the schema so the model cannot invent one.</summary>
    public const string Version = AgentTurnDocument.CurrentVersion;

    /// <summary>
    /// The name the CLI gives the tool it builds out of the schema, measured on claude 2.1.222
    /// (2026-08-06): the <c>tool_use</c> block, the matching <c>tool_result</c> and the validation errors
    /// all arrive under this name. Named here because it is the same fact seen from the other end, but
    /// OWNED by <see cref="AgentTurnOutcomeTool"/>: the approval gate has to know the same spelling, and
    /// one spelling is one place for a rename to be noticed.
    /// </summary>
    public const string ToolName = AgentTurnOutcomeTool.Name;

    /// <summary>
    /// The kinds v1 actually sends. Every other kind has its literal schema written and tested here so the
    /// library is versioned from its first commit, but is only added to this set once a renderer exists for
    /// it: a kind nothing renders buys a document the studio can only print as prose, at a per-turn cost.
    /// <para>
    /// <see cref="AgentTurnDocumentKind.Questions"/> joined the set when backlog item 92 built its
    /// renderer, and it is what makes "the agent cannot ask questions" answerable at all: without the kind
    /// in the sent schema the model has no way to end a turn in anything but prose. The bytes went from 225
    /// to 605 for it (about 52 more tokens a turn at ADR 0022's measured rate), which is the price of the
    /// only kind the owner actually reported missing. (Those two sizes are as measured on 2026-08-06; the
    /// version's second spelling has since added 3 bytes to every set, so the default set is 608.)
    /// </para>
    /// </summary>
    public static IReadOnlyList<AgentTurnDocumentKind> DefaultKinds { get; } =
        [AgentTurnDocumentKind.Answer, AgentTurnDocumentKind.Questions];

    // The version is pinned to ONE value in its two spellings, the string and the bare numeral. It was a
    // string const until 2026-09-21, when thirteen of fourteen turn-ending calls measured in an installed
    // build carried the numeral: const compares type as well as value, the harness's error ("must be equal
    // to constant") never names the constant, and two turns of three burned all five attempts and ended
    // with no answer. Demanding one spelling of "1" is exactly the needless constraint the summary above
    // warns about. The reader treats the two as one version. A version that is not a numeral would make
    // this invalid JSON, and the schema tests parse what this produces.
    private const string EnvelopeOpen =
        "{\"type\":\"object\",\"required\":[\"version\",\"kind\",\"message\"],\"properties\":{\"version\":{\"enum\":[\""
        + Version
        + "\","
        + Version
        + "]},\"kind\":{\"enum\":[";

    private const string EnvelopeMessage =
        "]},\"message\":{\"type\":\"string\",\"description\":\"Markdown prose for the user. This is what the user reads.\"}";

    private const string EnvelopeClose = "}}";

    /// <summary>
    /// The literal payload schema per kind, keyed so that a kind added to the enum without a decision here
    /// fails loudly rather than shipping an envelope with a slot nothing describes. A null value is the
    /// explicit statement "this kind carries no payload"; <see cref="AgentTurnDocumentKind.Answer"/> is the
    /// only one, because its envelope <c>message</c> is the whole document.
    /// </summary>
    private static readonly IReadOnlyDictionary<AgentTurnDocumentKind, string?> Payloads =
        new Dictionary<AgentTurnDocumentKind, string?>
        {
            [AgentTurnDocumentKind.Answer] = null,

            [AgentTurnDocumentKind.Questions] =
                "{\"type\":\"array\",\"items\":{\"type\":\"object\",\"required\":[\"id\",\"question\",\"options\"],"
                + "\"properties\":{\"id\":{\"type\":\"string\"},\"header\":{\"type\":\"string\"},"
                + "\"question\":{\"type\":\"string\"},\"multiSelect\":{\"type\":\"boolean\"},"
                + "\"options\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"required\":[\"label\"],"
                + "\"properties\":{\"label\":{\"type\":\"string\"},\"description\":{\"type\":\"string\"}}}}}}}",

            [AgentTurnDocumentKind.Plan] =
                "{\"type\":\"object\",\"required\":[\"steps\"],\"properties\":{\"summary\":{\"type\":\"string\"},"
                + "\"steps\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"required\":[\"title\"],"
                + "\"properties\":{\"title\":{\"type\":\"string\"},\"detail\":{\"type\":\"string\"}}}},"
                + "\"affects\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"required\":[\"item\"],"
                + "\"properties\":{\"item\":{\"type\":\"string\"},\"link\":{\"type\":\"string\"}}}},"
                + "\"risks\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}}}",

            [AgentTurnDocumentKind.Proposal] =
                "{\"type\":\"object\",\"required\":[\"title\"],\"properties\":{\"title\":{\"type\":\"string\"},"
                + "\"description\":{\"type\":\"string\"},\"acceptance\":{\"type\":\"string\"},"
                + "\"parent\":{\"type\":\"string\"}}}",

            // Backlog 243: the commit reviewer's verdict. A finding names the rule or acceptance it breaks and what
            // to change, because the building agent reads it as the reason its commit was refused and has to act on
            // it without a person. Those four are required; the line is not, since a missing test has none.
            // Work item #341: "answers" is optional and additive ("bump only when an old renderer would be wrong";
            // see AgentTurnDocument.CurrentVersion and docs/plans/typed-agent-turns.md, "Bump only when an old
            // renderer would be wrong"). It names the finding number, from an earlier review the builder disputed,
            // that THIS finding keeps: the reviewer re-asserting a disputed finding sets it, so the studio can tell
            // a kept dispute apart from a brand new finding without guessing from file/line/rule alone.
            [AgentTurnDocumentKind.Findings] =
                "{\"type\":\"array\",\"items\":{\"type\":\"object\",\"required\":[\"file\",\"rule\",\"change\",\"summary\"],"
                + "\"properties\":{\"file\":{\"type\":\"string\"},\"line\":{\"type\":\"integer\"},"
                + "\"severity\":{\"type\":\"string\"},\"summary\":{\"type\":\"string\"},"
                + "\"rule\":{\"type\":\"string\",\"description\":\"The rule, ADR or acceptance criterion it breaks.\"},"
                + "\"change\":{\"type\":\"string\",\"description\":\"What to change so it no longer does.\"},"
                + "\"answers\":{\"type\":\"integer\",\"description\":\"The finding number this one keeps, if the builder "
                + "disputed it earlier and you still think it holds. Omit for a finding nobody has disputed.\"}}}}",

            [AgentTurnDocumentKind.Blocked] =
                "{\"type\":\"object\",\"required\":[\"missing\"],\"properties\":{\"missing\":{\"type\":\"string\"},"
                + "\"action\":{\"type\":\"string\"}}}",

            // Backlog 300: the autopilot operator's retry-or-park decision. "retry" is required alone; a
            // decline with no reason is still a decline.
            [AgentTurnDocumentKind.Advice] =
                "{\"type\":\"object\",\"required\":[\"retry\"],\"properties\":{\"retry\":{\"type\":\"boolean\"},"
                + "\"reason\":{\"type\":\"string\"}}}",

            // Backlog 300: draft work items the operator wrote from findings, never sent to the tracker by the
            // operator itself. "title" is the only required field; a draft can name no acceptance at all.
            [AgentTurnDocumentKind.FollowUpDrafts] =
                "{\"type\":\"array\",\"items\":{\"type\":\"object\",\"required\":[\"title\"],"
                + "\"properties\":{\"title\":{\"type\":\"string\"},\"description\":{\"type\":\"string\"},"
                + "\"acceptance\":{\"type\":\"string\"}}}}",
        };

    /// <summary>
    /// The JSON type each kind's payload declares, read out of the literal schema above rather than
    /// restated. A reader that keeps its own copy of "questions is an array" is one more place for the
    /// studio's belief to drift from what it actually sent, which is the failure shape this subsystem
    /// keeps being bitten by.
    /// </summary>
    private static readonly IReadOnlyDictionary<AgentTurnDocumentKind, JsonValueKind> PayloadValueKinds =
        Payloads
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => DeclaredValueKind(pair.Key, pair.Value!));

    /// <summary>
    /// The literal schema for a kind's payload, or null when the kind carries none. Throws for a kind
    /// that has no entry, so a kind cannot be added to the vocabulary without a schema.
    /// </summary>
    public static string? PayloadSchema(AgentTurnDocumentKind kind) =>
        Payloads.TryGetValue(kind, out var schema)
            ? schema
            : throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "every document kind needs a literal payload schema, or an explicit null");

    /// <summary>
    /// The JSON type a kind's payload must be for its renderer to read it, or null when the kind carries
    /// no payload. Derived from <see cref="PayloadSchema"/>.
    /// </summary>
    public static JsonValueKind? PayloadValueKind(AgentTurnDocumentKind kind)
    {
        _ = PayloadSchema(kind);
        return PayloadValueKinds.TryGetValue(kind, out var valueKind) ? valueKind : null;
    }

    /// <summary>
    /// Assembles the schema to send for one turn. Kinds are emitted in
    /// <see cref="AgentTurnDocumentKinds.All"/> order and de-duplicated, so the same set always produces the
    /// same bytes, which is what makes the size guard in the tests meaningful.
    /// </summary>
    /// <param name="kinds">The kinds this turn could legitimately produce. Must not be empty.</param>
    public static string For(IReadOnlyCollection<AgentTurnDocumentKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        if (kinds.Count == 0)
        {
            throw new ArgumentException(
                "a turn must be allowed at least one document kind, or it has no way to end", nameof(kinds));
        }

        var ordered = AgentTurnDocumentKinds.All.Where(kinds.Contains).ToArray();
        if (ordered.Length == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(kinds), kinds, "none of the requested kinds is in the document library");
        }

        var builder = new StringBuilder(EnvelopeOpen);
        builder.AppendJoin(',', ordered.Select(kind => $"\"{AgentTurnDocumentKinds.Name(kind)}\""));
        builder.Append(EnvelopeMessage);

        foreach (var kind in ordered)
        {
            if (PayloadSchema(kind) is { } payload)
            {
                builder.Append(",\"").Append(AgentTurnDocumentKinds.Name(kind)).Append("\":").Append(payload);
            }
        }

        return builder.Append(EnvelopeClose).ToString();
    }

    private static JsonValueKind DeclaredValueKind(AgentTurnDocumentKind kind, string schema)
    {
        using var document = JsonDocument.Parse(schema);
        var declared = document.RootElement.TryGetProperty("type", out var type) ? type.GetString() : null;
        return declared switch
        {
            "object" => JsonValueKind.Object,
            "array" => JsonValueKind.Array,
            _ => throw new InvalidOperationException(
                $"the payload schema for '{AgentTurnDocumentKinds.Name(kind)}' must declare a root type of "
                + $"object or array, so a reader can tell whether the payload it found is the right shape "
                + $"(it declares '{declared ?? "nothing"}')"),
        };
    }
}
