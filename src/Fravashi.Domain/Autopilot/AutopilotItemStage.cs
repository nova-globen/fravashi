namespace Fravashi.Domain.Autopilot;

/// <summary>
/// Where one queued item has got to (ADR 0031 decisions 1 and 10; #297's own description names these nine). The
/// engine owns every move between them; no agent decides what happens next.
/// </summary>
public enum AutopilotItemStage
{
    /// <summary>Marked for the run, not started yet.</summary>
    Queued,

    /// <summary>The operator (#300) is writing the item's prompt.</summary>
    Prompting,

    /// <summary>The building agent is working the item.</summary>
    Building,

    /// <summary>A commit review (backlog 243) is reading what the builder would commit.</summary>
    Reviewing,

    /// <summary>Delivery (#301) is pushing the attempt branch and opening the pull request.</summary>
    Delivering,

    /// <summary>Delivery is reading CI and running the item's scenario.</summary>
    Verifying,

    /// <summary>Delivered, verified, evidence recorded. Terminal.</summary>
    Done,

    /// <summary>A limit fired. Terminal for this run; the item stays where delivery left it.</summary>
    Stopped,

    /// <summary>Waiting on a person. The run moves on to the next item without it.</summary>
    Parked,
}

/// <summary>Where <see cref="AutopilotItemStage"/> sits for the purposes the engine asks it about.</summary>
public static class AutopilotItemStages
{
    /// <summary>
    /// Stages the engine still has work to do in. <see cref="AutopilotItemStage.Done"/>,
    /// <see cref="AutopilotItemStage.Stopped"/> and <see cref="AutopilotItemStage.Parked"/> are not: the first two
    /// are finished, and a parked item waits for a person rather than for the engine.
    /// </summary>
    public static bool IsOpen(AutopilotItemStage stage) => stage is
        AutopilotItemStage.Queued or AutopilotItemStage.Prompting or AutopilotItemStage.Building
        or AutopilotItemStage.Reviewing or AutopilotItemStage.Delivering or AutopilotItemStage.Verifying;

    /// <summary>
    /// Stages whose one call, repeated after a restart, could create a duplicate the studio would then be unable
    /// to tell from a first attempt: a second commit, a second review verdict, a second pull request, a second
    /// verification run. Resuming one of these guesses whether the first call finished, which is exactly the
    /// outbox's own reason never to retry an <c>InFlight</c> write blindly. That is the line this draws, not
    /// whether a call was made at all: <see cref="AutopilotItemStage.Prompting"/> DOES make a real call (the
    /// operator writes a prompt, and that call is billed) and is still not here, because repeating it cannot
    /// create one of the duplicates above, only a second prompt that overwrites the item's first one. What
    /// repeating it does cost is real and is not hidden: the interrupted attempt's money was spent and is never
    /// added to the item's <c>SpentUsd</c>.
    /// </summary>
    public static bool MayHaveBeenInFlight(AutopilotItemStage stage) => stage is
        AutopilotItemStage.Building or AutopilotItemStage.Reviewing or AutopilotItemStage.Delivering
        or AutopilotItemStage.Verifying;
}
