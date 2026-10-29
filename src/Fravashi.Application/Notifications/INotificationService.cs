namespace Fravashi.Application.Notifications;

/// <summary>
/// Sends out-of-band notifications (e.g. Telegram) when the studio needs to reach the developer.
/// A no-op implementation is registered when the service is not configured.
/// </summary>
public interface INotificationService
{
    void Notify(string message);
}
