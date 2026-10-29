using Fravashi.Web.Services;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>A person's time zone, faked for a test (backlog 246): what the browser would have reported.</summary>
internal static class PersonZones
{
    /// <summary>The zone with IANA id <paramref name="ianaId"/>, as if the browser had said it.</summary>
    public static PersonTimeZone Fixed(string ianaId)
    {
        var zone = new PersonTimeZone();
        Assert.True(zone.Set(ianaId), $"The test host's time zone database does not know {ianaId}.");
        return zone;
    }

    /// <summary>A person whose clock is UTC, so an assertion written in UTC reads without the "UTC" label.</summary>
    public static PersonTimeZone Utc()
    {
        var zone = new PersonTimeZone();
        zone.Use(TimeZoneInfo.Utc);
        return zone;
    }
}
