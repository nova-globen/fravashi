using Fravashi.Domain.Projects;

namespace Fravashi.Application.WorkItems;

/// <summary>Resolves the tracker that is the source of truth for a project (plan section 3.1).</summary>
public interface ITrackerResolver
{
    /// <summary>The tracker bound to <paramref name="project"/>. In backlog item 37 this is always the local board.</summary>
    ITracker For(ProjectId project);
}
