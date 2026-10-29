using Fravashi.Domain.Projects;

namespace Fravashi.Application.Projects;

/// <summary>What a project does with one kind of provider (backlog 142).</summary>
public enum ProviderChoice
{
    /// <summary>It follows the connection the install is configured with, which is what every project did before.</summary>
    Install,

    /// <summary>It follows none: its board and wiki are the source of truth and nothing is sent anywhere.</summary>
    None,
}

/// <summary>A project's board and wiki choices.</summary>
public sealed record ProjectProviderChoice(ProviderChoice Board, ProviderChoice Wiki)
{
    /// <summary>What a project that never chose follows: the install's connections, as before this existed.</summary>
    public static ProjectProviderChoice Default { get; } = new(ProviderChoice.Install, ProviderChoice.Install);
}

/// <summary>
/// Which providers each project follows (backlog 142). A project can move between providers over its life (Jira and
/// Confluence, then Azure DevOps, then none), so the answer is a project's, not the install's.
/// A project that never chose follows the install's connections, which is what every project did before.
/// </summary>
public interface IProjectProviders
{
    /// <summary>The project's choices; <see cref="ProjectProviderChoice.Default"/> when it never made any.</summary>
    ProjectProviderChoice For(ProjectId project);

    /// <summary>Records the project's choices.</summary>
    void Set(ProjectId project, ProjectProviderChoice choice);
}
