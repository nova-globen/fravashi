namespace Fravashi.Domain.Autopilot;

/// <summary>How a project wants a run's work delivered (ADR 0031 decision 3), set per project.</summary>
public enum AutopilotDeliveryMode
{
    /// <summary>
    /// Each item is built on its own attempt branch, pushed, and opened as a pull request; the autopilot
    /// never writes main. The default, and this repository's own setting.
    /// </summary>
    BranchAndPullRequest,

    /// <summary>The autopilot commits straight to main.</summary>
    CommitToMain,
}
