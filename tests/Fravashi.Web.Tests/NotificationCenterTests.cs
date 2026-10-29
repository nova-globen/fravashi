using Bunit;
using Fravashi.Application.Notifications;
using Fravashi.Infrastructure.Notifications;
using Fravashi.Web.Components.Shell;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Web.Tests;

public class NotificationCenterTests
{
    // Longer than NotificationCenter's own 5s poll interval, since the dot's disappearance depends on it.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public void Emitted_failure_appears_in_the_notification_menu()
    {
        using var ctx = new BunitContext();
        var history = new InMemoryNotificationHistory();
        ctx.Services.AddSingleton<INotificationHistory>(history);
        ctx.Services.AddSingleton<IWaitingForPersonList>(new FakeWaitingForPersonList([]));

        var cut = ctx.Render<NotificationCenter>();

        history.Add("Autopilot gate failed: build broke", NotificationSeverity.Error);
        cut.Find(".notification-center-button").Click();

        Assert.Contains("Autopilot gate failed", cut.Markup);
        Assert.Contains("notification-error", cut.Markup);
        Assert.DoesNotContain("notification-count", cut.Markup);
    }

    [Fact]
    public void A_waiting_entry_shows_one_dot_and_one_summary_row_never_one_per_entry()
    {
        using var ctx = new BunitContext();
        var history = new InMemoryNotificationHistory();
        ctx.Services.AddSingleton<INotificationHistory>(history);
        var waiting = new FakeWaitingForPersonList([
            new WaitingForPersonEntry(WaitingForPersonKind.Approval, "Waiting on your approval: Bash", DateTimeOffset.UtcNow, "ws-1"),
            new WaitingForPersonEntry(WaitingForPersonKind.Question, "Waiting on your answer: ...", DateTimeOffset.UtcNow, "ws-1"),
        ]);
        ctx.Services.AddSingleton<IWaitingForPersonList>(waiting);

        var cut = ctx.Render<NotificationCenter>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".notification-waiting-dot")), Patience);

        cut.Find(".notification-center-button").Click();
        Assert.Single(cut.FindAll(".notification-waiting-row"));
        Assert.Contains("2 waiting for you", cut.Markup);

        waiting.Entries = [];
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".notification-waiting-dot")), Patience);
        Assert.Empty(cut.FindAll(".notification-waiting-row"));
    }

    private sealed class FakeWaitingForPersonList(IReadOnlyList<WaitingForPersonEntry> entries) : IWaitingForPersonList
    {
        public IReadOnlyList<WaitingForPersonEntry> Entries { get; set; } = entries;

        public Task<IReadOnlyList<WaitingForPersonEntry>> CurrentAsync(CancellationToken ct = default) =>
            Task.FromResult(Entries);
    }
}
