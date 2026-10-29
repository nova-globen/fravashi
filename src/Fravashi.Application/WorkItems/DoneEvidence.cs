using System.Text;
using System.Text.Json.Nodes;
using Fravashi.Domain.WorkItems;
using Fravashi.Domain.WorkItems.AgentLayer;

namespace Fravashi.Application.WorkItems;

/// <summary>
/// The evidence-on-Done rule (requested 2026-10-22, evidence on Done): moving an item INTO Done asks for
/// at least one evidence record on each of its acceptance criteria, or a written reason why the missing
/// evidence does not apply. Narrow on purpose:
/// <list type="bullet">
/// <item>only a move into Done, never Cancelled or any other status, and never Done to Done;</item>
/// <item>only an item that has acceptance criteria; one with none moves exactly as before;</item>
/// <item>never a move that arrives from a provider (<see cref="MutationContext.FromProvider"/>), because the
/// provider already holds that state and refusing it would only leave the two stores disagreeing;</item>
/// <item>nothing already Done is touched: the rule judges a move when it is made and is never re-checked.</item>
/// </list>
/// A reason is stored as an <see cref="EvidenceKind.NoEvidenceReason"/> record tied to the item, so it lives
/// in the same append-only history as the evidence it stands in for. Such a record never counts as evidence
/// for a criterion, and a reason given on an earlier move does not carry over to a later one.
/// </summary>
public static class DoneEvidence
{
    /// <summary>The gate a stored reason names, so a reader can tell where it came from.</summary>
    public const string Gate = "done";

    /// <summary>True when a move of <paramref name="current"/> to <paramref name="to"/> is one this rule judges.</summary>
    public static bool Applies(WorkItem current, WorkItemStatus to, MutationContext context)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(context);
        return to == WorkItemStatus.Done
            && current.Status != WorkItemStatus.Done
            && current.AcceptanceCriteria.Count > 0
            && !context.FromProvider;
    }

    /// <summary>The item's acceptance criteria that no evidence record points at, in the item's own order.</summary>
    public static IReadOnlyList<AcceptanceCriterion> CriteriaWithoutEvidence(
        WorkItem item, IEnumerable<EvidenceRecord> evidence)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(evidence);
        var proven = evidence
            .Where(e => e.Kind != EvidenceKind.NoEvidenceReason && e.Criterion is { Length: > 0 })
            .Select(e => e.Criterion!)
            .ToHashSet(StringComparer.Ordinal);
        return [.. item.AcceptanceCriteria.Where(c => !proven.Contains(c.Id))];
    }

    /// <summary>True when <paramref name="reason"/> is a reason someone could read later.</summary>
    public static bool HasReason(string? reason) => !string.IsNullOrWhiteSpace(reason);

    /// <summary>The refusal's sentence, naming every criterion without evidence.</summary>
    public static string Refusal(string described, IReadOnlyList<AcceptanceCriterion> missing)
    {
        ArgumentNullException.ThrowIfNull(missing);
        var text = new StringBuilder()
            .Append("Item ").Append(described).Append(" cannot move to Done yet: ")
            .Append(missing.Count == 1 ? "this acceptance criterion has" : "these acceptance criteria have")
            .Append(" no evidence: ")
            .Append(string.Join("; ", missing.Select(c => $"'{c.Text}'")))
            .Append(". Record evidence against ")
            .Append(missing.Count == 1 ? "it" : "each")
            .Append(", or move it to Done with a written reason why the missing evidence does not apply.");
        return text.ToString();
    }

    /// <summary>
    /// The record that stores a reason: tied to the item (no criterion), naming in its details which criteria
    /// had no evidence when the move was made, so the reason can be read against what it was excusing.
    /// </summary>
    public static NewEvidence ReasonRecord(string reason, IReadOnlyList<AcceptanceCriterion> missing)
    {
        ArgumentNullException.ThrowIfNull(missing);
        var criteria = new JsonArray([.. missing.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["text"] = c.Text })]);
        return new NewEvidence
        {
            Kind = EvidenceKind.NoEvidenceReason,

            // Pass is the move's own reading: the actor judged the item done. The kind is what says no check
            // stands behind it, and every reader that weighs evidence reads the kind first.
            Result = EvidenceResult.Pass,
            Summary = reason.Trim(),
            Gate = Gate,
            Details = new JsonObject { ["criteriaWithoutEvidence"] = criteria }.ToJsonString(),
        };
    }
}
