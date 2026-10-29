namespace Fravashi.Application.Notifications;

public enum NotificationSeverity
{
    Info,
    Warning,
    Error,
    Success,
}

public sealed record StudioNotification(
    Guid Id,
    DateTimeOffset Timestamp,
    string Message,
    NotificationSeverity Severity,
    bool IsRead);

public interface INotificationHistory
{
    event Action? Changed;

    IReadOnlyList<StudioNotification> Recent { get; }

    int UnreadCount { get; }

    void Add(string message, NotificationSeverity severity = NotificationSeverity.Info);

    void MarkAllRead();

    void Clear();
}
