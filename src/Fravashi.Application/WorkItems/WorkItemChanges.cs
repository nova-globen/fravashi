using Fravashi.Domain.Projects;

namespace Fravashi.Application.WorkItems;

/// <summary>
/// A process-wide signal that something changed a project's work items (backlog 41). It exists so the board can
/// show an agent's change as it happens, which is half of that item's acceptance: the user sees each change
/// appear in the board UI rather than after a manual reload.
/// <para>
/// It carries no payload beyond the project, deliberately. A surface that hears it re-reads through the port,
/// so there is exactly one answer about what an item says and no second copy of the model riding on an event.
/// </para>
/// </summary>
public interface IWorkItemChanges
{
    /// <summary>Raised after a mutation the tracker accepted. May arrive on any thread.</summary>
    event Action<ProjectId>? Changed;

    /// <summary>Announces that a project's items changed.</summary>
    void Announce(ProjectId project);
}

/// <summary>The in-process implementation; a studio is one process and the board is in it.</summary>
public sealed class WorkItemChanges : IWorkItemChanges
{
    /// <inheritdoc />
    public event Action<ProjectId>? Changed;

    /// <inheritdoc />
    public void Announce(ProjectId project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Changed?.Invoke(project);
    }
}
