using Fravashi.Domain.Projects;

namespace Fravashi.Application.Projects;

/// <summary>How much a project holds, for the setup's "Join an existing project" list (item #373).</summary>
/// <param name="WorkItems">Its work items, every status included.</param>
/// <param name="Documents">Its wiki documents.</param>
public sealed record ProjectContents(int WorkItems, int Documents)
{
    /// <summary>Nothing at all.</summary>
    public static ProjectContents Empty { get; } = new(0, 0);

    /// <summary>True when the project holds nothing, which is what an import needs.</summary>
    public bool IsEmpty => WorkItems == 0 && Documents == 0;
}

/// <summary>Counts what a project holds, cheaply, without reading any of it.</summary>
public interface IProjectContents
{
    /// <summary>The project's counts; <see cref="ProjectContents.Empty"/> for a project that does not exist.</summary>
    ProjectContents Of(ProjectId project);
}
