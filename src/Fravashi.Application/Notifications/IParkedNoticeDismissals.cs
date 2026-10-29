using Fravashi.Domain.WorkItems;

namespace Fravashi.Application.Notifications;

/// <summary>
/// Which parked-autopilot notices a person has dismissed from "Waiting for you" (work item #356). Nothing stored this
/// before: a parked item stayed listed until it was unmarked or dispatched again. A dismissal is per run AND item, so
/// the same item parking again in a LATER run is a new notice and shows again. It hides the notice only: the item
/// stays marked for the autopilot and a later dispatch still resumes it.
/// </summary>
public interface IParkedNoticeDismissals
{
    /// <summary>True when this run's notice for this item was dismissed.</summary>
    bool IsDismissed(Guid run, WorkItemId item);

    /// <summary>Hides this run's notice for this item from the list.</summary>
    void Dismiss(Guid run, WorkItemId item);
}
