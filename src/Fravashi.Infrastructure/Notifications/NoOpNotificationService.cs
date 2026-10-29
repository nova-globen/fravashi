using Fravashi.Application.Notifications;

namespace Fravashi.Infrastructure.Notifications;

/// <summary>No-op fallback when Telegram is not configured.</summary>
internal sealed class NoOpNotificationService : INotificationService
{
    public void Notify(string message) { }
}
