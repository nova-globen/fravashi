using Fravashi.Domain.WorkItems;

namespace Fravashi.Domain.Autopilot;

/// <summary>Which limit fired, or that a restart could not resume honestly (ADR 0031 decisions 6 and 10).</summary>
public enum AutopilotStopKind
{
    /// <summary>The item spent more than its money ceiling.</summary>
    MoneyPerItem,

    /// <summary>The run spent more than its money ceiling.</summary>
    MoneyPerRun,

    /// <summary>The item reached its ceiling of failed commit reviews.</summary>
    FailedCommitReviews,

    /// <summary>The item ran longer than its time ceiling.</summary>
    TimePerItem,

    /// <summary>The run went longer than its no-progress window without the queue shrinking or a commit landing.</summary>
    NoProgress,

    /// <summary>
    /// A restart found the run with a step that may have been in flight; whether it finished is unknown, so the
    /// run stops rather than guess.
    /// </summary>
    CannotResume,

    /// <summary>Nothing in the queue is open any more, but not every item reached Done.</summary>
    QueueExhausted,

    /// <summary>
    /// A seam raised an exception instead of returning an answer. Repeating the call risks the exact duplicate
    /// the restart rule exists to prevent, so this stops the item rather than retrying on the next tick.
    /// </summary>
    SeamFailed,

    /// <summary>
    /// A person stopped the run (ADR 0031 decision 13): "stop now" on the current item, or "stop after this
    /// item" once it next closes (reaches Done, Stopped or Parked).
    /// </summary>
    StoppedByPerson,

    /// <summary>
    /// An item asked for a person and nobody answered within the dispatch's
    /// <see cref="AutopilotDispatchSettings.PersonWaitTimeout"/> (ADR 0031 decision 12, #305), so it parks
    /// rather than wait indefinitely. Item-scoped: the run moves on to the next marked item.
    /// </summary>
    PersonWaitTimedOut,
}

/// <summary>Whether a stop ends one item (the run continues with the next) or the whole run.</summary>
public enum AutopilotStopScope
{
    /// <summary>Only the current item stops; the run moves to the next queued item.</summary>
    Item,

    /// <summary>The whole run stops.</summary>
    Run,
}

/// <summary>
/// A stop, always a stored row and never a log line only (ADR 0031 decision 6: "A stop is a record, never a log
/// line only"). Names the limit and the figure that fired it.
/// </summary>
/// <param name="Kind">Which limit, or <see cref="AutopilotStopKind.CannotResume"/>.</param>
/// <param name="Scope">Whether it stopped the item or the run.</param>
/// <param name="LimitName">The limit's name, in words a person reads on the run.</param>
/// <param name="Figure">The figure that fired it, formatted for display (e.g. "$15.00", "3 failed reviews").</param>
/// <param name="AtUtc">When it fired.</param>
/// <param name="Item">The item it is about, null for a stop that is about the run as a whole.</param>
/// <param name="Reason">A longer explanation, when the name and figure alone do not say enough.</param>
public sealed record AutopilotStop(
    AutopilotStopKind Kind,
    AutopilotStopScope Scope,
    string LimitName,
    string Figure,
    DateTimeOffset AtUtc,
    WorkItemId? Item,
    string? Reason = null);
