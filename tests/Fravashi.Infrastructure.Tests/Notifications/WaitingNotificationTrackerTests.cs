using AwesomeAssertions;
using Fravashi.Application.Notifications;
using Fravashi.Infrastructure.Notifications;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Notifications;

/// <summary>
/// The whole "toast once per new waiting entry, never repeatedly for the same one" rule (ADR 0031 decision
/// 12, backlog 304), as a pure diff with no timer involved.
/// </summary>
public sealed class WaitingNotificationTrackerTests
{
    private static readonly HashSet<string> NoKeys = new(StringComparer.Ordinal);

    [Fact]
    public void A_fresh_entry_is_reported_new()
    {
        var entry = Entry(DateTimeOffset.UtcNow);

        var diff = WaitingNotificationTracker.Diff(NoKeys, [entry]);

        diff.NewEntries.Should().ContainSingle().Which.Should().Be(entry);
        diff.NextKeys.Should().ContainSingle(entry.Key);
    }

    [Fact]
    public void The_same_entry_on_the_next_poll_is_not_reported_again()
    {
        var entry = Entry(DateTimeOffset.UtcNow);
        var first = WaitingNotificationTracker.Diff(NoKeys, [entry]);

        var second = WaitingNotificationTracker.Diff(first.NextKeys, [entry]);

        second.NewEntries.Should().BeEmpty();
    }

    [Fact]
    public void A_resolved_entry_followed_by_a_new_one_for_the_same_session_is_reported_new()
    {
        var since = DateTimeOffset.UtcNow;
        var resolvedAway = Entry(since);
        var first = WaitingNotificationTracker.Diff(NoKeys, [resolvedAway]);

        // The first approval was answered; a second, different one now waits on the same session, with a
        // new SinceUtc, so its key differs even though the session is the same.
        var newWait = Entry(since.AddMinutes(1));
        var second = WaitingNotificationTracker.Diff(first.NextKeys, [newWait]);

        second.NewEntries.Should().ContainSingle().Which.Should().Be(newWait);
    }

    [Fact]
    public void Nothing_waiting_reports_nothing_new_and_an_empty_key_set()
    {
        var diff = WaitingNotificationTracker.Diff(NoKeys, []);

        diff.NewEntries.Should().BeEmpty();
        diff.NextKeys.Should().BeEmpty();
    }

    private static WaitingForPersonEntry Entry(DateTimeOffset since) =>
        new(WaitingForPersonKind.Approval, "Waiting on your approval: Bash", since, "ws-1",
            SessionId: new Fravashi.Domain.AgentSessions.AgentSessionId(Guid.Parse("11111111-1111-1111-1111-111111111111")));
}
