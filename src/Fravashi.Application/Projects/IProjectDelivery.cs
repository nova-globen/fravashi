using Fravashi.Domain.Autopilot;
using Fravashi.Domain.Projects;

namespace Fravashi.Application.Projects;

/// <summary>
/// Which way a project's autopilot delivers its work (ADR 0031 decision 3), set in Settings > Project >
/// Project and variables. Shaped like <see cref="IProjectProviders"/> beside it: a project that never chose reads the
/// shipped default.
/// </summary>
public interface IProjectDelivery
{
    /// <summary>The project's delivery mode; <see cref="AutopilotDeliveryMode.BranchAndPullRequest"/> when it never chose.</summary>
    AutopilotDeliveryMode For(ProjectId project);

    /// <summary>Records the project's choice.</summary>
    void Set(ProjectId project, AutopilotDeliveryMode mode);
}
