using Bunit;
using Fravashi.Application.AgentRuntimes.Broker;
using Fravashi.Application.Network;
using Fravashi.Application.Time;
using Fravashi.Infrastructure.Onboarding.Auth;
using Fravashi.Infrastructure.Sessions;
using Fravashi.Web.Components.Auth;
using Fravashi.Web.Components.Shell;
using Fravashi.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

/// <summary>
/// Backlog 246 (plan @70): every time the studio writes into a sentence shown to a person is in the PERSON's zone, the
/// one the browser reported, never the container's. Each sentence is rendered through the shared mechanism
/// (<see cref="StampedText"/> and <see cref="LocalTime"/> over <see cref="PersonTime"/>) in a fixed zone and asserted on
/// the hour and the day, and its plain words, which a log line or an agent gets, are asserted unchanged.
/// </summary>
public sealed class PersonTimeTests
{
    /// <summary>12:00 UTC on 7 October 2026: 14:00 in Stockholm (UTC+2), 05:00 in Los Angeles (UTC-7), 17:30 in Kolkata (+05:30).</summary>
    private static readonly DateTimeOffset Noon = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly NetworkFailure NameFails = new(NetworkFailureKind.NameNotResolved, "test");

    public static TheoryData<string, string> Zones => new()
    {
        { "Europe/Stockholm", "14:00" },
        { "America/Los_Angeles", "05:00" },
        { "Asia/Kolkata", "17:30" },
    };

    private static TimeZoneInfo Zone(string id) => TimeZoneInfo.FindSystemTimeZoneById(id);

    private static BunitContext InZone(string? zone, DateTimeOffset now)
    {
        var ctx = new BunitContext();
        ctx.Services.AddSingleton(zone is null ? new PersonTimeZone() : PersonZones.Fixed(zone));
        ctx.Services.AddSingleton<TimeProvider>(new FixedClock(now));
        return ctx;
    }

    private static string Said(StampedWords words, string? zone, DateTimeOffset? now = null)
    {
        using var ctx = InZone(zone, now ?? Noon.AddMinutes(5));
        return string.Concat(ctx.Render<StampedText>(p => p.Add(c => c.Words, words)).Nodes.Select(n => n.TextContent));
    }

