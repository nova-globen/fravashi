namespace Fravashi.Application.Onboarding;

/// <summary>
/// The result of reading a board's actual statuses for the mapping step (O15 / F7). Either a non-empty list of
/// the native status names the user's board exposes, or an <see cref="Error"/> explaining why they could not be
/// read (so the step can degrade honestly to manual mapping rather than inventing states).
/// </summary>
/// <param name="States">The distinct native status names, in the order the provider returned them.</param>
/// <param name="Error">A human-readable reason the states could not be fetched, or null on success.</param>
public sealed record BoardStatesResult(IReadOnlyList<string> States, string? Error)
{
    public bool HasStates => States.Count > 0;

    public static BoardStatesResult Ok(IReadOnlyList<string> states) => new(states, null);

    public static BoardStatesResult Failed(string error) => new([], error);
}

/// <summary>
/// Reads the real statuses a work board exposes so the onboarding mapping step can offer the user's own states
/// to assign to each canonical status, instead of guessing. Read-only and product-neutral: Jira project
/// statuses (<c>GET /rest/api/3/project/{key}/statuses</c>) and Azure DevOps work-item-type states
/// (<c>_apis/wit/workitemtypes/{type}/states</c>). Runs only on explicit user action in the wizard.
/// </summary>
public interface IBoardStatesProbe
{
    Task<BoardStatesResult> FetchAsync(BoardAccessRequest request, CancellationToken cancellationToken = default);
}
