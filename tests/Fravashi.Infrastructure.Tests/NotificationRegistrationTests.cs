using Fravashi.Application.Notifications;
using Fravashi.Infrastructure.Notifications;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fravashi.Infrastructure.Tests;

public class NotificationRegistrationTests
{
    [Fact]
    public void AddNotifications_registers_history_and_telegram_when_credentials_are_present()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Telegram:BotToken"] = "bot-token",
            ["Telegram:ChatId"] = "12345",
        });

        provider.GetRequiredService<INotificationHistory>().Should().BeOfType<InMemoryNotificationHistory>();
        provider.GetRequiredService<TelegramNotificationService>().Should().NotBeNull();
        provider.GetRequiredService<INotificationService>().Should().BeOfType<NotificationHubService>();
    }

    [Fact]
    public void AddNotifications_records_history_when_chat_id_is_missing()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Telegram:BotToken"] = "bot-token",
        });

        var service = provider.GetRequiredService<INotificationService>();
        var history = provider.GetRequiredService<INotificationHistory>();

        service.Notify("Autopilot stopped");

        history.Recent.Should().ContainSingle(n => n.Message == "Autopilot stopped");
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddNotifications(configuration);
        return services.BuildServiceProvider();
    }
}
