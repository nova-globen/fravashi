namespace Fravashi.Domain.Projects;

/// <summary>
/// The identity of a project, the scope a work item, its links and its number belong to (backlog 81, the
/// canonical artifact model plan section 2). Every install has the seeded <see cref="Default"/> project.
/// </summary>
public sealed record ProjectId
{
    /// <summary>Creates a project id. A blank value is a programming fault, not user input.</summary>
    public ProjectId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A project id cannot be blank.", nameof(value));
        }

        Value = value;
    }

    /// <summary>The project every install has, and every workspace joins until someone creates another.</summary>
    public static ProjectId Default { get; } = new("default");

    /// <summary>The stored text of the id.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}
