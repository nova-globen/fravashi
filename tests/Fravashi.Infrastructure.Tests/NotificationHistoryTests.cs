using Fravashi.Application.Notifications;
using Fravashi.Infrastructure.Notifications;
using AwesomeAssertions;
using Xunit;

namespace Fravashi.Infrastructure.Tests;

public class NotificationHistoryTests
{
    [Fact]
    public void Add_records_unread_notification_and_mark_all_read_clears_count()
    {
        var history = new InMemoryNotificationHistory();

        history.Add("Autopilot gate failed", NotificationSeverity.Error);

        history.Recent.Should().ContainSingle(n =>
            n.Message == "Autopilot gate failed"
            && n.Severity == NotificationSeverity.Error
            && !n.IsRead);
        history.UnreadCount.Should().Be(1);

        history.MarkAllRead();

        history.UnreadCount.Should().Be(0);
        history.Recent.Should().ContainSingle(n => n.IsRead);
    }

    [Fact]
    public void Notification_hub_records_messages_even_without_telegram()
    {
        var history = new InMemoryNotificationHistory();
        var hub = new NotificationHubService(history);

        hub.Notify("Autopilot complete");

        history.Recent.Should().ContainSingle(n =>
            n.Message == "Autopilot complete"
            && n.Severity == NotificationSeverity.Success);
    }
}
