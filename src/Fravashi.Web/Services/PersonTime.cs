using System.Globalization;
using Fravashi.Application.Time;

namespace Fravashi.Web.Services;

/// <summary>How an instant is spelled on a page (backlog 246).</summary>
public enum LocalTimeStyle
{
    /// <summary>"19:19" today, "2026-10-06 19:19" on another local day.</summary>
    Clock,

    /// <summary>"19:19:05" today, "2026-10-06 19:19:05" on another local day.</summary>
    ClockSeconds,

    /// <summary>Always "2026-10-07 19:19".</summary>
    DateTime,

    /// <summary>Always "Oct 7, 19:19".</summary>
    Short,

    /// <summary>Always "2026-10-07".</summary>
    Date,
}

/// <summary>
/// The person's time zone for one circuit (backlog 246, plan @70). The SERVER does not know it: the container's own zone
/// is UTC, and a corporate install can serve people in several zones at once, so the zone is the BROWSER's, read once
/// per circuit through JS interop (<c>PersonTimeZoneReader</c>) and never the container's TZ. Null until the browser
/// has said, which is the prerender and anything the server's time zone database does not know.
/// </summary>
public sealed class PersonTimeZone
{
    private TimeZoneInfo? _zone;

    /// <summary>The person's zone, or null while it is not known.</summary>
    public TimeZoneInfo? Zone => _zone;

    /// <summary>
    /// The zone a component's services hold, or null; looked up rather than injected, so a host that registers no zone
    /// (an older test, a static page) renders labelled UTC instead of failing.
    /// </summary>
    public static PersonTimeZone? Of(IServiceProvider services) =>
        services?.GetService(typeof(PersonTimeZone)) as PersonTimeZone;

    /// <summary>Raised when the zone becomes known or changes, so every time on the page re-renders in it.</summary>
    public event Action? Changed;

    /// <summary>
    /// Sets the zone from the browser's IANA id (<c>Intl.DateTimeFormat().resolvedOptions().timeZone</c>). An empty or
    /// unknown id leaves the zone unknown, which renders labelled UTC rather than a guess. Answers whether it is known.
    /// </summary>
    public bool Set(string? ianaId)
    {
        TimeZoneInfo? zone = null;
        if (!string.IsNullOrWhiteSpace(ianaId))
        {
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(ianaId.Trim());
            }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
            {
                zone = null;
            }
        }

        Use(zone);
        return zone is not null;
    }

    /// <summary>Sets the zone directly (tests, and anything that already holds one).</summary>
    public void Use(TimeZoneInfo? zone)
    {
        if (ReferenceEquals(_zone, zone) || (_zone is not null && zone is not null && _zone.Id == zone.Id))
        {
            return;
        }

        _zone = zone;
        Changed?.Invoke();
    }
}

/// <summary>
/// The one formatter for an instant shown to a person (backlog 246, plan @70). In the person's zone when it is known;
/// in UTC, LABELLED as UTC, when it is not, so a time is never silently in somebody else's zone.
/// </summary>
public static class PersonTime
{
    /// <summary>
    /// <paramref name="at"/> as <paramref name="style"/> says, in <paramref name="zone"/>. "Today" for
    /// <see cref="LocalTimeStyle.Clock"/> is <paramref name="now"/>'s day in the same zone.
    /// </summary>
    public static string Format(DateTimeOffset at, TimeZoneInfo? zone, DateTimeOffset now, LocalTimeStyle style = LocalTimeStyle.Clock)
    {
        var known = zone is not null;
        var inZone = zone ?? TimeZoneInfo.Utc;
        var local = TimeZoneInfo.ConvertTime(at, inZone);
        var today = TimeZoneInfo.ConvertTime(now, inZone).Date;
        var sameDay = local.Date == today;

        var format = style switch
        {
            LocalTimeStyle.Clock => sameDay ? "HH:mm" : "yyyy-MM-dd HH:mm",
            LocalTimeStyle.ClockSeconds => sameDay ? "HH:mm:ss" : "yyyy-MM-dd HH:mm:ss",
            LocalTimeStyle.DateTime => "yyyy-MM-dd HH:mm",
            LocalTimeStyle.Short => "MMM d, HH:mm",
            LocalTimeStyle.Date => "yyyy-MM-dd",
            _ => "yyyy-MM-dd HH:mm",
        };

        var text = local.ToString(format, CultureInfo.InvariantCulture);
        return known ? text : text + " UTC";
    }

    /// <summary>
    /// A sentence of parts as one string in <paramref name="zone"/>, for a surface that composes a string rather than
    /// markup (a card's one-line state). Every instant is spelled as <c>LocalTime</c> would spell it.
    /// </summary>
    public static string Format(StampedWords words, TimeZoneInfo? zone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(words);
        var text = new System.Text.StringBuilder();
        foreach (var part in words.Parts)
        {
            text.Append(part switch
            {
                TextPart t => t.Text,
                InstantPart i => Format(i.At, zone, now, i.Seconds ? LocalTimeStyle.ClockSeconds : LocalTimeStyle.Clock),
                _ => string.Empty,
            });
        }

        return text.ToString();
    }
}
