using AwesomeAssertions;
using Fravashi.Application.Notifications;
using Xunit;

namespace Fravashi.Application.Tests.Notifications;

/// <summary>Backlog 304: the one ordering the home page's waiting list and any other reader agree on.</summary>
public sealed class WaitingForPersonOrderingTests
{
    [Fact]
    public void Oldest_wait_comes_first_across_mixed_kinds()
    {
        var now = DateTimeOffset.UtcNow;
        var approval = Entry(WaitingForPersonKind.Approval, now);
        var question = Entry(WaitingForPersonKind.Question, now.AddMinutes(-10));
        var parked = Entry(WaitingForPersonKind.Parked, now.AddHours(-1));

        var ordered = WaitingForPersonOrdering.Order([approval, question, parked]);

        ordered.Should().Equal(parked, question, approval);
    }

    [Fact]
    public void A_tie_keeps_the_given_order()
    {
        var since = DateTimeOffset.UtcNow;
        var first = Entry(WaitingForPersonKind.Approval, since);
        var second = Entry(WaitingForPersonKind.Question, since);

        var ordered = WaitingForPersonOrdering.Order([first, second]);

        ordered.Should().Equal(first, second);
    }

    private static WaitingForPersonEntry Entry(WaitingForPersonKind kind, DateTimeOffset since) =>
        new(kind, "waiting", since, "ws-1");
}