    private static string Plain(DateTimeOffset at, bool seconds = false) =>
        at.ToLocalTime().ToString(seconds ? "HH:mm:ss" : "HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    // ---- the formatter ---------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Zones))]
    public void The_formatter_says_the_hour_in_the_persons_zone(string zone, string hour)
    {
        Assert.Equal(hour, PersonTime.Format(Noon, Zone(zone), Noon));
        Assert.Equal(hour + ":09", PersonTime.Format(Noon.AddSeconds(9), Zone(zone), Noon, LocalTimeStyle.ClockSeconds));
        Assert.Equal("2026-10-07 " + hour, PersonTime.Format(Noon, Zone(zone), Noon, LocalTimeStyle.DateTime));
        Assert.Equal("Oct 7, " + hour, PersonTime.Format(Noon, Zone(zone), Noon, LocalTimeStyle.Short));
    }

    [Fact]
    public void An_instant_whose_local_day_is_not_its_utc_day_is_said_on_the_local_day()
    {
        // 20:00 UTC on the 7th is 01:30 on the 8th in Kolkata. Seen at 20:05 UTC it is the person's today, so only the
        // hour; an instant on the person's yesterday carries its date, which is the local date, not the UTC one.
        var kolkata = Zone("Asia/Kolkata");
        var now = new DateTimeOffset(2026, 10, 7, 20, 5, 0, TimeSpan.Zero);

        Assert.Equal("01:30", PersonTime.Format(new DateTimeOffset(2026, 10, 7, 20, 0, 0, TimeSpan.Zero), kolkata, now));
        Assert.Equal("2026-10-07 23:30", PersonTime.Format(new DateTimeOffset(2026, 10, 7, 18, 0, 0, TimeSpan.Zero), kolkata, now));
        Assert.Equal("2026-10-08", PersonTime.Format(new DateTimeOffset(2026, 10, 7, 20, 0, 0, TimeSpan.Zero), kolkata, now, LocalTimeStyle.Date));

        // And the other way: 03:00 UTC on the 8th is 20:00 on the 7th in Los Angeles.
        var la = Zone("America/Los_Angeles");
        Assert.Equal("2026-10-07 20:00", PersonTime.Format(
            new DateTimeOffset(2026, 10, 8, 3, 0, 0, TimeSpan.Zero), la, new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void When_the_browser_cannot_say_the_time_is_utc_and_labelled_utc()
    {
        Assert.Equal("12:00 UTC", PersonTime.Format(Noon, null, Noon));
        Assert.Equal("2026-10-06 23:30 UTC", PersonTime.Format(Noon.AddHours(-12.5), null, Noon));

        var zone = new PersonTimeZone();
        Assert.False(zone.Set("Not/AZone"));
        Assert.False(zone.Set(""));
        Assert.Null(zone.Zone);
        Assert.True(zone.Set("Asia/Kolkata"));
        Assert.Equal("Asia/Kolkata", zone.Zone!.Id);
    }

    [Fact]
    public void LocalTime_renders_utc_labelled_until_the_zone_arrives_and_then_the_persons_hour()
    {
        using var ctx = new BunitContext();
        var zone = new PersonTimeZone();
        ctx.Services.AddSingleton(zone);
        ctx.Services.AddSingleton<TimeProvider>(new FixedClock(Noon));

        var cut = ctx.Render<LocalTime>(p => p.Add(c => c.At, Noon));
        Assert.Equal("12:00 UTC", cut.Find("time").TextContent);
        Assert.Equal("utc", cut.Find("time").GetAttribute("data-zone"));
        Assert.Equal("2026-10-07T12:00:00Z", cut.Find("time").GetAttribute("datetime"));

        cut.InvokeAsync(() => { zone.Set("Europe/Stockholm"); });

        cut.WaitForAssertion(() => Assert.Equal("14:00", cut.Find("time").TextContent));
        Assert.Equal("person", cut.Find("time").GetAttribute("data-zone"));
    }

    [Fact]
    public void LocalTime_with_no_zone_registered_still_renders_labelled_utc()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<LocalTime>(p => p.Add(c => c.At, Noon));
        Assert.EndsWith(" UTC", cut.Find("time").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reader_sets_the_zone_the_browser_reports()
    {
        using var ctx = new BunitContext();
        var zone = new PersonTimeZone();
        ctx.Services.AddSingleton(zone);
        ctx.JSInterop.Setup<string?>("fravashiTimeZone.read").SetResult("America/Los_Angeles");

        ctx.Render<PersonTimeZoneReader>();

        Assert.Equal("America/Los_Angeles", zone.Zone?.Id);
    }

    // ---- each sentence -----------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Zones))]
    public void The_provider_outage_line_says_the_persons_hour(string zone, string hour)
    {
        var outage = new ProviderOutage("Azure DevOps", Noon, NameFails);

        Assert.Equal(
            $"Azure DevOps unreachable since {hour} (name not resolved); 2 change(s) waiting; nothing lost. What this page shows is this board's own copy, and Sync now sends what waits once Azure DevOps answers.",
            Said(outage.SentenceWithWaiting(2), zone));
        Assert.Equal($"Azure DevOps unreachable since {hour} (name not resolved)", Said(outage.Sentence, zone));

        // Unchanged for a log line or an agent.
        Assert.Equal("Azure DevOps unreachable since 12:00 (name not resolved)", outage.Words);
        Assert.Equal(outage.SentenceWithWaiting(2).Plain(), outage.WithWaiting(2));
    }

    [Theory]
    [MemberData(nameof(Zones))]
    public void The_reachability_words_say_the_persons_hour(string zone, string hour)
    {
        var state = new NetworkReachabilityState(new NetworkEndpoint("api.anthropic.com", 443), false, Noon, NameFails, null);

        Assert.Equal($"api.anthropic.com unreachable since {hour} (name not resolved)", Said(state.Sentence, zone));
        Assert.Equal("api.anthropic.com unreachable since 12:00 (name not resolved)", state.Words);
    }

    [Theory]
    [MemberData(nameof(Zones))]
    public void The_broker_health_words_say_the_persons_hour(string zone, string hour)
    {
        var health = new BrokerTokenHealth(new FixedClock(Noon));
        health.Unreachable("could not reach broker.example: name not resolved");

        Assert.Equal(
            $"Broker unreachable since {hour} (could not reach broker.example: name not resolved), retrying; next attempt at {hour}:15.",
            Said(health.Sentence!, zone));
        Assert.Equal(
            $"Broker unreachable since {Plain(Noon)} (could not reach broker.example: name not resolved), retrying; next attempt at {Plain(Noon.AddSeconds(15), seconds: true)}.",
            health.Words);
    }

    [Theory]
    [MemberData(nameof(Zones))]
    public void The_mint_words_say_the_persons_hour(string zone, string hour)
    {
        var outage = new BrokerOutage(Noon, "could not reach broker.example: name not resolved", Noon.AddSeconds(15));

        Assert.Equal(
            $"Could not mint a broker token (could not reach broker.example: name not resolved). Fravashi retries by itself; next attempt at {hour}:15.",
            Said(outage.MintWords, zone));
        Assert.Equal(
            $"Could not mint a broker token (could not reach broker.example: name not resolved). Fravashi retries by itself; next attempt at {Plain(Noon.AddSeconds(15), seconds: true)}.",
            outage.MintWords.Plain());
    }

    [Theory]
    [MemberData(nameof(Zones))]
    public void The_waiting_banner_says_the_persons_hour(string zone, string hour)
    {
        var wait = new NetworkWait(Noon, NameFails, "api.anthropic.com", Attempt: 2, ProcessEnded: false);

        Assert.Equal(
            $"Waiting for the network since {hour}: could not reach api.anthropic.com: name not resolved. Fravashi continues this conversation by itself when it is back. Automatic attempt 2 of 3.",
            Said(wait.Sentence, zone));
        Assert.Equal(
            $"Waiting for the network since {Plain(Noon)}: could not reach api.anthropic.com: name not resolved. Fravashi continues this conversation by itself when it is back. Automatic attempt 2 of 3.",
            wait.Words);
    }

    [Theory]
    [MemberData(nameof(Zones))]
    public void A_credential_wait_through_a_broker_outage_says_its_retry_in_the_persons_zone(string zone, string hour)
    {
        var outage = new BrokerOutage(Noon, "could not reach broker.example: name not resolved", Noon.AddSeconds(15));
        var wait = new NetworkWait(Noon, NetworkFailure.None, "api.anthropic.com", 1, false,
            Reason: WaitReason.Credentials, Credential: outage.MintWords.Plain(), CredentialOutage: outage);

        Assert.Equal(
            $"Waiting for a fresh broker token since {hour}: Could not mint a broker token (could not reach broker.example: name not resolved). Fravashi retries by itself; next attempt at {hour}:15. Fravashi continues this conversation by itself once a fresh token is minted.",
            Said(wait.Sentence, zone));
        // The words the transcript and a log get are the stored text, as before.
        Assert.Equal(
            $"Waiting for a fresh broker token since {Plain(Noon)}: {outage.MintWords.Plain().TrimEnd('.')}. Fravashi continues this conversation by itself once a fresh token is minted.",
            wait.Words);
    }

    [Theory]
    [MemberData(nameof(Zones))]
    public void The_sign_in_grace_words_say_the_persons_hour(string zone, string hour)
    {
        using var ctx = InZone(zone, Noon.AddMinutes(5));
        var grace = new IdpGraceView(Noon, "could not reach idp.example.test: name not resolved", Noon.AddHours(1), Lapsed: false);

        var cut = ctx.Render<SignInGraceWords>(p => p.Add(c => c.Grace, grace));

        var later = PersonTime.Format(Noon.AddHours(1), Zone(zone), Noon);
        Assert.Equal(
            $"Signed in; could not refresh since {hour} (could not reach idp.example.test: name not resolved); locks at {later}.",
            cut.Find("[data-testid='signin-grace-words']").TextContent);
    }

    [Fact]
    public void A_sentence_with_no_zone_known_says_utc_and_nothing_else_changes()
    {
        var outage = new ProviderOutage("Jira", Noon, NameFails);
        Assert.Equal("Jira unreachable since 12:00 UTC (name not resolved)", Said(outage.Sentence, zone: null));
    }

    [Fact]
    public void A_sentence_in_a_string_says_the_same_as_the_markup()
    {
        var outage = new BrokerOutage(Noon, "could not reach broker.example: name not resolved", Noon.AddSeconds(15));
        Assert.Equal(
            Said(outage.MintWords, "Asia/Kolkata"),
            PersonTime.Format(outage.MintWords, Zone("Asia/Kolkata"), Noon.AddMinutes(5)));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
