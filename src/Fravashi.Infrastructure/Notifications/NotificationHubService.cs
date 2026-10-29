using Fravashi.Application.Notifications;

namespace Fravashi.Infrastructure.Notifications;

public sealed class NotificationHubService(
    INotificationHistory history,
    TelegramNotificationService? telegram = null) : INotificationService
{
    // Backlog 74: this method used to also increment a StudioTelemetry OpenTelemetry counter. That
    // meter was registered with an Aspire dashboard the product no longer has and no exporter replaced
    // it, so the count went nowhere. Nothing was routed into the product-event recorder in its place:
    // the line above it already writes every notification, with its severity, into the notification
    // history the user can open, so a second record of the same fact would duplicate a store that
    // exists rather than answer a question that does not have an answer.
    public void Notify(string message)
    {
        var severity = Classify(message);
        history.Add(message, severity);
        telegram?.Notify(message);
    }

    private static NotificationSeverity Classify(string message)
    {
        if (message.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || message.Contains("error", StringComparison.OrdinalIgnoreCase)
            || message.Contains("stopped", StringComparison.OrdinalIgnoreCase))
        {
            return NotificationSeverity.Error;
        }

        if (message.Contains("warning", StringComparison.OrdinalIgnoreCase)
            || message.Contains("paid", StringComparison.OrdinalIgnoreCase)
            || message.Contains("credit", StringComparison.OrdinalIgnoreCase))
        {
            return NotificationSeverity.Warning;
        }

        if (message.Contains("complete", StringComparison.OrdinalIgnoreCase)
            || message.Contains("passed", StringComparison.OrdinalIgnoreCase))
        {
            return NotificationSeverity.Success;
        }

        return NotificationSeverity.Info;
    }
}
