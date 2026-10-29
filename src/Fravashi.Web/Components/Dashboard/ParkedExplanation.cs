using System.Globalization;
using Fravashi.Application.Notifications;
using Fravashi.Application.Sessions.Tools;

namespace Fravashi.Web.Components.Dashboard;

/// <summary>
/// What a parked autopilot item means, in plain words, for the explanation "Waiting for you" opens when one is clicked
/// (work item #356). The owner clicked "#23 ... is parked: Commits are on 'main' locally. Push it to run CI and the
/// scenario. This run has ended; dispatch the project again to resume it.", landed on the retired autopilot page's dispatch form,
/// and did not know what to do there. These lines say what happened and what each button below them does.
/// </summary>
public static class ParkedExplanation
{
    /// <summary>The explanation, one paragraph per line.</summary>
    public static IReadOnlyList<string> Lines(WaitingForPersonEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var item = entry.ItemNumber is { } number
            ? "#" + number.ToString(CultureInfo.InvariantCulture)
            : "this item";
        var lines = new List<string>
        {
            $"The autopilot set {item} aside because it needs a person before it can go on.",
        };

        if (entry.Reason is { Length: > 0 } reason)
        {
            lines.Add("Why: " + StudioToolDescriptions.Humanize(reason).Trim());
        }

        if (NeedsPush(entry))
        {
            lines.Add("The commits exist only on this machine. Push them (the Git page does that), "
                + "so CI and the item's test scenario can run on the forge.");
        }

        lines.Add(entry.RunEnded
            ? $"The run that was working on {item} has ended, so nothing is working on it now. It is still marked for "
              + "the autopilot: Dispatch again starts a new run for this project with the last run's settings, and "
              + "that run picks it up where it was left."
            : "The run is still going and has moved on to the other items; the Autopilot card on Home shows it. Answer what it is "
              + "waiting for, and the run comes back to it.");

        lines.Add("Dismiss hides this notice. The item stays marked, so a later dispatch still resumes it.");
        return lines;
    }

    /// <summary>True when the reason says the work waits on a push (the commits are local only).</summary>
    public static bool NeedsPush(WaitingForPersonEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var reason = entry.Reason ?? entry.What;
        return reason.Contains("Push it", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("locally", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("not pushed", StringComparison.OrdinalIgnoreCase);
    }
}
