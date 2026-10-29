using Fravashi.Application.WorkItems;
using Fravashi.Domain.WorkItems;

namespace Fravashi.Application.Mirroring;

/// <summary>
/// Reads and writes the provider-owned half of a work item as plain text, which is the only form two stores
/// owned by different vendors can be compared in (ADR 0018 decision 4).
/// <para>
/// OWNED and CARRIED are two different questions and this type only answers the second.
/// <c>FieldOwnership</c> says who wins when both sides changed a field; <see cref="Carried"/> says which of
/// those fields this translator can put into a text bag today. Three provider-ownable fields are
/// deliberately outside it:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Status</b> and <b>Resolution</b> move through <c>ITracker.TransitionAsync</c> under the authoring
/// rules, and mapping our canonical status BACK to a provider state is one-to-many and has to ask (ADR 0018
/// decision 6, item 48). A field bag would let a sync pick a provider state silently, which is precisely
/// what that decision forbids. Category is derived from Status and carries nothing of its own.
/// </item>
/// <item>
/// <b>Flags</b> are a record with reasons attached, not an enum, and a provider's own blocked field is a
/// bare yes or no with no reason in it. Flattening that to text and back would either invent a reason or
/// drop one, and item 47 is where a specific provider says what it actually exposes.
/// </item>
/// </list>
/// </summary>
public static class ProviderFields
{
    /// <summary>
    /// What an item imported from a provider with no description says here, because the board requires one
    /// (backlog 149). It is OURS, not the provider's: toward the provider it reads as no description at all, so
    /// the next sync never sends the sentence to a site whose item was simply empty, and a real description a
    /// person writes later replaces it and is sent as usual.
    /// </summary>
    public const string NoDescription = "Imported with no description.";

    /// <summary>The provider-owned fields this translator can carry, in a stable order.</summary>
    public static IReadOnlyList<string> Carried { get; } =
    [
        nameof(WorkItem.Title),
        nameof(WorkItem.Description),
        nameof(WorkItem.Level),
        nameof(WorkItem.Kind),
        nameof(WorkItem.Parent),
        nameof(WorkItem.Priority),
        nameof(WorkItem.Assignee),
    ];

    /// <summary>The item's carried fields as text, every one present even when it is empty.</summary>
    public static IReadOnlyDictionary<string, string?> Read(WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [nameof(WorkItem.Title)] = item.Title,
            [nameof(WorkItem.Description)] = string.Equals(item.Description, NoDescription, StringComparison.Ordinal)
                ? string.Empty
                : item.Description,
            [nameof(WorkItem.Level)] = StableNames.ToName(item.Level),
            [nameof(WorkItem.Kind)] = StableNames.ToName(item.Kind),
            [nameof(WorkItem.Parent)] = item.Parent?.Value,
            [nameof(WorkItem.Priority)] = item.Priority is { } priority ? StableNames.ToName(priority) : null,
            [nameof(WorkItem.Assignee)] = item.Assignee,
        };
    }

    /// <summary>
    /// Turns values a sync decided the provider owns into a patch. Only fields PRESENT in the dictionary are
    /// set: a provider that does not carry a field at all must leave the local value alone, and an absent
    /// key is how it says so. A present key with an empty value is a real clearing where the model allows
    /// one.
    /// <para>
    /// A value this cannot read (a level name no enum has, a priority spelled another way) is SKIPPED rather
    /// than defaulted. Defaulting would quietly demote a Feature to a Task because one provider spells its
    /// issue type differently, and that is a data change nobody asked for.
    /// </para>
    /// </summary>
    public static WorkItemPatch ToPatch(IReadOnlyDictionary<string, string?> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var patch = new WorkItemPatch();

        // Title and description are mandatory on the model, so an empty one is not a clearing and is
        // ignored rather than sent to be refused.
        if (fields.TryGetValue(nameof(WorkItem.Title), out var title) && title is { Length: > 0 })
        {
            patch = patch with { Title = title };
        }

        if (fields.TryGetValue(nameof(WorkItem.Description), out var description) && description is { Length: > 0 })
        {
            patch = patch with { Description = description };
        }

        if (fields.TryGetValue(nameof(WorkItem.Level), out var level))
        {
            var parsed = StableNames.Parse<WorkItemLevel>(level, "level");
            if (parsed.IsValid)
            {
                patch = patch with { Level = parsed.Value };
            }
        }

        if (fields.TryGetValue(nameof(WorkItem.Kind), out var kind))
        {
            var parsed = StableNames.Parse<WorkItemKind>(kind, "kind");
            if (parsed.IsValid)
            {
                patch = patch with { Kind = parsed.Value };
            }
        }

        if (fields.TryGetValue(nameof(WorkItem.Parent), out var parent))
        {
            patch = patch with { Parent = parent is { Length: > 0 } ? new WorkItemId(parent) : null };
        }

        if (fields.TryGetValue(nameof(WorkItem.Priority), out var priority))
        {
            if (priority is null or { Length: 0 })
            {
                patch = patch with { Priority = (WorkItemPriority?)null };
            }
            else
            {
                var parsed = StableNames.Parse<WorkItemPriority>(priority, "priority");
                if (parsed.IsValid)
                {
                    patch = patch with { Priority = parsed.Value };
                }
            }
        }

        if (fields.TryGetValue(nameof(WorkItem.Assignee), out var assignee))
        {
            patch = patch with { Assignee = assignee is { Length: > 0 } ? assignee : null };
        }

        return patch;
    }
}
