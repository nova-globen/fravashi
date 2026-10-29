namespace Fravashi.Domain.WorkItems;

/// <summary>Who made a change: the user, an agent session, or the system.</summary>
public enum WorkItemActorKind
{
    /// <summary>The person using the studio.</summary>
    User,

    /// <summary>An agent session, named by its id.</summary>
    Session,

    /// <summary>The studio itself (a migration, a sync).</summary>
    System,
}

/// <summary>
/// The actor of a mutation (plan section 3.2, <c>MutationContext</c>). A session actor always carries its
/// session id, so every change an agent makes is attributable to the session that made it.
/// </summary>
public sealed record WorkItemActor
{
    private WorkItemActor(WorkItemActorKind kind, string? sessionId)
    {
        Kind = kind;
        SessionId = sessionId;
    }

    /// <summary>The person using the studio.</summary>
    public static WorkItemActor User { get; } = new(WorkItemActorKind.User, null);

    /// <summary>The studio itself.</summary>
    public static WorkItemActor System { get; } = new(WorkItemActorKind.System, null);

    /// <summary>What sort of actor this is.</summary>
    public WorkItemActorKind Kind { get; }

    /// <summary>The session id when <see cref="Kind"/> is <see cref="WorkItemActorKind.Session"/>; null otherwise.</summary>
    public string? SessionId { get; }

    /// <summary>An agent session. A blank session id is a programming fault.</summary>
    public static WorkItemActor Session(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("A session actor needs its session id.", nameof(sessionId));
        }

        return new(WorkItemActorKind.Session, sessionId);
    }
}
