using Fravashi.Application.Notifications;

namespace Fravashi.Infrastructure.Notifications;

/// <summary>What one poll found, against what the previous poll saw (backlog 304).</summary>
/// <param name="NewEntries">Entries whose <see cref="WaitingForPersonEntry.Key"/> was not seen on the previous poll.</param>
/// <param name="NextKeys">Exactly the current poll's keys, to compare the NEXT poll against.</param>
public sealed record WaitingNotificationDiff(
    IReadOnlyList<WaitingForPersonEntry> NewEntries,
    IReadOnlySet<string> NextKeys);

/// <summary>
/// The whole "toast once per new waiting entry, never repeatedly for the same one" rule (ADR 0031 decision 12),
/// as a pure function so it is tested without any timer. A resolved entry's key simply falls out of
/// <see cref="WaitingNotificationDiff.NextKeys"/> on its own; if the same session or item later parks again with
/// a new <see cref="WaitingForPersonEntry.SinceUtc"/>, its key differs and it is reported new again.
/// </summary>
public static class WaitingNotificationTracker
{
    public static WaitingNotificationDiff Diff(
        IReadOnlySet<string> previousKeys, IReadOnlyList<WaitingForPersonEntry> current)
    {
        ArgumentNullException.ThrowIfNull(previousKeys);
        ArgumentNullException.ThrowIfNull(current);

        var next = new HashSet<string>(StringComparer.Ordinal);
        var added = new List<WaitingForPersonEntry>();
        foreach (var entry in current)
        {
            if (next.Add(entry.Key) && !previousKeys.Contains(entry.Key))
            {
                added.Add(entry);
            }
        }

        return new WaitingNotificationDiff(added, next);
    }
}
