using Fravashi.Domain.WorkItems;

namespace Fravashi.Domain.Autopilot;

/// <summary>
/// Whether a dispatch may actually run (#298's acceptance: "no marked Ready items, or branch + pull
/// request with no Git host token for the workspace's origin, refuses in words naming what is missing"; #310's
/// addition: "a dirty workspace refuses, naming how many changes"). Pure: the caller has already done every
/// read (the project's items, its delivery mode, whether a token is there, the workspace's dirty count), this
/// only judges what was read.
/// </summary>
public static class AutopilotDispatchValidation
{
    /// <summary>
    /// The refusal sentence, or null when the dispatch may proceed. <paramref name="workspaceDirtyCount"/> is
    /// checked first (#310): an attempt branch made from a dirty tree would carry a person's own edits into the
    /// run's working tree, and the run only ever commits the paths an agent was approved for, so those edits
    /// would simply sit there, uncommitted, for the whole run. <paramref name="concurrencyCapOverride"/> is
    /// checked next (#307, ADR 0031 decision 16): a dispatch may only LOWER the Settings concurrency cap
    /// (<paramref name="concurrencyCapSetting"/>), never raise it, and a mistaken higher number is told to the
    /// person here rather than silently clamped.
    /// </summary>
    public static string? Validate(
        IReadOnlyList<WorkItem> markedReadyItems,
        AutopilotDeliveryMode delivery,
        bool hasGitHostToken,
        string? gitHost,
        int workspaceDirtyCount = 0,
        int? concurrencyCapOverride = null,
        int concurrencyCapSetting = AutopilotConcurrency.DefaultCap)
    {
        ArgumentNullException.ThrowIfNull(markedReadyItems);

        if (workspaceDirtyCount > 0)
        {
            var plural = workspaceDirtyCount == 1 ? "change" : "changes";
            return $"The workspace has {workspaceDirtyCount} uncommitted {plural}; commit or discard them before dispatching.";
        }

        if (concurrencyCapOverride is { } overrideCap && (overrideCap <= 0 || overrideCap >= concurrencyCapSetting))
        {
            return $"The dispatch's concurrency cap ({overrideCap}) must be a positive number lower than the "
                + $"Settings cap ({concurrencyCapSetting}); a dispatch may only lower it, never raise it.";
        }

        if (markedReadyItems.Count == 0)
        {
            return "No Ready item is marked for autopilot; mark at least one before dispatching.";
        }

        if (delivery == AutopilotDeliveryMode.BranchAndPullRequest && !hasGitHostToken)
        {
            return gitHost is { Length: > 0 }
                ? $"This project delivers by branch and pull request, and there is no stored token for {gitHost}; add one under Settings > Connections > Git hosts."
                : "This project delivers by branch and pull request, and its origin remote could not be resolved to a Git host; add one under Settings > Connections > Git hosts.";
        }

        return null;
    }
}
