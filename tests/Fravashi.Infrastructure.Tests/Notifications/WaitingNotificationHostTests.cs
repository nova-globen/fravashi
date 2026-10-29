using AwesomeAssertions;
using Fravashi.Application.Notifications;
using Fravashi.Domain.AgentSessions;
using Fravashi.Infrastructure.Notifications;
using Xunit;

namespace Fravashi.Infrastructure.Tests.Notifications;

/// <summary>
/// Backlog 304: the host that polls <see cref="IWaitingForPersonList"/> and raises a toast through
/// <see cref="IWaitingChannel"/> for each NEW entry. <c>PollOnceAsync</c> is called directly (the
/// <c>AutopilotEngineHost</c> shape), never through the timer.
/// </summary>
public sealed class WaitingNotificationHostTests
{
    [Fact]
    public async Task A_new_entry_is_notified_once_and_not_again_on_the_next_poll()
    {
        var entry = Entry(DateTimeOffset.UtcNow);
        var list = new FakeList([entry]);
        var channel = new FakeChannel();
        var host = new WaitingNotificationHost(list, [channel]);

        await host.PollOnceAsync(CancellationToken.None);
        await host.PollOnceAsync(CancellationToken.None);

        channel.Notified.Should().ContainSingle().Which.Should().Be(entry);
    }

    [Fact]
    public async Task Every_registered_channel_is_notified_of_a_new_entry()
    {
        var entry = Entry(DateTimeOffset.UtcNow);
        var list = new FakeList([entry]);
        var first = new FakeChannel();
        var second = new FakeChannel();
        var host = new WaitingNotificationHost(list, [first, second]);

        await host.PollOnceAsync(CancellationToken.None);

        first.Notified.Should().ContainSingle().Which.Should().Be(entry);
        second.Notified.Should().ContainSingle().Which.Should().Be(entry);
    }

    [Fact]
    public async Task A_channel_that_throws_is_logged_and_does_not_stop_the_host()
    {
        var list = new FakeList([Entry(DateTimeOffset.UtcNow)]);
        var channel = new ThrowingChannel();
        var host = new WaitingNotificationHost(list, [channel]);

        await host.PollOnceAsync(CancellationToken.None);

        // Did not throw, and a later poll of a different entry still works.
        list.Entries = [Entry(DateTimeOffset.UtcNow.AddMinutes(1))];
        await host.PollOnceAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_channel_that_throws_does_not_stop_a_later_channel_for_the_same_entry()
    {
        var entry = Entry(DateTimeOffset.UtcNow);
        var list = new FakeList([entry]);
        var throwing = new ThrowingChannel();
        var working = new FakeChannel();
        var host = new WaitingNotificationHost(list, [throwing, working]);

        await host.PollOnceAsync(CancellationToken.None);

        working.Notified.Should().ContainSingle().Which.Should().Be(entry);
    }

    [Fact]
    public async Task A_list_read_that_throws_is_logged_and_skipped()
    {
        var list = new ThrowingList();
        var channel = new FakeChannel();
        var host = new WaitingNotificationHost(list, [channel]);

        await host.PollOnceAsync(CancellationToken.None);

        channel.Notified.Should().BeEmpty();
    }

    private static WaitingForPersonEntry Entry(DateTimeOffset since) =>
        new(WaitingForPersonKind.Approval, "Waiting on your approval: Bash", since, "ws-1",
            SessionId: new AgentSessionId(Guid.NewGuid()));

    private sealed class FakeList(IReadOnlyList<WaitingForPersonEntry> entries) : IWaitingForPersonList
    {
        public IReadOnlyList<WaitingForPersonEntry> Entries { get; set; } = entries;

        public Task<IReadOnlyList<WaitingForPersonEntry>> CurrentAsync(CancellationToken ct = default) =>
            Task.FromResult(Entries);
    }

    private sealed class ThrowingList : IWaitingForPersonList
    {
        public Task<IReadOnlyList<WaitingForPersonEntry>> CurrentAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("the store is unreadable");
    }

    private sealed class FakeChannel : IWaitingChannel
    {
        public List<WaitingForPersonEntry> Notified { get; } = [];

        public Task NotifyAsync(WaitingForPersonEntry entry, CancellationToken ct = default)
        {
            Notified.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingChannel : IWaitingChannel
    {
        public Task NotifyAsync(WaitingForPersonEntry entry, CancellationToken ct = default) =>
            throw new InvalidOperationException("the channel failed");
    }
}
