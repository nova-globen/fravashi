namespace Fravashi.Application.Notifications;

/// <summary>
/// A configurable channel for reaching a person about something waiting (ADR 0031 decision 12, backlog 304).
/// This version ships exactly one implementation (a Windows toast through fravashi.exe's tray); later channels
/// (phone push, email, Telegram, Slack, Teams) are recorded in docs/v2.md until one is picked.
/// </summary>
public interface IWaitingChannel
{
    /// <summary>
    /// Tells the channel about one new waiting entry. Implementations must build their own words from
    /// <see cref="WaitingForPersonEntry.Kind"/> rather than forwarding <see cref="WaitingForPersonEntry.What"/>,
    /// a tool's command line, a question's text, or any other content: the channel names the item and that it
    /// waits, never what it is about.
    /// </summary>
    Task NotifyAsync(WaitingForPersonEntry entry, CancellationToken ct = default);
}
