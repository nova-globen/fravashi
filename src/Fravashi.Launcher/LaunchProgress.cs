namespace Fravashi.Launcher;

/// <summary>Where one segment of the splash's progress bar stands.</summary>
public enum LaunchSegmentState
{
    /// <summary>The launch has not reached this step.</summary>
    Pending,

    /// <summary>The launch is on this step now. A launch that stalls, stalls showing this one.</summary>
    Current,

    /// <summary>The launch is past this step, whether it ran or had nothing to do.</summary>
    Done,
}

/// <summary>
/// The splash's progress as a function of the current <see cref="LaunchPhase"/> (backlog 4): one segment per
/// step a launch can stand on, in the order the launcher raises them. It is a pure function of the phase, with
/// no memory, for two reasons. A launch can start over in the same process (the tray's Open after a Stop), and
/// then the bar must show the start again. And two steps are conditional (the engine's machine is only started
/// when it was down, the tunnel only when the variant has one): a step that never raised its phase reads as
/// done once a later one arrives, because a gap in the bar would read as a failure.
/// </summary>
public static class LaunchProgress
{
    /// <summary>The steps, in launch order. <see cref="LaunchPhase.Ready"/> is not a step: it is the end.</summary>
    public static IReadOnlyList<LaunchPhase> Steps { get; } =
        Enum.GetValues<LaunchPhase>().Where(phase => phase != LaunchPhase.Ready).ToArray();

    /// <summary>One state per entry of <see cref="Steps"/>, for the phase the launch is on now.</summary>
    public static IReadOnlyList<LaunchSegmentState> For(LaunchPhase phase)
    {
        var states = new LaunchSegmentState[Steps.Count];
        for (var i = 0; i < Steps.Count; i++)
        {
            states[i] = phase == LaunchPhase.Ready || Steps[i] < phase
                ? LaunchSegmentState.Done
                : Steps[i] == phase
                    ? LaunchSegmentState.Current
                    : LaunchSegmentState.Pending;
        }

        return states;
    }
}
