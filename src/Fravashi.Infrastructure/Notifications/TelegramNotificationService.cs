using System.Text.Json;
using Fravashi.Application.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Fravashi.Infrastructure.Notifications;

/// <summary>
/// Sends plain-text notifications via the Telegram Bot API. Reads <c>Telegram:BotToken</c> and
/// <c>Telegram:ChatId</c> from configuration (user-secrets). No-op if either is absent.
/// </summary>
public sealed class TelegramNotificationService : INotificationService
{
    private readonly string? _botToken;
    private readonly string? _chatId;
    private readonly ILogger<TelegramNotificationService> _logger;

    public TelegramNotificationService(IConfiguration configuration, ILogger<TelegramNotificationService> logger)
    {
        _botToken = configuration["Telegram:BotToken"];
        _chatId = configuration["Telegram:ChatId"];
        _logger = logger;
    }

    public void Notify(string message)
    {
        if (string.IsNullOrWhiteSpace(_botToken) || string.IsNullOrWhiteSpace(_chatId)) return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var url = $"https://api.telegram.org/bot{_botToken}/sendMessage";
                object chatIdValue = long.TryParse(_chatId, out var numId) ? (object)numId : _chatId;
                var body = JsonSerializer.Serialize(new { chat_id = chatIdValue, text = message });
                using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                var response = await http.PostAsync(url, content);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Telegram API returned {Status}", (int)response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Telegram notification failed");
            }
        });
    }
}
