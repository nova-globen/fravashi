namespace Fravashi.Domain.WorkItems;

/// <summary>The generated, never reused identity of a work item.</summary>
public sealed record WorkItemId : IComparable<WorkItemId>
{
    /// <summary>Creates an id from its stored text. A blank value is a programming fault.</summary>
    public WorkItemId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A work item id cannot be blank.", nameof(value));
        }

        Value = value;
    }

    /// <summary>The stored text of the id.</summary>
    public string Value { get; }

    /// <summary>A fresh id.</summary>
    public static WorkItemId New() => new(Guid.NewGuid().ToString("N"));

    /// <summary>Ordinal order over the stored text, the order a symmetric link is stored in.</summary>
    public int CompareTo(WorkItemId? other) =>
        other is null ? 1 : string.CompareOrdinal(Value, other.Value);

    /// <inheritdoc />
    public override string ToString() => Value;
}
